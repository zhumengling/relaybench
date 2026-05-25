using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using RelayBench.Core.Models;

namespace RelayBench.Core.Services;

public sealed partial class StunProbeService
{
    internal static StunResponse ParseResponse(byte[] buffer, byte[] transactionId)
    {
        if (buffer.Length < 20)
        {
            throw new InvalidOperationException("STUN 响应长度小于最小头部长度。");
        }

        var messageType = ReadUInt16(buffer, 0);
        if (messageType != 0x0101)
        {
            throw new InvalidOperationException($"收到未预期的 STUN 消息类型：0x{messageType:X4}");
        }

        var responseTransactionId = buffer[8..20];
        if (!responseTransactionId.SequenceEqual(transactionId))
        {
            throw new InvalidOperationException("STUN 事务 ID 与请求不匹配。");
        }

        Dictionary<string, string> attributes = new(StringComparer.OrdinalIgnoreCase);
        IPEndPoint? mappedEndPoint = null;
        IPEndPoint? otherEndpoint = null;
        IPEndPoint? changedEndpoint = null;
        IPEndPoint? responseOrigin = null;

        var offset = 20;
        while (offset + 4 <= buffer.Length)
        {
            var attributeType = ReadUInt16(buffer, offset);
            var attributeLength = ReadUInt16(buffer, offset + 2);
            var valueOffset = offset + 4;

            if (valueOffset + attributeLength > buffer.Length)
            {
                break;
            }

            var value = buffer[valueOffset..(valueOffset + attributeLength)];
            var attributeName = attributeType switch
            {
                0x0001 => "MAPPED-ADDRESS",
                0x0005 => "CHANGED-ADDRESS",
                0x0020 => "XOR-MAPPED-ADDRESS",
                0x802B => "RESPONSE-ORIGIN",
                0x802C => "OTHER-ADDRESS",
                _ => $"0x{attributeType:X4}"
            };

            var decodedEndpoint = attributeType switch
            {
                0x0001 => DecodeEndpoint(value, xorMapped: false),
                0x0005 => DecodeEndpoint(value, xorMapped: false),
                0x0020 => DecodeEndpoint(value, xorMapped: true),
                0x802B => DecodeEndpoint(value, xorMapped: false),
                0x802C => DecodeEndpoint(value, xorMapped: false),
                _ => null
            };

            if (decodedEndpoint is not null)
            {
                attributes[attributeName] = decodedEndpoint.ToString();

                switch (attributeType)
                {
                    case 0x0001:
                    case 0x0020:
                        mappedEndPoint = decodedEndpoint;
                        break;
                    case 0x0005:
                        changedEndpoint = decodedEndpoint;
                        break;
                    case 0x802B:
                        responseOrigin = decodedEndpoint;
                        break;
                    case 0x802C:
                        otherEndpoint = decodedEndpoint;
                        break;
                }
            }
            else
            {
                attributes[attributeName] = Convert.ToHexString(value);
            }

            offset += 4 + attributeLength;
            if (attributeLength % 4 != 0)
            {
                offset += 4 - (attributeLength % 4);
            }
        }

        return new StunResponse(
            mappedEndPoint,
            otherEndpoint,
            changedEndpoint,
            responseOrigin,
            attributes);
    }

    private static IPEndPoint? DecodeEndpoint(byte[] value, bool xorMapped)
    {
        if (value.Length < 8)
        {
            return null;
        }

        var family = value[1];
        if (family != 0x01)
        {
            return null;
        }

        var port = ReadUInt16(value, 2);
        var ipBytes = value[4..8].ToArray();

        if (xorMapped)
        {
            port ^= 0x2112;
            var magicCookie = new byte[] { 0x21, 0x12, 0xA4, 0x42 };
            for (var index = 0; index < ipBytes.Length; index++)
            {
                ipBytes[index] ^= magicCookie[index];
            }
        }

        return new IPEndPoint(new IPAddress(ipBytes), port);
    }

    private static ushort ReadUInt16(IReadOnlyList<byte> buffer, int offset)
        => (ushort)((buffer[offset] << 8) | buffer[offset + 1]);

    private enum ChangeRequestMode
    {
        None,
        ChangeIpAndPort,
        ChangePortOnly
    }

    private sealed record BindingTestOutcome(
        string TestName,
        string RequestTarget,
        string RequestMode,
        bool Success,
        IPEndPoint? LocalEndpoint,
        IPEndPoint? RequestEndpoint,
        IPEndPoint? RespondingEndpoint,
        StunResponse? Response,
        TimeSpan? RoundTrip,
        string Summary,
        string? Error)
    {
        public StunNatBindingTestResult ToPublicModel()
            => new(
                TestName,
                RequestTarget,
                RequestMode,
                Success,
                LocalEndpoint?.ToString(),
                Response?.MappedAddress,
                Response?.ResponseOrigin,
                Response?.AlternateAddress,
                RoundTrip,
                Summary,
                Error);
    }

    internal sealed record StunResponse(
        IPEndPoint? MappedEndPoint,
        IPEndPoint? OtherEndpoint,
        IPEndPoint? ChangedEndpoint,
        IPEndPoint? ResponseOriginEndpoint,
        IReadOnlyDictionary<string, string> Attributes)
    {
        public string? MappedAddress => MappedEndPoint?.ToString();

        public string? OtherAddress => OtherEndpoint?.ToString();

        public string? ChangedAddress => ChangedEndpoint?.ToString();

        public string? ResponseOrigin => ResponseOriginEndpoint?.ToString();

        public string? AlternateAddress => OtherAddress ?? ChangedAddress;

        public IPEndPoint? AlternateEndpoint => OtherEndpoint ?? ChangedEndpoint;
    }
}
