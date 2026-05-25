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
    private static async Task<ProbeOutcome> TryProbeApplicationProtocolAsync(
        string target,
        IPAddress address,
        int port,
        bool useTls,
        int timeoutMilliseconds,
        CancellationToken cancellationToken)
    {
        return port switch
        {
            21 => await TryProbeTextCommandAsync(target, address, port, false, "SYST\r\n", timeoutMilliseconds, "FTP", cancellationToken),
            25 or 587 => await TryProbeTextCommandAsync(target, address, port, false, "EHLO relaybench.local\r\n", timeoutMilliseconds, "SMTP", cancellationToken),
            110 => await TryProbeTextCommandAsync(target, address, port, false, "CAPA\r\n", timeoutMilliseconds, "POP3", cancellationToken),
            143 => await TryProbeTextCommandAsync(target, address, port, false, "a001 CAPABILITY\r\n", timeoutMilliseconds, "IMAP", cancellationToken),
            465 when useTls => await TryProbeTextCommandAsync(target, address, port, true, "EHLO relaybench.local\r\n", timeoutMilliseconds, "SMTPS", cancellationToken),
            993 when useTls => await TryProbeTextCommandAsync(target, address, port, true, "a001 CAPABILITY\r\n", timeoutMilliseconds, "IMAPS", cancellationToken),
            995 when useTls => await TryProbeTextCommandAsync(target, address, port, true, "CAPA\r\n", timeoutMilliseconds, "POP3S", cancellationToken),
            53 => await TryProbeDnsTcpAsync(address, port, timeoutMilliseconds, cancellationToken),
            _ => default
        };
    }

    private static async Task<UdpProbeResult> TryProbeUdpAsync(
        string target,
        IPAddress address,
        int port,
        int timeoutMilliseconds,
        CancellationToken cancellationToken)
    {
        return port switch
        {
            53 => await TryProbeDnsUdpAsync(target, address, port, timeoutMilliseconds, cancellationToken),
            123 => await TryProbeNtpAsync(address, port, timeoutMilliseconds, cancellationToken),
            1900 => await TryProbeSsdpAsync(address, port, timeoutMilliseconds, cancellationToken),
            3478 => await TryProbeStunUdpAsync(address, port, timeoutMilliseconds, cancellationToken),
            _ => default
        };
    }

    private static async Task<long?> TryMeasureConnectLatencyAsync(
        IPAddress address,
        int port,
        int timeoutMilliseconds,
        CancellationToken cancellationToken)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();

        try
        {
            using TcpClient client = new(address.AddressFamily);
            await ConnectWithTimeoutAsync(client, address, port, timeoutMilliseconds, cancellationToken);
            stopwatch.Stop();
            return stopwatch.ElapsedMilliseconds;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (SocketException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static async Task ConnectWithTimeoutAsync(
        TcpClient client,
        IPAddress address,
        int port,
        int timeoutMilliseconds,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeoutMilliseconds);
        await client.ConnectAsync(address, port, timeoutCts.Token);
    }

    private static async Task<ProbeOutcome> TryReadPassiveBannerAsync(
        IPAddress address,
        int port,
        int timeoutMilliseconds,
        CancellationToken cancellationToken)
    {
        try
        {
            using TcpClient client = new(address.AddressFamily);
            await ConnectWithTimeoutAsync(client, address, port, timeoutMilliseconds, cancellationToken);
            using var stream = client.GetStream();
            var banner = await ReadTextAsync(stream, 512, Math.Min(timeoutMilliseconds, 450), cancellationToken);
            if (string.IsNullOrWhiteSpace(banner))
            {
                return default;
            }

            return new ProbeOutcome(true, ShortenText(banner, 180), null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return default;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new ProbeOutcome(false, null, ex.Message);
        }
    }

    private static async Task<ProbeOutcome> TryProbeTlsAsync(
        string target,
        IPAddress address,
        int port,
        int timeoutMilliseconds,
        CancellationToken cancellationToken)
    {
        try
        {
            using TcpClient client = new(address.AddressFamily);
            await ConnectWithTimeoutAsync(client, address, port, timeoutMilliseconds, cancellationToken);
            using var networkStream = client.GetStream();
            using SslStream sslStream = new(
                networkStream,
                leaveInnerStreamOpen: false,
                static (_, _, _, _) => true);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeoutMilliseconds);

            await sslStream.AuthenticateAsClientAsync(
                new SslClientAuthenticationOptions
                {
                    TargetHost = target,
                    RemoteCertificateValidationCallback = static (_, _, _, _) => true,
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck
                },
                timeoutCts.Token);

            X509Certificate2? certificate = sslStream.RemoteCertificate is null
                ? null
                : new X509Certificate2(sslStream.RemoteCertificate);

            List<string> summaryParts = [sslStream.SslProtocol.ToString()];

            if (certificate is not null)
            {
                var certificateName = certificate.GetNameInfo(X509NameType.DnsName, forIssuer: false);
                if (string.IsNullOrWhiteSpace(certificateName))
                {
                    certificateName = certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false);
                }

                if (!string.IsNullOrWhiteSpace(certificateName))
                {
                    summaryParts.Add($"证书={certificateName}");
                }

                summaryParts.Add($"到期={certificate.NotAfter:yyyy-MM-dd}");
            }

            return new ProbeOutcome(true, string.Join("; ", summaryParts), null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new ProbeOutcome(false, null, "握手超时");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new ProbeOutcome(false, null, ex.Message);
        }
    }

    private static async Task<ProbeOutcome> TryProbeHttpAsync(
        string target,
        IPAddress address,
        int port,
        bool useTls,
        int timeoutMilliseconds,
        CancellationToken cancellationToken)
    {
        try
        {
            using TcpClient client = new(address.AddressFamily);
            await ConnectWithTimeoutAsync(client, address, port, timeoutMilliseconds, cancellationToken);
            using var networkStream = client.GetStream();

            if (useTls)
            {
                using SslStream sslStream = new(networkStream, leaveInnerStreamOpen: false, static (_, _, _, _) => true);
                using var authCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                authCts.CancelAfter(timeoutMilliseconds);
                await sslStream.AuthenticateAsClientAsync(
                    new SslClientAuthenticationOptions
                    {
                        TargetHost = target,
                        RemoteCertificateValidationCallback = static (_, _, _, _) => true,
                        CertificateRevocationCheckMode = X509RevocationMode.NoCheck
                    },
                    authCts.Token);

                return await SendHttpHeadAsync(sslStream, target, timeoutMilliseconds, cancellationToken);
            }

            return await SendHttpHeadAsync(networkStream, target, timeoutMilliseconds, cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return default;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new ProbeOutcome(false, null, ex.Message);
        }
    }

    private static async Task<ProbeOutcome> SendHttpHeadAsync(
        Stream stream,
        string target,
        int timeoutMilliseconds,
        CancellationToken cancellationToken)
    {
        var hostHeader = target.Contains(':') && !target.Contains(']')
            ? $"[{target}]"
            : target;

        var requestText =
            $"HEAD / HTTP/1.1\r\nHost: {hostHeader}\r\nUser-Agent: RelayBench/{EngineVersion}\r\nAccept: */*\r\nConnection: close\r\n\r\n";
        var requestBytes = Encoding.ASCII.GetBytes(requestText);

        await stream.WriteAsync(requestBytes.AsMemory(0, requestBytes.Length), cancellationToken);
        await stream.FlushAsync(cancellationToken);

        var responseText = await ReadTextAsync(stream, 2048, timeoutMilliseconds, cancellationToken);
        if (string.IsNullOrWhiteSpace(responseText))
        {
            return default;
        }

        var summary = BuildHttpSummary(responseText);
        return string.IsNullOrWhiteSpace(summary)
            ? default
            : new ProbeOutcome(true, summary, null);
    }

    private static async Task<ProbeOutcome> TryProbeRedisAsync(
        IPAddress address,
        int port,
        int timeoutMilliseconds,
        CancellationToken cancellationToken)
    {
        try
        {
            using TcpClient client = new(address.AddressFamily);
            await ConnectWithTimeoutAsync(client, address, port, timeoutMilliseconds, cancellationToken);
            using var stream = client.GetStream();

            var payload = Encoding.ASCII.GetBytes("*1\r\n$4\r\nPING\r\n");
            await stream.WriteAsync(payload.AsMemory(0, payload.Length), cancellationToken);
            await stream.FlushAsync(cancellationToken);

            var responseText = await ReadTextAsync(stream, 256, timeoutMilliseconds, cancellationToken);
            if (string.IsNullOrWhiteSpace(responseText))
            {
                return default;
            }

            var summary = ShortenText(responseText, 80);
            if (summary.Contains("PONG", StringComparison.OrdinalIgnoreCase))
            {
                return new ProbeOutcome(true, $"Redis {summary}", null);
            }

            return default;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return default;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new ProbeOutcome(false, null, ex.Message);
        }
    }

    private static async Task<ProbeOutcome> TryProbeTextCommandAsync(
        string target,
        IPAddress address,
        int port,
        bool useTls,
        string requestText,
        int timeoutMilliseconds,
        string prefix,
        CancellationToken cancellationToken)
    {
        try
        {
            using TcpClient client = new(address.AddressFamily);
            await ConnectWithTimeoutAsync(client, address, port, timeoutMilliseconds, cancellationToken);
            using var networkStream = client.GetStream();
            Stream stream = networkStream;
            SslStream? sslStream = null;

            if (useTls)
            {
                sslStream = new SslStream(networkStream, leaveInnerStreamOpen: false, static (_, _, _, _) => true);
                using var authCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                authCts.CancelAfter(timeoutMilliseconds);
                await sslStream.AuthenticateAsClientAsync(
                    new SslClientAuthenticationOptions
                    {
                        TargetHost = target,
                        RemoteCertificateValidationCallback = static (_, _, _, _) => true,
                        CertificateRevocationCheckMode = X509RevocationMode.NoCheck
                    },
                    authCts.Token);
                stream = sslStream;
            }

            try
            {
                _ = await ReadTextAsync(stream, 512, Math.Min(timeoutMilliseconds, 250), cancellationToken);
                var payload = Encoding.ASCII.GetBytes(requestText);
                await stream.WriteAsync(payload.AsMemory(0, payload.Length), cancellationToken);
                await stream.FlushAsync(cancellationToken);

                var responseText = await ReadTextAsync(stream, 2048, timeoutMilliseconds, cancellationToken);
                if (string.IsNullOrWhiteSpace(responseText))
                {
                    return default;
                }

                return new ProbeOutcome(true, NormalizeProtocolSummary(prefix, responseText), null);
            }
            finally
            {
                sslStream?.Dispose();
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return default;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new ProbeOutcome(false, null, ex.Message);
        }
    }

    private static async Task<ProbeOutcome> TryProbeDnsTcpAsync(
        IPAddress address,
        int port,
        int timeoutMilliseconds,
        CancellationToken cancellationToken)
    {
        try
        {
            var transactionId = (ushort)RandomNumberGenerator.GetInt32(1, ushort.MaxValue);
            var queryBytes = BuildDnsQueryPacket("example.com", transactionId);

            using TcpClient client = new(address.AddressFamily);
            await ConnectWithTimeoutAsync(client, address, port, timeoutMilliseconds, cancellationToken);
            using var stream = client.GetStream();

            var framed = new byte[queryBytes.Length + 2];
            framed[0] = (byte)((queryBytes.Length >> 8) & 0xFF);
            framed[1] = (byte)(queryBytes.Length & 0xFF);
            Buffer.BlockCopy(queryBytes, 0, framed, 2, queryBytes.Length);

            await stream.WriteAsync(framed.AsMemory(0, framed.Length), cancellationToken);
            await stream.FlushAsync(cancellationToken);

            var header = await ReadExactAsync(stream, 2, timeoutMilliseconds, cancellationToken);
            if (header is null || header.Length < 2)
            {
                return default;
            }

            var responseLength = (header[0] << 8) | header[1];
            if (responseLength <= 0 || responseLength > 4096)
            {
                return default;
            }

            var responseBytes = await ReadExactAsync(stream, responseLength, timeoutMilliseconds, cancellationToken);
            if (responseBytes is null)
            {
                return default;
            }

            var summary = BuildDnsSummary(responseBytes, useUdp: false);
            return string.IsNullOrWhiteSpace(summary)
                ? default
                : new ProbeOutcome(true, summary, null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return default;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new ProbeOutcome(false, null, ex.Message);
        }
    }

    private static async Task<UdpProbeResult> TryProbeDnsUdpAsync(
        string target,
        IPAddress address,
        int port,
        int timeoutMilliseconds,
        CancellationToken cancellationToken)
    {
        try
        {
            var domain = IsLikelyDnsName(target) ? target : "example.com";
            var transactionId = (ushort)RandomNumberGenerator.GetInt32(1, ushort.MaxValue);
            var queryBytes = BuildDnsQueryPacket(domain, transactionId);
            var exchange = await SendUdpPayloadAsync(address, port, queryBytes, timeoutMilliseconds, cancellationToken);
            if (exchange is null)
            {
                return default;
            }

            var summary = BuildDnsSummary(exchange.Value.ResponseBytes, useUdp: true);
            return string.IsNullOrWhiteSpace(summary)
                ? default
                : new UdpProbeResult(true, exchange.Value.RoundTripMilliseconds, "dns", summary, null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return default;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new UdpProbeResult(false, 0, "dns", null, ex.Message);
        }
    }

    private static async Task<UdpProbeResult> TryProbeNtpAsync(
        IPAddress address,
        int port,
        int timeoutMilliseconds,
        CancellationToken cancellationToken)
    {
        try
        {
            byte[] payload = new byte[48];
            payload[0] = 0x1B;

            var exchange = await SendUdpPayloadAsync(address, port, payload, timeoutMilliseconds, cancellationToken);
            if (exchange is null || exchange.Value.ResponseBytes.Length < 48)
            {
                return default;
            }

            var response = exchange.Value.ResponseBytes;
            var version = (response[0] >> 3) & 0x07;
            var mode = response[0] & 0x07;
            var stratum = response[1];
            var summary = $"NTP v{version}; mode={mode}; stratum={stratum}";
            return new UdpProbeResult(true, exchange.Value.RoundTripMilliseconds, "ntp", summary, null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return default;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new UdpProbeResult(false, 0, "ntp", null, ex.Message);
        }
    }

    private static async Task<UdpProbeResult> TryProbeSsdpAsync(
        IPAddress address,
        int port,
        int timeoutMilliseconds,
        CancellationToken cancellationToken)
    {
        try
        {
            var requestText =
                "M-SEARCH * HTTP/1.1\r\n" +
                "HOST:239.255.255.250:1900\r\n" +
                "MAN:\"ssdp:discover\"\r\n" +
                "MX:1\r\n" +
                "ST:ssdp:all\r\n\r\n";
            var payload = Encoding.ASCII.GetBytes(requestText);
            var exchange = await SendUdpPayloadAsync(address, port, payload, timeoutMilliseconds, cancellationToken);
            if (exchange is null)
            {
                return default;
            }

            var responseText = SanitizeText(Encoding.ASCII.GetString(exchange.Value.ResponseBytes));
            if (string.IsNullOrWhiteSpace(responseText))
            {
                return default;
            }

            var lines = responseText.Split(['\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            List<string> parts = [];
            if (lines.Length > 0)
            {
                parts.Add(lines[0]);
            }

            foreach (var headerName in new[] { "ST:", "SERVER:", "LOCATION:" })
            {
                var match = lines.FirstOrDefault(line => line.StartsWith(headerName, StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrWhiteSpace(match))
                {
                    parts.Add(match);
                }
            }

            var summary = ShortenText(string.Join("; ", parts), 220);
            return new UdpProbeResult(true, exchange.Value.RoundTripMilliseconds, "ssdp", summary, null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return default;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new UdpProbeResult(false, 0, "ssdp", null, ex.Message);
        }
    }

    private static async Task<UdpProbeResult> TryProbeStunUdpAsync(
        IPAddress address,
        int port,
        int timeoutMilliseconds,
        CancellationToken cancellationToken)
    {
        try
        {
            byte[] transactionId = RandomNumberGenerator.GetBytes(12);
            byte[] payload = new byte[20];
            payload[0] = 0x00;
            payload[1] = 0x01;
            payload[2] = 0x00;
            payload[3] = 0x00;
            payload[4] = 0x21;
            payload[5] = 0x12;
            payload[6] = 0xA4;
            payload[7] = 0x42;
            Buffer.BlockCopy(transactionId, 0, payload, 8, transactionId.Length);

            var exchange = await SendUdpPayloadAsync(address, port, payload, timeoutMilliseconds, cancellationToken);
            if (exchange is null)
            {
                return default;
            }

            var summary = BuildStunSummary(exchange.Value.ResponseBytes, transactionId);
            return string.IsNullOrWhiteSpace(summary)
                ? default
                : new UdpProbeResult(true, exchange.Value.RoundTripMilliseconds, "stun", summary, null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return default;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new UdpProbeResult(false, 0, "stun", null, ex.Message);
        }
    }

    private static async Task<UdpExchangeResult?> SendUdpPayloadAsync(
        IPAddress address,
        int port,
        byte[] payload,
        int timeoutMilliseconds,
        CancellationToken cancellationToken)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();

        try
        {
            using Socket socket = new(address.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeoutMilliseconds);

            await socket.ConnectAsync(new IPEndPoint(address, port), timeoutCts.Token);
            await socket.SendAsync(payload, SocketFlags.None, timeoutCts.Token);

            byte[] responseBuffer = new byte[4096];
            var receivedCount = await socket.ReceiveAsync(responseBuffer, SocketFlags.None, timeoutCts.Token);
            if (receivedCount <= 0)
            {
                return null;
            }

            stopwatch.Stop();
            var responseBytes = new byte[receivedCount];
            Buffer.BlockCopy(responseBuffer, 0, responseBytes, 0, receivedCount);
            return new UdpExchangeResult(responseBytes, stopwatch.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (SocketException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static async Task<string?> ReadTextAsync(
        Stream stream,
        int maxBytes,
        int timeoutMilliseconds,
        CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[Math.Min(512, maxBytes)];
        using MemoryStream collector = new();

        while (collector.Length < maxBytes)
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeoutMilliseconds);

            int readCount;
            try
            {
                var remaining = Math.Min(buffer.Length, maxBytes - (int)collector.Length);
                readCount = await stream.ReadAsync(buffer.AsMemory(0, remaining), timeoutCts.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                break;
            }

            if (readCount <= 0)
            {
                break;
            }

            collector.Write(buffer, 0, readCount);

            var snapshot = Encoding.ASCII.GetString(collector.ToArray());
            if (snapshot.Contains("\r\n\r\n", StringComparison.Ordinal) ||
                snapshot.Contains("\n\n", StringComparison.Ordinal))
            {
                break;
            }

            if (readCount < buffer.Length)
            {
                break;
            }
        }

        if (collector.Length == 0)
        {
            return null;
        }

        return SanitizeText(Encoding.ASCII.GetString(collector.ToArray()));
    }

    private static async Task<byte[]?> ReadExactAsync(
        Stream stream,
        int byteCount,
        int timeoutMilliseconds,
        CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[byteCount];
        var offset = 0;

        while (offset < byteCount)
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeoutMilliseconds);

            int readCount;
            try
            {
                readCount = await stream.ReadAsync(buffer.AsMemory(offset, byteCount - offset), timeoutCts.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return null;
            }

            if (readCount <= 0)
            {
                return null;
            }

            offset += readCount;
        }

        return buffer;
    }
}
