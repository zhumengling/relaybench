using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using RelayBench.Core.Models;
using RelayBench.Core.Services;
using RelayBench.WinUI.Storage;

namespace RelayBench.WinUI.Services;

public sealed class GlobalEndpointProtocolProbeCoordinator
{
    private static readonly Lazy<GlobalEndpointProtocolProbeCoordinator> s_instance = new(() => new GlobalEndpointProtocolProbeCoordinator());
    private readonly ConcurrentDictionary<string, Task> _running = new(StringComparer.OrdinalIgnoreCase);
    private readonly EndpointHistoryStore _historyStore = new();
    private readonly ProxyDiagnosticsService _diagnosticsService = new();
    private readonly ProxyEndpointModelCacheService _modelCacheService = new();

    public static GlobalEndpointProtocolProbeCoordinator Instance => s_instance.Value;

    public async Task RecordEndpointAsync(
        string baseUrl,
        string apiKey,
        string model,
        IEnumerable<string>? models = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(apiKey))
        {
            return;
        }

        var modelList = NormalizeModels(models, model);
        if (modelList.Count > 0)
        {
            await _historyStore.RecordWithModelsAsync(baseUrl, apiKey, model, modelList, cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            await _historyStore.RecordAsync(baseUrl, apiKey, model, cancellationToken)
                .ConfigureAwait(false);
        }

        await SharedEndpointStore.SaveAsync(baseUrl, apiKey, model, cancellationToken).ConfigureAwait(false);
    }

    public void EnqueueEndpointProbe(
        string baseUrl,
        string apiKey,
        string model,
        IEnumerable<string>? models = null,
        bool force = false,
        IProgress<ModelProtocolProbeProgress>? progress = null)
    {
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(apiKey))
        {
            return;
        }

        var modelList = NormalizeModels(models, model);
        if (modelList.Count == 0)
        {
            return;
        }

        var normalizedBaseUrl = baseUrl.Trim().TrimEnd('/');
        var normalizedApiKey = apiKey.Trim();
        var key = $"{normalizedBaseUrl}|{HashApiKey(normalizedApiKey)}|{string.Join(",", modelList)}|{force}";
        _running.GetOrAdd(key, cacheKey => Task.Run(async () =>
        {
            try
            {
                await ProbeAsync(
                    new ProxyEndpointSettings(
                        normalizedBaseUrl,
                        normalizedApiKey,
                        model?.Trim() ?? string.Empty,
                        IgnoreTlsErrors: false,
                        TimeoutSeconds: 45),
                    modelList,
                    force,
                    progress).ConfigureAwait(false);
            }
            finally
            {
                _running.TryRemove(cacheKey, out var _);
            }
        }));
    }

    public async Task ProbeAsync(
        ProxyEndpointSettings endpoint,
        IReadOnlyList<string> models,
        bool force,
        IProgress<ModelProtocolProbeProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var normalizedModels = NormalizeModels(models, endpoint.Model);
        if (normalizedModels.Count == 0)
        {
            return;
        }

        if (!force)
        {
            var cachedModels = await _modelCacheService.ListModelsAsync(endpoint.BaseUrl, endpoint.ApiKey, cancellationToken)
                .ConfigureAwait(false);
            if (!ShouldProbe(normalizedModels, cachedModels))
            {
                return;
            }
        }

        var protocolProbeService = new ProxyEndpointProtocolProbeService(_diagnosticsService, _modelCacheService);
        var modelProbeService = new ModelProtocolProbeService(protocolProbeService, _modelCacheService);
        await modelProbeService.ProbeModelsAsync(
            endpoint,
            normalizedModels,
            new ModelProtocolProbeOptions(
                ForceProbe: force,
                UseCache: !force,
                SaveResult: true,
                MaxConcurrency: 8),
            progress,
            cancellationToken).ConfigureAwait(false);
    }

    public static bool ShouldProbe(
        IReadOnlyList<string> fetchedModels,
        IReadOnlyList<CachedProxyEndpointModelInfo> cachedModels)
    {
        var fetched = NormalizeModels(fetchedModels, null);
        if (fetched.Count == 0)
        {
            return false;
        }

        var cachedByModel = cachedModels
            .Where(static item => !string.IsNullOrWhiteSpace(item.Model))
            .GroupBy(static item => item.Model.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static group => group.Key, static group => group.First(), StringComparer.OrdinalIgnoreCase);
        if (cachedByModel.Count != fetched.Count ||
            fetched.Any(model => !cachedByModel.ContainsKey(model)))
        {
            return true;
        }

        return fetched.Any(model =>
        {
            var cached = cachedByModel[model];
            return (cached.ProtocolProbeVersion ?? 0) < ProxyWireApiProbeService.CurrentProtocolProbeVersion ||
                   (!cached.ChatCompletionsSupported.HasValue &&
                    !cached.ResponsesSupported.HasValue &&
                    !cached.AnthropicMessagesSupported.HasValue &&
                    string.IsNullOrWhiteSpace(cached.PreferredWireApi));
        });
    }

    private static List<string> NormalizeModels(IEnumerable<string>? models, string? model)
        => (models ?? Array.Empty<string>())
            .Append(model ?? string.Empty)
            .Where(static item => !string.IsNullOrWhiteSpace(item))
            .Select(static item => item.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static item => item, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static string HashApiKey(string apiKey)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(apiKey));
        return Convert.ToHexString(bytes);
    }
}
