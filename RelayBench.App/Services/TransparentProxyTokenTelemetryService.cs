using System.Text;
using System.Text.Json;
using RelayBench.Core.Services;
using RelayBench.Core.Support;

namespace RelayBench.App.Services;

internal sealed class TransparentProxyTokenTelemetryService
{
    private readonly object _syncRoot = new();
    private readonly TransparentProxyUsageEventQueue _usageEvents;
    private readonly AsyncLocal<TransparentProxyUsageContext?> _usageContext = new();
    private readonly Queue<TransparentProxyTokenSample> _samples = new();
    private long _promptCacheTokens;
    private long _totalOutputTokens;
    private DateTimeOffset? _lastTokenActivityAt;

    public TransparentProxyTokenTelemetryService(TransparentProxyUsageEventQueue? usageEvents = null)
    {
        _usageEvents = usageEvents ?? new TransparentProxyUsageEventQueue();
    }

    public TransparentProxyUsageEventQueue UsageEvents => _usageEvents;

    internal void RestoreUsageContext(TransparentProxyUsageContext? previous)
        => _usageContext.Value = previous;

    public IDisposable PushUsageContext(
        string modelName,
        string routeName,
        string wireApi,
        string cacheState,
        string ingressKind,
        string sourceApplication,
        string captureMode)
    {
        var previous = _usageContext.Value;
        _usageContext.Value = new TransparentProxyUsageContext(
            modelName,
            routeName,
            wireApi,
            cacheState,
            ingressKind,
            sourceApplication,
            captureMode);
        return new TransparentProxyUsageContextScope(this, previous);
    }

    public void Reset()
    {
        lock (_syncRoot)
        {
            _promptCacheTokens = 0;
            _totalOutputTokens = 0;
            _lastTokenActivityAt = null;
            _samples.Clear();
        }

        _usageEvents.Clear();
        _usageEvents.Publish(
            TransparentProxyUsageEventKind.Reset,
            outputTokenDelta: 0,
            totalOutputTokens: 0,
            tokensPerSecond: 0);
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

    public TransparentProxyTokenStreamTracker CreateStreamTracker()
        => new(this);

    public bool TrackResponseBody(byte[] body, bool includePromptCache = true)
    {
        if (body.Length == 0)
        {
            return false;
        }

        try
        {
            var text = Encoding.UTF8.GetString(body);
            var changed = includePromptCache && TrackPromptCache(text);
            if (ChatSseParser.TryExtractOutputTokenCount(text, out var outputTokens))
            {
                TrackOutputTokens(outputTokens, estimated: false);
                return true;
            }

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
        if (string.IsNullOrWhiteSpace(data))
        {
            return false;
        }

        return CreateStreamTracker().TrackSseData(data);
    }

    public bool TrackOutputText(string? text)
    {
        var tokenCount = TokenCountEstimator.EstimateOutputTokens(text);
        if (tokenCount <= 0)
        {
            return false;
        }

        TrackOutputTokens(tokenCount, estimated: true);
        return true;
    }

    public bool TrackPromptCache(string? json)
    {
        var cachedTokens = TryExtractPromptCacheTokens(json);
        if (cachedTokens <= 0)
        {
            return false;
        }

        var promptCacheDelta = Math.Min(cachedTokens, 1_000_000);
        var totalPromptCacheTokens = Interlocked.Add(ref _promptCacheTokens, promptCacheDelta);
        var snapshot = CreateSnapshot();
        _usageEvents.Publish(
            TransparentProxyUsageEventKind.PromptCache,
            outputTokenDelta: 0,
            totalOutputTokens: snapshot.TotalOutputTokens,
            tokensPerSecond: snapshot.TokensPerSecond,
            promptCacheTokenDelta: promptCacheDelta,
            totalPromptCacheTokens: totalPromptCacheTokens,
            estimated: false,
            modelName: _usageContext.Value?.ModelName ?? string.Empty,
            routeName: _usageContext.Value?.RouteName ?? string.Empty,
            wireApi: _usageContext.Value?.WireApi ?? string.Empty,
            cacheState: _usageContext.Value?.CacheState ?? string.Empty,
            ingressKind: _usageContext.Value?.IngressKind ?? string.Empty,
            sourceApplication: _usageContext.Value?.SourceApplication ?? string.Empty,
            captureMode: _usageContext.Value?.CaptureMode ?? string.Empty);
        return true;
    }

    internal void TrackOutputTokens(int tokenCount)
        => TrackOutputTokens(tokenCount, estimated: false);

    internal void TrackOutputTokens(int tokenCount, bool estimated)
        => AdjustOutputTokens(
            tokenCount,
            estimated,
            estimated
                ? TransparentProxyUsageEventKind.OutputDelta
                : TransparentProxyUsageEventKind.OutputReconciled);

    internal void AdjustOutputTokens(int tokenDelta)
        => AdjustOutputTokens(tokenDelta, estimated: false, TransparentProxyUsageEventKind.OutputReconciled);

    internal void AdjustOutputTokens(
        int tokenDelta,
        bool estimated,
        TransparentProxyUsageEventKind kind)
    {
        if (tokenDelta == 0)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        long totalOutputTokens;
        double tokensPerSecond;
        lock (_syncRoot)
        {
            _totalOutputTokens = Math.Max(0, _totalOutputTokens + tokenDelta);
            if (tokenDelta > 0)
            {
                _lastTokenActivityAt = now;
                _samples.Enqueue(new TransparentProxyTokenSample(now, tokenDelta));
            }

            PruneSamples(now);
            totalOutputTokens = _totalOutputTokens;
            tokensPerSecond = _samples.Sum(static item => item.TokenCount) / 1.25d;
        }

        _usageEvents.Publish(
            kind,
            tokenDelta,
            totalOutputTokens,
            tokensPerSecond,
            totalPromptCacheTokens: Interlocked.Read(ref _promptCacheTokens),
            estimated: estimated,
            modelName: _usageContext.Value?.ModelName ?? string.Empty,
            routeName: _usageContext.Value?.RouteName ?? string.Empty,
            wireApi: _usageContext.Value?.WireApi ?? string.Empty,
            cacheState: _usageContext.Value?.CacheState ?? string.Empty,
            ingressKind: _usageContext.Value?.IngressKind ?? string.Empty,
            sourceApplication: _usageContext.Value?.SourceApplication ?? string.Empty,
            captureMode: _usageContext.Value?.CaptureMode ?? string.Empty);
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
                        Math.Max(
                            TryReadCacheTokenPair(root, "usage"),
                            TryReadCacheTokenPair(root, "response", "usage")),
                        Math.Max(
                            Math.Max(
                                TryReadCacheTokenPair(root, "message", "usage"),
                                TryReadIntPath(root, "usageMetadata", "cachedContentTokenCount")),
                            TryReadIntPath(root, "response", "usageMetadata", "cachedContentTokenCount")))));
        }
        catch
        {
            return 0;
        }
    }

    private static int TryReadCacheTokenPair(JsonElement root, params string[] path)
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

        return Math.Min(
            1_000_000,
            TryReadIntPath(current, "cache_read_input_tokens") +
            TryReadIntPath(current, "cache_creation_input_tokens"));
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

internal sealed record TransparentProxyUsageContext(
    string ModelName,
    string RouteName,
    string WireApi,
    string CacheState,
    string IngressKind,
    string SourceApplication,
    string CaptureMode);

internal sealed class TransparentProxyUsageContextScope : IDisposable
{
    private readonly TransparentProxyTokenTelemetryService _owner;
    private readonly TransparentProxyUsageContext? _previous;
    private bool _disposed;

    public TransparentProxyUsageContextScope(
        TransparentProxyTokenTelemetryService owner,
        TransparentProxyUsageContext? previous)
    {
        _owner = owner;
        _previous = previous;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _owner.RestoreUsageContext(_previous);
        _disposed = true;
    }
}

internal sealed record TransparentProxyTokenTelemetrySnapshot(
    long TotalOutputTokens,
    double TokensPerSecond,
    DateTimeOffset? LastTokenActivityAt,
    long PromptCacheTokens);

internal sealed record TransparentProxyTokenSample(DateTimeOffset Timestamp, int TokenCount);

internal sealed class TransparentProxyTokenStreamTracker
{
    private readonly TransparentProxyTokenTelemetryService _owner;
    private int _accountedOutputTokens;

    public TransparentProxyTokenStreamTracker(TransparentProxyTokenTelemetryService owner)
    {
        _owner = owner;
    }

    public bool TrackSseData(string data)
    {
        if (string.IsNullOrWhiteSpace(data) ||
            string.Equals(data.Trim(), "[DONE]", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var changed = false;
        var estimatedDeltaTokens = TokenCountEstimator.EstimateOutputTokens(ChatSseParser.TryExtractDelta(data));
        if (estimatedDeltaTokens > 0)
        {
            _accountedOutputTokens += estimatedDeltaTokens;
            _owner.TrackOutputTokens(estimatedDeltaTokens, estimated: true);
            changed = true;
        }

        if (ChatSseParser.TryExtractOutputTokenCount(data, out var actualOutputTokens))
        {
            var reconciliationDelta = actualOutputTokens - _accountedOutputTokens;
            if (reconciliationDelta != 0)
            {
                _owner.AdjustOutputTokens(
                    reconciliationDelta,
                    estimated: false,
                    TransparentProxyUsageEventKind.OutputReconciled);
                _accountedOutputTokens = actualOutputTokens;
                changed = true;
            }
        }

        return _owner.TrackPromptCache(data) || changed;
    }
}
