using System.Globalization;
using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using Microsoft.Win32;
using RelayBench.App.Infrastructure;
using RelayBench.App.Services;
using RelayBench.Core.Models;
using RelayBench.Core.Services;

namespace RelayBench.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    public string TransparentProxyPortText
    {
        get => _transparentProxyPortText;
        set
        {
            if (SetProperty(ref _transparentProxyPortText, value))
            {
                NotifyTransparentProxyEndpointChanged();
            }
        }
    }

    public string TransparentProxyRoutesText
    {
        get => _transparentProxyRoutesText;
        set
        {
            if (SetProperty(ref _transparentProxyRoutesText, value))
            {
                if (!_isUpdatingTransparentProxyRoutesTextFromEditor)
                {
                    RefreshTransparentProxyRouteEditorItemsFromText();
                }

                RefreshTransparentProxyRoutePreview();
            }
        }
    }

    public TransparentProxyRouteEditorItemViewModel? SelectedTransparentProxyRouteEditorItem
    {
        get => _selectedTransparentProxyRouteEditorItem;
        set
        {
            if (SetProperty(ref _selectedTransparentProxyRouteEditorItem, value))
            {
                RemoveTransparentProxyRouteEditorItemCommand.RaiseCanExecuteChanged();
                MoveTransparentProxyRouteEditorItemUpCommand.RaiseCanExecuteChanged();
                MoveTransparentProxyRouteEditorItemDownCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string TransparentProxyRateLimitPerMinuteText
    {
        get => _transparentProxyRateLimitPerMinuteText;
        set => SetProperty(ref _transparentProxyRateLimitPerMinuteText, value);
    }

    public string TransparentProxyMaxConcurrencyText
    {
        get => _transparentProxyMaxConcurrencyText;
        set => SetProperty(ref _transparentProxyMaxConcurrencyText, value);
    }

    public string TransparentProxyRouteStrategyKey
    {
        get => _transparentProxyRouteStrategyKey;
        set
        {
            var normalized = TransparentProxyRouteStrategies.Normalize(value);
            if (SetProperty(ref _transparentProxyRouteStrategyKey, normalized))
            {
                RefreshTransparentProxyRoutePreview();
            }
        }
    }

    public bool TransparentProxyEnableFallback
    {
        get => _transparentProxyEnableFallback;
        set => SetProperty(ref _transparentProxyEnableFallback, value);
    }

    public bool TransparentProxyEnableCache
    {
        get => _transparentProxyEnableCache;
        set => SetProperty(ref _transparentProxyEnableCache, value);
    }

    public string TransparentProxyCacheTtlSecondsText
    {
        get => _transparentProxyCacheTtlSecondsText;
        set => SetProperty(ref _transparentProxyCacheTtlSecondsText, value);
    }

    public string TransparentProxyRequestRetryText
    {
        get => _transparentProxyRequestRetryText;
        set => SetProperty(ref _transparentProxyRequestRetryText, value);
    }

    public string TransparentProxyMaxRetryIntervalSecondsText
    {
        get => _transparentProxyMaxRetryIntervalSecondsText;
        set => SetProperty(ref _transparentProxyMaxRetryIntervalSecondsText, value);
    }

    public string TransparentProxySessionAffinityTtlSecondsText
    {
        get => _transparentProxySessionAffinityTtlSecondsText;
        set => SetProperty(ref _transparentProxySessionAffinityTtlSecondsText, value);
    }

    public string TransparentProxyModelCooldownSecondsText
    {
        get => _transparentProxyModelCooldownSecondsText;
        set => SetProperty(ref _transparentProxyModelCooldownSecondsText, value);
    }

    public bool TransparentProxyRewriteModel
    {
        get => _transparentProxyRewriteModel;
        set => SetProperty(ref _transparentProxyRewriteModel, value);
    }

    public bool TransparentProxyStartUnifiedEndpointOnLaunch
    {
        get => _transparentProxyStartUnifiedEndpointOnLaunch;
        set
        {
            if (SetProperty(ref _transparentProxyStartUnifiedEndpointOnLaunch, value))
            {
                OnPropertyChanged(nameof(TransparentProxyUnifiedEndpointSummary));
            }
        }
    }

    public bool TransparentProxyEnableAppCapture
    {
        get => _transparentProxyEnableAppCapture;
        set
        {
            if (SetProperty(ref _transparentProxyEnableAppCapture, value))
            {
                OnPropertyChanged(nameof(TransparentProxyAppCaptureSummary));
            }
        }
    }

    public bool IsTransparentProxySettingsDrawerOpen
    {
        get => _isTransparentProxySettingsDrawerOpen;
        private set => SetProperty(ref _isTransparentProxySettingsDrawerOpen, value);
    }

    public bool IsTransparentProxyListenSettingsOpen
    {
        get => _isTransparentProxyListenSettingsOpen;
        private set => SetProperty(ref _isTransparentProxyListenSettingsOpen, value);
    }

    public bool IsTransparentProxyAppCaptureSettingsOpen
    {
        get => _isTransparentProxyAppCaptureSettingsOpen;
        private set => SetProperty(ref _isTransparentProxyAppCaptureSettingsOpen, value);
    }

    public bool IsTransparentProxyProviderSettingsOpen
    {
        get => _isTransparentProxyProviderSettingsOpen;
        private set => SetProperty(ref _isTransparentProxyProviderSettingsOpen, value);
    }

    public bool IsTransparentProxyOAuthPanelOpen
    {
        get => _isTransparentProxyOAuthPanelOpen;
        private set => SetProperty(ref _isTransparentProxyOAuthPanelOpen, value);
    }

    public bool IsCodexOAuthLoginInProgress
    {
        get => _isCodexOAuthLoginInProgress;
        private set
        {
            if (SetProperty(ref _isCodexOAuthLoginInProgress, value))
            {
                StartCodexOAuthLoginCommand.RaiseCanExecuteChanged();
                CancelCodexOAuthLoginCommand.RaiseCanExecuteChanged();
                SubmitCodexOAuthCallbackCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsCodexOAuthManualCallbackVisible
    {
        get => _isCodexOAuthManualCallbackVisible;
        private set => SetProperty(ref _isCodexOAuthManualCallbackVisible, value);
    }

    public string CodexOAuthLoginStatusText
    {
        get => _codexOAuthLoginStatusText;
        private set => SetProperty(ref _codexOAuthLoginStatusText, value);
    }

    public string CodexOAuthLoginStepText
    {
        get => _codexOAuthLoginStepText;
        private set => SetProperty(ref _codexOAuthLoginStepText, value);
    }

    public string CodexOAuthManualCallbackText
    {
        get => _codexOAuthManualCallbackText;
        set => SetProperty(ref _codexOAuthManualCallbackText, value ?? string.Empty);
    }

    public string CodexOAuthLoginUrlText
    {
        get => _codexOAuthLoginUrlText;
        private set
        {
            if (SetProperty(ref _codexOAuthLoginUrlText, value))
            {
                CopyCodexOAuthLoginUrlCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public CodexOAuthCredentialViewModel? SelectedCodexOAuthCredential
    {
        get => _selectedCodexOAuthCredential;
        set => SetProperty(ref _selectedCodexOAuthCredential, value);
    }

    public bool HasCodexOAuthCredentials
        => CodexOAuthCredentials.Count > 0;

    public bool HasNoCodexOAuthCredentials
        => CodexOAuthCredentials.Count == 0;

    public bool IsTransparentProxyRouteSettingsOpen
    {
        get => _isTransparentProxyRouteSettingsOpen;
        private set => SetProperty(ref _isTransparentProxyRouteSettingsOpen, value);
    }

    public bool IsTransparentProxyLogExpanded
    {
        get => _isTransparentProxyLogExpanded;
        private set
        {
            if (SetProperty(ref _isTransparentProxyLogExpanded, value))
            {
                OnPropertyChanged(nameof(TransparentProxyLogExpandIcon));
                OnPropertyChanged(nameof(TransparentProxyLogExpandToolTip));
            }
        }
    }

    public string TransparentProxyLogExpandIcon
        => IsTransparentProxyLogExpanded ? "\uE73F" : "\uE740";

    public string TransparentProxyLogExpandToolTip
        => IsTransparentProxyLogExpanded ? "收起日志" : "展开日志";

    public string TransparentProxyLogSourceFilterKey
    {
        get => _transparentProxyLogSourceFilterKey;
        set
        {
            if (SetProperty(ref _transparentProxyLogSourceFilterKey, string.IsNullOrWhiteSpace(value) ? "all" : value))
            {
                RefreshTransparentProxyLogView();
            }
        }
    }

    public string TransparentProxyLogFilterText
    {
        get => _transparentProxyLogFilterText;
        set
        {
            if (SetProperty(ref _transparentProxyLogFilterText, value ?? string.Empty))
            {
                RefreshTransparentProxyLogView();
            }
        }
    }

    public TransparentProxyLogEntryViewModel? SelectedTransparentProxyLog
    {
        get => _selectedTransparentProxyLog;
        set
        {
            if (SetProperty(ref _selectedTransparentProxyLog, value))
            {
                IsTransparentProxyLogDetailOpen = value is not null;
            }
        }
    }

    public bool IsTransparentProxyLogDetailOpen
    {
        get => _isTransparentProxyLogDetailOpen;
        private set => SetProperty(ref _isTransparentProxyLogDetailOpen, value);
    }

    public TransparentProxyRouteEditorItemViewModel? TransparentProxyRouteSettingsItem
    {
        get => _transparentProxyRouteSettingsItem;
        private set => SetProperty(ref _transparentProxyRouteSettingsItem, value);
    }

    public bool IsTransparentProxyRunning
    {
        get => _isTransparentProxyRunning;
        private set
        {
            if (SetProperty(ref _isTransparentProxyRunning, value))
            {
                StartTransparentProxyCommand.RaiseCanExecuteChanged();
                StopTransparentProxyCommand.RaiseCanExecuteChanged();
                ApplyTransparentProxyToAppsCommand.RaiseCanExecuteChanged();
                OnPropertyChanged(nameof(TransparentProxyRunStateText));
                OnPropertyChanged(nameof(TransparentProxyRunStateBrush));
                NotifyTransparentProxyEndpointChanged();
            }
        }
    }

    public string TransparentProxyStatusSummary
    {
        get => _transparentProxyStatusSummary;
        private set => SetProperty(ref _transparentProxyStatusSummary, value);
    }

    public string TransparentProxyMetricsSummary
    {
        get => _transparentProxyMetricsSummary;
        private set => SetProperty(ref _transparentProxyMetricsSummary, value);
    }

    public string TransparentProxyIngressSummary
    {
        get => _transparentProxyIngressSummary;
        private set => SetProperty(ref _transparentProxyIngressSummary, value);
    }

    public string TransparentProxyRoutingSummary
    {
        get => _transparentProxyRoutingSummary;
        private set => SetProperty(ref _transparentProxyRoutingSummary, value);
    }

    public string TransparentProxyHealthSummary
    {
        get => _transparentProxyHealthSummary;
        private set => SetProperty(ref _transparentProxyHealthSummary, value);
    }

    public string TransparentProxyProtocolSummary
    {
        get => _transparentProxyProtocolSummary;
        private set => SetProperty(ref _transparentProxyProtocolSummary, value);
    }

    public string TransparentProxyCaptureDiagnosticsSummary
    {
        get => _transparentProxyCaptureDiagnosticsSummary;
        private set => SetProperty(ref _transparentProxyCaptureDiagnosticsSummary, value);
    }

    public string TransparentProxyCodexPreviewText
    {
        get => _transparentProxyCodexPreviewText;
        private set => SetProperty(ref _transparentProxyCodexPreviewText, value);
    }

    public string TransparentProxyCodexConfigPath
    {
        get => _transparentProxyCodexConfigPath;
        private set => SetProperty(ref _transparentProxyCodexConfigPath, value);
    }

    public string TransparentProxyCodexStatusText
    {
        get => _transparentProxyCodexStatusText;
        private set => SetProperty(ref _transparentProxyCodexStatusText, value);
    }

    public string TransparentProxyClaudePreviewText
    {
        get => _transparentProxyClaudePreviewText;
        private set => SetProperty(ref _transparentProxyClaudePreviewText, value);
    }

    public string TransparentProxyClaudeConfigPath
    {
        get => _transparentProxyClaudeConfigPath;
        private set => SetProperty(ref _transparentProxyClaudeConfigPath, value);
    }

    public string TransparentProxyClaudeStatusText
    {
        get => _transparentProxyClaudeStatusText;
        private set => SetProperty(ref _transparentProxyClaudeStatusText, value);
    }

    public string TransparentProxyVsCodePreviewText
    {
        get => _transparentProxyVsCodePreviewText;
        private set => SetProperty(ref _transparentProxyVsCodePreviewText, value);
    }

    public string TransparentProxyVsCodeConfigPath
    {
        get => _transparentProxyVsCodeConfigPath;
        private set => SetProperty(ref _transparentProxyVsCodeConfigPath, value);
    }

    public string TransparentProxyVsCodeStatusText
    {
        get => _transparentProxyVsCodeStatusText;
        private set => SetProperty(ref _transparentProxyVsCodeStatusText, value);
    }

    public string TransparentProxyVsCodeSettingsScopeKey
    {
        get => _transparentProxyVsCodeSettingsScopeKey;
        set
        {
            var normalized = TransparentProxyVsCodeSettingsScopes.NormalizeKey(value);
            if (SetProperty(ref _transparentProxyVsCodeSettingsScopeKey, normalized))
            {
                OnPropertyChanged(nameof(TransparentProxyVsCodeSettingsScopeToolTip));
                TransparentProxyVsCodeStatusText =
                    $"VS Code 接管范围已切换为 {TransparentProxyVsCodeSettingsScopes.GetDisplayName(GetTransparentProxyVsCodeSettingsScope())}，点击预览查看目标文件。";
            }
        }
    }

    public string TransparentProxyVsCodeSettingsScopeToolTip
        => GetTransparentProxyVsCodeSettingsScope() is TransparentProxyVsCodeSettingsScope.User
            ? "写入 VS Code 用户 settings.json，影响新开的 VS Code 集成终端。"
            : $"工作区目录：{GetTransparentProxyVsCodeWorkspaceDirectory()}";

    public string TransparentProxyLaunchWrapperPreviewText
    {
        get => _transparentProxyLaunchWrapperPreviewText;
        private set => SetProperty(ref _transparentProxyLaunchWrapperPreviewText, value);
    }

    public string TransparentProxyLaunchWrapperPathText
    {
        get => _transparentProxyLaunchWrapperPathText;
        private set => SetProperty(ref _transparentProxyLaunchWrapperPathText, value);
    }

    public string TransparentProxyLaunchWrapperStatusText
    {
        get => _transparentProxyLaunchWrapperStatusText;
        private set => SetProperty(ref _transparentProxyLaunchWrapperStatusText, value);
    }

    public string TransparentProxyTotalRequestsText
    {
        get => _transparentProxyTotalRequestsText;
        private set => SetProperty(ref _transparentProxyTotalRequestsText, value);
    }

    public string TransparentProxySuccessRateText
    {
        get => _transparentProxySuccessRateText;
        private set => SetProperty(ref _transparentProxySuccessRateText, value);
    }

    public string TransparentProxyActiveRequestsText
    {
        get => _transparentProxyActiveRequestsText;
        private set => SetProperty(ref _transparentProxyActiveRequestsText, value);
    }

    public string TransparentProxyFallbackRequestsText
    {
        get => _transparentProxyFallbackRequestsText;
        private set => SetProperty(ref _transparentProxyFallbackRequestsText, value);
    }

    public string TransparentProxyCacheHitsText
    {
        get => _transparentProxyCacheHitsText;
        private set => SetProperty(ref _transparentProxyCacheHitsText, value);
    }

    public string TransparentProxyCacheEfficiencyToolTip
    {
        get => _transparentProxyCacheEfficiencyToolTip;
        private set => SetProperty(ref _transparentProxyCacheEfficiencyToolTip, value);
    }

    public string TransparentProxyP95LatencyText
    {
        get => _transparentProxyP95LatencyText;
        private set => SetProperty(ref _transparentProxyP95LatencyText, value);
    }

    public string TransparentProxyTotalTokensText
    {
        get => _transparentProxyTotalTokensText;
        private set => SetProperty(ref _transparentProxyTotalTokensText, value);
    }

    public string TransparentProxyTokensPerSecondText
    {
        get => _transparentProxyTokensPerSecondText;
        private set => SetProperty(ref _transparentProxyTokensPerSecondText, value);
    }

    public string TransparentProxyTokenMeterPrimaryText
    {
        get => _transparentProxyTokenMeterPrimaryText;
        private set => SetProperty(ref _transparentProxyTokenMeterPrimaryText, value);
    }

    public string TransparentProxyTokenMeterSecondaryText
    {
        get => _transparentProxyTokenMeterSecondaryText;
        private set => SetProperty(ref _transparentProxyTokenMeterSecondaryText, value);
    }

    public string TransparentProxyTokenMeterAccentBrush
    {
        get => _transparentProxyTokenMeterAccentBrush;
        private set => SetProperty(ref _transparentProxyTokenMeterAccentBrush, value);
    }

    public string TransparentProxyCopyEndpointButtonText
    {
        get => _transparentProxyCopyEndpointButtonText;
        private set => SetProperty(ref _transparentProxyCopyEndpointButtonText, value);
    }

    public string TransparentProxyCopyPowerShellButtonText
    {
        get => _transparentProxyCopyPowerShellButtonText;
        private set => SetProperty(ref _transparentProxyCopyPowerShellButtonText, value);
    }

    public string TransparentProxyCopyCmdButtonText
    {
        get => _transparentProxyCopyCmdButtonText;
        private set => SetProperty(ref _transparentProxyCopyCmdButtonText, value);
    }

    public string TransparentProxyHealthTestButtonText
    {
        get => _transparentProxyHealthTestButtonText;
        private set => SetProperty(ref _transparentProxyHealthTestButtonText, value);
    }

    public string TransparentProxyLocalEndpoint
        => $"http://127.0.0.1:{ParseTransparentProxyPort()}/v1";

    public string TransparentProxyOpenAiChatEndpoint
        => $"{TransparentProxyLocalEndpoint}/chat/completions";

    public string TransparentProxyResponsesEndpoint
        => $"{TransparentProxyLocalEndpoint}/responses";

    public string TransparentProxyAnthropicEndpoint
        => $"http://127.0.0.1:{ParseTransparentProxyPort()}/v1/messages";

    public string TransparentProxyModelsEndpoint
        => $"{TransparentProxyLocalEndpoint}/models";

    public string TransparentProxyHealthEndpoint
        => $"http://127.0.0.1:{ParseTransparentProxyPort()}/relaybench/health";

    public string TransparentProxyPowerShellEnvSnippet
        => _transparentProxyCliEnvironmentService.Build(ParseTransparentProxyPort()).PowerShell;

    public string TransparentProxyCmdEnvSnippet
        => _transparentProxyCliEnvironmentService.Build(ParseTransparentProxyPort()).Cmd;

    public string TransparentProxyUnifiedEndpointSummary
        => TransparentProxyStartUnifiedEndpointOnLaunch
            ? "启动 RelayBench 时自动打开本地统一出口；本地 agent 可直接接入，应用接管关闭也不影响。"
            : "本地统一出口不会随启动自动打开；需要手动启动后应用和本地 agent 才能接入。";

    public string TransparentProxyAppCaptureSummary
        => TransparentProxyEnableAppCapture
            ? "AI 应用接管已启用：后续将按应用配置接入统一出口，不默认改系统代理。"
            : "AI 应用接管未启用：Codex、VS Codex、Codex CLI、Claude CLI 暂不自动改配置。";

    public string TransparentProxyAppCaptureSafetySummary
        => "默认只做显式 Base URL/配置接入；不会修改系统代理、浏览器访问路径、GitHub、npm、插件市场或普通网络。";

    public string TransparentProxyRunStateText
        => _isTransparentProxyStarting
            ? "启动中"
            : IsTransparentProxyRunning
                ? "统一出口运行中"
                : "统一出口已停止";

    public string TransparentProxyRunStateBrush
        => _isTransparentProxyStarting
            ? "#D97706"
            : IsTransparentProxyRunning
                ? "#059669"
                : "#64748B";

    private bool CanStartTransparentProxy()
        => !IsTransparentProxyRunning && !IsBusy && !_isTransparentProxyStarting;

    private IReadOnlyList<TransparentProxyRoute> BuildTransparentProxyRuntimeRoutes(bool includeWorkspaceFallback = false)
    {
        var providerRoutes = ParseTransparentProxyProviderRoutes(TransparentProxyRoutesText);
        var routes = ComposeTransparentProxyRuntimeRoutes(providerRoutes);
        if (routes.Count > 0 || !includeWorkspaceFallback)
        {
            return routes;
        }

        return ComposeTransparentProxyRuntimeRoutes(ParseTransparentProxyProviderRoutes(BuildTransparentProxyRoutesTextFromWorkspace()));
    }

    private IReadOnlyList<TransparentProxyRoute> ComposeTransparentProxyRuntimeRoutes(IReadOnlyList<TransparentProxyRoute> providerRoutes)
    {
        List<TransparentProxyRoute> routes = providerRoutes
            .Where(static route => !route.IsCodexOAuth)
            .Select(ApplyKnownTransparentProxyRouteProtocol)
            .ToList();

        var existingCredentialIds = new HashSet<string>(
            routes
                .Where(static route => route.IsCodexOAuth)
                .Select(static route => route.OAuthCredentialId)
                .Where(static id => !string.IsNullOrWhiteSpace(id)),
            StringComparer.OrdinalIgnoreCase);

        foreach (var credential in _codexOAuthService.GetCredentials()
                     .Where(IsUsableCodexOAuthCredential)
                     .OrderBy(static credential => credential.PlanType, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(static credential => credential.Email, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(static credential => credential.Id, StringComparer.OrdinalIgnoreCase))
        {
            if (!existingCredentialIds.Add(credential.Id))
            {
                continue;
            }

            routes.Add(BuildCodexOAuthRuntimeRoute(credential));
        }

        return routes;
    }

    private IReadOnlyList<TransparentProxyRoute> ParseTransparentProxyProviderRoutes(string text)
        => TransparentProxyRouteTextCodec.ParseRoutes(text)
            .Where(static route => !route.IsCodexOAuth)
            .ToArray();

    private TransparentProxyRoute ApplyKnownTransparentProxyRouteProtocol(TransparentProxyRoute route)
        => route.IsCodexOAuth
            ? ApplyCodexOAuthRouteProtocol(route)
            : _transparentProxyProtocolSnapshots.TryGetValue(route.Id, out var snapshot)
                ? route.WithProtocol(
                    snapshot.PreferredWireApi,
                    snapshot.ChatCompletionsSupported,
                    snapshot.ResponsesSupported,
                    snapshot.AnthropicMessagesSupported,
                    snapshot.CheckedAt)
                : route;

    private static TransparentProxyRoute ApplyCodexOAuthRouteProtocol(TransparentProxyRoute route)
        => route.WithProtocol(
            ProxyWireApiProbeService.ResponsesWireApi,
            chatCompletionsSupported: true,
            responsesSupported: true,
            anthropicMessagesSupported: true,
            DateTimeOffset.UtcNow);

    private static TransparentProxyRoute BuildCodexOAuthRuntimeRoute(CodexOAuthCredential credential)
    {
        string[] models =
        [
            "gpt-5.4",
            "gpt-5.4-codex",
            "gpt-5.4-mini"
        ];
        var mappings = models
            .Select(static model => new TransparentProxyModelMapping(model, model))
            .ToArray();
        var id = TransparentProxyRouteTextCodec.BuildRouteId(
            "Codex OAuth",
            CodexOAuthConstants.DefaultBackendBaseUrl,
            credential.Id);
        var plan = string.IsNullOrWhiteSpace(credential.PlanType) ? "Codex" : credential.PlanType.Trim();
        var displayName = string.IsNullOrWhiteSpace(credential.DisplayName) ? credential.Id : credential.DisplayName;

        return new TransparentProxyRoute(
            id,
            $"Codex OAuth {plan} {displayName}".Trim(),
            CodexOAuthConstants.DefaultBackendBaseUrl,
            string.Empty,
            models[0],
            ProxyWireApiProbeService.ResponsesWireApi,
            chatCompletionsSupported: true,
            responsesSupported: true,
            anthropicMessagesSupported: true,
            DateTimeOffset.UtcNow,
            models,
            priority: 0,
            modelMappings: mappings,
            authMode: TransparentProxyRouteAuthModes.CodexOAuth,
            oauthProvider: CodexOAuthConstants.Provider,
            oauthCredentialId: credential.Id,
            codexBackendBaseUrl: CodexOAuthConstants.DefaultBackendBaseUrl);
    }

    private static bool IsUsableCodexOAuthCredential(CodexOAuthCredential credential)
        => string.Equals(credential.Provider, CodexOAuthConstants.Provider, StringComparison.OrdinalIgnoreCase) &&
           credential.State is not CodexOAuthCredentialState.Disabled and not CodexOAuthCredentialState.NeedsRelogin &&
           (!string.IsNullOrWhiteSpace(credential.RefreshToken) || !string.IsNullOrWhiteSpace(credential.AccessToken));

    private void RefreshTransparentProxyRuntimeRoutesAfterOAuthChange()
    {
        RefreshTransparentProxyRoutePreview();
        if (IsTransparentProxyRunning)
        {
            _transparentProxyService.UpdateRoutes(BuildTransparentProxyRuntimeRoutes());
        }
    }

    private async Task StartTransparentProxyAsync()
        => await StartTransparentProxyCoreAsync(isAutoStart: false);

    private async Task StartTransparentProxyCoreAsync(bool isAutoStart)
    {
        if (_isTransparentProxyStarting || IsTransparentProxyRunning)
        {
            return;
        }

        _isTransparentProxyStarting = true;
        StartTransparentProxyCommand.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(TransparentProxyRunStateText));
        OnPropertyChanged(nameof(TransparentProxyRunStateBrush));
        var routes = BuildTransparentProxyRuntimeRoutes();
        try
        {
            if (routes.Count == 0)
            {
                TransparentProxyRoutesText = BuildTransparentProxyRoutesTextFromWorkspace();
                routes = BuildTransparentProxyRuntimeRoutes();
            }

            if (routes.Count > 0 && !isAutoStart)
            {
                routes = await ResolveTransparentProxyRouteProtocolsAsync(
                    routes,
                    forceProbe: false,
                    fetchCatalogModels: false,
                    CancellationToken.None);
            }

            TransparentProxyServerConfig config = new(
                ParseTransparentProxyPort(),
                routes,
                ParseBoundedInt(TransparentProxyRateLimitPerMinuteText, fallback: 60, min: 0, max: 6000),
                ParseBoundedInt(TransparentProxyMaxConcurrencyText, fallback: 8, min: 1, max: 128),
                TransparentProxyEnableFallback,
                TransparentProxyEnableCache,
                ParseBoundedInt(TransparentProxyCacheTtlSecondsText, fallback: 60, min: 1, max: 3600),
                false,
                ProxyIgnoreTlsErrors,
                ParseBoundedInt(ProxyTimeoutSecondsText, fallback: 20, min: 5, max: 300))
            {
                RouteStrategy = TransparentProxyRouteStrategyKey,
                RequestRetry = ParseBoundedInt(TransparentProxyRequestRetryText, fallback: 1, min: 0, max: 5),
                MaxRetryIntervalSeconds = ParseBoundedInt(TransparentProxyMaxRetryIntervalSecondsText, fallback: 8, min: 1, max: 60),
                SessionAffinityTtlSeconds = ParseBoundedInt(TransparentProxySessionAffinityTtlSecondsText, fallback: 1800, min: 30, max: 86400),
                ModelCooldownSeconds = ParseBoundedInt(TransparentProxyModelCooldownSecondsText, fallback: 120, min: 15, max: 1800)
            };

            await _transparentProxyService.StartAsync(config);
            IsTransparentProxyRunning = true;
            TransparentProxyStatusSummary = routes.Count > 0
                ? $"本地统一出口已监听：{TransparentProxyLocalEndpoint}"
                : $"本地统一出口已监听：{TransparentProxyLocalEndpoint}，当前还没有配置上游路由。";
            TransparentProxyHealthSummary = $"健康检查：{TransparentProxyHealthEndpoint}";
            RefreshTransparentProxyRoutePreview();
            if (routes.Count > 0)
            {
                BeginTransparentProxyProtocolAutoDiscovery(routes);
            }

            SaveState();
        }
        catch (Exception ex)
        {
            AppDiagnosticLog.Write(isAutoStart ? "TransparentProxy.AutoStart" : "TransparentProxy.Start", ex);
            TransparentProxyStatusSummary = BuildTransparentProxyStartFailureMessage(ex, isAutoStart);
            TransparentProxyHealthSummary = "健康检查：未监听。";
        }
        finally
        {
            _isTransparentProxyStarting = false;
            StartTransparentProxyCommand.RaiseCanExecuteChanged();
            OnPropertyChanged(nameof(TransparentProxyRunStateText));
            OnPropertyChanged(nameof(TransparentProxyRunStateBrush));
        }
    }

    private string BuildTransparentProxyStartFailureMessage(Exception ex, bool isAutoStart)
    {
        var port = ParseTransparentProxyPort();
        var prefix = isAutoStart ? "本地统一出口自动启动失败" : "本地统一出口启动失败";
        if (!IsTransparentProxyPortListenFailure(ex))
        {
            return $"{prefix}：{ex.Message}";
        }

        var inspection = _transparentProxyPortInspectorService.Inspect(port);
        return inspection.IsListening
            ? $"{prefix}：{inspection.Summary} 请换端口或关闭占用进程后重试。"
            : $"{prefix}：端口 {port} 无法监听。{inspection.Summary}";
    }

    private static bool IsTransparentProxyPortListenFailure(Exception ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current is System.Net.HttpListenerException)
            {
                return true;
            }

            if (current.Message.Contains("Failed to listen", StringComparison.OrdinalIgnoreCase) ||
                current.Message.Contains("already in use", StringComparison.OrdinalIgnoreCase) ||
                current.Message.Contains("正在使用", StringComparison.OrdinalIgnoreCase) ||
                current.Message.Contains("访问被拒绝", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private async Task StopTransparentProxyAsync()
    {
        CancelTransparentProxyProtocolAutoDiscovery();
        await _transparentProxyService.StopAsync();
        IsTransparentProxyRunning = false;
        TransparentProxyStatusSummary = "本地统一出口已停止。";
        TransparentProxyHealthSummary = "健康检查：未监听。";
    }

    private void StartTransparentProxyUnifiedEndpointOnLaunch()
    {
        if (!TransparentProxyStartUnifiedEndpointOnLaunch)
        {
            TransparentProxyStatusSummary = "本地统一出口未自动启动，可在透明代理页手动开启。";
            return;
        }

        _ = StartTransparentProxyUnifiedEndpointOnLaunchAsync();
    }

    private async Task StartTransparentProxyUnifiedEndpointOnLaunchAsync()
    {
        await Task.Delay(400);
        if (IsTransparentProxyRunning || _isTransparentProxyStarting)
        {
            return;
        }

        await StartTransparentProxyCoreAsync(isAutoStart: true);
    }

    public async Task StopTransparentProxyForExitAsync()
    {
        if (!IsTransparentProxyRunning)
        {
            return;
        }

        await StopTransparentProxyAsync();
    }

    private bool CanApplyTransparentProxyToApps()
        => !IsBusy;

    private async Task ApplyTransparentProxyToAppsAsync()
    {
        if (!IsTransparentProxyRunning)
        {
            await StartTransparentProxyAsync();
        }

        var routes = BuildTransparentProxyRuntimeRoutes();
        if (routes.Count == 0)
        {
            StatusMessage = "透明代理还没有可用路由，暂时不能应用到软件。";
            return;
        }

        var model = ResolveTransparentProxyClientDefaultModel(routes);
        var apiKey = ResolveTransparentProxyClientApiKey(routes);
        var settings = BuildProxySettings(TransparentProxyLocalEndpoint, apiKey, model);
        var protocolProbeResult = BuildTransparentProxyClientApplyProbeResult(routes, settings);
        var selectedTargets = await ChooseClientApplyTargetsAsync(
            "应用本地透明代理到软件",
            settings,
            protocolProbeResult);
        if (selectedTargets.Count == 0)
        {
            StatusMessage = "已取消本地透明代理应用。";
            return;
        }

        await ExecuteBusyActionAsync(
            "正在应用本地透明代理到软件...",
            async () =>
            {
                var endpoint = new ClientApplyEndpoint(
                    settings.BaseUrl,
                    settings.ApiKey,
                    settings.Model,
                    "RelayBench Transparent Proxy",
                    null,
                    protocolProbeResult.PreferredWireApi);
                var anthropicEndpoint = BuildLocalClaudeEndpointIfSelected(settings.Model, selectedTargets);
                var result = await _clientAppConfigApplyService.ApplyAsync(
                    endpoint,
                    selectedTargets,
                    anthropicEndpoint);

                CodexChatMergeResult? mergeResult = null;
                if (ShouldAskCodexChatMerge(selectedTargets, result))
                {
                    var shouldMergeChats = await ConfirmCodexChatMergeAsync(
                        CodexChatMergeTarget.ThirdPartyCustom,
                        "切到 RelayBench 本地透明代理");
                    mergeResult = await MergeCodexChatsIfRequestedAsync(
                        shouldMergeChats,
                        CodexChatMergeTarget.ThirdPartyCustom);
                }

                AppendModuleOutput(
                    "本地透明代理应用到软件",
                    BuildClientApplyResultSummary(result, mergeResult),
                    BuildClientApplyResultDetail(
                        "本地透明代理",
                        settings.BaseUrl,
                        settings.ApiKey,
                        settings.Model,
                        result,
                        mergeResult));

                StatusMessage = BuildClientApplyStatusMessage(result, mergeResult);
                if (HasSucceededTarget(result))
                {
                    SaveState();
                    await RunClientApiDiagnosticsCoreAsync();
                }
            });
    }

    private bool CanApplyTransparentProxyToAdvancedLab()
        => !IsBusy;

    private async Task ApplyTransparentProxyToAdvancedLabAsync()
    {
        if (!IsTransparentProxyRunning)
        {
            await StartTransparentProxyAsync();
        }

        var routes = BuildTransparentProxyRuntimeRoutes();
        if (routes.Count == 0)
        {
            StatusMessage = "透明代理还没有可用路由，暂时不能接入数据安全。";
            return;
        }

        var model = ResolveTransparentProxyClientDefaultModel(routes);
        var apiKey = ResolveTransparentProxyClientApiKey(routes);
        var models = ResolveTransparentProxyClientModels(routes);
        var context = BuildTransparentProxyAdvancedLabContext(routes, model);
        AdvancedTestLab.ApplyTransparentProxyEndpoint(
            TransparentProxyLocalEndpoint,
            apiKey,
            models,
            model,
            context);
        StatusMessage = $"数据安全已接入本地透明代理：{model}。";
        AppendHistory("数据安全", "接入透明代理模型池", $"{TransparentProxyLocalEndpoint} · {model} · {context}");
        SaveState();
    }

    private Task RefreshTransparentProxyRoutesFromWorkspaceAsync()
    {
        TransparentProxyRoutesText = BuildTransparentProxyRoutesTextFromWorkspace();
        RefreshTransparentProxyRouteEditorItemsFromText();
        SaveState();
        return Task.CompletedTask;
    }

    private Task ImportProxyBatchToTransparentProxyAsync()
    {
        TransparentProxyRoutesText = BuildTransparentProxyRoutesTextFromWorkspace();
        RefreshTransparentProxyRouteEditorItemsFromText();
        IsTransparentProxySettingsDrawerOpen = true;
        IsTransparentProxyProviderSettingsOpen = true;
        IsTransparentProxyListenSettingsOpen = false;
        IsTransparentProxyAppCaptureSettingsOpen = false;
        IsTransparentProxyOAuthPanelOpen = false;
        TransparentProxyStatusSummary = $"已从当前接口和批量入口组生成 {TransparentProxyRouteEditorItems.Count} 个透明代理节点。";
        SaveState();
        return Task.CompletedTask;
    }

    private Task ExportTransparentProxyRoutesToBatchAsync()
    {
        var routes = ParseTransparentProxyProviderRoutes(TransparentProxyRoutesText);
        if (routes.Count == 0)
        {
            TransparentProxyStatusSummary = "没有可导出的透明代理节点。";
            return Task.CompletedTask;
        }

        var added = 0;
        var updated = 0;
        foreach (var route in routes)
        {
            var model = ResolveBatchModelFromTransparentProxyRoute(route);
            var existing = ProxyBatchEditorItems.FirstOrDefault(item =>
                string.Equals(item.BaseUrl.Trim(), route.BaseUrl.Trim(), StringComparison.OrdinalIgnoreCase) &&
                string.Equals((item.EntryModel ?? item.SiteGroupModel ?? string.Empty).Trim(), model, StringComparison.OrdinalIgnoreCase));
            var next = new ProxyBatchEditorItemViewModel(
                string.IsNullOrWhiteSpace(route.Name) ? BuildBatchDefaultName(route.BaseUrl, ProxyBatchEditorItems.Count + 1) : route.Name.Trim(),
                route.BaseUrl.Trim(),
                route.ApiKey.Trim(),
                model,
                "透明代理",
                null,
                null,
                includeInBatchTest: true);

            if (existing is null)
            {
                AttachProxyBatchEditorItem(next);
                ProxyBatchEditorItems.Add(next);
                added++;
            }
            else
            {
                existing.ApplyFrom(next);
                updated++;
            }
        }

        RefreshProxyBatchSiteGroups();
        RefreshProxyBatchEditorCollectionState();
        RebuildProxyBatchTargetsTextFromEditorItems();
        TransparentProxyStatusSummary = $"已导出到批量入口组：新增 {added} 个，更新 {updated} 个。";
        StatusMessage = TransparentProxyStatusSummary;
        SaveState();
        return Task.CompletedTask;
    }

    private Task AddTransparentProxyRouteEditorItemAsync()
    {
        var index = TransparentProxyRouteEditorItems.Count + 1;
        var item = new TransparentProxyRouteEditorItemViewModel
        {
            IsEnabled = true,
            Name = $"路由 {index}",
            BaseUrl = string.Empty,
            ModelsText = string.Empty,
            ApiKey = string.Empty
        };
        AttachTransparentProxyRouteEditorItem(item);
        TransparentProxyRouteEditorItems.Add(item);
        SelectedTransparentProxyRouteEditorItem = item;
        UpdateTransparentProxyRoutesTextFromEditor();
        return OpenTransparentProxyRouteSettingsAsync(item);
    }

    private Task ToggleTransparentProxySettingsDrawerAsync()
    {
        IsTransparentProxySettingsDrawerOpen = !IsTransparentProxySettingsDrawerOpen;
        if (!IsTransparentProxySettingsDrawerOpen)
        {
            IsTransparentProxyListenSettingsOpen = false;
            IsTransparentProxyAppCaptureSettingsOpen = false;
            IsTransparentProxyProviderSettingsOpen = false;
            IsTransparentProxyOAuthPanelOpen = false;
        }

        return Task.CompletedTask;
    }

    private Task ToggleTransparentProxyListenSettingsAsync()
    {
        var shouldOpen = !IsTransparentProxyListenSettingsOpen;
        IsTransparentProxyListenSettingsOpen = shouldOpen;
        if (shouldOpen)
        {
            IsTransparentProxyAppCaptureSettingsOpen = false;
            IsTransparentProxyProviderSettingsOpen = false;
            IsTransparentProxyOAuthPanelOpen = false;
        }

        return Task.CompletedTask;
    }

    private Task ToggleTransparentProxyAppCaptureSettingsAsync()
    {
        var shouldOpen = !IsTransparentProxyAppCaptureSettingsOpen;
        IsTransparentProxyAppCaptureSettingsOpen = shouldOpen;
        if (shouldOpen)
        {
            IsTransparentProxyListenSettingsOpen = false;
            IsTransparentProxyProviderSettingsOpen = false;
            IsTransparentProxyOAuthPanelOpen = false;
            RefreshTransparentProxyCaptureTargets();
            RefreshTransparentProxyCaptureDiagnostics();
        }

        return Task.CompletedTask;
    }

    private Task RefreshTransparentProxyCaptureTargetsAsync()
    {
        RefreshTransparentProxyCaptureTargets();
        RefreshTransparentProxyCaptureDiagnostics();
        TransparentProxyStatusSummary = "已刷新 AI 应用接管检测状态。";
        StatusMessage = TransparentProxyCaptureDiagnosticsSummary;
        return Task.CompletedTask;
    }

    private Task RefreshTransparentProxyCaptureDiagnosticsAsync()
    {
        RefreshTransparentProxyCaptureTargets();
        RefreshTransparentProxyCaptureDiagnostics();
        TransparentProxyStatusSummary = TransparentProxyCaptureDiagnosticsSummary;
        StatusMessage = TransparentProxyCaptureDiagnosticsSummary;
        return Task.CompletedTask;
    }

    private void RefreshTransparentProxyCaptureTargets()
    {
        var apps = _transparentProxyAppDetectorService.Detect();
        var artifacts = _transparentProxyCaptureArtifactStore.ScanDefaultArtifacts();
        TransparentProxyCaptureTargets.Clear();
        foreach (var app in apps)
        {
            var target = new TransparentProxyCaptureTargetViewModel(app);
            target.ApplyArtifact(FindTransparentProxyCaptureArtifact(app, artifacts));
            TransparentProxyCaptureTargets.Add(target);
        }

        ApplyTransparentProxyCaptureTargetMetrics(_latestTransparentProxyMetricsSnapshot);
    }

    private static TransparentProxyCaptureArtifactSnapshot? FindTransparentProxyCaptureArtifact(
        TransparentProxyDetectedApp app,
        IReadOnlyList<TransparentProxyCaptureArtifactSnapshot> artifacts)
        => artifacts.FirstOrDefault(artifact =>
               string.Equals(artifact.TargetId, app.Id, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(artifact.Path, app.ConfigPath, StringComparison.OrdinalIgnoreCase)) ??
           artifacts.FirstOrDefault(artifact =>
               app.Id.Contains("codex", StringComparison.OrdinalIgnoreCase) &&
               artifact.TargetId.Contains("codex", StringComparison.OrdinalIgnoreCase));

    private void RefreshTransparentProxyCaptureDiagnostics()
    {
        var apps = _transparentProxyAppDetectorService.Detect();
        var artifacts = _transparentProxyCaptureArtifactStore.ScanDefaultArtifacts();
        var launcherCount = _transparentProxyLaunchWrapperService
            .ScanKnownLaunchers()
            .Sum(static item => item.ExistingCount);
        var detectedCount = apps.Count(static app =>
            !app.Status.Contains("未检测到", StringComparison.OrdinalIgnoreCase));
        var configCount = apps.Count(static app =>
            !string.IsNullOrWhiteSpace(app.ConfigPath) &&
            System.IO.File.Exists(app.ConfigPath));
        var restorePointCount = artifacts.Count(static artifact => artifact.BackupCount > 0);
        var unifiedText = IsTransparentProxyRunning
            ? $"统一出口 {TransparentProxyLocalEndpoint}"
            : "统一出口未运行";

        TransparentProxyCaptureDiagnosticsSummary =
            $"接管诊断：{unifiedText}；目标 {apps.Count} 个，已检测 {detectedCount} 个，配置文件 {configCount} 个，恢复点 {restorePointCount} 个，临时启动器 {launcherCount} 个。";
    }

    private void ApplyTransparentProxyCaptureTargetMetrics(TransparentProxyMetricsSnapshot? snapshot)
    {
        foreach (var target in TransparentProxyCaptureTargets)
        {
            target.ApplyMetrics(snapshot?.Ingresses);
        }
    }

    private bool CanConfigureTransparentProxyCodexCapture()
        => !IsBusy;

    private Task PreviewTransparentProxyCodexCaptureAsync()
        => ExecuteBusyActionAsync(
            "正在生成 Codex CLI 接管预览...",
            async () =>
            {
                UpdateGlobalTaskProgress("生成预览", 42d);
                var context = BuildTransparentProxyCodexCaptureContext();
                var preview = _transparentProxyCodexConfigService.Preview(
                    context.BaseUrl,
                    context.Model,
                    context.WireApi);
                ApplyTransparentProxyCodexPreview(preview);
                TransparentProxyCodexStatusText = preview.Changed
                    ? "预览已生成：写入前会备份现有 config.toml，只更新 RelayBench provider。"
                    : "预览已生成：当前 Codex CLI 配置已经指向 RelayBench。";
                TransparentProxyStatusSummary = TransparentProxyCodexStatusText;
                StatusMessage = TransparentProxyCodexStatusText;
                RefreshTransparentProxyCaptureTargets();
                await Task.CompletedTask;
            },
            "Codex CLI 接管",
            "预览中",
            18d);

    private Task ApplyTransparentProxyCodexCaptureAsync()
        => ExecuteBusyActionAsync(
            "正在写入 Codex CLI 接管配置...",
            async () =>
            {
                UpdateGlobalTaskProgress("启动统一出口", 24d);
                if (!IsTransparentProxyRunning)
                {
                    await StartTransparentProxyCoreAsync(isAutoStart: false);
                }

                if (!IsTransparentProxyRunning)
                {
                    throw new InvalidOperationException("本地统一出口未能启动，已取消写入 Codex CLI 配置。");
                }

                UpdateGlobalTaskProgress("写入 Codex 配置", 58d);
                var context = BuildTransparentProxyCodexCaptureContext();
                var result = _transparentProxyCodexConfigService.Apply(
                    context.BaseUrl,
                    context.Model,
                    context.WireApi);
                var preview = _transparentProxyCodexConfigService.Preview(
                    context.BaseUrl,
                    context.Model,
                    context.WireApi);
                ApplyTransparentProxyCodexPreview(preview);

                TransparentProxyEnableAppCapture = true;
                TransparentProxyCodexStatusText = string.IsNullOrWhiteSpace(result.BackupPath)
                    ? result.Summary
                    : $"{result.Summary} 备份：{result.BackupPath}";
                TransparentProxyCodexStatusText += await BuildTransparentProxyPostApplyHealthTextAsync();
                TransparentProxyStatusSummary = result.Summary;
                StatusMessage = result.Summary;
                RefreshTransparentProxyCaptureTargets();
                SaveState();
            },
            "Codex CLI 接管",
            "写入中",
            16d);

    private Task RestoreTransparentProxyCodexCaptureAsync()
        => ExecuteBusyActionAsync(
            "正在恢复 Codex CLI 接管配置...",
            async () =>
            {
                UpdateGlobalTaskProgress("恢复配置", 48d);
                var result = _transparentProxyCodexConfigService.RestoreLatestBackup();
                var context = BuildTransparentProxyCodexCaptureContext();
                var preview = _transparentProxyCodexConfigService.Preview(
                    context.BaseUrl,
                    context.Model,
                    context.WireApi);
                ApplyTransparentProxyCodexPreview(preview);
                TransparentProxyCodexStatusText = string.IsNullOrWhiteSpace(result.BackupPath)
                    ? result.Summary
                    : $"{result.Summary} 来源：{result.BackupPath}";
                TransparentProxyStatusSummary = result.Summary;
                StatusMessage = result.Summary;
                RefreshTransparentProxyCaptureTargets();
                await Task.CompletedTask;
            },
            "Codex CLI 接管",
            "恢复中",
            18d);

    private bool CanConfigureTransparentProxyLauncher()
        => !IsBusy;

    private Task PreviewTransparentProxyCodexLauncherAsync()
        => PreviewTransparentProxyLauncherAsync("codex-cli", "Codex CLI", "codex");

    private Task WriteTransparentProxyCodexLauncherAsync()
        => WriteTransparentProxyLauncherAsync("codex-cli", "Codex CLI", "codex");

    private Task PreviewTransparentProxyClaudeLauncherAsync()
        => PreviewTransparentProxyLauncherAsync("claude-cli", "Claude CLI", "claude");

    private Task WriteTransparentProxyClaudeLauncherAsync()
        => WriteTransparentProxyLauncherAsync("claude-cli", "Claude CLI", "claude");

    private Task PreviewTransparentProxyLauncherAsync(string id, string displayName, string command)
        => ExecuteBusyActionAsync(
            $"正在生成 {displayName} 临时启动器预览...",
            async () =>
            {
                UpdateGlobalTaskProgress("生成启动器预览", 42d);
                var preview = _transparentProxyLaunchWrapperService.Preview(
                    id,
                    displayName,
                    command,
                    ParseTransparentProxyPort());
                ApplyTransparentProxyLaunchWrapperPreview(preview);
                TransparentProxyLaunchWrapperStatusText =
                    $"{displayName} 临时启动器预览已生成；使用时请保持统一出口运行。";
                TransparentProxyStatusSummary = TransparentProxyLaunchWrapperStatusText;
                StatusMessage = TransparentProxyLaunchWrapperStatusText;
                await Task.CompletedTask;
            },
            $"{displayName} 临时启动器",
            "预览中",
            18d);

    private Task WriteTransparentProxyLauncherAsync(string id, string displayName, string command)
        => ExecuteBusyActionAsync(
            $"正在生成 {displayName} 临时启动器...",
            async () =>
            {
                UpdateGlobalTaskProgress("写入启动器", 48d);
                var result = _transparentProxyLaunchWrapperService.Write(
                    id,
                    displayName,
                    command,
                    ParseTransparentProxyPort());
                var preview = _transparentProxyLaunchWrapperService.Preview(
                    id,
                    displayName,
                    command,
                    ParseTransparentProxyPort());
                ApplyTransparentProxyLaunchWrapperPreview(preview);
                TransparentProxyEnableAppCapture = true;
                TransparentProxyLaunchWrapperStatusText =
                    $"{result.Summary} PowerShell：{result.PowerShellPath}；CMD：{result.CmdPath}";
                TransparentProxyStatusSummary = result.Summary;
                StatusMessage = result.Summary;
                SaveState();
                await Task.CompletedTask;
            },
            $"{displayName} 临时启动器",
            "生成中",
            18d);

    private void ApplyTransparentProxyLaunchWrapperPreview(TransparentProxyLaunchWrapperPreview preview)
    {
        TransparentProxyLaunchWrapperPathText =
            $"PowerShell：{preview.PowerShellPath}    CMD：{preview.CmdPath}";
        TransparentProxyLaunchWrapperPreviewText = string.Join(
            Environment.NewLine,
            [
                $"# {preview.DisplayName} PowerShell",
                preview.PowerShellScript.TrimEnd(),
                string.Empty,
                $"# {preview.DisplayName} CMD",
                preview.CmdScript.TrimEnd()
            ]);
    }

    private TransparentProxyCodexCaptureContext BuildTransparentProxyCodexCaptureContext()
    {
        var routes = BuildTransparentProxyRuntimeRoutes(includeWorkspaceFallback: true);

        var model = routes.Count == 0
            ? "gpt-5.4"
            : ResolveTransparentProxyClientDefaultModel(routes);
        return new(
            TransparentProxyLocalEndpoint,
            string.IsNullOrWhiteSpace(model) ? "gpt-5.4" : model,
            ResolveTransparentProxyCodexWireApi(routes));
    }

    private static string ResolveTransparentProxyCodexWireApi(IReadOnlyList<TransparentProxyRoute> routes)
    {
        if (routes.Count == 0)
        {
            return "responses";
        }

        return routes.Any(static route =>
            route.ResponsesSupported != false ||
            route.AnthropicMessagesSupported == true ||
            route.AnthropicMessagesSupported is null)
            ? "responses"
            : "chat";
    }

    private void ApplyTransparentProxyCodexPreview(TransparentProxyCodexConfigPreview preview)
    {
        TransparentProxyCodexConfigPath = preview.ConfigPath;
        TransparentProxyCodexPreviewText = preview.PreviewText;
    }

    private bool CanConfigureTransparentProxyClaudeCapture()
        => !IsBusy;

    private Task PreviewTransparentProxyClaudeCaptureAsync()
        => ExecuteBusyActionAsync(
            "正在生成 Claude CLI 接管预览...",
            async () =>
            {
                UpdateGlobalTaskProgress("生成预览", 42d);
                var preview = _transparentProxyClaudeConfigService.Preview(TransparentProxyLocalEndpoint);
                ApplyTransparentProxyClaudePreview(preview);
                TransparentProxyClaudeStatusText = preview.Changed
                    ? "预览已生成：写入前会备份现有 settings.json，只更新 env 接管变量。"
                    : "预览已生成：当前 Claude CLI 配置已经指向 RelayBench。";
                TransparentProxyStatusSummary = TransparentProxyClaudeStatusText;
                StatusMessage = TransparentProxyClaudeStatusText;
                RefreshTransparentProxyCaptureTargets();
                await Task.CompletedTask;
            },
            "Claude CLI 接管",
            "预览中",
            18d);

    private Task ApplyTransparentProxyClaudeCaptureAsync()
        => ExecuteBusyActionAsync(
            "正在写入 Claude CLI 接管配置...",
            async () =>
            {
                UpdateGlobalTaskProgress("启动统一出口", 24d);
                if (!IsTransparentProxyRunning)
                {
                    await StartTransparentProxyCoreAsync(isAutoStart: false);
                }

                if (!IsTransparentProxyRunning)
                {
                    throw new InvalidOperationException("本地统一出口未能启动，已取消写入 Claude CLI 配置。");
                }

                UpdateGlobalTaskProgress("写入 Claude 配置", 58d);
                var result = _transparentProxyClaudeConfigService.Apply(TransparentProxyLocalEndpoint);
                var preview = _transparentProxyClaudeConfigService.Preview(TransparentProxyLocalEndpoint);
                ApplyTransparentProxyClaudePreview(preview);

                TransparentProxyEnableAppCapture = true;
                TransparentProxyClaudeStatusText = string.IsNullOrWhiteSpace(result.BackupPath)
                    ? result.Summary
                    : $"{result.Summary} 备份：{result.BackupPath}";
                TransparentProxyClaudeStatusText += await BuildTransparentProxyPostApplyHealthTextAsync();
                TransparentProxyStatusSummary = result.Summary;
                StatusMessage = result.Summary;
                RefreshTransparentProxyCaptureTargets();
                SaveState();
            },
            "Claude CLI 接管",
            "写入中",
            16d);

    private Task RestoreTransparentProxyClaudeCaptureAsync()
        => ExecuteBusyActionAsync(
            "正在恢复 Claude CLI 接管配置...",
            async () =>
            {
                UpdateGlobalTaskProgress("恢复配置", 48d);
                var result = _transparentProxyClaudeConfigService.RestoreLatestBackup();
                var preview = _transparentProxyClaudeConfigService.Preview(TransparentProxyLocalEndpoint);
                ApplyTransparentProxyClaudePreview(preview);
                TransparentProxyClaudeStatusText = string.IsNullOrWhiteSpace(result.BackupPath)
                    ? result.Summary
                    : $"{result.Summary} 来源：{result.BackupPath}";
                TransparentProxyStatusSummary = result.Summary;
                StatusMessage = result.Summary;
                RefreshTransparentProxyCaptureTargets();
                await Task.CompletedTask;
            },
            "Claude CLI 接管",
            "恢复中",
            18d);

    private void ApplyTransparentProxyClaudePreview(TransparentProxyClaudeConfigPreview preview)
    {
        TransparentProxyClaudeConfigPath = preview.SettingsPath;
        TransparentProxyClaudePreviewText = preview.PreviewText;
    }

    private bool CanConfigureTransparentProxyVsCodeCapture()
        => !IsBusy;

    private TransparentProxyVsCodeSettingsScope GetTransparentProxyVsCodeSettingsScope()
        => TransparentProxyVsCodeSettingsScopes.Parse(TransparentProxyVsCodeSettingsScopeKey);

    private static string GetTransparentProxyVsCodeWorkspaceDirectory()
        => RelayBenchPaths.RootDirectory;

    private TransparentProxyVsCodeSettingsPreview BuildTransparentProxyVsCodePreview()
        => _transparentProxyVsCodeSettingsService.Preview(
            TransparentProxyLocalEndpoint,
            GetTransparentProxyVsCodeSettingsScope(),
            GetTransparentProxyVsCodeWorkspaceDirectory());

    private Task PreviewTransparentProxyVsCodeCaptureAsync()
        => ExecuteBusyActionAsync(
            "正在生成 VS Code 终端接管预览...",
            async () =>
            {
                UpdateGlobalTaskProgress("生成预览", 42d);
                var preview = BuildTransparentProxyVsCodePreview();
                ApplyTransparentProxyVsCodePreview(preview);
                var scopeName = TransparentProxyVsCodeSettingsScopes.GetDisplayName(GetTransparentProxyVsCodeSettingsScope());
                TransparentProxyVsCodeStatusText = preview.Changed
                    ? $"预览已生成：{scopeName}写入前会备份 VS Code settings.json，只更新终端环境变量。"
                    : $"预览已生成：VS Code {scopeName}终端环境已经指向 RelayBench。";
                TransparentProxyStatusSummary = TransparentProxyVsCodeStatusText;
                StatusMessage = TransparentProxyVsCodeStatusText;
                RefreshTransparentProxyCaptureTargets();
                await Task.CompletedTask;
            },
            "VS Code 终端接管",
            "预览中",
            18d);

    private Task ApplyTransparentProxyVsCodeCaptureAsync()
        => ExecuteBusyActionAsync(
            "正在写入 VS Code 终端接管配置...",
            async () =>
            {
                UpdateGlobalTaskProgress("启动统一出口", 24d);
                if (!IsTransparentProxyRunning)
                {
                    await StartTransparentProxyCoreAsync(isAutoStart: false);
                }

                if (!IsTransparentProxyRunning)
                {
                    throw new InvalidOperationException("本地统一出口未能启动，已取消写入 VS Code 终端配置。");
                }

                UpdateGlobalTaskProgress("写入 VS Code 设置", 58d);
                var scope = GetTransparentProxyVsCodeSettingsScope();
                var workspaceDirectory = GetTransparentProxyVsCodeWorkspaceDirectory();
                var result = _transparentProxyVsCodeSettingsService.Apply(TransparentProxyLocalEndpoint, scope, workspaceDirectory);
                var preview = _transparentProxyVsCodeSettingsService.Preview(TransparentProxyLocalEndpoint, scope, workspaceDirectory);
                ApplyTransparentProxyVsCodePreview(preview);

                TransparentProxyEnableAppCapture = true;
                TransparentProxyVsCodeStatusText = result.BackupFiles.Count == 0
                    ? result.Summary
                    : $"{result.Summary} 备份：{string.Join("；", result.BackupFiles)}";
                TransparentProxyVsCodeStatusText += await BuildTransparentProxyPostApplyHealthTextAsync();
                TransparentProxyStatusSummary = result.Summary;
                StatusMessage = result.Summary;
                RefreshTransparentProxyCaptureTargets();
                SaveState();
            },
            "VS Code 终端接管",
            "写入中",
            16d);

    private Task RestoreTransparentProxyVsCodeCaptureAsync()
        => ExecuteBusyActionAsync(
            "正在恢复 VS Code 终端接管配置...",
            async () =>
            {
                UpdateGlobalTaskProgress("恢复配置", 48d);
                var scope = GetTransparentProxyVsCodeSettingsScope();
                var workspaceDirectory = GetTransparentProxyVsCodeWorkspaceDirectory();
                var result = _transparentProxyVsCodeSettingsService.RestoreLatestBackups(scope, workspaceDirectory);
                var preview = _transparentProxyVsCodeSettingsService.Preview(TransparentProxyLocalEndpoint, scope, workspaceDirectory);
                ApplyTransparentProxyVsCodePreview(preview);
                TransparentProxyVsCodeStatusText = result.BackupFiles.Count == 0
                    ? result.Summary
                    : $"{result.Summary} 来源：{string.Join("；", result.BackupFiles)}";
                TransparentProxyStatusSummary = result.Summary;
                StatusMessage = result.Summary;
                RefreshTransparentProxyCaptureTargets();
                await Task.CompletedTask;
            },
            "VS Code 终端接管",
            "恢复中",
            18d);

    private void ApplyTransparentProxyVsCodePreview(TransparentProxyVsCodeSettingsPreview preview)
    {
        TransparentProxyVsCodeConfigPath = string.Join("；", preview.SettingsPaths);
        TransparentProxyVsCodePreviewText = preview.PreviewText;
    }

    private sealed record TransparentProxyCodexCaptureContext(
        string BaseUrl,
        string Model,
        string WireApi);

    private Task ToggleTransparentProxyProviderSettingsAsync()
    {
        var shouldOpen = !IsTransparentProxyProviderSettingsOpen;
        IsTransparentProxyProviderSettingsOpen = shouldOpen;
        if (shouldOpen)
        {
            IsTransparentProxyListenSettingsOpen = false;
            IsTransparentProxyAppCaptureSettingsOpen = false;
            IsTransparentProxyOAuthPanelOpen = false;
        }

        return Task.CompletedTask;
    }

    private Task ToggleTransparentProxyOAuthPanelAsync()
    {
        var shouldOpen = !IsTransparentProxyOAuthPanelOpen;
        IsTransparentProxyOAuthPanelOpen = shouldOpen;
        if (shouldOpen)
        {
            IsTransparentProxyListenSettingsOpen = false;
            IsTransparentProxyProviderSettingsOpen = false;
            IsTransparentProxyAppCaptureSettingsOpen = false;
            RefreshCodexOAuthCredentials();
        }

        return Task.CompletedTask;
    }

    private async Task StartCodexOAuthLoginAsync()
    {
        IsTransparentProxySettingsDrawerOpen = true;
        IsTransparentProxyProviderSettingsOpen = false;
        IsTransparentProxyListenSettingsOpen = false;
        IsTransparentProxyAppCaptureSettingsOpen = false;
        IsTransparentProxyOAuthPanelOpen = true;
        IsCodexOAuthLoginInProgress = true;
        IsCodexOAuthManualCallbackVisible = false;
        CodexOAuthManualCallbackText = string.Empty;
        CodexOAuthLoginStatusText = "正在打开 Codex OAuth 登录窗口...";
        CodexOAuthLoginStepText = "1/4 生成登录请求";
        CodexOAuthLoginUrlText = string.Empty;

        CodexOAuthLoginSession? session = null;
        try
        {
            session = await _codexOAuthService.BeginLoginAsync(CancellationToken.None);
            _currentCodexOAuthLoginSession = session;
            CodexOAuthLoginUrlText = session.AuthUrl;
            CodexOAuthLoginStepText = "2/4 等待浏览器授权";
            CodexOAuthLoginStatusText = (session.CallbackServerStarted, session.BrowserOpened) switch
            {
                (true, true) => "浏览器已打开，请完成 Codex 登录。15 秒后可手动粘贴回调地址。",
                (true, false) => "浏览器打开失败，请复制登录链接到浏览器完成授权。",
                (false, true) => "OAuth 回调端口不可用，浏览器已打开；请复制最终回调地址手动粘贴。",
                _ => "OAuth 回调端口不可用且浏览器未打开，请复制登录链接并手动粘贴回调地址。"
            };
            IsCodexOAuthManualCallbackVisible = !session.CallbackServerStarted || !session.BrowserOpened;
            _ = ShowCodexOAuthManualCallbackLaterAsync(session.Id);
            var credential = await _codexOAuthService.CompleteLoginAsync(
                session,
                CancellationToken.None,
                () =>
                {
                    CodexOAuthLoginStepText = "3/4 换取并保存令牌";
                    CodexOAuthLoginStatusText = "已收到浏览器授权，正在换取令牌并安全保存。";
                });
            CodexOAuthLoginStepText = "4/4 保存完成";
            CodexOAuthLoginStatusText = $"Codex OAuth 登录完成：{credential.DisplayName}";
            RefreshCodexOAuthCredentials();
            RefreshTransparentProxyRuntimeRoutesAfterOAuthChange();
            SaveState();
        }
        catch (OperationCanceledException)
        {
            CodexOAuthLoginStepText = "已取消";
            CodexOAuthLoginStatusText = "Codex OAuth 登录已取消。";
        }
        catch (Exception ex)
        {
            AppDiagnosticLog.Write("CodexOAuth.Login", ex);
            CodexOAuthLoginStepText = "需要处理";
            CodexOAuthLoginStatusText = $"Codex OAuth 登录失败：{ProbeTraceRedactor.RedactText(ex.Message)}";
            IsCodexOAuthManualCallbackVisible = true;
        }
        finally
        {
            if (ReferenceEquals(_currentCodexOAuthLoginSession, session))
            {
                _currentCodexOAuthLoginSession = null;
            }

            session?.Dispose();
            IsCodexOAuthLoginInProgress = false;
        }
    }

    private async Task ShowCodexOAuthManualCallbackLaterAsync(string sessionId)
    {
        await Task.Delay(TimeSpan.FromSeconds(15));
        if (_currentCodexOAuthLoginSession?.Id == sessionId && IsCodexOAuthLoginInProgress)
        {
            IsCodexOAuthManualCallbackVisible = true;
            CodexOAuthLoginStatusText = "如果浏览器没有自动回到 RelayBench，请粘贴回调地址。";
        }
    }

    private Task CancelCodexOAuthLoginAsync()
    {
        _currentCodexOAuthLoginSession?.Cancel();
        CodexOAuthLoginStatusText = "正在取消 Codex OAuth 登录...";
        return Task.CompletedTask;
    }

    private Task SubmitCodexOAuthCallbackAsync()
    {
        if (!_codexOAuthService.SubmitManualCallback(_currentCodexOAuthLoginSession, CodexOAuthManualCallbackText))
        {
            CodexOAuthLoginStatusText = "回调地址无效，请确认地址中包含 code 和 state。";
        }

        return Task.CompletedTask;
    }

    private Task CopyCodexOAuthLoginUrlAsync()
    {
        if (!string.IsNullOrWhiteSpace(CodexOAuthLoginUrlText))
        {
            Clipboard.SetText(CodexOAuthLoginUrlText);
            CodexOAuthLoginStatusText = "Codex OAuth 登录链接已复制。";
        }

        return Task.CompletedTask;
    }

    private async Task ImportCodexOAuthCredentialAsync()
    {
        IsTransparentProxySettingsDrawerOpen = true;
        IsTransparentProxyListenSettingsOpen = false;
        IsTransparentProxyProviderSettingsOpen = false;
        IsTransparentProxyAppCaptureSettingsOpen = false;
        IsTransparentProxyOAuthPanelOpen = true;

        OpenFileDialog dialog = new()
        {
            Title = "导入 Codex OAuth 令牌",
            DefaultExt = ".json",
            CheckFileExists = true,
            Multiselect = false,
            Filter = "CPA 认证文件 (*.json)|*.json|JSON 文件 (*.json)|*.json|所有文件 (*.*)|*.*"
        };
        if (dialog.ShowDialog() != true)
        {
            CodexOAuthLoginStatusText = "已取消导入 Codex OAuth。";
            return;
        }

        CodexOAuthLoginStepText = "导入令牌";
        CodexOAuthLoginStatusText = "正在导入 CPA 兼容 Codex OAuth 认证文件...";
        try
        {
            var result = await _codexOAuthService.ImportCpaCredentialFileAsync(dialog.FileName, CancellationToken.None);
            RefreshCodexOAuthCredentials();
            SelectedCodexOAuthCredential = CodexOAuthCredentials.FirstOrDefault(item =>
                string.Equals(item.Id, result.Credential.Id, StringComparison.OrdinalIgnoreCase));
            RefreshTransparentProxyRuntimeRoutesAfterOAuthChange();
            SaveState();

            CodexOAuthLoginStatusText = !string.IsNullOrWhiteSpace(result.RefreshError)
                ? $"已导入 {result.Credential.DisplayName}，但刷新令牌失败：{result.RefreshError}"
                : result.Refreshed
                    ? $"已导入并刷新 Codex OAuth 账号：{result.Credential.DisplayName}"
                    : $"已导入 Codex OAuth 账号：{result.Credential.DisplayName}";
            StatusMessage = CodexOAuthLoginStatusText;
        }
        catch (Exception ex)
        {
            AppDiagnosticLog.Write("CodexOAuth.Import", ex);
            CodexOAuthLoginStepText = "导入失败";
            CodexOAuthLoginStatusText = $"Codex OAuth 导入失败：{ProbeTraceRedactor.RedactText(ex.Message)}";
            StatusMessage = CodexOAuthLoginStatusText;
        }
    }

    private async Task RefreshCodexOAuthCredentialAsync(CodexOAuthCredentialViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        CodexOAuthLoginStatusText = $"正在刷新 {item.DisplayName}...";
        await _codexOAuthService.RefreshCredentialAsync(item.Id, CancellationToken.None);
        CodexOAuthLoginStatusText = $"{item.DisplayName} 刷新完成。";
        RefreshCodexOAuthCredentials();
        RefreshTransparentProxyRuntimeRoutesAfterOAuthChange();
    }

    private Task DisableCodexOAuthCredentialAsync(CodexOAuthCredentialViewModel? item)
    {
        if (item is null)
        {
            return Task.CompletedTask;
        }

        var isDisabled = string.Equals(item.State, CodexOAuthCredentialState.Disabled.ToString(), StringComparison.OrdinalIgnoreCase);
        _codexOAuthService.DisableCredential(item.Id, !isDisabled);
        CodexOAuthLoginStatusText = isDisabled ? $"{item.DisplayName} 已启用。" : $"{item.DisplayName} 已停用。";
        RefreshCodexOAuthCredentials();
        RefreshTransparentProxyRuntimeRoutesAfterOAuthChange();
        return Task.CompletedTask;
    }

    private async Task ExportCodexOAuthCredentialAsync(CodexOAuthCredentialViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        var credential = _codexOAuthService.GetCredentials()
            .FirstOrDefault(credential => string.Equals(credential.Id, item.Id, StringComparison.OrdinalIgnoreCase));
        if (credential is null)
        {
            CodexOAuthLoginStatusText = "未找到要导出的 Codex OAuth 账号。";
            return;
        }

        if (string.IsNullOrWhiteSpace(credential.RefreshToken))
        {
            throw new InvalidOperationException($"{credential.DisplayName} 缺少 refresh_token，无法导出 CPA 兼容认证文件。");
        }

        SaveFileDialog dialog = new()
        {
            Title = "导出 Codex OAuth 到 CPA",
            FileName = BuildCpaCodexOAuthFileName(credential),
            DefaultExt = ".json",
            AddExtension = true,
            OverwritePrompt = true,
            Filter = "CPA 认证文件 (*.json)|*.json|JSON 文件 (*.json)|*.json|所有文件 (*.*)|*.*"
        };
        if (dialog.ShowDialog() != true)
        {
            CodexOAuthLoginStatusText = "已取消导出 Codex OAuth。";
            return;
        }

        var json = BuildCpaCodexOAuthJson(credential);
        await File.WriteAllTextAsync(dialog.FileName, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        CodexOAuthLoginStatusText = $"已导出 CPA 认证文件：{dialog.FileName}";
        StatusMessage = CodexOAuthLoginStatusText;
    }

    private Task DeleteCodexOAuthCredentialAsync(CodexOAuthCredentialViewModel? item)
    {
        if (item is null)
        {
            return Task.CompletedTask;
        }

        _codexOAuthService.DeleteCredential(item.Id);
        foreach (var route in TransparentProxyRouteEditorItems.Where(route =>
                     string.Equals(route.OAuthCredentialId, item.Id, StringComparison.OrdinalIgnoreCase)))
        {
            route.AuthMode = TransparentProxyRouteAuthModes.ApiKey;
            route.OAuthCredentialId = string.Empty;
        }

        UpdateTransparentProxyRoutesTextFromEditor();
        CodexOAuthLoginStatusText = $"{item.DisplayName} 已删除。";
        RefreshCodexOAuthCredentials();
        RefreshTransparentProxyRuntimeRoutesAfterOAuthChange();
        SaveState();
        return Task.CompletedTask;
    }

    private static string BuildCpaCodexOAuthJson(CodexOAuthCredential credential)
    {
        var now = DateTimeOffset.UtcNow;
        Dictionary<string, object?> payload = new(StringComparer.Ordinal)
        {
            ["id_token"] = credential.IdToken,
            ["access_token"] = credential.AccessToken,
            ["refresh_token"] = credential.RefreshToken,
            ["account_id"] = credential.AccountId,
            ["last_refresh"] = FormatCpaTimestamp(credential.LastRefreshAt ?? credential.UpdatedAt),
            ["email"] = credential.Email,
            ["type"] = "codex",
            ["expired"] = FormatCpaTimestamp(credential.AccessTokenExpiresAt ?? now),
            ["disabled"] = credential.State == CodexOAuthCredentialState.Disabled
        };

        return JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            WriteIndented = true
        });
    }

    private static string BuildCpaCodexOAuthFileName(CodexOAuthCredential credential)
    {
        var email = SanitizeCpaAuthFileNamePart(credential.Email);
        if (string.IsNullOrWhiteSpace(email))
        {
            email = SanitizeCpaAuthFileNamePart(credential.Id);
        }

        var plan = NormalizeCpaPlanType(credential.PlanType);
        if (string.IsNullOrWhiteSpace(plan))
        {
            return $"codex-{email}.json";
        }

        if (string.Equals(plan, "team", StringComparison.OrdinalIgnoreCase))
        {
            var accountHash = SanitizeCpaAuthFileNamePart(credential.AccountIdHash);
            return string.IsNullOrWhiteSpace(accountHash)
                ? $"codex-{email}-{plan}.json"
                : $"codex-{accountHash}-{email}-{plan}.json";
        }

        return $"codex-{email}-{plan}.json";
    }

    private static string FormatCpaTimestamp(DateTimeOffset value)
        => value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static string NormalizeCpaPlanType(string value)
    {
        var builder = new StringBuilder();
        foreach (var character in (value ?? string.Empty).Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(character);
            }
            else if (builder.Length > 0 && builder[^1] != '-')
            {
                builder.Append('-');
            }
        }

        return builder.ToString().Trim('-');
    }

    private static string SanitizeCpaAuthFileNamePart(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder();
        foreach (var character in (value ?? string.Empty).Trim())
        {
            builder.Append(invalid.Contains(character) ? '-' : character);
        }

        return builder.ToString().Trim('-', ' ');
    }

    private void RefreshCodexOAuthCredentials()
    {
        var credentials = _codexOAuthService.GetCredentials();
        CodexOAuthCredentials.Clear();
        foreach (var credential in credentials)
        {
            CodexOAuthCredentials.Add(new CodexOAuthCredentialViewModel(credential));
        }

        OnPropertyChanged(nameof(HasCodexOAuthCredentials));
        OnPropertyChanged(nameof(HasNoCodexOAuthCredentials));
        ExportCodexOAuthCredentialCommand.RaiseCanExecuteChanged();
        if (credentials.Count > 0 && CodexOAuthLoginStatusText.Contains("尚未登录", StringComparison.Ordinal))
        {
            CodexOAuthLoginStatusText = $"已保存 {credentials.Count} 个 Codex OAuth 账号。";
        }
    }

    private void OnCodexOAuthCredentialsChanged(object? sender, EventArgs e)
    {
        if (Application.Current?.Dispatcher is null)
        {
            return;
        }

        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            var selectedId = SelectedCodexOAuthCredential?.Id ?? string.Empty;
            RefreshCodexOAuthCredentials();
            if (!string.IsNullOrWhiteSpace(selectedId))
            {
                SelectedCodexOAuthCredential = CodexOAuthCredentials.FirstOrDefault(item =>
                    string.Equals(item.Id, selectedId, StringComparison.OrdinalIgnoreCase));
            }

            RefreshTransparentProxyRuntimeRoutesAfterOAuthChange();
        });
    }

    private Task OpenTransparentProxyRouteSettingsAsync(TransparentProxyRouteEditorItemViewModel? item)
    {
        if (item is null)
        {
            return Task.CompletedTask;
        }

        SelectedTransparentProxyRouteEditorItem = item;
        TransparentProxyRouteSettingsItem = item;
        IsTransparentProxyRouteSettingsOpen = true;
        return Task.CompletedTask;
    }

    private Task OpenTransparentProxyRuntimeRouteSettingsAsync(TransparentProxyRouteViewModel? route)
    {
        if (route is null)
        {
            return Task.CompletedTask;
        }

        var item = TransparentProxyRouteEditorItems.FirstOrDefault(editor =>
            string.Equals(TransparentProxyRouteTextCodec.BuildRouteId(editor.Name, editor.BaseUrl, editor.Prefix), route.Id, StringComparison.OrdinalIgnoreCase));
        if (item is not null)
        {
            return OpenTransparentProxyRouteSettingsAsync(item);
        }

        TransparentProxyStatusSummary = "未找到可编辑的路由节点。";
        return Task.CompletedTask;
    }

    private Task CloseTransparentProxyRouteSettingsAsync()
    {
        IsTransparentProxyRouteSettingsOpen = false;
        TransparentProxyRouteSettingsItem = null;
        UpdateTransparentProxyRoutesTextFromEditor();
        SaveState();
        return Task.CompletedTask;
    }

    private async Task FetchTransparentProxyRouteEditorItemModelsAsync(TransparentProxyRouteEditorItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(item.BaseUrl))
        {
            throw new InvalidOperationException("请先填写接口地址。");
        }

        var effectiveApiKey = !string.IsNullOrWhiteSpace(item.ApiKey)
            ? item.ApiKey
            : ProxyApiKey;
        if (string.IsNullOrWhiteSpace(effectiveApiKey))
        {
            throw new InvalidOperationException("请先填写接口密钥。");
        }

        var settings = new ProxyEndpointSettings(
            item.BaseUrl,
            effectiveApiKey,
            item.Models.FirstOrDefault() ?? ProxyModel,
            ProxyIgnoreTlsErrors,
            ParseBoundedInt(ProxyTimeoutSecondsText, fallback: 20, min: 5, max: 300));
        var catalog = await _proxyDiagnosticsService.FetchModelsAsync(settings);
        await CacheProxyModelCatalogResultAsync(settings, catalog, CancellationToken.None);
        if (!catalog.Success)
        {
            throw new InvalidOperationException(catalog.Error ?? catalog.Summary);
        }

        var models = catalog.ModelItems is { Count: > 0 }
            ? catalog.ModelItems.Select(static model => model.Id)
            : catalog.Models;
        item.ReplaceModelMappings(models
            .Where(static model => !string.IsNullOrWhiteSpace(model))
            .Select(static model => model.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase));
        UpdateTransparentProxyRoutesTextFromEditor();
        SaveState();
        TransparentProxyProtocolSummary = $"模型列表：{item.Name} 已拉取 {item.Models.Count} 个模型。";
    }

    private Task AddTransparentProxyRouteModelMappingAsync(TransparentProxyRouteEditorItemViewModel? item)
    {
        item ??= TransparentProxyRouteSettingsItem ?? SelectedTransparentProxyRouteEditorItem;
        if (item is null)
        {
            return Task.CompletedTask;
        }

        item.AddModelMapping();
        UpdateTransparentProxyRoutesTextFromEditor();
        return Task.CompletedTask;
    }

    private Task ResetTransparentProxyRouteCircuitAsync(TransparentProxyRouteEditorItemViewModel? item)
    {
        item ??= TransparentProxyRouteSettingsItem ?? SelectedTransparentProxyRouteEditorItem;
        if (item is null)
        {
            return Task.CompletedTask;
        }

        var routeId = TransparentProxyRouteTextCodec.BuildRouteId(item.Name, item.BaseUrl, item.Prefix);
        TransparentProxyStatusSummary = _transparentProxyService.ResetRouteCircuit(routeId)
            ? $"已重置路由熔断：{item.Name}"
            : $"该路由还没有运行态熔断记录：{item.Name}";
        return Task.CompletedTask;
    }

    private Task RemoveTransparentProxyRouteModelMappingAsync(TransparentProxyModelMappingViewModel? model)
    {
        if (model is null)
        {
            return Task.CompletedTask;
        }

        var owner = TransparentProxyRouteEditorItems.FirstOrDefault(item => item.ModelMappings.Contains(model));
        owner?.RemoveModelMapping(model);
        UpdateTransparentProxyRoutesTextFromEditor();
        return Task.CompletedTask;
    }

    private Task RemoveTransparentProxyRouteEditorItemAsync()
    {
        var item = SelectedTransparentProxyRouteEditorItem;
        if (item is null)
        {
            return Task.CompletedTask;
        }

        var index = TransparentProxyRouteEditorItems.IndexOf(item);
        DetachTransparentProxyRouteEditorItem(item);
        TransparentProxyRouteEditorItems.Remove(item);
        SelectedTransparentProxyRouteEditorItem = TransparentProxyRouteEditorItems.Count == 0
            ? null
            : TransparentProxyRouteEditorItems[Math.Clamp(index, 0, TransparentProxyRouteEditorItems.Count - 1)];
        UpdateTransparentProxyRoutesTextFromEditor();
        return Task.CompletedTask;
    }

    private Task MoveTransparentProxyRouteEditorItemUpAsync()
    {
        var item = SelectedTransparentProxyRouteEditorItem;
        if (item is null)
        {
            return Task.CompletedTask;
        }

        var index = TransparentProxyRouteEditorItems.IndexOf(item);
        if (index > 0)
        {
            TransparentProxyRouteEditorItems.Move(index, index - 1);
            SelectedTransparentProxyRouteEditorItem = item;
            UpdateTransparentProxyRoutesTextFromEditor();
        }

        return Task.CompletedTask;
    }

    private Task MoveTransparentProxyRouteEditorItemDownAsync()
    {
        var item = SelectedTransparentProxyRouteEditorItem;
        if (item is null)
        {
            return Task.CompletedTask;
        }

        var index = TransparentProxyRouteEditorItems.IndexOf(item);
        if (index >= 0 && index < TransparentProxyRouteEditorItems.Count - 1)
        {
            TransparentProxyRouteEditorItems.Move(index, index + 1);
            SelectedTransparentProxyRouteEditorItem = item;
            UpdateTransparentProxyRoutesTextFromEditor();
        }

        return Task.CompletedTask;
    }

    private Task GenerateTransparentProxyCandidateRoutesAsync()
        => ExecuteBusyActionAsync(
            "正在生成透明代理候选路由...",
            async () =>
            {
                UpdateGlobalTaskProgress("生成路由", 10d);
                var generatedItems = TransparentProxyRouteTextCodec.ParseEditorItems(BuildTransparentProxyRoutesTextFromWorkspace())
                    .Where(static item => !item.IsCodexOAuthAuth)
                    .ToArray();
                TransparentProxyRoutesText = TransparentProxyRouteTextCodec.BuildRoutesTextFromEditor(generatedItems);
                RefreshTransparentProxyRouteEditorItemsFromText();
                RefreshTransparentProxyRoutePreview();

                var routes = BuildTransparentProxyRuntimeRoutes();
                IReadOnlyList<TransparentProxyRoute> hydratedRoutes = routes;
                if (routes.Count > 0)
                {
                    UpdateGlobalTaskProgress("拉取模型", 30d);
                    hydratedRoutes = await ResolveTransparentProxyRouteProtocolsAsync(
                        routes,
                        forceProbe: true,
                        fetchCatalogModels: true,
                        CancellationToken.None);
                    ApplyHydratedTransparentProxyRoutesToEditor(hydratedRoutes);
                    RefreshTransparentProxyRoutePreview();
                    if (IsTransparentProxyRunning)
                    {
                        _transparentProxyService.UpdateRoutes(hydratedRoutes);
                    }
                }
                else
                {
                    TransparentProxyProtocolSummary = "协议探测：没有从当前接口或批量候选中找到可用路由。";
                }

                UpdateGlobalTaskProgress("测试出口", 86d);
                if (!IsTransparentProxyRunning)
                {
                    await StartTransparentProxyCoreAsync(isAutoStart: false);
                }

                await RefreshTransparentProxyHealthAsync();
                TransparentProxyStatusSummary = hydratedRoutes.Count > 0
                    ? $"生成候选路由完成：{hydratedRoutes.Count} 个节点，协议探测和统一出口端点测试已完成。{BuildCodexOAuthCandidateHint(hydratedRoutes)}{TransparentProxyHealthSummary}"
                    : $"生成候选路由完成：没有可用节点。{TransparentProxyHealthSummary}";
                StatusMessage = TransparentProxyStatusSummary;
                SaveState();
            },
            "生成候选路由",
            "生成中",
            8d);

    private string BuildCodexOAuthCandidateHint(IReadOnlyList<TransparentProxyRoute> routes)
    {
        var oauthRouteCount = routes.Count(static route => route.IsCodexOAuth);
        if (oauthRouteCount > 0)
        {
            return $"Codex OAuth 账号已自动加入路由队列 {oauthRouteCount} 条。{BuildCodexOAuthRouteCredentialSummary(routes)}";
        }

        return CodexOAuthCredentials.Count > 0
            ? "已有 Codex OAuth 账号，但当前没有可用账号路由。"
            : string.Empty;
    }

    private string BuildCodexOAuthRouteCredentialSummary(IReadOnlyList<TransparentProxyRoute> routes)
    {
        var oauthRoutes = routes.Where(static route => route.IsCodexOAuth).ToArray();
        if (oauthRoutes.Length == 0)
        {
            return string.Empty;
        }

        var credentials = _codexOAuthService.GetCredentials()
            .ToDictionary(static item => item.Id, StringComparer.OrdinalIgnoreCase);
        var ready = 0;
        var warning = 0;
        var missing = 0;
        foreach (var route in oauthRoutes)
        {
            if (!credentials.TryGetValue(route.OAuthCredentialId, out var credential))
            {
                missing++;
                continue;
            }

            if (credential.State == CodexOAuthCredentialState.Ready ||
                credential.State == CodexOAuthCredentialState.RefreshBackoff &&
                credential.AccessTokenExpiresAt is { } expiresAt &&
                expiresAt > DateTimeOffset.UtcNow)
            {
                ready++;
            }
            else
            {
                warning++;
            }
        }

        return $"OAuth 账号状态：可用 {ready}，需处理 {warning}，缺失 {missing}。";
    }

    private Task ProbeTransparentProxyProtocolsAsync()
        => ExecuteBusyActionAsync(
            "正在拉取透明代理上游模型并探测协议...",
            async () =>
            {
                var routes = BuildTransparentProxyRuntimeRoutes();
                if (routes.Count == 0)
                {
                    TransparentProxyRoutesText = BuildTransparentProxyRoutesTextFromWorkspace();
                    routes = BuildTransparentProxyRuntimeRoutes();
                }

                if (routes.Count == 0)
                {
                    TransparentProxyProtocolSummary = "协议探测：没有可用路由。";
                    return;
                }

                UpdateGlobalTaskProgress("拉取模型", 12d);
                var hydratedRoutes = await ResolveTransparentProxyRouteProtocolsAsync(
                    routes,
                    forceProbe: true,
                    fetchCatalogModels: true,
                    CancellationToken.None);
                ApplyHydratedTransparentProxyRoutesToEditor(hydratedRoutes);
                RefreshTransparentProxyRoutePreview();
                if (IsTransparentProxyRunning)
                {
                    _transparentProxyService.UpdateRoutes(hydratedRoutes);
                }

                SaveState();
            },
            "透明代理协议探测",
            "探测中",
            6d);

    private Task RunTransparentProxySelfTestAsync()
        => ExecuteBusyActionAsync(
            "正在运行透明代理本地自检...",
            async () =>
            {
                UpdateGlobalTaskProgress("启动本地 fake upstream", 12d);
                var result = await _transparentProxySelfTestService.RunAsync(CancellationToken.None);
                UpdateGlobalTaskProgress("校验协议和缓存", 86d);
                TransparentProxyProtocolSummary = result.Summary;
                TransparentProxyStatusSummary = result.Summary;
                StatusMessage = result.Summary;
                var createdAt = DateTimeOffset.Now;
                foreach (var check in result.Checks)
                {
                    _allTransparentProxyLogs.Insert(0, new TransparentProxyLogEntryViewModel(new TransparentProxyLogEntry(
                        createdAt,
                        check.Passed ? "INFO" : "ERROR",
                        "SELFTEST",
                        "/relaybench/self-test",
                        "local",
                        check.Passed ? 200 : 500,
                        0,
                        check.DisplayText,
                        "self-test",
                        $"selftest-{createdAt.ToUnixTimeMilliseconds()}")));
                }

                RefreshTransparentProxyLogView();
                if (!result.Success)
                {
                    throw new InvalidOperationException(result.Summary);
                }
            },
            "透明代理自检",
            "本地自检",
            8d);

    private void BeginTransparentProxyProtocolAutoDiscovery(IReadOnlyList<TransparentProxyRoute> routes)
    {
        CancelTransparentProxyProtocolAutoDiscovery();
        if (routes.Count == 0 || routes.All(static route => string.IsNullOrWhiteSpace(route.ApiKey)))
        {
            return;
        }

        _transparentProxyAutoDiscoveryCancellationSource = new CancellationTokenSource();
        _ = RunTransparentProxyProtocolAutoDiscoveryAsync(
            routes.ToArray(),
            _transparentProxyAutoDiscoveryCancellationSource);
    }

    private void CancelTransparentProxyProtocolAutoDiscovery()
    {
        if (_transparentProxyAutoDiscoveryCancellationSource is null)
        {
            return;
        }

        _transparentProxyAutoDiscoveryCancellationSource.Cancel();
        _transparentProxyAutoDiscoveryCancellationSource = null;
    }

    private async Task RunTransparentProxyProtocolAutoDiscoveryAsync(
        IReadOnlyList<TransparentProxyRoute> routes,
        CancellationTokenSource cancellationSource)
    {
        var cancellationToken = cancellationSource.Token;
        try
        {
            TransparentProxyProtocolSummary = "自动探测：后台拉取上游模型，补齐 Responses / Anthropic / OpenAI Chat 协议缓存。";
            var hydratedRoutes = await ResolveTransparentProxyRouteProtocolsAsync(
                routes,
                forceProbe: false,
                fetchCatalogModels: true,
                cancellationToken);
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            ApplyHydratedTransparentProxyRoutesToEditor(hydratedRoutes);
            RefreshTransparentProxyRoutePreview();
            if (IsTransparentProxyRunning)
            {
                _transparentProxyService.UpdateRoutes(hydratedRoutes);
            }

            SaveState();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            AppDiagnosticLog.Write("TransparentProxy.AutoDiscovery", ex);
            TransparentProxyProtocolSummary = $"自动探测：后台刷新失败，代理继续运行；{ex.Message}";
        }
        finally
        {
            if (ReferenceEquals(_transparentProxyAutoDiscoveryCancellationSource, cancellationSource))
            {
                _transparentProxyAutoDiscoveryCancellationSource = null;
            }

            cancellationSource.Dispose();
        }
    }

    private Task CopyTransparentProxyEndpointAsync()
    {
        Clipboard.SetText(TransparentProxyLocalEndpoint);
        TransparentProxyStatusSummary = $"已复制本地入口：{TransparentProxyLocalEndpoint}";
        StatusMessage = TransparentProxyStatusSummary;
        _ = ShowTransparentProxyCopyFeedbackAsync("endpoint");
        return Task.CompletedTask;
    }

    private Task CopyTransparentProxyPowerShellEnvAsync()
    {
        Clipboard.SetText(TransparentProxyPowerShellEnvSnippet);
        TransparentProxyStatusSummary = "已复制 PowerShell 临时环境变量片段。";
        StatusMessage = TransparentProxyStatusSummary;
        _ = ShowTransparentProxyCopyFeedbackAsync("powershell");
        return Task.CompletedTask;
    }

    private Task CopyTransparentProxyCmdEnvAsync()
    {
        Clipboard.SetText(TransparentProxyCmdEnvSnippet);
        TransparentProxyStatusSummary = "已复制 CMD 临时环境变量片段。";
        StatusMessage = TransparentProxyStatusSummary;
        _ = ShowTransparentProxyCopyFeedbackAsync("cmd");
        return Task.CompletedTask;
    }

    private async Task TestTransparentProxyHealthAsync()
    {
        TransparentProxyHealthTestButtonText = "\uE895";
        try
        {
            await RefreshTransparentProxyHealthAsync();
        }
        finally
        {
            TransparentProxyHealthTestButtonText = "\uE9D9";
        }
    }

    private async Task RefreshTransparentProxyHealthAsync()
    {
        try
        {
            using var client = new HttpClient();
            client.Timeout = TimeSpan.FromSeconds(3);
            using var response = await client.GetAsync(TransparentProxyHealthEndpoint);
            TransparentProxyHealthSummary = response.IsSuccessStatusCode
                ? $"健康检查通过：{TransparentProxyHealthEndpoint}"
                : $"健康检查失败：HTTP {(int)response.StatusCode}，{TransparentProxyHealthEndpoint}";
            TransparentProxyStatusSummary = TransparentProxyHealthSummary;
            StatusMessage = TransparentProxyHealthSummary;
        }
        catch (Exception ex)
        {
            TransparentProxyHealthSummary = $"健康检查失败：{ex.Message}";
            TransparentProxyStatusSummary = TransparentProxyHealthSummary;
            StatusMessage = TransparentProxyHealthSummary;
        }
    }

    private async Task<string> BuildTransparentProxyPostApplyHealthTextAsync()
    {
        try
        {
            using var client = new HttpClient();
            client.Timeout = TimeSpan.FromSeconds(3);
            using var response = await client.GetAsync(TransparentProxyHealthEndpoint);
            return response.IsSuccessStatusCode
                ? "；接入测试：统一出口健康。"
                : $"；接入测试：统一出口返回 HTTP {(int)response.StatusCode}。";
        }
        catch (Exception ex)
        {
            return $"；接入测试失败：{ex.Message}";
        }
    }

    private async Task ShowTransparentProxyCopyFeedbackAsync(string target)
    {
        _transparentProxyCopyFeedbackCancellationSource?.Cancel();
        _transparentProxyCopyFeedbackCancellationSource?.Dispose();
        var cancellationSource = new CancellationTokenSource();
        _transparentProxyCopyFeedbackCancellationSource = cancellationSource;

        ApplyTransparentProxyCopyButtonText(target, "OK");
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(1.2), cancellationSource.Token);
            if (!cancellationSource.IsCancellationRequested)
            {
                ResetTransparentProxyCopyButtonTexts();
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void ApplyTransparentProxyCopyButtonText(string target, string text)
    {
        switch (target)
        {
            case "endpoint":
                TransparentProxyCopyEndpointButtonText = text;
                break;
            case "powershell":
                TransparentProxyCopyPowerShellButtonText = text;
                break;
            case "cmd":
                TransparentProxyCopyCmdButtonText = text;
                break;
        }
    }

    private void ResetTransparentProxyCopyButtonTexts()
    {
        TransparentProxyCopyEndpointButtonText = "\uE8C8";
        TransparentProxyCopyPowerShellButtonText = "PS";
        TransparentProxyCopyCmdButtonText = "CMD";
    }

    private Task ClearTransparentProxyLogsAsync()
    {
        _ = _transparentProxyLogStore.ClearAsync();
        _allTransparentProxyLogs.Clear();
        TransparentProxyLogs.Clear();
        SelectedTransparentProxyLog = null;
        IsTransparentProxyLogDetailOpen = false;
        return Task.CompletedTask;
    }

    private async Task ExportTransparentProxyLogsAsync()
    {
        var exportPath = await _transparentProxyLogStore.ExportCsvAsync(RelayBenchPaths.ExportsDirectory);
        TransparentProxyStatusSummary = $"透明代理日志已导出：{exportPath}";
    }

    private Task CloseTransparentProxyLogDetailAsync()
    {
        SelectedTransparentProxyLog = null;
        IsTransparentProxyLogDetailOpen = false;
        return Task.CompletedTask;
    }

    private Task ClearTransparentProxyCacheAsync()
    {
        var count = _transparentProxyService.ClearCache();
        TransparentProxyStatusSummary = count <= 0
            ? "短缓存为空，无需清理。"
            : $"已清空短缓存：{count} 条。";
        return Task.CompletedTask;
    }

    private Task ToggleTransparentProxyLogExpandedAsync()
    {
        IsTransparentProxyLogExpanded = !IsTransparentProxyLogExpanded;
        return Task.CompletedTask;
    }

    private void LoadTransparentProxyState(AppStateSnapshot snapshot)
    {
        var config = _transparentProxyConfigStore.Load(snapshot);
        TransparentProxyStartUnifiedEndpointOnLaunch = config.StartUnifiedEndpointOnLaunch;
        TransparentProxyEnableAppCapture = config.EnableAppCapture;
        TransparentProxyPortText = config.PortText;
        TransparentProxyRoutesText = config.RoutesText;
        TransparentProxyRateLimitPerMinuteText = config.RateLimitPerMinuteText;
        TransparentProxyMaxConcurrencyText = config.MaxConcurrencyText;
        TransparentProxyRouteStrategyKey = config.RouteStrategyKey;
        TransparentProxyEnableFallback = config.EnableFallback;
        TransparentProxyEnableCache = config.EnableCache;
        TransparentProxyCacheTtlSecondsText = config.CacheTtlSecondsText;
        TransparentProxyRequestRetryText = config.RequestRetryText;
        TransparentProxyMaxRetryIntervalSecondsText = config.MaxRetryIntervalSecondsText;
        TransparentProxySessionAffinityTtlSecondsText = config.SessionAffinityTtlSecondsText;
        TransparentProxyModelCooldownSecondsText = config.ModelCooldownSecondsText;
        TransparentProxyRewriteModel = config.RewriteModel;
        TransparentProxyVsCodeSettingsScopeKey = config.VsCodeSettingsScopeKey;

        if (string.IsNullOrWhiteSpace(TransparentProxyRoutesText))
        {
            TransparentProxyRoutesText = BuildTransparentProxyRoutesTextFromWorkspace();
        }

        RefreshTransparentProxyRouteEditorItemsFromText();
        RefreshCodexOAuthCredentials();
        RefreshTransparentProxyRoutePreview();
        RefreshTransparentProxyCaptureTargets();
    }

    private void ApplyTransparentProxyStateToSnapshot(AppStateSnapshot snapshot)
    {
        var config = CreateTransparentProxyConfigSnapshot();
        TransparentProxyConfigStore.ApplyToAppState(config, snapshot);
        _transparentProxyConfigStore.Save(config);
    }

    private TransparentProxyConfigSnapshot CreateTransparentProxyConfigSnapshot()
        => new()
        {
            PortText = TransparentProxyPortText,
            RoutesText = TransparentProxyRoutesText,
            RateLimitPerMinuteText = TransparentProxyRateLimitPerMinuteText,
            MaxConcurrencyText = TransparentProxyMaxConcurrencyText,
            RouteStrategyKey = TransparentProxyRouteStrategyKey,
            EnableFallback = TransparentProxyEnableFallback,
            EnableCache = TransparentProxyEnableCache,
            CacheTtlSecondsText = TransparentProxyCacheTtlSecondsText,
            RequestRetryText = TransparentProxyRequestRetryText,
            MaxRetryIntervalSecondsText = TransparentProxyMaxRetryIntervalSecondsText,
            SessionAffinityTtlSecondsText = TransparentProxySessionAffinityTtlSecondsText,
            ModelCooldownSecondsText = TransparentProxyModelCooldownSecondsText,
            RewriteModel = TransparentProxyRewriteModel,
            StartUnifiedEndpointOnLaunch = TransparentProxyStartUnifiedEndpointOnLaunch,
            EnableAppCapture = TransparentProxyEnableAppCapture,
            VsCodeSettingsScopeKey = TransparentProxyVsCodeSettingsScopeKey
        };

    private async Task<IReadOnlyList<TransparentProxyRoute>> ResolveTransparentProxyRouteProtocolsAsync(
        IReadOnlyList<TransparentProxyRoute> routes,
        bool forceProbe,
        bool fetchCatalogModels,
        CancellationToken cancellationToken)
    {
        if (routes.Count == 0)
        {
            TransparentProxyProtocolSummary = "协议探测：没有可用路由。";
            return routes;
        }

        var codexOAuthRoutes = routes
            .Where(static route => route.IsCodexOAuth)
            .Select(static route => route.WithProtocol(
                ProxyWireApiProbeService.ResponsesWireApi,
                chatCompletionsSupported: true,
                responsesSupported: true,
                anthropicMessagesSupported: true,
                DateTimeOffset.UtcNow))
            .ToArray();
        var routesToProbe = routes
            .Where(static route => !route.IsCodexOAuth)
            .ToArray();
        if (routesToProbe.Length == 0)
        {
            TransparentProxyProtocolSummary = $"协议探测：Codex OAuth 路由使用 Codex Responses 后端，并适配 Chat / Responses / Anthropic 客户端，跳过常规探测 {codexOAuthRoutes.Length} 条。{BuildCodexOAuthRouteCredentialSummary(codexOAuthRoutes)}";
            return codexOAuthRoutes;
        }

        var options = new TransparentProxyProtocolDiscoveryOptions(
            forceProbe,
            fetchCatalogModels,
            ProxyIgnoreTlsErrors,
            ParseBoundedInt(ProxyTimeoutSecondsText, fallback: 20, min: 5, max: 300),
            ProxyModel);
        var progress = new Progress<TransparentProxyProtocolDiscoveryProgress>(item =>
        {
            if (IsGlobalTaskProgressVisible)
            {
                UpdateGlobalTaskProgress(
                    item.CurrentRoute,
                    item.TotalRoutes,
                    fetchCatalogModels && !string.IsNullOrWhiteSpace(item.RouteName)
                        ? $"探测 {item.RouteName}"
                        : "读取协议缓存");
            }
        });
        var result = await _transparentProxyProtocolDiscoveryService.DiscoverAsync(
            routesToProbe,
            options,
            progress,
            cancellationToken);

        foreach (var snapshot in result.Snapshots)
        {
            _transparentProxyProtocolSnapshots[snapshot.Key] = snapshot.Value;
        }

        TransparentProxyProtocolSummary =
            $"协议探测：写入/刷新 {result.ProbedModels} 个模型，命中缓存 {result.CachedModels} 个，跳过 {result.SkippedRoutes} 条路由。优先级 Responses → Anthropic → OpenAI Chat。{BuildCodexOAuthRouteCredentialSummary(codexOAuthRoutes)}";
        if (codexOAuthRoutes.Length == 0)
        {
            return result.HydratedRoutes;
        }

        var hydratedById = result.HydratedRoutes.ToDictionary(static route => route.Id, StringComparer.OrdinalIgnoreCase);
        var codexById = codexOAuthRoutes.ToDictionary(static route => route.Id, StringComparer.OrdinalIgnoreCase);
        return routes
            .Select(route => codexById.TryGetValue(route.Id, out var codexRoute)
                ? codexRoute
                : hydratedById.TryGetValue(route.Id, out var hydratedRoute)
                    ? hydratedRoute
                    : route)
            .ToArray();
    }

    private string BuildTransparentProxyRoutesTextFromWorkspace()
    {
        List<TransparentProxyRouteTextSeed> seeds = [];

        if (!string.IsNullOrWhiteSpace(ProxyBaseUrl))
        {
            seeds.Add(new TransparentProxyRouteTextSeed("当前接口", ProxyBaseUrl, ProxyModel, ProxyApiKey));
        }

        foreach (var row in ProxyBatchRankingRows
                     .Where(static item => item.IsSelected || item.Rank is > 0 and <= 3)
                     .OrderBy(static item => item.Rank <= 0 ? int.MaxValue : item.Rank))
        {
            seeds.Add(new TransparentProxyRouteTextSeed(
                string.IsNullOrWhiteSpace(row.EntryName) ? $"候选 #{row.Rank}" : row.EntryName,
                row.BaseUrl,
                row.Model,
                row.ApiKey));
        }

        foreach (var item in ProxyBatchEditorItems.Where(static item => item.IncludeInBatchTest))
        {
            var apiKey = string.IsNullOrWhiteSpace(item.EntryApiKey) ? item.SiteGroupApiKey : item.EntryApiKey;
            var model = string.IsNullOrWhiteSpace(item.EntryModel) ? item.SiteGroupModel : item.EntryModel;
            seeds.Add(new TransparentProxyRouteTextSeed(
                string.IsNullOrWhiteSpace(item.EntryName) ? item.DisplayTitle : item.EntryName,
                item.BaseUrl,
                model ?? string.Empty,
                apiKey ?? string.Empty));
        }

        var distinct = seeds
            .Where(static item => Uri.TryCreate(item.BaseUrl?.Trim(), UriKind.Absolute, out _))
            .GroupBy(static item => $"{item.BaseUrl.Trim()}|{item.Model.Trim()}", StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.First())
            .Take(12)
            .ToArray();

        return TransparentProxyRouteTextCodec.BuildRoutesTextFromSeeds(distinct);
    }

    private void RefreshTransparentProxyRouteEditorItemsFromText()
    {
        if (_isRefreshingTransparentProxyRouteEditor)
        {
            return;
        }

        _isRefreshingTransparentProxyRouteEditor = true;
        try
        {
            foreach (var existing in TransparentProxyRouteEditorItems)
            {
                DetachTransparentProxyRouteEditorItem(existing);
            }

            TransparentProxyRouteEditorItems.Clear();
            foreach (var item in TransparentProxyRouteTextCodec.ParseEditorItems(TransparentProxyRoutesText)
                         .Where(static item => !item.IsCodexOAuthAuth))
            {
                AttachTransparentProxyRouteEditorItem(item);
                TransparentProxyRouteEditorItems.Add(item);
            }

            SelectedTransparentProxyRouteEditorItem = TransparentProxyRouteEditorItems.FirstOrDefault();
        }
        finally
        {
            _isRefreshingTransparentProxyRouteEditor = false;
        }
    }

    private void AttachTransparentProxyRouteEditorItem(TransparentProxyRouteEditorItemViewModel item)
        => item.PropertyChanged += TransparentProxyRouteEditorItem_OnPropertyChanged;

    private void DetachTransparentProxyRouteEditorItem(TransparentProxyRouteEditorItemViewModel item)
        => item.PropertyChanged -= TransparentProxyRouteEditorItem_OnPropertyChanged;

    private void TransparentProxyRouteEditorItem_OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_isRefreshingTransparentProxyRouteEditor)
        {
            return;
        }

        UpdateTransparentProxyRoutesTextFromEditor();
    }

    private void UpdateTransparentProxyRoutesTextFromEditor()
    {
        if (_isRefreshingTransparentProxyRouteEditor)
        {
            return;
        }

        _isUpdatingTransparentProxyRoutesTextFromEditor = true;
        try
        {
            TransparentProxyRoutesText = TransparentProxyRouteTextCodec.BuildRoutesTextFromEditor(
                TransparentProxyRouteEditorItems.Where(static item => !item.IsCodexOAuthAuth).ToArray());
        }
        finally
        {
            _isUpdatingTransparentProxyRoutesTextFromEditor = false;
        }

        RefreshTransparentProxyRoutePreview();
    }

    private void ApplyHydratedTransparentProxyRoutesToEditor(IReadOnlyList<TransparentProxyRoute> hydratedRoutes)
    {
        if (hydratedRoutes.Count == 0 || TransparentProxyRouteEditorItems.Count == 0)
        {
            return;
        }

        var byId = hydratedRoutes
            .GroupBy(static route => route.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static group => group.Key, static group => group.First(), StringComparer.OrdinalIgnoreCase);
        var changed = false;
        _isRefreshingTransparentProxyRouteEditor = true;
        try
        {
            foreach (var item in TransparentProxyRouteEditorItems)
            {
                var id = TransparentProxyRouteTextCodec.BuildRouteId(item.Name, item.BaseUrl, item.Prefix);
                if (!byId.TryGetValue(id, out var route) || route.Models.Count == 0)
                {
                    continue;
                }

                if (!AreTransparentProxyModelMappingsEquivalent(item.ModelMappings, route.ModelMappings))
                {
                    item.ReplaceModelMappings(route.ModelMappings);
                    changed = true;
                }
            }
        }
        finally
        {
            _isRefreshingTransparentProxyRouteEditor = false;
        }

        if (changed)
        {
            UpdateTransparentProxyRoutesTextFromEditor();
        }
    }

    private static bool AreTransparentProxyModelMappingsEquivalent(
        IReadOnlyCollection<TransparentProxyModelMappingViewModel> current,
        IReadOnlyList<TransparentProxyModelMapping> next)
    {
        var currentKeys = current
            .Where(static item => !string.IsNullOrWhiteSpace(item.Name))
            .Select(static item => $"{item.Name.Trim()}\u001F{item.Alias.Trim()}")
            .OrderBy(static item => item, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var nextKeys = next
            .Where(static item => !string.IsNullOrWhiteSpace(item.Name))
            .Select(static item => $"{item.Name.Trim()}\u001F{item.Alias.Trim()}")
            .OrderBy(static item => item, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return currentKeys.SequenceEqual(nextKeys, StringComparer.OrdinalIgnoreCase);
    }

    private void RefreshTransparentProxyRoutePreview()
    {
        var routes = BuildTransparentProxyRuntimeRoutes();
        var metrics = _transparentProxyService.IsRunning
            ? null
            : new TransparentProxyMetricsSnapshot(false, ParseTransparentProxyPort(), 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, [], 0, 0d, null);
        var registry = _transparentProxyModelRegistryService.BuildSnapshot(routes, metrics?.Routes);

        TransparentProxyRoutes.Clear();
        var priority = 0;
        foreach (var route in routes)
        {
            priority++;
            var viewModel = new TransparentProxyRouteViewModel(route, priority);
            viewModel.ApplyMetrics(metrics?.Routes.FirstOrDefault(item => string.Equals(item.Id, route.Id, StringComparison.OrdinalIgnoreCase)));
            if (_transparentProxyProtocolSnapshots.TryGetValue(route.Id, out var protocol))
            {
                viewModel.ApplyProtocol(
                    protocol.PreferredWireApi,
                    protocol.ChatCompletionsSupported,
                    protocol.ResponsesSupported,
                    protocol.AnthropicMessagesSupported,
                    protocol.CheckedAt);
            }

            TransparentProxyRoutes.Add(viewModel);
        }

        ReplaceTransparentProxyModelPools(registry.Pools);
        TransparentProxyRoutingSummary = routes.Count == 0
            ? "尚未配置上游路由。每行格式：名称 | 接口地址 | 模型 | 接口密钥。"
            : $"已配置 {routes.Count} 路上游，{registry.Pools.Count(static pool => !pool.IsPassThrough)} 个模型池；自动选路会综合优先级、熔断、错误率和延迟。";
    }

    private void ReplaceTransparentProxyModelPools(IReadOnlyList<TransparentProxyModelPoolSnapshot> pools)
    {
        TransparentProxyModelPools.Clear();
        foreach (var pool in pools
                     .Where(static item => !item.IsPassThrough || item.RouteCount > 0)
                     .Take(12))
        {
            TransparentProxyModelPools.Add(new TransparentProxyModelPoolViewModel(pool));
        }
    }

    private void OnTransparentProxyLogEmitted(object? sender, TransparentProxyLogEntry entry)
    {
        _ = _transparentProxyLogStore.AppendAsync(entry);
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            _allTransparentProxyLogs.Insert(0, new TransparentProxyLogEntryViewModel(entry));
            while (_allTransparentProxyLogs.Count > 500)
            {
                _allTransparentProxyLogs.RemoveAt(_allTransparentProxyLogs.Count - 1);
            }

            RefreshTransparentProxyLogView();
        });
    }

    private async Task LoadTransparentProxyLogsAsync()
    {
        try
        {
            var entries = await _transparentProxyLogStore.LoadRecentAsync(500);
            _ = Application.Current.Dispatcher.BeginInvoke(() =>
            {
                _allTransparentProxyLogs.Clear();
                foreach (var entry in entries)
                {
                    _allTransparentProxyLogs.Add(new TransparentProxyLogEntryViewModel(entry));
                }

                RefreshTransparentProxyLogView();
            });
        }
        catch (Exception ex)
        {
            AppDiagnosticLog.Write("TransparentProxy.LoadLogs", ex);
        }
    }

    private void RefreshTransparentProxyLogView()
    {
        var filter = TransparentProxyLogFilterText.Trim();
        var sourceFilter = TransparentProxyLogSourceFilterKey;
        var selectedRequestId = SelectedTransparentProxyLog?.RequestId;
        var rows = _allTransparentProxyLogs
            .Where(row => MatchesTransparentProxyLogSourceFilter(row, sourceFilter))
            .Where(row => MatchesTransparentProxyLogFilter(row, filter))
            .Take(300)
            .ToArray();

        TransparentProxyLogs.Clear();
        foreach (var row in rows)
        {
            TransparentProxyLogs.Add(row);
        }

        if (!string.IsNullOrWhiteSpace(selectedRequestId))
        {
            SelectedTransparentProxyLog = TransparentProxyLogs.FirstOrDefault(row => string.Equals(row.RequestId, selectedRequestId, StringComparison.OrdinalIgnoreCase));
        }
    }

    private static bool MatchesTransparentProxyLogSourceFilter(TransparentProxyLogEntryViewModel row, string sourceFilter)
        => sourceFilter switch
        {
            "unified" => Contains(row.IngressKind, "UnifiedLocalEndpoint") ||
                         Contains(row.SourceApplication, "本地统一出口"),
            "codex" => Contains(row.SourceApplication, "Codex") ||
                       Contains(row.CaptureMode, "Codex"),
            "claude" => Contains(row.SourceApplication, "Claude") ||
                        Contains(row.CaptureMode, "Claude"),
            "vscode" => Contains(row.SourceApplication, "VS") ||
                        Contains(row.SourceApplication, "Code") ||
                        Contains(row.CaptureMode, "VS") ||
                        Contains(row.CaptureMode, "Code"),
            _ => true
        };

    private static bool MatchesTransparentProxyLogFilter(TransparentProxyLogEntryViewModel row, string filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
        {
            return true;
        }

        return Contains(row.Level, filter) ||
               Contains(row.Method, filter) ||
               Contains(row.Path, filter) ||
               Contains(row.ModelName, filter) ||
               Contains(row.RouteName, filter) ||
               Contains(row.Message, filter) ||
               Contains(row.RequestId, filter) ||
               Contains(row.WireApi, filter) ||
               Contains(row.IngressKind, filter) ||
               Contains(row.SourceApplication, filter) ||
               Contains(row.CaptureMode, filter) ||
               Contains(row.TargetHost, filter) ||
               Contains(row.AttemptSummary, filter);

        static bool Contains(string value, string keyword)
            => value?.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool Contains(string value, string keyword)
        => value?.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0;

    private void OnTransparentProxyMetricsChanged(object? sender, TransparentProxyMetricsSnapshot snapshot)
    {
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            _latestTransparentProxyMetricsSnapshot = snapshot;
            IsTransparentProxyRunning = snapshot.IsRunning;
            TransparentProxyTotalRequestsText = snapshot.TotalRequests.ToString(CultureInfo.InvariantCulture);
            TransparentProxySuccessRateText = snapshot.TotalRequests <= 0
                ? "-"
                : $"{snapshot.SuccessRequests * 100d / snapshot.TotalRequests:0.#}%";
            TransparentProxyActiveRequestsText = snapshot.ActiveRequests.ToString(CultureInfo.InvariantCulture);
            TransparentProxyFallbackRequestsText = snapshot.FallbackRequests.ToString(CultureInfo.InvariantCulture);
            TransparentProxyCacheHitsText = snapshot.ResponseCacheLeaseWaits > 0
                ? $"{snapshot.CacheHits.ToString(CultureInfo.InvariantCulture)}/{snapshot.ResponseCacheLeaseWaits.ToString(CultureInfo.InvariantCulture)}"
                : snapshot.CacheHits.ToString(CultureInfo.InvariantCulture);
            TransparentProxyP95LatencyText = snapshot.P95LatencyMs <= 0
                ? "-"
                : $"{snapshot.P95LatencyMs.ToString(CultureInfo.InvariantCulture)} ms";
            _transparentProxyLatestTotalOutputTokens = snapshot.TotalOutputTokens;
            _transparentProxyLatestTokensPerSecond = snapshot.TokensPerSecond;
            _transparentProxyLatestTokenActivityAt = snapshot.LastTokenActivityAt;
            _transparentProxyLatestIsRunning = snapshot.IsRunning;
            TransparentProxyTotalTokensText = $"{FormatCompactTokenCount(snapshot.TotalOutputTokens)} tokens";
            TransparentProxyTokensPerSecondText = FormatTokensPerSecond(snapshot.TokensPerSecond);
            var latestUsageSource = ResolveTransparentProxyLatestUsageSource(snapshot.RecentUsageEvents);
            UpdateTransparentProxyTokenMeter(
                snapshot.IsRunning,
                snapshot.TotalOutputTokens,
                snapshot.TokensPerSecond,
                snapshot.LastTokenActivityAt,
                latestUsageSource);
            TransparentProxyIngressSummary = BuildTransparentProxyIngressSummary(snapshot.Ingresses);
            ApplyTransparentProxyCaptureTargetMetrics(snapshot);
            var responseCacheRate = FormatRate(snapshot.ResponseCacheHits, snapshot.ResponseCacheHits + snapshot.ResponseCacheMisses);
            var promptSessionRate = FormatRate(snapshot.PromptSessionCacheHits, snapshot.PromptSessionCacheHits + snapshot.PromptSessionCacheMisses);
            TransparentProxyCacheEfficiencyToolTip =
                $"本地缓存命中 {snapshot.CacheHits}；响应缓存 {snapshot.ResponseCacheHits}/{snapshot.ResponseCacheMisses}（{responseCacheRate}）；并发合并等待 {snapshot.ResponseCacheLeaseWaits}；进行中 key {snapshot.ResponseCacheInFlightKeys}；Prompt 会话 {snapshot.PromptSessionCacheEntryCount}（{promptSessionRate}）。";
            TransparentProxyMetricsSummary =
                $"请求 {snapshot.TotalRequests}，成功 {snapshot.SuccessRequests}，失败 {snapshot.FailedRequests}，fallback {snapshot.FallbackRequests}，本地缓存命中 {snapshot.CacheHits}，响应缓存 {snapshot.ResponseCacheHits}/{snapshot.ResponseCacheMisses}（{responseCacheRate}），模型列表 {snapshot.ModelListCacheEntryCount} 条，Prompt 会话 {snapshot.PromptSessionCacheEntryCount}（{promptSessionRate}），上游 Prompt Cache {FormatCompactTokenCount(snapshot.PromptCacheTokens)}，限速 {snapshot.RateLimitedRequests}，P50 {snapshot.P50LatencyMs} ms，P95 {snapshot.P95LatencyMs} ms，输出 {FormatCompactTokenCount(snapshot.TotalOutputTokens)} tokens。{TransparentProxyIngressSummary}";
            TransparentProxyStatusSummary = snapshot.IsRunning
                ? $"运行中：{TransparentProxyLocalEndpoint}，活跃 {snapshot.ActiveRequests}，缓存条目 {snapshot.CacheEntryCount}。"
                : "本地透明代理未启动。";
            TransparentProxyHealthSummary = snapshot.IsRunning
                ? $"健康检查：{TransparentProxyHealthEndpoint}"
                : "健康检查：未监听。";

            var byId = snapshot.Routes.ToDictionary(static item => item.Id, StringComparer.OrdinalIgnoreCase);
            foreach (var route in TransparentProxyRoutes)
            {
                byId.TryGetValue(route.Id, out var metrics);
                route.ApplyMetrics(metrics);
            }

            if (snapshot.ModelPools is { Count: > 0 } modelPools)
            {
                ReplaceTransparentProxyModelPools(modelPools);
            }
            else
            {
                var routes = BuildTransparentProxyRuntimeRoutes();
                ReplaceTransparentProxyModelPools(
                    _transparentProxyModelRegistryService.BuildSnapshot(routes, snapshot.Routes).Pools);
            }
        });
    }

    public void RefreshTransparentProxyTokenMeterIdleState()
        => UpdateTransparentProxyTokenMeter(
            _transparentProxyLatestIsRunning,
            _transparentProxyLatestTotalOutputTokens,
            _transparentProxyLatestTokensPerSecond,
            _transparentProxyLatestTokenActivityAt);

    public void ResetTransparentProxyTokenMeter()
    {
        _transparentProxyService.ResetTokenTelemetry();
        _transparentProxyLatestTotalOutputTokens = 0;
        _transparentProxyLatestTokensPerSecond = 0d;
        _transparentProxyLatestTokenActivityAt = null;
        TransparentProxyTotalTokensText = "0 tokens";
        TransparentProxyTokensPerSecondText = "0 tok/s";
        UpdateTransparentProxyTokenMeter(_transparentProxyLatestIsRunning, 0, 0d, null);
        TransparentProxyStatusSummary = _transparentProxyLatestIsRunning
            ? $"Token 阶段计数已重置：{TransparentProxyLocalEndpoint}"
            : "Token 阶段计数已重置。";
    }

    private void UpdateTransparentProxyTokenMeter(
        bool isRunning,
        long totalOutputTokens,
        double tokensPerSecond,
        DateTimeOffset? lastTokenActivityAt,
        string? sourceApplication = null)
    {
        var now = DateTimeOffset.UtcNow;
        var isStreaming = tokensPerSecond >= 0.5d &&
                          lastTokenActivityAt is not null &&
                          (now - lastTokenActivityAt.Value).TotalSeconds <= 5d;
        if (isStreaming)
        {
            TransparentProxyTokenMeterPrimaryText = FormatTokensPerSecond(tokensPerSecond);
            TransparentProxyTokenMeterSecondaryText = ResolveTransparentProxyTokenMeterSecondaryText(
                now,
                isRunning,
                totalOutputTokens,
                tokensPerSecond,
                isStreaming,
                sourceApplication);
            TransparentProxyTokenMeterAccentBrush = "#0E9F6E";
            return;
        }

        TransparentProxyTokenMeterPrimaryText = $"{FormatCompactTokenCount(totalOutputTokens)} tokens";
        TransparentProxyTokenMeterSecondaryText = ResolveTransparentProxyTokenMeterSecondaryText(
            now,
            isRunning,
            totalOutputTokens,
            tokensPerSecond,
            isStreaming: false,
            sourceApplication);
        TransparentProxyTokenMeterAccentBrush = isRunning ? "#64748B" : "#94A3B8";
    }

    private static string ResolveTransparentProxyTokenMeterSecondaryText(
        DateTimeOffset now,
        bool isRunning,
        long totalOutputTokens,
        double tokensPerSecond,
        bool isStreaming,
        string? sourceApplication)
    {
        var phase = (now.ToUnixTimeSeconds() / 4) % 3;
        var sourceText = string.IsNullOrWhiteSpace(sourceApplication) || string.Equals(sourceApplication, "-", StringComparison.Ordinal)
            ? "本地统一出口"
            : sourceApplication.Trim();
        if (isStreaming)
        {
            return phase switch
            {
                1 => sourceText,
                2 => $"累计 {FormatCompactTokenCount(totalOutputTokens)}",
                _ => "tokens / sec"
            };
        }

        if (!isRunning)
        {
            return "等待启动";
        }

        return phase switch
        {
            1 => FormatTokensPerSecond(tokensPerSecond),
            2 => sourceText,
            _ => "本阶段累计"
        };
    }

    private static string ResolveTransparentProxyLatestUsageSource(IReadOnlyList<TransparentProxyUsageEvent>? events)
    {
        var latest = events?
            .Where(static item => item.OutputTokenDelta > 0 || item.PromptCacheTokenDelta > 0)
            .OrderByDescending(static item => item.Sequence)
            .FirstOrDefault();
        return latest?.SourceApplication ?? string.Empty;
    }

    private static string BuildTransparentProxyIngressSummary(IReadOnlyList<TransparentProxyIngressMetricsSnapshot>? ingresses)
    {
        if (ingresses is null || ingresses.Count == 0)
        {
            return "入口来源：等待请求。";
        }

        var top = ingresses
            .OrderByDescending(static item => item.Requests)
            .ThenByDescending(static item => item.OutputTokens)
            .Take(3)
            .Select(static item =>
            {
                var tokens = item.OutputTokens > 0 ? $"，{FormatCompactTokenCount(item.OutputTokens)} tokens" : string.Empty;
                return $"{item.SourceApplication} {item.Requests} 请求{tokens}";
            });
        return "入口来源：" + string.Join("；", top) + "。";
    }

    private static string FormatTokensPerSecond(double value)
        => value <= 0.05d
            ? "0 tok/s"
            : $"{value.ToString("0.#", CultureInfo.InvariantCulture)} tok/s";

    private static string FormatRate(long numerator, long denominator)
        => denominator <= 0
            ? "0%"
            : $"{(numerator * 100d / denominator).ToString("0.#", CultureInfo.InvariantCulture)}%";

    private static string FormatCompactTokenCount(long value)
    {
        if (value >= 1_000_000)
        {
            return $"{(value / 1_000_000d).ToString("0.#", CultureInfo.InvariantCulture)}m";
        }

        if (value >= 10_000)
        {
            return $"{(value / 1_000d).ToString("0.#", CultureInfo.InvariantCulture)}k";
        }

        return value.ToString(CultureInfo.InvariantCulture);
    }

    private static ProxyEndpointProtocolProbeResult BuildTransparentProxyClientApplyProbeResult(
        IReadOnlyList<TransparentProxyRoute> routes,
        ProxyEndpointSettings settings)
    {
        var responsesSupported = routes.Any(static route => route.ResponsesSupported != false);
        var anthropicSupported = routes.Any(static route => route.AnthropicMessagesSupported != false);
        var chatSupported = routes.Any(static route => route.ChatCompletionsSupported != false);
        var preferred = responsesSupported
            ? ProxyWireApiProbeService.ResponsesWireApi
            : anthropicSupported
                ? ProxyWireApiProbeService.AnthropicMessagesWireApi
                : ProxyWireApiProbeService.ChatCompletionsWireApi;

        return new ProxyEndpointProtocolProbeResult(
            DateTimeOffset.Now,
            settings.BaseUrl,
            settings.Model,
            chatSupported,
            responsesSupported,
            anthropicSupported,
            preferred,
            "RelayBench 本地透明代理会在本机接管请求，并继续由路由队列负责自动选路、fallback、缓存、限速和脱敏日志。",
            null);
    }

    private static string ResolveTransparentProxyClientDefaultModel(IReadOnlyList<TransparentProxyRoute> routes)
    {
        foreach (var route in routes
                     .OrderBy(static item => item.Priority > 0 ? item.Priority : int.MaxValue)
                     .ThenBy(static item => item.Name, StringComparer.OrdinalIgnoreCase))
        {
            foreach (var mapping in route.ModelMappings)
            {
                var model = mapping.EffectiveAlias;
                if (string.IsNullOrWhiteSpace(model))
                {
                    continue;
                }

                var prefix = route.Prefix.Trim().Trim('/');
                return string.IsNullOrWhiteSpace(prefix)
                    ? model.Trim()
                    : $"{prefix}/{model.Trim()}";
            }

            if (!string.IsNullOrWhiteSpace(route.Model))
            {
                return route.Model.Trim();
            }
        }

        return "relaybench-auto";
    }

    private string BuildTransparentProxyAdvancedLabContext(IReadOnlyList<TransparentProxyRoute> routes, string model)
    {
        var routeCount = routes.Count;
        var modelCount = ResolveTransparentProxyClientModels(routes).Count;
        var strategyName = TransparentProxyRouteStrategyOptions
            .FirstOrDefault(option => string.Equals(option.Key, TransparentProxyRouteStrategyKey, StringComparison.OrdinalIgnoreCase))
            ?.DisplayName ?? TransparentProxyRouteStrategyKey;
        var protocolSummary = routes.Any(static route =>
                route.ResponsesSupported.HasValue ||
                route.AnthropicMessagesSupported.HasValue ||
                route.ChatCompletionsSupported.HasValue)
            ? BuildTransparentProxyProtocolContext(routes)
            : "协议：Responses → Anthropic Messages → OpenAI Chat 自动探测回退";
        var cacheText = TransparentProxyEnableCache ? "缓存开启" : "缓存关闭";
        var fallbackText = TransparentProxyEnableFallback ? "fallback 开启" : "fallback 关闭";
        return $"透明代理上下文：{routeCount} 个节点，{modelCount} 个模型入口，当前模型 {model}，策略 {strategyName}，{fallbackText}，{cacheText}，{protocolSummary}。";
    }

    private static string BuildTransparentProxyProtocolContext(IReadOnlyList<TransparentProxyRoute> routes)
    {
        static string Format(bool? value)
            => value == true ? "可用" : value == false ? "不可用" : "待探测";

        var responses = MergeSupport(routes.Select(static route => route.ResponsesSupported));
        var anthropic = MergeSupport(routes.Select(static route => route.AnthropicMessagesSupported));
        var chat = MergeSupport(routes.Select(static route => route.ChatCompletionsSupported));
        return $"协议：Responses {Format(responses)} / Anthropic {Format(anthropic)} / OpenAI Chat {Format(chat)}";
    }

    private static bool? MergeSupport(IEnumerable<bool?> values)
    {
        var materialized = values.ToArray();
        if (materialized.Any(static value => value == true))
        {
            return true;
        }

        return materialized.Any(static value => value is null) ? null : false;
    }

    private static IReadOnlyList<string> ResolveTransparentProxyClientModels(IReadOnlyList<TransparentProxyRoute> routes)
    {
        List<string> models = [];
        foreach (var route in routes
                     .OrderBy(static item => item.Priority > 0 ? item.Priority : int.MaxValue)
                     .ThenBy(static item => item.Name, StringComparer.OrdinalIgnoreCase))
        {
            var prefix = route.Prefix.Trim().Trim('/');
            foreach (var mapping in route.ModelMappings)
            {
                var model = mapping.EffectiveAlias;
                if (string.IsNullOrWhiteSpace(model))
                {
                    continue;
                }

                models.Add(string.IsNullOrWhiteSpace(prefix) ? model.Trim() : $"{prefix}/{model.Trim()}");
            }

            if (route.ModelMappings.Count == 0 && !string.IsNullOrWhiteSpace(route.Model))
            {
                models.Add(string.IsNullOrWhiteSpace(prefix) ? route.Model.Trim() : $"{prefix}/{route.Model.Trim()}");
            }
        }

        return models
            .Where(static item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(200)
            .ToArray();
    }

    private static string ResolveBatchModelFromTransparentProxyRoute(TransparentProxyRoute route)
    {
        var model = route.ModelMappings.FirstOrDefault()?.Name;
        if (!string.IsNullOrWhiteSpace(model))
        {
            return model.Trim();
        }

        return string.IsNullOrWhiteSpace(route.Model)
            ? "relaybench-auto"
            : route.Model.Trim();
    }

    private string ResolveTransparentProxyClientApiKey(IReadOnlyList<TransparentProxyRoute> routes)
    {
        var routeKeys = routes
            .Select(static route => route.ApiKey?.Trim() ?? string.Empty)
            .Where(static key => !string.IsNullOrWhiteSpace(key))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (routeKeys.Length > 0)
        {
            return "relaybench-local-proxy";
        }

        if (!string.IsNullOrWhiteSpace(ProxyApiKey))
        {
            return ProxyApiKey.Trim();
        }

        if (!string.IsNullOrWhiteSpace(ApplicationCenterApiKey))
        {
            return ApplicationCenterApiKey.Trim();
        }

        return "relaybench-local-proxy";
    }

    private int ParseTransparentProxyPort()
        => ParseBoundedInt(TransparentProxyPortText, fallback: 17880, min: 1024, max: 65535);

    private void NotifyTransparentProxyEndpointChanged()
    {
        OnPropertyChanged(nameof(TransparentProxyLocalEndpoint));
        OnPropertyChanged(nameof(TransparentProxyOpenAiChatEndpoint));
        OnPropertyChanged(nameof(TransparentProxyResponsesEndpoint));
        OnPropertyChanged(nameof(TransparentProxyAnthropicEndpoint));
        OnPropertyChanged(nameof(TransparentProxyModelsEndpoint));
        OnPropertyChanged(nameof(TransparentProxyHealthEndpoint));
        OnPropertyChanged(nameof(TransparentProxyPowerShellEnvSnippet));
        OnPropertyChanged(nameof(TransparentProxyCmdEnvSnippet));
        OnPropertyChanged(nameof(TransparentProxyStatusSummary));
    }
}
