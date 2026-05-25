using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using RelayBench.Core.Models;
using RelayBench.Core.Services;
using RelayBench.Services;
using RelayBench.WinUI.Services;
using RelayBench.WinUI.Storage;

namespace RelayBench.WinUI.ViewModels;

public sealed partial class ApplicationCenterViewModel : ObservableObject
{
    private bool TryBuildSettings(out ProxyEndpointSettings settings, bool requireModel)
    {
        settings = default!;
        if (string.IsNullOrWhiteSpace(BaseUrl) ||
            string.IsNullOrWhiteSpace(ApiKey) ||
            (requireModel && string.IsNullOrWhiteSpace(Model)))
        {
            return false;
        }

        settings = new ProxyEndpointSettings(
            BaseUrl.Trim(),
            ApiKey.Trim(),
            Model.Trim(),
            IgnoreTlsErrors: false,
            TimeoutSeconds: 15);
        return true;
    }

    private ClientApplyEndpoint BuildClientApplyEndpoint(
        ProxyEndpointProtocolProbeResult probeResult,
        ClientApplyProtocolKind protocolKind)
        => new(
            BaseUrl.Trim(),
            ApiKey.Trim(),
            ResolveApplyModel(protocolKind),
            "RelayBench",
            ContextWindow: null,
            probeResult.PreferredWireApi);

    private ClientApplyEndpoint BuildVsCodeClientApplyEndpoint()
        => new(
            BaseUrl.Trim(),
            string.IsNullOrWhiteSpace(ApiKey) ? "relaybench-local" : ApiKey.Trim(),
            string.IsNullOrWhiteSpace(Model) ? "relaybench-local" : Model.Trim(),
            "RelayBench",
            ContextWindow: null,
            _lastProbeResult?.PreferredWireApi ?? PreferredProtocol);

    private string ResolveApplyModel(ClientApplyProtocolKind protocolKind)
    {
        if (protocolKind == ClientApplyProtocolKind.Gemini)
        {
            var geminiModel = AvailableModels
                .Concat([Model])
                .Select(static item => item?.Trim() ?? string.Empty)
                .FirstOrDefault(static item => item.StartsWith("gemini-", StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(geminiModel))
            {
                return geminiModel;
            }
        }

        return Model.Trim();
    }

    private bool LoadPersistedEndpoint()
    {
        try
        {
            var shared = _sharedEndpointLoader();
            if (shared is not null && !string.IsNullOrWhiteSpace(shared.BaseUrl))
            {
                ApplyPersistedEndpoint(shared.BaseUrl, shared.ApiKey, shared.Model, [shared.Model]);
                return true;
            }

            var items = _endpointHistoryLoader(CancellationToken.None).GetAwaiter().GetResult();
            if (items is { Count: > 0 })
            {
                var latest = items[0];
                ApplyPersistedEndpoint(latest.BaseUrl, latest.ApiKey, latest.Model, latest.Models ?? []);
                return true;
            }
        }
        catch
        {
            // Best effort; the page remains usable with manual entry.
        }

        return false;
    }

    private void ApplyPersistedEndpoint(
        string baseUrl,
        string apiKey,
        string model,
        IEnumerable<string> models)
    {
        BaseUrl = baseUrl;
        ApiKey = apiKey;
        Model = model;
        AvailableModels.Clear();

        foreach (var item in models.Append(model)
                     .Select(static value => value?.Trim() ?? string.Empty)
                     .Where(static value => !string.IsNullOrWhiteSpace(value))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            AvailableModels.Add(item);
        }
    }

    private bool CanReplaceWithTransparentProxy()
    {
        if (string.IsNullOrWhiteSpace(BaseUrl))
        {
            return true;
        }

        return IsLocalEndpoint(BaseUrl) &&
               string.IsNullOrWhiteSpace(ApiKey) &&
               string.IsNullOrWhiteSpace(Model);
    }

    private bool IsTransparentProxySentinelEndpoint()
        => IsLocalEndpoint(BaseUrl) &&
           string.Equals(ApiKey.Trim(), "relaybench-local", StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<string> BuildTransparentProxyModelList(TransparentProxyViewModel proxy)
    {
        List<string> models = [];
        foreach (var item in proxy.ModelPool)
        {
            AddTransparentProxyModel(models, item.Name);
        }

        foreach (var route in proxy.Routes.Where(static route => route.Enabled))
        {
            foreach (var model in SplitModelFilter(route.ModelFilter))
            {
                AddTransparentProxyModel(models, model);
            }
        }

        foreach (var rule in proxy.ModelRewriteRules)
        {
            AddTransparentProxyModel(models, rule.SourceModel);
            AddTransparentProxyModel(models, rule.TargetModel);
        }

        return models;
    }

    private static void AddTransparentProxyModel(List<string> models, string? value)
    {
        var model = value?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(model) ||
            model == "*" ||
            models.Contains(model, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        models.Add(model);
    }

    private static IEnumerable<string> SplitModelFilter(string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
        {
            yield break;
        }

        foreach (var item in filter.Split([',', ';', '|', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!string.IsNullOrWhiteSpace(item))
            {
                yield return item.Trim();
            }
        }
    }

    private static string BuildTransparentProxyContext(TransparentProxyViewModel proxy, int modelCount)
    {
        var enabledRoutes = proxy.Routes.Count(static route => route.Enabled);
        var activeConnections = proxy.ActiveConnections.ToString(CultureInfo.InvariantCulture);
        return $"\u900f\u660e\u4ee3\u7406\uff1a{proxy.LocalEndpoint} \u00b7 {enabledRoutes} \u6761\u542f\u7528\u8def\u7531 \u00b7 {modelCount} \u4e2a\u5019\u9009\u6a21\u578b \u00b7 \u6d3b\u8dc3\u8fde\u63a5 {activeConnections}";
    }

    private static string ResolveTransparentProxyPreferredProtocol(TransparentProxyViewModel proxy)
    {
        var protocol = proxy.RouteQueue
            .Select(static item => item.Protocol?.Trim() ?? string.Empty)
            .FirstOrDefault(static item =>
                item.Equals("responses", StringComparison.OrdinalIgnoreCase) ||
                item.Contains("Responses", StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(protocol))
        {
            return "responses";
        }

        protocol = proxy.RouteQueue
            .Select(static item => item.Protocol?.Trim() ?? string.Empty)
            .FirstOrDefault(static item =>
                item.Equals("chat", StringComparison.OrdinalIgnoreCase) ||
                item.Contains("Chat", StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(protocol))
        {
            return "chat";
        }

        protocol = proxy.RouteQueue
            .Select(static item => item.Protocol?.Trim() ?? string.Empty)
            .FirstOrDefault(static item =>
                item.Contains("Anthropic", StringComparison.OrdinalIgnoreCase) ||
                item.Contains("Messages", StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(protocol))
        {
            return "anthropic";
        }

        return "--";
    }

    private bool IsPreferredProtocol(string protocol)
        => !string.IsNullOrWhiteSpace(PreferredProtocol) &&
           PreferredProtocol.Equals(protocol, StringComparison.OrdinalIgnoreCase);

}
