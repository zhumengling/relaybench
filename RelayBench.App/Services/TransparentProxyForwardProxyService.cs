using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace RelayBench.App.Services;

internal sealed class TransparentProxyForwardProxyService : IAsyncDisposable
{
    private const int MaxHeaderBytes = 16 * 1024;

    private TcpListener? _listener;
    private CancellationTokenSource? _cancellationSource;
    private Task? _acceptLoopTask;

    public event EventHandler<TransparentProxyLogEntry>? TunnelLogEmitted;

    public bool IsRunning => _listener is not null;

    public int Port { get; private set; }

    public Task StartAsync(int port)
    {
        StopAsync().GetAwaiter().GetResult();
        Port = port is >= 1 and <= 65535 ? port : 17881;
        _cancellationSource = new CancellationTokenSource();
        _listener = new TcpListener(IPAddress.Loopback, Port);
        _listener.Start();
        _acceptLoopTask = Task.Run(() => AcceptLoopAsync(_cancellationSource.Token));
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        var listener = _listener;
        _listener = null;
        if (listener is null)
        {
            return;
        }

        try
        {
            _cancellationSource?.Cancel();
            listener.Stop();
            if (_acceptLoopTask is not null)
            {
                await _acceptLoopTask.WaitAsync(TimeSpan.FromSeconds(2));
            }
        }
        catch
        {
        }
        finally
        {
            _acceptLoopTask = null;
            _cancellationSource?.Dispose();
            _cancellationSource = null;
            Port = 0;
        }
    }

    public async ValueTask DisposeAsync()
        => await StopAsync();

    internal static bool IsAllowedTunnelHost(string host)
    {
        var normalized = host.Trim().TrimEnd('.').ToLowerInvariant();
        return normalized is "api.openai.com" or "api.anthropic.com" ||
               normalized.EndsWith(".api.openai.com", StringComparison.Ordinal) ||
               normalized.EndsWith(".api.anthropic.com", StringComparison.Ordinal);
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient? client = null;
            try
            {
                var listener = _listener;
                if (listener is null)
                {
                    return;
                }

                client = await listener.AcceptTcpClientAsync(cancellationToken);
                _ = Task.Run(() => HandleClientAsync(client, cancellationToken), CancellationToken.None);
                client = null;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch
            {
                client?.Dispose();
            }
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.Now;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        using var clientScope = client;
        client.NoDelay = true;
        var clientStream = client.GetStream();
        var headerBytes = await ReadHeaderAsync(clientStream, cancellationToken);
        if (headerBytes.Length == 0)
        {
            return;
        }

        var headerText = Encoding.ASCII.GetString(headerBytes);
        var requestLine = headerText.Split(["\r\n", "\n"], StringSplitOptions.None).FirstOrDefault() ?? string.Empty;
        var parts = requestLine.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 3 || !string.Equals(parts[0], "CONNECT", StringComparison.OrdinalIgnoreCase))
        {
            await WriteAsciiAsync(clientStream, "HTTP/1.1 501 Not Implemented\r\nConnection: close\r\n\r\nRelayBench forward proxy is tunnel-only.", cancellationToken);
            EmitTunnelLog(startedAt, "WARN", parts.FirstOrDefault() ?? "-", parts.ElementAtOrDefault(1) ?? requestLine, 501, stopwatch.ElapsedMilliseconds, "Rejected non-CONNECT request.", string.Empty);
            return;
        }

        if (!TryParseHostPort(parts[1], out var host, out var port) || !IsAllowedTunnelHost(host))
        {
            await WriteAsciiAsync(clientStream, "HTTP/1.1 403 Forbidden\r\nConnection: close\r\n\r\nRelayBench TUN tunnel allows AI API domains only.", cancellationToken);
            EmitTunnelLog(startedAt, "WARN", "CONNECT", parts[1], 403, stopwatch.ElapsedMilliseconds, "Rejected tunnel target outside AI API domain allowlist.", host);
            return;
        }

        using TcpClient upstream = new();
        upstream.NoDelay = true;
        await upstream.ConnectAsync(host, port, cancellationToken);
        await WriteAsciiAsync(clientStream, "HTTP/1.1 200 Connection Established\r\nProxy-Agent: RelayBench\r\n\r\n", cancellationToken);
        EmitTunnelLog(startedAt, "INFO", "CONNECT", parts[1], 200, stopwatch.ElapsedMilliseconds, "Tunnel established; HTTPS payload remains encrypted.", host);

        var upstreamStream = upstream.GetStream();
        var clientToUpstream = clientStream.CopyToAsync(upstreamStream, cancellationToken);
        var upstreamToClient = upstreamStream.CopyToAsync(clientStream, cancellationToken);
        await Task.WhenAny(clientToUpstream, upstreamToClient);
    }

    private static async Task<byte[]> ReadHeaderAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        MemoryStream buffer = new();
        byte[] chunk = new byte[1024];
        while (buffer.Length < MaxHeaderBytes)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken);
            if (read <= 0)
            {
                break;
            }

            buffer.Write(chunk, 0, read);
            var current = buffer.ToArray();
            if (ContainsHeaderTerminator(current))
            {
                return current;
            }
        }

        return buffer.ToArray();
    }

    private static bool ContainsHeaderTerminator(byte[] bytes)
    {
        for (var index = 3; index < bytes.Length; index++)
        {
            if (bytes[index - 3] == '\r' &&
                bytes[index - 2] == '\n' &&
                bytes[index - 1] == '\r' &&
                bytes[index] == '\n')
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryParseHostPort(string target, out string host, out int port)
    {
        host = string.Empty;
        port = 443;
        var normalized = target.Trim();
        var separatorIndex = normalized.LastIndexOf(':');
        if (separatorIndex <= 0 || separatorIndex == normalized.Length - 1)
        {
            return false;
        }

        host = normalized[..separatorIndex].Trim('[', ']');
        return !string.IsNullOrWhiteSpace(host) &&
               int.TryParse(normalized[(separatorIndex + 1)..], out port) &&
               port is >= 1 and <= 65535;
    }

    private static Task WriteAsciiAsync(NetworkStream stream, string text, CancellationToken cancellationToken)
    {
        var bytes = Encoding.ASCII.GetBytes(text);
        return stream.WriteAsync(bytes, cancellationToken).AsTask();
    }

    private void EmitTunnelLog(
        DateTimeOffset timestamp,
        string level,
        string method,
        string target,
        int statusCode,
        long elapsedMs,
        string message,
        string targetHost)
    {
        TunnelLogEmitted?.Invoke(
            this,
            new TransparentProxyLogEntry(
                timestamp,
                level,
                method,
                target,
                "tunnel-only",
                statusCode,
                elapsedMs,
                message,
                "-",
                string.Empty,
                "CONNECT",
                "TUN tunnel-only",
                "TunnelOnlyProxy",
                "TUN tunnel-only",
                "HTTP CONNECT",
                targetHost,
                WasTunnelOnly: true));
    }
}
