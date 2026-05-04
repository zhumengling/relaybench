using System.Text;
using System.Text.Json;
using RelayBench.Core.Services;
using RelayBench.Core.Support;

namespace RelayBench.App.Services;

internal sealed class TransparentProxyTokenTelemetryService
{
    private readonly object _syncRoot = new();
    private readonly Queue<TransparentProxyTokenSample> _samples = new();
    private long _promptCacheTokens;
    private long _totalOutputTokens;
    private DateTimeOffset? _lastTokenActivityAt;

    public void Reset()
    {
        lock (_syncRoot)
        {
            _promptCacheTokens = 0;
            _totalOutputTokens = 0;
            _lastTokenActivityAt = null;
            _samples.Clear();
        }
    }

    public TransparentProxyTokenTelemetrySnapshot CreateSnapshot()
    {
        lock (_syncRoot)
        {
            PruneSamples(DateTimeOffset.UtcNow);
            return new TransparentProxyTokenTelemetrySnapshot(
                _totalOutputTokens,
                _samples.Sum(static item => item.TokenCount) / 1.25d,
                _lastTokenActivityAt,
                _promptCacheTokens);
        }
    }

    public bool TrackResponseBody(byte[] body)
    {
        if (body.Length == 0)
        {
            return false;
        }

        try
        {
            var text = Encoding.UTF8.GetString(body);
            var changed = TrackPromptCache(text);
            var assistantText = ModelResponseTextExtractor.TryExtractAssistantText(text);
            return TrackOutputText(assistantText) || changed;
        }
        catch
        {
            return false;
        }
    }

    public bool TrackSseData(string data)
    {
        if (string.IsNullOrWhiteSpace(data) || ChatSseParser.IsDone(data))
        {
            return false;
        }

        var changed = TrackOutputText(ChatSseParser.TryExtractDelta(data));
        return TrackPromptCache(data) || changed;
    }

    public bool TrackOutputText(string? text)
    {
        var tokenCount = TokenCountEstimator.EstimateOutputTokens(text);
        if (tokenCount <= 0)
        {
            return false;
        }

        TrackOutputTokens(tokenCount);
        return true;
    }

    public bool TrackPromptCache(string? json)
    {
        var cachedTokens = TryExtractPromptCacheTokens(json);
        if (cachedTokens <= 0)
        {
            return false;
        }

        Interlocked.Add(ref _promptCacheTokens, Math.Min(cachedTokens, 1_000_000));
        return true;
    }

    private void TrackOutputTokens(int tokenCount)
    {
        var now = DateTimeOffset.UtcNow;
        lock (_syncRoot)
        {
            _totalOutputTokens += tokenCount;
            _lastTokenActivityAt = now;
            _samples.Enqueue(new TransparentProxyTokenSample(now, tokenCount));
            PruneSamples(now);
        }
    }

    private void PruneSamples(DateTimeOffset now)
    {
        while (_samples.Count > 0 &&
               (now - _samples.Peek().Timestamp).TotalMilliseconds > 1250)
        {
            _samples.Dequeue();
        }
    }

    private static int TryExtractPromptCacheTokens(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return 0;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            return Math.Max(
                Math.Max(
                    TryReadIntPath(root, "usage", "input_tokens_details", "cached_tokens"),
                    TryReadIntPath(root, "response", "usage", "input_tokens_details", "cached_tokens")),
                Math.Max(
                    Math.Max(
                        TryReadIntPath(root, "usage", "prompt_tokens_details", "cached_tokens"),
                        TryReadIntPath(root, "response", "usage", "prompt_tokens_details", "cached_tokens")),
                    Math.Max(
                        TryReadIntPath(root, "usage", "cache_read_input_tokens"),
                        TryReadIntPath(root, "message", "usage", "cache_read_input_tokens"))));
        }
        catch
        {
            return 0;
        }
    }

    private static int TryReadIntPath(JsonElement root, params string[] path)
    {
        var current = root;
        foreach (var segment in path)
        {
            if (current.ValueKind != JsonValueKind.Object ||
                !current.TryGetProperty(segment, out current))
            {
                return 0;
            }
        }

        return current.ValueKind switch
        {
            JsonValueKind.Number when current.TryGetInt32(out var value) => Math.Max(0, value),
            JsonValueKind.String when int.TryParse(current.GetString(), out var value) => Math.Max(0, value),
            _ => 0
        };
    }
}

internal sealed record TransparentProxyTokenTelemetrySnapshot(
    long TotalOutputTokens,
    double TokensPerSecond,
    DateTimeOffset? LastTokenActivityAt,
    long PromptCacheTokens);

internal sealed record TransparentProxyTokenSample(DateTimeOffset Timestamp, int TokenCount);
