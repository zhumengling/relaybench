using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using RelayBench.Core.Models;

namespace RelayBench.Core.Services;

public sealed partial class PortScanDiagnosticsService
{
    private static string BuildHttpSummary(string responseText)
    {
        var lines = responseText
            .Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (lines.Length == 0 || !lines[0].StartsWith("HTTP/", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        List<string> parts = [lines[0]];

        var serverHeader = lines.FirstOrDefault(static line => line.StartsWith("Server:", StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(serverHeader))
        {
            parts.Add(serverHeader);
        }

        var locationHeader = lines.FirstOrDefault(static line => line.StartsWith("Location:", StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(locationHeader))
        {
            parts.Add(locationHeader);
        }

        return ShortenText(string.Join("; ", parts), 220);
    }

    private static string NormalizeProtocolSummary(string prefix, string responseText)
    {
        var lines = responseText
            .Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Take(4)
            .ToArray();

        if (lines.Length == 0)
        {
            return string.Empty;
        }

        return ShortenText($"{prefix} {string.Join("; ", lines)}", 220);
    }

    private static byte[] BuildDnsQueryPacket(string domain, ushort transactionId)
    {
        var normalizedDomain = string.IsNullOrWhiteSpace(domain) ? "example.com" : domain.Trim().Trim('.');
        List<byte> packet = new();
        packet.Add((byte)((transactionId >> 8) & 0xFF));
        packet.Add((byte)(transactionId & 0xFF));
        packet.Add(0x01);
        packet.Add(0x00);
        packet.Add(0x00);
        packet.Add(0x01);
        packet.Add(0x00);
        packet.Add(0x00);
        packet.Add(0x00);
        packet.Add(0x00);
        packet.Add(0x00);
        packet.Add(0x00);

        foreach (var label in normalizedDomain.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var bytes = Encoding.ASCII.GetBytes(label);
            packet.Add((byte)Math.Min(bytes.Length, 63));
            packet.AddRange(bytes.Take(63));
        }

        packet.Add(0x00);
        packet.Add(0x00);
        packet.Add(0x01);
        packet.Add(0x00);
        packet.Add(0x01);
        return packet.ToArray();
    }

    private static string BuildDnsSummary(byte[] responseBytes, bool useUdp)
    {
        if (responseBytes.Length < 12)
        {
            return string.Empty;
        }

        var answerCount = (responseBytes[6] << 8) | responseBytes[7];
        var authorityCount = (responseBytes[8] << 8) | responseBytes[9];
        var additionalCount = (responseBytes[10] << 8) | responseBytes[11];
        var flags = (responseBytes[2] << 8) | responseBytes[3];
        var rcode = flags & 0x000F;
        var transport = useUdp ? "UDP" : "TCP";
        return $"DNS/{transport}; answers={answerCount}; authority={authorityCount}; additional={additionalCount}; rcode={rcode}";
    }

    private static string BuildStunSummary(byte[] responseBytes, byte[] expectedTransactionId)
    {
        if (responseBytes.Length < 20 ||
            responseBytes[0] != 0x01 ||
            responseBytes[1] != 0x01)
        {
            return string.Empty;
        }

        for (var index = 0; index < expectedTransactionId.Length; index++)
        {
            if (responseBytes[8 + index] != expectedTransactionId[index])
            {
                return string.Empty;
            }
        }

        var attributeOffset = 20;
        string? mappedAddress = null;
        while (attributeOffset + 4 <= responseBytes.Length)
        {
            var attributeType = (responseBytes[attributeOffset] << 8) | responseBytes[attributeOffset + 1];
            var attributeLength = (responseBytes[attributeOffset + 2] << 8) | responseBytes[attributeOffset + 3];
            var attributeValueOffset = attributeOffset + 4;
            if (attributeValueOffset + attributeLength > responseBytes.Length)
            {
                break;
            }

            if (attributeType is 0x0001 or 0x0020)
            {
                mappedAddress = TryParseStunMappedAddress(responseBytes, attributeValueOffset, attributeLength, attributeType == 0x0020);
                if (!string.IsNullOrWhiteSpace(mappedAddress))
                {
                    break;
                }
            }

            attributeOffset = attributeValueOffset + attributeLength;
            while (attributeOffset % 4 != 0)
            {
                attributeOffset++;
            }
        }

        return string.IsNullOrWhiteSpace(mappedAddress)
            ? "STUN Binding Success"
            : $"STUN Binding Success; mapped={mappedAddress}";
    }

    private static string? TryParseStunMappedAddress(byte[] buffer, int offset, int length, bool xorMapped)
    {
        if (length < 4)
        {
            return null;
        }

        var family = buffer[offset + 1];
        var port = (buffer[offset + 2] << 8) | buffer[offset + 3];
        if (xorMapped)
        {
            port ^= 0x2112;
        }

        if (family == 0x01 && length >= 8)
        {
            byte[] addressBytes = new byte[4];
            Buffer.BlockCopy(buffer, offset + 4, addressBytes, 0, 4);
            if (xorMapped)
            {
                addressBytes[0] ^= 0x21;
                addressBytes[1] ^= 0x12;
                addressBytes[2] ^= 0xA4;
                addressBytes[3] ^= 0x42;
            }

            return $"{new IPAddress(addressBytes)}:{port}";
        }

        if (family == 0x02 && length >= 20)
        {
            byte[] addressBytes = new byte[16];
            Buffer.BlockCopy(buffer, offset + 4, addressBytes, 0, 16);
            if (xorMapped)
            {
                byte[] cookieAndTransaction =
                [
                    0x21, 0x12, 0xA4, 0x42,
                    0, 0, 0, 0,
                    0, 0, 0, 0,
                    0, 0, 0, 0
                ];
                for (var index = 0; index < addressBytes.Length; index++)
                {
                    addressBytes[index] ^= cookieAndTransaction[index];
                }
            }

            return $"[{new IPAddress(addressBytes)}]:{port}";
        }

        return null;
    }
}
