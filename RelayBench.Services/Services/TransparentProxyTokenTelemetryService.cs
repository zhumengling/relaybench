using System.Text;
using System.Text.Json;
using RelayBench.Core.Services;
using RelayBench.Core.Support;

namespace RelayBench.Services;

internal sealed class TransparentProxyTokenTelemetryService
{
    private readonly object _syncRoot = new();
    private readonly TransparentProxyUsageEventQueue _usageEvents;
    private readonly AsyncLocal<TransparentProxyUsageContext?> _usageContext = new();
    private readonly Queue<TransparentProxyTokenSample> _samples = new();
    private long _promptCacheTokens;
    private long _totalInputTokens;
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
            _totalInputTokens = 0;
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
                _totalInputTokens,
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

    public bool TrackRequestBodyInputTokens(byte[] body)
    {
        var tokenCount = EstimateRequestInputTokens(body);
        if (tokenCount <= 0)
        {
            return false;
        }

        TrackInputTokens(tokenCount);
        return true;
    }

    public bool TrackLocalCacheTokens(byte[] requestBody)
    {
        var tokenCount = EstimateRequestInputTokens(requestBody);
        if (tokenCount <= 0)
        {
            return false;
        }

        TrackPromptCacheTokens(tokenCount);
        return true;
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

        TrackPromptCacheTokens(cachedTokens);
        return true;
    }

    internal void TrackPromptCacheTokens(int tokenCount)
    {
        if (tokenCount <= 0)
        {
            return;
        }

        var promptCacheDelta = Math.Min(tokenCount, 1_000_000);
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
    }

    internal void TrackInputTokens(int tokenCount)
    {
        if (tokenCount <= 0)
        {
            return;
        }

        var totalInputTokens = Interlocked.Add(ref _totalInputTokens, Math.Min(tokenCount, 1_000_000));
        var snapshot = CreateSnapshot();
        _usageEvents.Publish(
            TransparentProxyUsageEventKind.InputTokens,
            outputTokenDelta: 0,
            totalOutputTokens: snapshot.TotalOutputTokens,
            tokensPerSecond: snapshot.TokensPerSecond,
            totalPromptCacheTokens: snapshot.PromptCacheTokens,
            inputTokenDelta: tokenCount,
            totalInputTokens: totalInputTokens,
            estimated: false,
            modelName: _usageContext.Value?.ModelName ?? string.Empty,
            routeName: _usageContext.Value?.RouteName ?? string.Empty,
            wireApi: _usageContext.Value?.WireApi ?? string.Empty,
            cacheState: _usageContext.Value?.CacheState ?? string.Empty,
            ingressKind: _usageContext.Value?.IngressKind ?? string.Empty,
            sourceApplication: _usageContext.Value?.SourceApplication ?? string.Empty,
            captureMode: _usageContext.Value?.CaptureMode ?? string.Empty);
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

    internal static int TryExtractPromptCacheTokens(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return 0;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var direct = new[]
            {
                TryReadIntPath(root, "usage", "input_tokens_details", "cached_tokens"),
                TryReadIntPath(root, "response", "usage", "input_tokens_details", "cached_tokens"),
                TryReadIntPath(root, "usage", "prompt_tokens_details", "cached_tokens"),
                TryReadIntPath(root, "response", "usage", "prompt_tokens_details", "cached_tokens"),
                TryReadCacheTokenPair(root, "usage"),
                TryReadCacheTokenPair(root, "response", "usage"),
                TryReadCacheTokenPair(root, "message", "usage"),
                TryReadIntPath(root, "usageMetadata", "cachedContentTokenCount"),
                TryReadIntPath(root, "response", "usageMetadata", "cachedContentTokenCount"),
                TryReadIntPath(root, "usage_metadata", "cachedContentTokenCount"),
                TryReadIntPath(root, "response", "usage_metadata", "cachedContentTokenCount")
            }.Max();
            return direct > 0
                ? direct
                : Math.Min(1_000_000, SumPromptCacheTokensRecursive(root));
        }
        catch
        {
            return 0;
        }
    }

    private static int EstimateRequestInputTokens(byte[] body)
    {
        if (body.Length == 0)
        {
            return 0;
        }

        try
        {
            var text = Encoding.UTF8.GetString(body);
            using var document = JsonDocument.Parse(text);
            StringBuilder builder = new();
            AppendRequestInputText(document.RootElement, builder);
            return Math.Min(1_000_000, TokenCountEstimator.EstimateOutputTokens(builder.ToString()));
        }
        catch
        {
            try
            {
                return Math.Min(1_000_000, TokenCountEstimator.EstimateOutputTokens(Encoding.UTF8.GetString(body)));
            }
            catch
            {
                return 0;
            }
        }
    }

    private static void AppendRequestInputText(JsonElement element, StringBuilder builder, string propertyName = "")
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    AppendRequestInputText(property.Value, builder, property.Name);
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    AppendRequestInputText(item, builder, propertyName);
                }

                break;
            case JsonValueKind.String when IsRequestTextProperty(propertyName):
                var value = element.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    builder.AppendLine(value);
                }

                break;
        }
    }

    private static bool IsRequestTextProperty(string propertyName)
        => string.Equals(propertyName, "content", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(propertyName, "text", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(propertyName, "input", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(propertyName, "prompt", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(propertyName, "instructions", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(propertyName, "system", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(propertyName, "developer", StringComparison.OrdinalIgnoreCase);

    internal static int TryExtractInputTokens(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return 0;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            return new[]
            {
                TryReadUsageInputTokens(root, "usage"),
                TryReadUsageInputTokens(root, "response", "usage"),
                TryReadUsageInputTokens(root, "message", "usage"),
                TryReadIntPath(root, "usageMetadata", "promptTokenCount"),
                TryReadIntPath(root, "response", "usageMetadata", "promptTokenCount"),
                TryReadIntPath(root, "usage_metadata", "promptTokenCount"),
                TryReadIntPath(root, "response", "usage_metadata", "promptTokenCount")
            }.Max();
        }
        catch
        {
            return 0;
        }
    }

    private static int TryReadUsageInputTokens(JsonElement root, params string[] path)
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

        return Math.Max(
            Math.Max(
                TryReadIntPath(current, "prompt_tokens"),
                TryReadIntPath(current, "input_tokens")),
            Math.Max(
                TryReadIntPath(current, "cache_read_input_tokens") + TryReadIntPath(current, "cache_creation_input_tokens"),
                TryReadIntPath(current, "inputTokenCount")));
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

    private static int SumPromptCacheTokensRecursive(JsonElement element)
    {
        var total = 0;
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (IsPromptCacheTokenProperty(property.Name))
                    {
                        total += TryReadInt(property.Value);
                    }
                    else
                    {
                        total += SumPromptCacheTokensRecursive(property.Value);
                    }
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    total += SumPromptCacheTokensRecursive(item);
                }

                break;
        }

        return Math.Min(1_000_000, total);
    }

    private static bool IsPromptCacheTokenProperty(string propertyName)
        => string.Equals(propertyName, "cached_tokens", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(propertyName, "cache_read_input_tokens", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(propertyName, "cache_creation_input_tokens", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(propertyName, "cachedContentTokenCount", StringComparison.OrdinalIgnoreCase);

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

    private static int TryReadInt(JsonElement element)
        => element.ValueKind switch
        {
            JsonValueKind.Number when element.TryGetInt32(out var value) => Math.Max(0, value),
            JsonValueKind.String when int.TryParse(element.GetString(), out var value) => Math.Max(0, value),
            _ => 0
        };
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
    long TotalInputTokens,
    double TokensPerSecond,
    DateTimeOffset? LastTokenActivityAt,
    long PromptCacheTokens)
{
    public long TotalTokens => Math.Max(0, TotalInputTokens) + Math.Max(0, TotalOutputTokens);
}

internal sealed record TransparentProxyTokenSample(DateTimeOffset Timestamp, int TokenCount);

internal sealed class TransparentProxyTokenStreamTracker
{
    private readonly TransparentProxyTokenTelemetryService _owner;
    private int _accountedInputTokens;
    private int _accountedOutputTokens;
    private int _accountedPromptCacheTokens;

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

        var actualInputTokens = TransparentProxyTokenTelemetryService.TryExtractInputTokens(data);
        if (actualInputTokens > _accountedInputTokens)
        {
            var inputDelta = actualInputTokens - _accountedInputTokens;
            _accountedInputTokens = actualInputTokens;
            _owner.TrackInputTokens(inputDelta);
            changed = true;
        }

        var actualPromptCacheTokens = TransparentProxyTokenTelemetryService.TryExtractPromptCacheTokens(data);
        if (actualPromptCacheTokens > _accountedPromptCacheTokens)
        {
            var promptCacheDelta = actualPromptCacheTokens - _accountedPromptCacheTokens;
            _accountedPromptCacheTokens = actualPromptCacheTokens;
            _owner.TrackPromptCacheTokens(promptCacheDelta);
            changed = true;
        }

        return changed;
    }
}
