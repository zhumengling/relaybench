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
{
    [RelayCommand]
    private async Task StartTransparentProxyAsync()
        => await StartProxyAsync();

    [RelayCommand]
    private async Task StartProxyAsync()
    {
        StatusText = "正在启动...";
        try
        {
            await LoadStrategiesAsync();
            await LoadRoutesAsync();
            var routes = BuildRuntimeRoutes();
            var port = ParseListenPort();
            var config = new TransparentProxyServerConfig(
                Port: port,
                Routes: routes,
                RateLimitPerMinute: Math.Max(1, RateLimitPerMinute),
                MaxConcurrency: Math.Max(1, MaxConcurrency),
                EnableFallback: EnableFallback,
                EnableCache: EnableResponseCache,
                CacheTtlSeconds: Math.Max(0, CacheTtlSeconds),
                RewriteModel: ModelRewriteRules.Count > 0,
                IgnoreTlsErrors: IgnoreTlsErrors,
                UpstreamTimeoutSeconds: Math.Max(1, UpstreamTimeoutSeconds))
            {
                RouteStrategy = TransparentProxyRouteStrategies.Normalize(SelectedRouteStrategy),
                SessionAffinityTtlSeconds = SessionAffinityTtlSeconds,
                ModelCooldownSeconds = ModelCooldownSeconds,
                RequestRetry = ResolveEffectiveRequestRetry(null),
                MaxRetryIntervalSeconds = ResolveEffectiveMaxRetryInterval(null),
                AllowRemoteManagement = AllowRemoteManagement && !string.IsNullOrWhiteSpace(ManagementSecret),
                ManagementSecret = ManagementSecret.Trim()
            };
            await _proxyService.StartAsync(config);
            IsRunning = _proxyService.IsRunning;
            ListenAddress = $"0.0.0.0:{port}";
            StatusText = IsRunning
                ? $"已在 0.0.0.0:{port} 运行，路由 {routes.Count} 条，策略 {Strategies.Count} 条"
                : "启动失败";
            await RunHistoryRecorder.RecordAsync(
                "透明代理",
                ListenAddress,
                StatusText,
                routes.Count > 0 ? 100 : 60,
                payloadJson: BuildCurrentProxyHistoryPayload(_lastMetricsSnapshot));
            _lastProxyHistoryTotalRequests = _lastMetricsSnapshot?.TotalRequests ?? -1;
            _lastProxyHistoryTotalTokens = ResolveTotalTokens(_lastMetricsSnapshot);
            StartProxyHistoryTimer();
        }
        catch (Exception ex)
        {
            StatusText = $"错误: {ex.Message}";
        }
    }

    private void StartProxyHistoryTimer()
    {
        _proxyHistoryTimer?.Dispose();
        _proxyHistoryTimer = new Timer(
            static state => _ = ((TransparentProxyViewModel)state!).FlushProxyHistorySnapshotAsync(),
            this,
            TimeSpan.FromMinutes(1),
            TimeSpan.FromMinutes(1));
    }

    private void StopProxyHistoryTimer()
    {
        _proxyHistoryTimer?.Dispose();
        _proxyHistoryTimer = null;
    }

    private async Task FlushProxyHistorySnapshotAsync()
    {
        var snapshot = _lastMetricsSnapshot;
        if (!IsRunning || snapshot is null)
        {
            return;
        }

        var totalTokens = ResolveTotalTokens(snapshot);
        if (snapshot.TotalRequests == _lastProxyHistoryTotalRequests &&
            totalTokens == _lastProxyHistoryTotalTokens)
        {
            return;
        }

        if (!await _proxyHistoryWriteLock.WaitAsync(0).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            snapshot = _lastMetricsSnapshot;
            if (!IsRunning || snapshot is null)
            {
                return;
            }

            totalTokens = ResolveTotalTokens(snapshot);
            if (snapshot.TotalRequests == _lastProxyHistoryTotalRequests &&
                totalTokens == _lastProxyHistoryTotalTokens)
            {
                return;
            }

            var score = snapshot.TotalRequests > 0
                ? (int)Math.Clamp((double)snapshot.SuccessRequests / snapshot.TotalRequests * 100, 0, 100)
                : 0;
            await RunHistoryRecorder.RecordAsync(
                "透明代理",
                ListenAddress,
                $"运行快照：请求 {snapshot.TotalRequests}，Token {FormatTokenCount(totalTokens)}，成功率 {FormatPercent(snapshot.SuccessRequests, snapshot.TotalRequests)}",
                score,
                payloadJson: BuildCurrentProxyHistoryPayload(snapshot)).ConfigureAwait(false);
            _lastProxyHistoryTotalRequests = snapshot.TotalRequests;
            _lastProxyHistoryTotalTokens = totalTokens;
        }
        catch (Exception ex)
        {
            AppDiagnosticLog.Write("TransparentProxy.HistorySnapshot", ex);
        }
        finally
        {
            _proxyHistoryWriteLock.Release();
        }
    }

    private static long ResolveTotalTokens(TransparentProxyMetricsSnapshot? snapshot)
        => snapshot is null ? -1 : snapshot.TotalInputTokens + snapshot.TotalOutputTokens;

    private string BuildCurrentProxyHistoryPayload(TransparentProxyMetricsSnapshot? metrics)
    {
        var credentials = _codexOAuthService.GetCredentials();
        return BuildProxyHistoryPayload(
            metrics,
            Routes,
            credentials.Count,
            credentials.Count(static item => item.State == CodexOAuthCredentialState.Ready),
            AllowRemoteManagement && !string.IsNullOrWhiteSpace(ManagementSecret),
            !string.IsNullOrWhiteSpace(ManagementSecret),
            ManagementSecuritySummary,
            Math.Max(1, RateLimitPerMinute),
            Math.Max(1, MaxConcurrency),
            EnableFallback,
            EnableResponseCache,
            Math.Max(0, CacheTtlSeconds),
            Math.Max(1, UpstreamTimeoutSeconds),
            IgnoreTlsErrors,
            ResolveEffectiveRequestRetry(null),
            ResolveEffectiveMaxRetryInterval(null));
    }

    [RelayCommand]
    private async Task StopProxyAsync()
    {
        StatusText = "正在停止...";
        try
        {
            StopProxyHistoryTimer();
            await FlushProxyHistorySnapshotAsync();
            await _proxyService.StopAsync();
            IsRunning = false;
            StatusText = "已停止";
            await RunHistoryRecorder.RecordAsync(
                "透明代理",
                ListenAddress,
                "透明代理已停止",
                100,
                payloadJson: BuildCurrentProxyHistoryPayload(_lastMetricsSnapshot));
        }
        catch (Exception ex)
        {
            StatusText = $"错误: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task StopTransparentProxyAsync()
        => await StopProxyAsync();

    public async Task ToggleProxyAsync()
    {
        if (IsRunning)
        {
            await StopProxyAsync();
        }
        else
        {
            await StartProxyAsync();
        }
    }

    [RelayCommand]
    private void ResetTokenMeter()
    {
        _proxyService.ResetTokenTelemetry();
        _latestTokenMeterTotalOutputTokens = 0;
        _latestTokenMeterTokensPerSecond = 0d;
        _latestTokenMeterActivityAt = null;
        _latestTokenMeterSource = string.Empty;
        RefreshTokenMeterIdleState();
        TokenMeterMetricsSummary = _latestTokenMeterIsRunning
            ? $"入口 {LocalEndpoint} | 输出 0 tokens | 0 tok/s | 计数已重置"
            : "透明代理未运行，Token 计计数已重置。";
        StatusText = _latestTokenMeterIsRunning
            ? "Token 计计数已重置"
            : "Token 计计数已重置；透明代理启动后会继续统计";
    }

    public void RefreshTokenMeterIdleState()
        => ApplyTokenMeterState(
            _latestTokenMeterIsRunning,
            _latestTokenMeterTotalOutputTokens,
            _latestTokenMeterTokensPerSecond,
            _latestTokenMeterActivityAt,
            _latestTokenMeterSource);

    private void ApplyTokenMeterState(
        bool isRunning,
        long totalOutputTokens,
        double tokensPerSecond,
        DateTimeOffset? lastTokenActivityAt,
        string? sourceApplication)
    {
        var now = DateTimeOffset.UtcNow;
        var isStreaming = isRunning &&
                          tokensPerSecond >= 0.5d &&
                          lastTokenActivityAt is not null &&
                          (now - lastTokenActivityAt.Value).TotalSeconds <= 5d;
        var sourceText = NormalizeTokenMeterSource(sourceApplication);

        if (isStreaming)
        {
            TokenMeterPrimaryText = FormatTokenSpeed(tokensPerSecond);
            TokenMeterSecondaryText = ResolveTokenMeterSecondaryText(
                now,
                totalOutputTokens,
                tokensPerSecond,
                sourceText,
                isStreaming: true);
            TokenMeterModeText = "运行";
            TokenMeterTone = TokenMeterVisualTone.Live;
            return;
        }

        TokenMeterPrimaryText = $"{FormatTokenCount(totalOutputTokens)} tokens";
        TokenMeterSecondaryText = isRunning
            ? ResolveTokenMeterSecondaryText(
                now,
                totalOutputTokens,
                tokensPerSecond,
                sourceText,
                isStreaming: false)
            : "等待代理";
        TokenMeterModeText = isRunning ? "空闲" : "等待";
        TokenMeterTone = isRunning ? TokenMeterVisualTone.Idle : TokenMeterVisualTone.Wait;
    }

    private static string ResolveTokenMeterSecondaryText(
        DateTimeOffset now,
        long totalOutputTokens,
        double tokensPerSecond,
        string sourceText,
        bool isStreaming)
    {
        var phase = (now.ToUnixTimeSeconds() / 4) % 3;
        if (isStreaming)
        {
            return phase switch
            {
                1 => sourceText,
                2 => $"累计 {FormatTokenCount(totalOutputTokens)}",
                _ => "tokens / sec"
            };
        }

        return phase switch
        {
            1 => FormatTokenSpeed(tokensPerSecond),
            2 => sourceText,
            _ => "阶段累计"
        };
    }

    private static string ResolveLatestTokenMeterSource(IReadOnlyList<TransparentProxyUsageEvent>? events)
    {
        var latest = events?
            .Where(static item => item.OutputTokenDelta > 0 || item.PromptCacheTokenDelta > 0 || item.InputTokenDelta > 0)
            .OrderByDescending(static item => item.Sequence)
            .FirstOrDefault();

        if (latest is null)
        {
            return string.Empty;
        }

        if (!string.IsNullOrWhiteSpace(latest.SourceApplication) &&
            !string.Equals(latest.SourceApplication, "-", StringComparison.Ordinal))
        {
            return latest.SourceApplication;
        }

        if (!string.IsNullOrWhiteSpace(latest.RouteName) &&
            !string.Equals(latest.RouteName, "-", StringComparison.Ordinal))
        {
            return latest.RouteName;
        }

        return latest.WireApi;
    }

    private static string NormalizeTokenMeterSource(string? sourceApplication)
        => string.IsNullOrWhiteSpace(sourceApplication) || string.Equals(sourceApplication, "-", StringComparison.Ordinal)
            ? "本地统一入口"
            : sourceApplication.Trim();

    private string BuildTokenMeterMetricsSummary(TransparentProxyMetricsSnapshot metrics)
        => $"入口 {LocalEndpoint} | 输出 {FormatTokenCount(metrics.TotalOutputTokens)} tokens | " +
           $"{FormatTokenSpeed(metrics.TokensPerSecond)} | 活跃 {metrics.ActiveRequests} | " +
           $"请求 {metrics.TotalRequests} | 成功率 {FormatPercent(metrics.SuccessRequests, metrics.TotalRequests)} | " +
           $"缓存 {CalculateCacheHitRateText(metrics)}";

    private static string FormatTokenCount(long count)
    {
        if (count >= 1_000_000) return $"{count / 1_000_000.0:F1}M";
        if (count >= 1_000) return $"{count / 1_000.0:F1}K";
        return count.ToString();
    }

    internal static string BuildProxyHistoryPayload(
        TransparentProxyMetricsSnapshot? metrics,
        IReadOnlyList<RouteDefinition> configuredRoutes,
        int codexOAuthCredentialCount = 0,
        int readyCodexOAuthCredentialCount = 0,
        bool allowRemoteManagement = false,
        bool managementSecretConfigured = false,
        string managementSecuritySummary = "",
        int rateLimitPerMinute = 600,
        int maxConcurrency = 32,
        bool enableFallback = true,
        bool enableCache = true,
        int cacheTtlSeconds = 600,
        int upstreamTimeoutSeconds = 60,
        bool ignoreTlsErrors = false,
        int requestRetry = 1,
        int maxRetryIntervalSeconds = 8)
    {
        object[] routeHits = metrics?.Routes
            .Select(static route => new
            {
                route.Id,
                route.Name,
                route.Sent,
                route.Success,
                route.Failed,
                SuccessRate = route.Sent > 0 ? (double)route.Success / route.Sent * 100 : 0,
                FailureRate = route.Sent > 0 ? (double)route.Failed / route.Sent * 100 : 0,
                route.LastStatusCode,
                route.LastLatencyMs,
                route.ConsecutiveFailures,
                route.ConsecutiveSuccesses,
                route.CircuitState,
                CooldownSeconds = Math.Max(0, (route.CircuitOpenUntil - DateTimeOffset.UtcNow).TotalSeconds),
                PreferredProtocol = route.PreferredWireApi,
                ProtocolSummary = BuildRouteProtocolSummary(route),
                route.ChatCompletionsSupported,
                route.ResponsesSupported,
                route.AnthropicMessagesSupported,
                Cooldown = route.CircuitOpenUntil > DateTimeOffset.UtcNow,
                ModelCooldowns = route.ModelCooldowns?
                    .Select(static cooldown => new
                    {
                        cooldown.ModelName,
                        FailureCount = cooldown.ConsecutiveFailures,
                        CooldownSeconds = Math.Max(0, (cooldown.CooldownUntil - DateTimeOffset.UtcNow).TotalSeconds)
                    })
                    .ToArray() ?? []
            })
            .Cast<object>()
            .ToArray() ?? Array.Empty<object>();

        object[] modelPools = metrics?.ModelPools?
            .Select(static pool => new
            {
                pool.ModelName,
                pool.MemberCount,
                pool.HealthyMembers,
                pool.OpenCircuitMembers,
                pool.Sent,
                pool.Success,
                pool.Failed,
                SuccessRate = pool.Sent > 0 ? (double)pool.Success / pool.Sent * 100 : 0,
                pool.BestLatencyMs,
                pool.ProtocolSummary,
                Members = pool.Members
                    .Select(static member => new
                    {
                        member.RouteId,
                        member.RouteName,
                        ModelName = member.UpstreamModel,
                        EffectiveModel = member.ClientModel,
                        member.Sent,
                        member.Success,
                        member.Failed,
                        member.LastStatusCode,
                        member.LastLatencyMs,
                        member.CircuitState,
                        CircuitCooldownSeconds = Math.Max(0, (member.CircuitOpenUntil - DateTimeOffset.UtcNow).TotalSeconds),
                        ModelCooldownSeconds = Math.Max(0, (member.ModelCooldownUntil - DateTimeOffset.UtcNow).TotalSeconds)
                    })
                    .ToArray()
            })
            .Cast<object>()
            .ToArray() ?? Array.Empty<object>();

        var cacheHitRate = metrics is null ? 0 : CalculateCacheHitPercentValue(metrics);

        var payload = new
        {
            Schema = "transparent-proxy-dashboard-v1",
            CapturedAtUtc = DateTime.UtcNow,
            TotalRequests = metrics?.TotalRequests ?? 0,
            SuccessRequests = metrics?.SuccessRequests ?? 0,
            FailedRequests = metrics?.FailedRequests ?? 0,
            FallbackRequests = metrics?.FallbackRequests ?? 0,
            RateLimitedRequests = metrics?.RateLimitedRequests ?? 0,
            SuccessRate = metrics is { TotalRequests: > 0 }
                ? (double)metrics.SuccessRequests / metrics.TotalRequests * 100
                : 0,
            ErrorRate = metrics is { TotalRequests: > 0 }
                ? (double)metrics.FailedRequests / metrics.TotalRequests * 100
                : 0,
            CacheHits = metrics?.CacheHits ?? 0,
            CacheHitRate = cacheHitRate,
            CacheBreakdown = new
            {
                ResponseCacheHits = metrics?.ResponseCacheHits ?? 0,
                ResponseCacheMisses = metrics?.ResponseCacheMisses ?? 0,
                ResponseCacheStores = metrics?.ResponseCacheStores ?? 0,
                ResponseCacheEvictions = metrics?.ResponseCacheEvictions ?? 0,
                ResponseCacheEntryCount = metrics?.ResponseCacheEntryCount ?? 0,
                PromptSessionCacheHits = metrics?.PromptSessionCacheHits ?? 0,
                PromptSessionCacheMisses = metrics?.PromptSessionCacheMisses ?? 0,
                PromptSessionCacheEntryCount = metrics?.PromptSessionCacheEntryCount ?? 0,
                ModelListCacheEntryCount = metrics?.ModelListCacheEntryCount ?? 0,
                InFlightKeys = metrics?.ResponseCacheInFlightKeys ?? 0,
                LeaseWaits = metrics?.ResponseCacheLeaseWaits ?? 0
            },
            TotalInputTokens = metrics?.TotalInputTokens ?? 0,
            TotalOutputTokens = metrics?.TotalOutputTokens ?? 0,
            PromptCacheTokens = metrics?.PromptCacheTokens ?? 0,
            CodexOAuthCredentialCount = Math.Max(0, codexOAuthCredentialCount),
            ReadyCodexOAuthCredentialCount = Math.Max(0, readyCodexOAuthCredentialCount),
            Management = new
            {
                AllowRemote = allowRemoteManagement,
                SecretConfigured = managementSecretConfigured,
                Summary = string.IsNullOrWhiteSpace(managementSecuritySummary)
                    ? (allowRemoteManagement ? "远程管理" : "仅本机管理")
                    : managementSecuritySummary
            },
            RuntimeConfig = new
            {
                RateLimitPerMinute = Math.Max(1, rateLimitPerMinute),
                MaxConcurrency = Math.Max(1, maxConcurrency),
                EnableFallback = enableFallback,
                EnableCache = enableCache,
                CacheTtlSeconds = Math.Max(0, cacheTtlSeconds),
                UpstreamTimeoutSeconds = Math.Max(1, upstreamTimeoutSeconds),
                IgnoreTlsErrors = ignoreTlsErrors,
                RequestRetry = Math.Clamp(requestRetry, 0, 10),
                MaxRetryIntervalSeconds = Math.Clamp(maxRetryIntervalSeconds, 1, 300)
            },
            P50LatencyMs = metrics?.P50LatencyMs ?? 0,
            P95LatencyMs = metrics?.P95LatencyMs ?? 0,
            OutputTokensPerSecond = metrics?.TokensPerSecond ?? 0,
            ActiveConnections = metrics?.ActiveRequests ?? 0,
            RouteCount = metrics?.Routes.Count ?? configuredRoutes.Count(static route => route.Enabled),
            ModelPoolCount = metrics?.ModelPools?.Count ?? 0,
            LastStatusCodeByRoute = metrics?.Routes
                .Where(static route => route.LastStatusCode > 0)
                .Select(static route => new
                {
                    route.Id,
                    route.Name,
                    route.LastStatusCode,
                    route.Failed
                })
                .ToArray() ?? [],
            RouteHits = routeHits,
            ModelPoolSummary = modelPools,
            ConfiguredRoutes = configuredRoutes.Select(static route => new
            {
                route.Id,
                route.Name,
                route.UpstreamUrl,
                route.Priority,
                route.ModelFilter,
                route.Enabled,
                route.Prefix,
                route.OutboundProxy,
                route.RequestRetry,
                route.MaxRetryIntervalSeconds,
                route.ModelCooldownSeconds,
                route.ExcludedModelPatterns,
                route.PreferredWireApi,
                HasHeaders = !string.IsNullOrWhiteSpace(route.HeadersText),
                route.AuthMode,
                route.OAuthProvider,
                route.OAuthCredentialId,
                route.CodexBackendBaseUrl,
                route.CodexOAuthFastMode,
                HasPayloadRules = !string.IsNullOrWhiteSpace(route.PayloadRulesText)
            }).ToArray()
        };

        return JsonSerializer.Serialize(payload);
    }

}
