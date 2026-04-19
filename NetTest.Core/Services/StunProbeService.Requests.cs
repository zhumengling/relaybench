using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using NetTest.Core.Models;

namespace NetTest.Core.Services;

public sealed partial class StunProbeService
{
    private static async Task<BindingTestOutcome> RunBindingTestAsync(
        UdpClient client,
        IPEndPoint endpoint,
        ChangeRequestMode mode,
        string testName,
        CancellationToken cancellationToken)
    {
        var request = BuildBindingRequest(mode, out var transactionId);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(3));

        try
        {
            var stopwatch = Stopwatch.StartNew();
            await client.SendAsync(request, request.Length, endpoint);
            var localEndpoint = client.Client.LocalEndPoint as IPEndPoint;
            var received = await client.ReceiveAsync(timeoutCts.Token);
            stopwatch.Stop();

            var response = ParseResponse(received.Buffer, transactionId);
            return new BindingTestOutcome(
                testName,
                endpoint.ToString(),
                DescribeMode(mode),
                true,
                localEndpoint,
                response,
                stopwatch.Elapsed,
                $"{testName} 成功：映射地址 {response.MappedAddress ?? "--"}，耗时 {stopwatch.Elapsed.TotalMilliseconds:F0} ms。",
                null);
        }
        catch (Exception ex)
        {
            return new BindingTestOutcome(
                testName,
                endpoint.ToString(),
                DescribeMode(mode),
                false,
                client.Client.LocalEndPoint as IPEndPoint,
                null,
                null,
                $"{testName} 失败。",
                ex.Message);
        }
    }

    private static byte[] BuildBindingRequest(ChangeRequestMode mode, out byte[] transactionId)
    {
        transactionId = Guid.NewGuid().ToByteArray()[..12];
        var attributes = mode == ChangeRequestMode.None ? Array.Empty<byte>() : BuildChangeRequestAttribute(mode);
        var payload = new byte[20 + attributes.Length];

        payload[0] = 0x00;
        payload[1] = 0x01;
        payload[2] = (byte)((attributes.Length >> 8) & 0xFF);
        payload[3] = (byte)(attributes.Length & 0xFF);
        payload[4] = 0x21;
        payload[5] = 0x12;
        payload[6] = 0xA4;
        payload[7] = 0x42;
        Array.Copy(transactionId, 0, payload, 8, transactionId.Length);

        if (attributes.Length > 0)
        {
            Array.Copy(attributes, 0, payload, 20, attributes.Length);
        }

        return payload;
    }

    private static byte[] BuildChangeRequestAttribute(ChangeRequestMode mode)
    {
        byte flag = mode switch
        {
            ChangeRequestMode.ChangeIpAndPort => 0x06,
            ChangeRequestMode.ChangePortOnly => 0x02,
            _ => 0x00
        };

        return
        [
            0x00, 0x03,
            0x00, 0x04,
            0x00, 0x00, 0x00, flag
        ];
    }

    private static string DescribeMode(ChangeRequestMode mode)
        => mode switch
        {
            ChangeRequestMode.None => "基础 Binding",
            ChangeRequestMode.ChangeIpAndPort => "CHANGE-REQUEST：切换 IP 与端口",
            ChangeRequestMode.ChangePortOnly => "CHANGE-REQUEST：仅切换端口",
            _ => "Binding"
        };
}
