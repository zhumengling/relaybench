using RelayBench.Core.Services;
using RelayBench.Core.Models;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using RelayBench.App.Services;
using RelayBench.App.ViewModels;

namespace RelayBench.Core.Tests;

internal static partial class TestSupport
{
    internal static async Task RunAnthropicChatConversationFallbackAsync()
    {
        using var cancellationSource = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var serverTask = RunAnthropicFallbackServerAsync(listener, cancellationSource.Token);

        try
        {
            var service = new ChatConversationService();
            var options = new ChatRequestOptions(
                $"http://127.0.0.1:{port}/anthropic",
                "sk-test",
                "mimo-v2.5-pro",
                string.Empty,
                0,
                128,
                false,
                10,
                ChatReasoningEffort.Auto,
                false);
            var messages = new[]
            {
                new ChatMessage(
                    Guid.NewGuid().ToString("N"),
                    "user",
                    "Reply with exactly: proxy-ok",
                    DateTimeOffset.Now,
                    Array.Empty<ChatAttachment>(),
                    null,
                    null)
            };

            List<ChatStreamUpdate> updates = [];
            await foreach (var update in service.SendStreamingAsync(options, messages, Array.Empty<ChatAttachment>(), cancellationSource.Token))
            {
                updates.Add(update);
            }

            var text = string.Concat(updates.Where(static item => item.Kind == ChatStreamUpdateKind.Delta).Select(static item => item.Delta));
            AssertEqual(text, "proxy-ok");
            AssertTrue(updates.Any(static item => item.Kind == ChatStreamUpdateKind.Completed), "Anthropic fallback conversation should complete.");
            AssertEqual(updates.Last(static item => item.Kind == ChatStreamUpdateKind.Completed).Metrics?.WireApi ?? string.Empty, "anthropic");
        }
        finally
        {
            cancellationSource.Cancel();
            listener.Stop();
            try
            {
                await serverTask;
            }
            catch (OperationCanceledException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    internal static async Task RunAnthropicFallbackServerAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            using var client = await listener.AcceptTcpClientAsync(cancellationToken);
            await using var stream = client.GetStream();
            using StreamReader reader = new(stream, Encoding.ASCII, leaveOpen: true);
            var requestLine = await reader.ReadLineAsync(cancellationToken) ?? string.Empty;
            var contentLength = 0;
            while (true)
            {
                var header = await reader.ReadLineAsync(cancellationToken);
                if (header is null || header.Length == 0)
                {
                    break;
                }

                if (header.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase) &&
                    int.TryParse(header["Content-Length:".Length..].Trim(), out var parsedLength))
                {
                    contentLength = parsedLength;
                }
            }

            if (contentLength > 0)
            {
                var buffer = new char[contentLength];
                _ = await reader.ReadBlockAsync(buffer, cancellationToken);
            }

            var response = requestLine.Contains("/v1/messages", StringComparison.OrdinalIgnoreCase)
                ? "HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nConnection: close\r\n\r\n" +
                  "event: content_block_delta\n" +
                  "data: {\"type\":\"content_block_delta\",\"delta\":{\"type\":\"text_delta\",\"text\":\"proxy-ok\"}}\n\n" +
                  "event: message_stop\n" +
                  "data: {\"type\":\"message_stop\"}\n\n"
                : "HTTP/1.1 404 Not Found\r\nContent-Type: application/json\r\nConnection: close\r\n\r\n" +
                  "{\"error\":{\"message\":\"not found\"}}";

            var bytes = Encoding.UTF8.GetBytes(response);
            await stream.WriteAsync(bytes, cancellationToken);
        }
    }

    internal static async Task RunAnthropicDiagnosticsAdvancedAndBatchProbesAsync()
    {
        using var cancellationSource = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var chatRequestCount = 0;
        var serverTask = RunAnthropicDiagnosticsServerAsync(
            listener,
            () => Interlocked.Increment(ref chatRequestCount),
            cancellationSource.Token);

        try
        {
            var service = new ProxyDiagnosticsService();
            var settings = new ProxyEndpointSettings(
                $"http://127.0.0.1:{port}/v1",
                "sk-test",
                "mimo-v2.5-pro",
                false,
                10);

            var baseline = await service.RunAsync(settings, cancellationToken: cancellationSource.Token);
            AssertTrue(baseline.ChatRequestSucceeded, baseline.Error ?? baseline.Summary);
            AssertTrue(baseline.StreamRequestSucceeded, baseline.Error ?? baseline.Summary);

            var supplemental = await service.RunSupplementalScenariosAsync(
                settings,
                baseline,
                includeProtocolCompatibility: false,
                includeErrorTransparency: false,
                includeStreamingIntegrity: false,
                includeOfficialReferenceIntegrity: false,
                officialReferenceBaseUrl: null,
                officialReferenceApiKey: null,
                officialReferenceModel: null,
                includeMultiModal: false,
                includeCacheMechanism: false,
                includeCacheIsolation: false,
                cacheIsolationAlternateApiKey: null,
                includeInstructionFollowing: true,
                includeDataExtraction: false,
                includeStructuredOutputEdge: false,
                includeToolCallDeep: false,
                includeReasonMathConsistency: false,
                includeCodeBlockDiscipline: false,
                progress: null,
                cancellationToken: cancellationSource.Token);
            var instructionProbe = supplemental.ScenarioResults?.FirstOrDefault(static item => item.Scenario == ProxyProbeScenarioKind.InstructionFollowing);
            AssertTrue(instructionProbe?.Success == true, instructionProbe?.Error ?? instructionProbe?.Summary ?? "Instruction probe missing.");
            AssertContains(instructionProbe?.Trace?.Path, "messages");

            var longStream = await service.RunLongStreamingTestAsync(settings, 24, cancellationSource.Token);
            AssertTrue(longStream.Success, longStream.Error ?? longStream.Summary);

            var throughput = await service.RunThroughputBenchmarkAsync(settings, 1, 24, null, cancellationSource.Token);
            AssertTrue(throughput.SuccessfulSampleCount == 1, throughput.Error ?? throughput.Summary);

            var multiModel = await service.RunMultiModelSpeedTestAsync(settings, ["mimo-v2.5-pro"], cancellationSource.Token);
            AssertTrue(multiModel.Count == 1 && multiModel[0].Success, multiModel.FirstOrDefault()?.Error ?? "Multi-model speed test failed.");

            var concurrency = await service.RunConcurrencyPressureAsync(settings, [1], null, cancellationSource.Token);
            AssertTrue(concurrency.Stages.Count == 1 && concurrency.Stages[0].SuccessCount == concurrency.Stages[0].TotalRequests, concurrency.Error ?? concurrency.Summary);
            AssertTrue(chatRequestCount == 0, $"Anthropic-only diagnostics should not call chat/completions, got {chatRequestCount} calls.");
        }
        finally
        {
            cancellationSource.Cancel();
            listener.Stop();
            try
            {
                await serverTask;
            }
            catch (OperationCanceledException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    internal static async Task RunAnthropicDiagnosticsServerAsync(
        TcpListener listener,
        Action onChatRequest,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            using var client = await listener.AcceptTcpClientAsync(cancellationToken);
            await using var stream = client.GetStream();
            using StreamReader reader = new(stream, Encoding.UTF8, leaveOpen: true);
            var requestLine = await reader.ReadLineAsync(cancellationToken) ?? string.Empty;
            var contentLength = 0;
            while (true)
            {
                var header = await reader.ReadLineAsync(cancellationToken);
                if (header is null || header.Length == 0)
                {
                    break;
                }

                if (header.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase) &&
                    int.TryParse(header["Content-Length:".Length..].Trim(), out var parsedLength))
                {
                    contentLength = parsedLength;
                }
            }

            var body = string.Empty;
            if (contentLength > 0)
            {
                var buffer = new char[contentLength];
                var read = await reader.ReadBlockAsync(buffer.AsMemory(0, contentLength), cancellationToken);
                body = new string(buffer, 0, read);
            }

            string response;
            if (requestLine.Contains("/v1/chat/completions", StringComparison.OrdinalIgnoreCase))
            {
                onChatRequest();
                response = BuildHttpJsonResponse(404, "{\"error\":{\"message\":\"chat disabled\"}}");
            }
            else if (requestLine.Contains("/v1/responses", StringComparison.OrdinalIgnoreCase) ||
                     requestLine.Contains("/v1/models", StringComparison.OrdinalIgnoreCase))
            {
                response = BuildHttpJsonResponse(404, "{\"error\":{\"message\":\"not found\"}}");
            }
            else if (requestLine.Contains("/v1/messages", StringComparison.OrdinalIgnoreCase))
            {
                var text = ResolveAnthropicDiagnosticsResponseText(body);
                response = body.Contains("\"stream\":true", StringComparison.OrdinalIgnoreCase)
                    ? BuildHttpSseResponse(text)
                    : BuildHttpJsonResponse(
                        200,
                        JsonSerializer.Serialize(new
                        {
                            id = "msg_test",
                            type = "message",
                            role = "assistant",
                            content = new[] { new { type = "text", text } }
                        }));
            }
            else
            {
                response = BuildHttpJsonResponse(404, "{\"error\":{\"message\":\"unknown path\"}}");
            }

            var bytes = Encoding.UTF8.GetBytes(response);
            await stream.WriteAsync(bytes, cancellationToken);
        }
    }

    internal static string ResolveAnthropicDiagnosticsResponseText(string requestBody)
    {
        if (requestBody.Contains("IF-20260501", StringComparison.Ordinal))
        {
            return """{"task_id":"IF-20260501","verdict":"pass","priority":3,"marker":"relay-instruction-ok","checks":["system-first","json-only"]}""";
        }

        var segmentMatch = Regex.Match(requestBody, "Output exactly (?<count>\\d+) lines", RegexOptions.IgnoreCase);
        if (segmentMatch.Success && int.TryParse(segmentMatch.Groups["count"].Value, out var segmentCount))
        {
            return string.Join(
                "\n",
                Enumerable.Range(1, segmentCount)
                    .Select(static index => $"[{index:000}] relay stream stability sample line"));
        }

        if (requestBody.Contains("numbers 1 to", StringComparison.OrdinalIgnoreCase))
        {
            return string.Join(" ", Enumerable.Range(1, 80));
        }

        return "proxy-ok";
    }

    internal static string BuildHttpJsonResponse(int statusCode, string body)
    {
        var reason = statusCode == 200 ? "OK" : "Not Found";
        var bodyBytes = Encoding.UTF8.GetBytes(body);
        return $"HTTP/1.1 {statusCode} {reason}\r\nContent-Type: application/json\r\nContent-Length: {bodyBytes.Length}\r\nConnection: close\r\n\r\n{body}";
    }

    internal static string BuildHttpSseResponse(string text)
    {
        var payload = JsonSerializer.Serialize(new
        {
            type = "content_block_delta",
            delta = new { type = "text_delta", text }
        });
        var body =
            "event: content_block_delta\n" +
            $"data: {payload}\n\n" +
            "event: message_stop\n" +
            "data: {\"type\":\"message_stop\"}\n\n";
        var bodyBytes = Encoding.UTF8.GetBytes(body);
        return $"HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nContent-Length: {bodyBytes.Length}\r\nConnection: close\r\n\r\n{body}";
    }
}

internal sealed class ScriptedHttpServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly Func<ScriptedHttpRequest, Task<ScriptedHttpResponse>> _handler;
    private readonly CancellationTokenSource _cancellationSource = new();
    private readonly Task _serverTask;

    private ScriptedHttpServer(
        TcpListener listener,
        Func<ScriptedHttpRequest, Task<ScriptedHttpResponse>> handler)
    {
        _listener = listener;
        _handler = handler;
        var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        BaseUrl = $"http://127.0.0.1:{port}/v1";
        _serverTask = RunAsync();
    }

    public string BaseUrl { get; }

    public static Task<ScriptedHttpServer> StartAsync(
        Func<ScriptedHttpRequest, Task<ScriptedHttpResponse>> handler)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return Task.FromResult(new ScriptedHttpServer(listener, handler));
    }

    public async ValueTask DisposeAsync()
    {
        _cancellationSource.Cancel();
        _listener.Stop();
        try
        {
            await _serverTask;
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            _cancellationSource.Dispose();
        }
    }

    private async Task RunAsync()
    {
        while (!_cancellationSource.IsCancellationRequested)
        {
            using var client = await _listener.AcceptTcpClientAsync(_cancellationSource.Token);
            await using var stream = client.GetStream();
            var request = await ReadRequestAsync(stream, _cancellationSource.Token);
            var response = await _handler(request);
            var bytes = Encoding.UTF8.GetBytes(response.ToHttpText());
            await stream.WriteAsync(bytes, _cancellationSource.Token);
        }
    }

    private static async Task<ScriptedHttpRequest> ReadRequestAsync(
        NetworkStream stream,
        CancellationToken cancellationToken)
    {
        using StreamReader reader = new(stream, Encoding.UTF8, leaveOpen: true);
        var requestLine = await reader.ReadLineAsync(cancellationToken) ?? string.Empty;
        var parts = requestLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var path = parts.Length >= 2 ? parts[1] : string.Empty;
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var contentLength = 0;

        while (true)
        {
            var header = await reader.ReadLineAsync(cancellationToken);
            if (string.IsNullOrEmpty(header))
            {
                break;
            }

            var separator = header.IndexOf(':');
            if (separator > 0)
            {
                var name = header[..separator].Trim();
                var value = header[(separator + 1)..].Trim();
                headers[name] = value;
                if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) &&
                    int.TryParse(value, out var parsedLength))
                {
                    contentLength = parsedLength;
                }
            }
        }

        var body = string.Empty;
        if (contentLength > 0)
        {
            var buffer = new char[contentLength];
            var read = await reader.ReadBlockAsync(buffer.AsMemory(0, contentLength), cancellationToken);
            body = new string(buffer, 0, read);
        }

        return new ScriptedHttpRequest(path, headers, body);
    }
}

internal sealed record ScriptedHttpRequest(
    string Path,
    IReadOnlyDictionary<string, string> Headers,
    string Body);

internal sealed record ScriptedHttpResponse(
    int StatusCode,
    string ContentType,
    string Body,
    string ReasonPhrase)
{
    public static ScriptedHttpResponse Json(int statusCode, string body)
        => new(statusCode, "application/json", body, statusCode == 200 ? "OK" : "Error");

    public static ScriptedHttpResponse Sse(string body)
        => new(200, "text/event-stream", body, "OK");

    public string ToHttpText()
    {
        var length = Encoding.UTF8.GetByteCount(Body);
        return $"HTTP/1.1 {StatusCode} {ReasonPhrase}\r\n" +
            $"Content-Type: {ContentType}\r\n" +
            $"Content-Length: {length}\r\n" +
            "Connection: close\r\n\r\n" +
            Body;
    }
}
