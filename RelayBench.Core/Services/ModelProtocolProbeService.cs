using RelayBench.Core.Models;

namespace RelayBench.Core.Services;

/// <summary>
/// 对一个接口下每个模型单独探测 Chat/Responses/Anthropic 三种协议支持情况的服务。
/// 支持并发探测（可限流）并写入 SQLite 缓存。
/// </summary>
public sealed class ModelProtocolProbeService
{
    private readonly ProxyEndpointProtocolProbeService _protocolProbeService;
    private readonly ProxyEndpointModelCacheService _cacheService;

    public ModelProtocolProbeService(
        ProxyEndpointProtocolProbeService protocolProbeService,
        ProxyEndpointModelCacheService cacheService)
    {
        _protocolProbeService = protocolProbeService ?? throw new ArgumentNullException(nameof(protocolProbeService));
        _cacheService = cacheService ?? throw new ArgumentNullException(nameof(cacheService));
    }

    public async Task<IReadOnlyList<ProxyModelProtocolProbeOutcome>> ProbeModelsAsync(
        ProxyEndpointSettings endpoint,
        IReadOnlyList<string> models,
        ModelProtocolProbeOptions? options = null,
        IProgress<ModelProtocolProbeProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        options ??= ModelProtocolProbeOptions.Default;

        var normalized = models
            .Where(static m => !string.IsNullOrWhiteSpace(m))
            .Select(static m => m.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (normalized.Length == 0)
        {
            return Array.Empty<ProxyModelProtocolProbeOutcome>();
        }

        var outcomes = new ProxyModelProtocolProbeOutcome?[normalized.Length];
        var completed = 0;
        var total = normalized.Length;
        using var gate = new SemaphoreSlim(Math.Max(1, options.MaxConcurrency));

        progress?.Report(new ModelProtocolProbeProgress(0, total, null));

        var tasks = new Task[normalized.Length];
        for (var index = 0; index < normalized.Length; index++)
        {
            var i = index;
            var model = normalized[i];
            tasks[i] = Task.Run(async () =>
            {
                await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    var outcome = await ProbeSingleAsync(endpoint, model, options, cancellationToken).ConfigureAwait(false);
                    outcomes[i] = outcome;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    outcomes[i] = new ProxyModelProtocolProbeOutcome(
                        model,
                        ChatCompletionsSupported: false,
                        ResponsesSupported: false,
                        AnthropicMessagesSupported: false,
                        PreferredWireApi: null,
                        CheckedAt: DateTimeOffset.Now,
                        Summary: $"Protocol probe failed: {ex.Message}",
                        Error: ex.Message);
                }
                finally
                {
                    var current = Interlocked.Increment(ref completed);
                    progress?.Report(new ModelProtocolProbeProgress(current, total, outcomes[i]));
                    gate.Release();
                }
            }, cancellationToken);
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);

        var results = outcomes
            .Where(static o => o is not null)
            .Cast<ProxyModelProtocolProbeOutcome>()
            .ToArray();

        if (options.SaveResult && results.Length > 0)
        {
            try
            {
                await _cacheService
                    .SaveModelProtocolsAsync(endpoint, results, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch
            {
                // intentionally swallow: best-effort cache write.
            }
        }

        return results;
    }

    private async Task<ProxyModelProtocolProbeOutcome> ProbeSingleAsync(
        ProxyEndpointSettings endpoint,
        string model,
        ModelProtocolProbeOptions options,
        CancellationToken cancellationToken)
    {
        var settings = endpoint with { Model = model };
        var resolution = await _protocolProbeService
            .ResolveAsync(
                settings,
                new ProxyEndpointProtocolProbeOptions(
                    ForceProbe: options.ForceProbe,
                    UseCache: options.UseCache,
                    // we write our own per-model batch in SaveModelProtocolsAsync
                    SaveResult: false),
                cancellationToken)
            .ConfigureAwait(false);
        var result = resolution.Result;
        return new ProxyModelProtocolProbeOutcome(
            model,
            result.ChatCompletionsSupported,
            result.ResponsesSupported,
            result.AnthropicMessagesSupported,
            result.PreferredWireApi,
            result.CheckedAt,
            result.Summary,
            result.Error);
    }
}

public sealed record ModelProtocolProbeOptions(
    bool ForceProbe,
    bool UseCache,
    bool SaveResult,
    int MaxConcurrency)
{
    public static ModelProtocolProbeOptions Default { get; } = new(
        ForceProbe: false,
        UseCache: true,
        SaveResult: true,
        MaxConcurrency: 3);
}

public sealed record ModelProtocolProbeProgress(
    int Completed,
    int Total,
    ProxyModelProtocolProbeOutcome? LastOutcome);
