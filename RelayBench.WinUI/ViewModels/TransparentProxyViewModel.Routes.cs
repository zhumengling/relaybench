using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using Microsoft.UI.Xaml;
using RelayBench.Core.Models;
using RelayBench.Services.Infrastructure;
using RelayBench.Services;
using RelayBench.Core.Services;
using RelayBench.WinUI.Charts;
using RelayBench.WinUI.Storage;
using Windows.ApplicationModel.DataTransfer;

namespace RelayBench.WinUI.ViewModels;

public sealed partial class TransparentProxyViewModel
{    /// <summary>
     /// Loads persisted routes from the repository into the Routes collection.
     /// </summary>
    public async Task LoadRoutesAsync()
    {
        if (_routeRepository is null) return;
        try
        {
            var routes = await _routeRepository.GetAllAsync();
            Routes.Clear();
            foreach (var route in routes)
            {
                Routes.Add(route);
            }

            RefreshRouteQueueFromDefinitions();
            await ApplyRoutesToProxyAsync();
        }
        catch
        {
            // Silently fail; routes will remain empty until next load attempt.
        }
    }

    /// <summary>
    /// Loads persisted strategies and refreshes the runtime route view.
    /// </summary>
    public async Task LoadStrategiesAsync()
    {
        try
        {
            var strategies = await _strategyRepository.GetAllAsync();
            Strategies.Clear();
            foreach (var strategy in strategies)
            {
                Strategies.Add(strategy);
            }

            RefreshRouteQueueFromDefinitions();
            if (IsRunning)
            {
                _proxyService.UpdateRoutes(BuildRuntimeRoutes());
            }
        }
        catch
        {
            // Silently fail; strategies will remain at their previous state until the next load.
        }
    }

    /// <summary>
    /// Adds a new route or updates an existing one, persists it, and applies to the proxy.
    /// </summary>
    public async Task AddOrUpdateRouteAsync(RouteDefinition route)
    {
        if (_routeRepository is not null)
        {
            await _routeRepository.UpsertAsync(route);
        }

        // Update the observable collection
        var existingIndex = -1;
        for (int i = 0; i < Routes.Count; i++)
        {
            if (Routes[i].Id == route.Id)
            {
                existingIndex = i;
                break;
            }
        }

        if (existingIndex >= 0)
            Routes[existingIndex] = route;
        else
            Routes.Add(route);

        await ApplyRoutesToProxyAsync();
    }

    public async Task<ClientApplyEndpoint?> EnsureClaudeRelayEndpointForClientApplyAsync(
        ProxyEndpointSettings settings,
        ProxyEndpointProtocolProbeResult probeResult,
        string sourceName,
        bool startProxy = true)
    {
        if (probeResult.AnthropicMessagesSupported || !probeResult.ChatCompletionsSupported)
        {
            return null;
        }

        var model = settings.Model.Trim();
        var existingRoute = Routes.FirstOrDefault(route =>
            route.Enabled &&
            !string.Equals(route.AuthMode, TransparentProxyRouteAuthModes.CodexOAuth, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(
                NormalizeEndpointForClientApplyRoute(route.UpstreamUrl),
                NormalizeEndpointForClientApplyRoute(settings.BaseUrl),
                StringComparison.OrdinalIgnoreCase) &&
            RouteContainsModel(route, model));

        if (existingRoute is null)
        {
            var routeName = BuildClientApplyRelayRouteName(sourceName, model);
            var route = new RouteDefinition(
                TransparentProxyRouteTextCodec.BuildRouteId(routeName, settings.BaseUrl, string.Empty),
                routeName,
                settings.BaseUrl.Trim(),
                string.IsNullOrWhiteSpace(settings.ApiKey) ? null : settings.ApiKey.Trim(),
                Priority: Math.Max(100, Routes.Count == 0 ? 100 : Routes.Max(static item => item.Priority) + 1),
                ModelFilter: model,
                Enabled: true,
                UpdatedAtUtc: DateTime.UtcNow,
                PreferredWireApi: ProxyWireApiProbeService.ChatCompletionsWireApi,
                AuthMode: TransparentProxyRouteAuthModes.ApiKey);
            await AddOrUpdateRouteAsync(route);
        }

        if (startProxy && !IsRunning)
        {
            await StartProxyAsync();
        }
        else
        {
            _proxyService.UpdateRoutes(BuildRuntimeRoutes());
            RefreshRouteQueueFromDefinitions();
        }

        StatusText = $"已准备 Claude CLI 本地转换路由：{model} -> {LocalEndpoint}";
        return new ClientApplyEndpoint(
            LocalEndpoint,
            "relaybench-local",
            model,
            "RelayBench local Claude relay",
            ContextWindow: null,
            PreferredWireApi: ProxyWireApiProbeService.AnthropicMessagesWireApi);
    }

    /// <summary>
    /// Deletes a route from persistence and the observable collection, then applies to the proxy.
    /// </summary>
    public async Task RemoveRouteAsync(RouteDefinition route)
    {
        if (_routeRepository is null) return;

        await _routeRepository.DeleteAsync(route.Id);
        Routes.Remove(route);
        await ApplyRoutesToProxyAsync();
    }

    public bool ResetRouteCircuit(string? routeId)
    {
        if (string.IsNullOrWhiteSpace(routeId))
        {
            StatusText = "请选择要重置熔断器的路由";
            return false;
        }

        var normalizedRouteId = routeId.Trim();
        var routeName = Routes.FirstOrDefault(route =>
            string.Equals(route.Id, normalizedRouteId, StringComparison.OrdinalIgnoreCase))?.Name ?? normalizedRouteId;

        var reset = _proxyService.ResetRouteCircuit(normalizedRouteId);
        if (reset)
        {
            StatusText = $"已重置路由“{routeName}”的熔断状态";
            RefreshRouteQueueFromDefinitions();
            return true;
        }

        StatusText = IsRunning
            ? $"未找到路由“{routeName}”的运行时熔断状态"
            : $"代理未运行，路由“{routeName}”暂无运行时熔断状态";
        return false;
    }

    [RelayCommand]
    private void ResetTransparentProxyRouteCircuit(RouteDefinition? route)
        => ResetRouteCircuit(route?.Id);

    [RelayCommand]
    private async Task FetchTransparentProxyRouteEditorItemModelsAsync(RouteDefinition? route)
    {
        if (route is null)
        {
            StatusText = "请选择要拉取模型的透明代理路由";
            return;
        }

        if (string.Equals(route.AuthMode, TransparentProxyRouteAuthModes.CodexOAuth, StringComparison.OrdinalIgnoreCase))
        {
            StatusText = "Codex OAuth 路由的模型池来自已导入账号，无需从 /v1/models 拉取";
            return;
        }

        if (string.IsNullOrWhiteSpace(route.UpstreamUrl))
        {
            StatusText = $"路由“{route.Name}”缺少上游 URL";
            return;
        }

        if (string.IsNullOrWhiteSpace(route.ApiKeyProtected))
        {
            StatusText = $"路由“{route.Name}”没有 API Key，无法直接拉取 /v1/models";
            return;
        }

        StatusText = $"正在从路由“{route.Name}”拉取模型...";
        try
        {
            var probeModel = SplitCsv(route.ModelFilter)
                .Select(static item => ParseInlineModelMapping(item).Name)
                .FirstOrDefault(static item => !string.IsNullOrWhiteSpace(item)) ?? string.Empty;
            var settings = new ProxyEndpointSettings(
                route.UpstreamUrl,
                route.ApiKeyProtected,
                probeModel,
                IgnoreTlsErrors,
                Math.Clamp(UpstreamTimeoutSeconds, 5, 120));
            var diagnostics = new ProxyDiagnosticsService();
            var catalog = await diagnostics.FetchModelsAsync(settings);
            if (!catalog.Success)
            {
                StatusText = $"路由“{route.Name}”模型拉取失败：{catalog.Error ?? catalog.Summary}";
                return;
            }

            var models = catalog.ModelItems is { Count: > 0 }
                ? catalog.ModelItems.Select(static item => item.Id)
                : catalog.Models;
            var modelList = models
                .Where(static item => !string.IsNullOrWhiteSpace(item))
                .Select(static item => item.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (modelList.Length == 0)
            {
                StatusText = $"路由“{route.Name}”/v1/models 没有返回可用模型";
                return;
            }

            var modelFilter = JoinRouteModelTokens(modelList.Select(static model => $"{EscapeInlineModelToken(model)}=>{EscapeInlineModelToken(model)}"));
            await UpdateRouteModelFilterAsync(route, modelFilter, $"已从路由“{route.Name}”拉取 {modelList.Length} 个模型映射");
            var cache = new ProxyEndpointModelCacheService();
            await cache.SaveCatalogAsync(settings, catalog);
        }
        catch (Exception ex)
        {
            StatusText = $"路由“{route.Name}”模型拉取失败：{ex.Message}";
        }
    }

    [RelayCommand]
    private async Task AddTransparentProxyRouteModelMappingAsync(RouteDefinition? route)
    {
        if (route is null)
        {
            StatusText = "请选择要添加模型映射的透明代理路由";
            return;
        }

        var tokens = SplitCsv(route.ModelFilter).ToList();
        var plainIndex = tokens.FindIndex(static token => !HasInlineModelMapping(token));
        if (plainIndex < 0)
        {
            StatusText = tokens.Count == 0
                ? $"路由“{route.Name}”还没有模型；请先编辑路由或从 /v1/models 拉取"
                : $"路由“{route.Name}”的模型已经是显式映射";
            return;
        }

        var (name, alias) = ParseInlineModelMapping(tokens[plainIndex]);
        if (string.IsNullOrWhiteSpace(name))
        {
            StatusText = $"路由“{route.Name}”没有可转换的模型名称";
            return;
        }

        tokens[plainIndex] = $"{EscapeInlineModelToken(name)}=>{EscapeInlineModelToken(alias)}";
        await UpdateRouteModelFilterAsync(
            route,
            JoinRouteModelTokens(tokens),
            $"已为路由“{route.Name}”添加模型映射：{name}=>{alias}");
    }

    [RelayCommand]
    private async Task RemoveTransparentProxyRouteModelMappingAsync(RouteDefinition? route)
    {
        if (route is null)
        {
            StatusText = "请选择要移除模型映射的透明代理路由";
            return;
        }

        var tokens = SplitCsv(route.ModelFilter).ToList();
        var mappingIndex = tokens.FindLastIndex(static token => HasInlineModelMapping(token));
        if (mappingIndex < 0)
        {
            StatusText = $"路由“{route.Name}”没有可移除的模型映射";
            return;
        }

        var (name, _) = ParseInlineModelMapping(tokens[mappingIndex]);
        tokens.RemoveAt(mappingIndex);
        await UpdateRouteModelFilterAsync(
            route,
            JoinRouteModelTokens(tokens),
            $"已移除路由“{route.Name}”的模型映射：{name}");
    }

    [RelayCommand]
    private async Task MoveTransparentProxyRouteEditorItemUpAsync(RouteDefinition? route)
        => await MoveTransparentProxyRouteAsync(route, -1);

    [RelayCommand]
    private async Task MoveTransparentProxyRouteEditorItemDownAsync(RouteDefinition? route)
        => await MoveTransparentProxyRouteAsync(route, 1);

    private async Task MoveTransparentProxyRouteAsync(RouteDefinition? route, int direction)
    {
        if (route is null)
        {
            StatusText = "请选择要排序的透明代理路由";
            return;
        }

        var currentIndex = IndexOfRoute(route.Id);
        if (currentIndex < 0)
        {
            StatusText = $"未找到路由“{route.Name}”";
            return;
        }

        var nextIndex = currentIndex + Math.Sign(direction);
        if (nextIndex < 0 || nextIndex >= Routes.Count)
        {
            StatusText = direction < 0
                ? $"路由“{Routes[currentIndex].Name}”已经在最前"
                : $"路由“{Routes[currentIndex].Name}”已经在最后";
            return;
        }

        Routes.Move(currentIndex, nextIndex);
        await ReorderRoutesAsync();
        StatusText = direction < 0
            ? $"已上移路由“{Routes[nextIndex].Name}”"
            : $"已下移路由“{Routes[nextIndex].Name}”";
    }

    private int IndexOfRoute(string id)
    {
        for (var i = 0; i < Routes.Count; i++)
        {
            if (string.Equals(Routes[i].Id, id, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    private async Task UpdateRouteModelFilterAsync(RouteDefinition route, string? modelFilter, string statusText)
    {
        var updated = route with
        {
            ModelFilter = string.IsNullOrWhiteSpace(modelFilter) ? null : modelFilter,
            UpdatedAtUtc = DateTime.UtcNow
        };

        await AddOrUpdateRouteAsync(updated);
        StatusText = statusText;
    }

    private static bool HasInlineModelMapping(string value)
        => value.Contains("=>", StringComparison.Ordinal) ||
           value.Contains("->", StringComparison.Ordinal);

    private static string JoinRouteModelTokens(IEnumerable<string> tokens)
        => string.Join(", ",
            tokens
                .Select(static item => item.Trim())
                .Where(static item => !string.IsNullOrWhiteSpace(item))
                .Distinct(StringComparer.OrdinalIgnoreCase));

    private static string EscapeInlineModelToken(string value)
        => (value ?? string.Empty)
            .Replace("|", " ", StringComparison.Ordinal)
            .Replace(",", " ", StringComparison.Ordinal)
            .Replace(";", " ", StringComparison.Ordinal)
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Trim();

    /// <summary>
    /// Persists the new route order after a drag-reorder and applies to the proxy.
    /// </summary>
    public async Task ReorderRoutesAsync()
    {
        if (Routes.Count == 0)
        {
            return;
        }

        var ordering = new List<(string id, int priority)>();
        var updatedAt = DateTime.UtcNow;
        for (int i = 0; i < Routes.Count; i++)
        {
            var route = Routes[i];
            var priority = Routes.Count - i;
            ordering.Add((route.Id, priority));
            if (route.Priority != priority)
            {
                Routes[i] = route with
                {
                    Priority = priority,
                    UpdatedAtUtc = updatedAt
                };
            }
        }

        if (_routeRepository is not null)
        {
            await _routeRepository.ReorderAsync(ordering);
        }

        await ApplyRoutesToProxyAsync();
    }

    private async Task ApplyRoutesToProxyAsync()
    {
        try
        {
            _proxyService.UpdateRoutes(BuildRuntimeRoutes());
            RefreshRouteQueueFromDefinitions();
        }
        catch
        {
            // Best-effort apply; proxy continues with previous routes.
        }
    }

    private void PopulateModelPool()
    {
        ModelPool.Clear();
        foreach (var route in BuildRuntimeRoutes())
        {
            foreach (var model in route.Models)
            {
                ModelPool.Add(new ModelPoolEntry(
                    model,
                    1,
                    1,
                    FormatProtocol(route.PreferredWireApi),
                    0,
                    0,
                    false,
                    0));
            }
        }

        UpdateModelPoolTitle();
    }

    private void RefreshRouteQueueFromDefinitions()
    {
        RouteQueue.Clear();
        foreach (var route in BuildRuntimeRoutes())
        {
            RouteQueue.Add(new RouteQueueEntry(
                route.Name,
                0,
                route.Priority,
                0,
                new CircuitBreakerInfo(CircuitState.Closed, 0),
                FormatProtocol(route.PreferredWireApi),
                "0 ms",
                "0 tok/s",
                route.ChatCompletionsSupported == false &&
                route.ResponsesSupported == false &&
                route.AnthropicMessagesSupported == false
                    ? "待复核"
                    : "Ready",
                route.CircuitOpenUntil > DateTimeOffset.UtcNow ? "冷却中" : "活跃",
                PolicyDisplay: BuildRoutePolicyDisplay(route),
                CooldownDisplay: FormatCooldown(route.CircuitOpenUntil),
                RouteId: route.Id));
        }

        ActiveRoutes = RouteQueue.Count;
        PopulateModelPool();
        RefreshProviderAccounts();
    }

    private IReadOnlyList<TransparentProxyRoute> BuildRuntimeRoutes()
    {
        List<TransparentProxyRoute> routes = [];
        foreach (var route in Routes.Where(static route => route.Enabled))
        {
            var runtimeRoute = BuildRuntimeRoute(route);
            routes.Add(_discoveredRoutesById.TryGetValue(runtimeRoute.Id, out var discovered)
                ? discovered
                : runtimeRoute);
        }

        var existingIds = new HashSet<string>(routes.Select(static route => route.Id), StringComparer.OrdinalIgnoreCase);
        foreach (var codexRoute in BuildCodexOAuthRuntimeRoutes())
        {
            if (existingIds.Add(codexRoute.Id))
            {
                routes.Add(codexRoute);
            }
        }

        var runtimePriorityByRouteId = ResolveRuntimePriorities(routes);
        if (runtimePriorityByRouteId.Count > 0)
        {
            for (var index = 0; index < routes.Count; index++)
            {
                var route = routes[index];
                if (runtimePriorityByRouteId.TryGetValue(route.Id, out var runtimePriority))
                {
                    routes[index] = route.WithRuntimePriority(runtimePriority);
                }
            }
        }

        return routes
            .OrderByDescending(static route => route.RuntimePriority)
            .ThenByDescending(static route => route.Priority)
            .ThenBy(static route => route.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private TransparentProxyRoute BuildRuntimeRoute(RouteDefinition route)
    {
        var routeModels = SplitCsv(route.ModelFilter).ToArray();
        var primaryModel = routeModels.FirstOrDefault() ?? string.Empty;
        var mappings = BuildModelMappings(routeModels);
        var models = mappings
            .Select(static mapping => mapping.Name)
            .Where(static item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new TransparentProxyRoute(
            id: route.Id,
            name: route.Name,
            baseUrl: route.UpstreamUrl,
            apiKey: route.ApiKeyProtected ?? string.Empty,
            model: primaryModel,
            models: models,
            priority: route.Priority,
            prefix: route.Prefix,
            preferredWireApi: route.PreferredWireApi,
            headers: ParseRouteHeaders(route.HeadersText),
            modelMappings: mappings,
            excludedModelPatterns: SplitCsv(route.ExcludedModelPatterns),
            outboundProxy: route.OutboundProxy,
            requestRetry: route.RequestRetry,
            maxRetryIntervalSeconds: route.MaxRetryIntervalSeconds,
            modelCooldownSeconds: route.ModelCooldownSeconds,
            payloadRulesText: route.PayloadRulesText,
            authMode: route.AuthMode,
            oauthProvider: route.OAuthProvider,
            oauthCredentialId: route.OAuthCredentialId,
            codexBackendBaseUrl: route.CodexBackendBaseUrl,
            codexOAuthFastMode: route.CodexOAuthFastMode);
    }

    private IReadOnlyList<TransparentProxyModelMapping> BuildModelMappings(IReadOnlyList<string> routeModels)
    {
        List<TransparentProxyModelMapping> mappings = [];
        foreach (var rule in ModelRewriteRules)
        {
            if (string.IsNullOrWhiteSpace(rule.SourceModel) || string.IsNullOrWhiteSpace(rule.TargetModel))
            {
                continue;
            }

            mappings.Add(new TransparentProxyModelMapping(rule.SourceModel.Trim(), rule.TargetModel.Trim()));
        }

        foreach (var routeModel in routeModels)
        {
            var (name, alias) = ParseInlineModelMapping(routeModel);
            if (!mappings.Any(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                mappings.Add(new TransparentProxyModelMapping(name, alias));
            }
        }

        return mappings;
    }

    private static (string Name, string Alias) ParseInlineModelMapping(string value)
    {
        var text = value.Trim();
        var separator = text.IndexOf("=>", StringComparison.Ordinal);
        if (separator < 0)
        {
            separator = text.IndexOf("->", StringComparison.Ordinal);
        }

        if (separator < 0)
        {
            return (text, text);
        }

        var name = text[..separator].Trim();
        var alias = text[(separator + 2)..].Trim();
        return string.IsNullOrWhiteSpace(name)
            ? (text, text)
            : (name, string.IsNullOrWhiteSpace(alias) ? name : alias);
    }

    private IEnumerable<TransparentProxyRoute> BuildCodexOAuthRuntimeRoutes()
    {
        foreach (var credential in _codexOAuthService.GetCredentials()
                     .Where(IsUsableCodexOAuthCredential)
                     .OrderBy(static credential => credential.PlanType, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(static credential => credential.Email, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(static credential => credential.Id, StringComparer.OrdinalIgnoreCase))
        {
            var models = CodexOAuthModelCatalog.GetModels(credential.PlanType)
                .Where(static model => !string.IsNullOrWhiteSpace(model))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var primaryModel = models.FirstOrDefault() ?? CodexOAuthModelCatalog.DefaultModel;
            var mappings = models.Select(static model => new TransparentProxyModelMapping(model, model)).ToArray();
            var plan = string.IsNullOrWhiteSpace(credential.PlanType) ? "Codex" : credential.PlanType.Trim();
            var displayName = string.IsNullOrWhiteSpace(credential.DisplayName) ? credential.Id : credential.DisplayName;
            yield return new TransparentProxyRoute(
                id: $"codex-oauth-{credential.Id}",
                name: $"Codex OAuth {plan} {displayName}".Trim(),
                baseUrl: CodexOAuthConstants.DefaultBackendBaseUrl,
                apiKey: string.Empty,
                model: primaryModel,
                preferredWireApi: ProxyWireApiProbeService.ResponsesWireApi,
                chatCompletionsSupported: true,
                responsesSupported: true,
                anthropicMessagesSupported: true,
                protocolCheckedAt: DateTimeOffset.UtcNow,
                models: models,
                priority: 0,
                modelMappings: mappings,
                authMode: TransparentProxyRouteAuthModes.CodexOAuth,
                oauthProvider: CodexOAuthConstants.Provider,
                oauthCredentialId: credential.Id,
                codexBackendBaseUrl: CodexOAuthConstants.DefaultBackendBaseUrl);
        }
    }

    private static bool IsUsableCodexOAuthCredential(CodexOAuthCredential credential)
        => string.Equals(credential.Provider, CodexOAuthConstants.Provider, StringComparison.OrdinalIgnoreCase) &&
           credential.State is not CodexOAuthCredentialState.Disabled and not CodexOAuthCredentialState.NeedsRelogin &&
           (!string.IsNullOrWhiteSpace(credential.RefreshToken) || !string.IsNullOrWhiteSpace(credential.AccessToken));

}
