using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Text.Json;
using NetTest.Core.Models;

namespace NetTest.Core.Services;

public sealed partial class ProxyDiagnosticsService
{
    private static readonly string[] InterestingHeaderNames =
    [
        "server",
        "via",
        "cf-ray",
        "cf-cache-status",
        "request-id",
        "x-request-id",
        "openai-request-id",
        "anthropic-request-id",
        "trace-id",
        "x-trace-id",
        "x-amzn-trace-id",
        "x-cache",
        "openai-processing-ms",
        "content-type",
        "alt-svc"
    ];

    public async Task<ProxyDiagnosticsResult> RunAsync(
        ProxyEndpointSettings settings,
        IProgress<ProxyDiagnosticsLiveProgress>? progress = null,
        CancellationToken cancellationToken = default,
        int streamThroughputSampleCount = 1)
    {
        if (!TryValidateSettings(settings, out var normalizedSettings, out var baseUri, out var error))
        {
            return BuildValidationFailure(settings, error);
        }

        return await RunSingleCoreAsync(normalizedSettings, baseUri, progress, cancellationToken, streamThroughputSampleCount);
    }

    public async Task<ProxyModelCatalogResult> FetchModelsAsync(
        ProxyEndpointSettings settings,
        CancellationToken cancellationToken = default)
    {
        if (!TryValidateSettings(settings, out var normalizedSettings, out var baseUri, out var error))
        {
            return new ProxyModelCatalogResult(
                DateTimeOffset.Now,
                settings.BaseUrl,
                false,
                null,
                0,
                Array.Empty<string>(),
                null,
                "模型列表拉取失败。",
                error);
        }

        using var client = CreateClient(baseUri, normalizedSettings);
        var resolvedAddresses = await ResolveAddressesAsync(baseUri, cancellationToken);
        var modelPath = BuildApiPath(baseUri, "models");
        var modelsProbe = await ProbeModelsAsync(client, modelPath, normalizedSettings.Model, cancellationToken);
        var edgeObservation = BuildEdgeObservation(baseUri, new[] { modelsProbe.ScenarioResult }, resolvedAddresses);
        var traceability = BuildTraceabilityObservation(new[] { modelsProbe.ScenarioResult });
        var summary = modelsProbe.ScenarioResult.Success
            ? $"模型列表拉取成功，共解析 {modelsProbe.ModelCount} 个模型。{edgeObservation.CdnSummary}"
            : $"模型列表拉取失败。{modelsProbe.ScenarioResult.Summary}{(string.IsNullOrWhiteSpace(edgeObservation.CdnSummary) ? string.Empty : $" {edgeObservation.CdnSummary}")}";

        return new ProxyModelCatalogResult(
            DateTimeOffset.Now,
            baseUri.ToString(),
            modelsProbe.ScenarioResult.Success,
            modelsProbe.ScenarioResult.StatusCode,
            modelsProbe.ModelCount,
            modelsProbe.SampleModels,
            modelsProbe.ScenarioResult.Latency,
            summary,
            modelsProbe.ScenarioResult.Error,
            modelsProbe.ScenarioResult.ResponseHeaders,
            resolvedAddresses,
            edgeObservation.CdnProvider,
            edgeObservation.EdgeSignature,
            edgeObservation.CdnSummary,
            traceability.RequestId,
            traceability.TraceId,
            traceability.Summary);
    }

    public async Task<ProxyStabilityResult> RunSeriesAsync(
        ProxyEndpointSettings settings,
        int requestedRounds,
        int delayMilliseconds,
        IProgress<string>? progress = null,
        IProgress<ProxyDiagnosticsResult>? roundProgress = null,
        CancellationToken cancellationToken = default)
    {
        var clampedRounds = Math.Clamp(requestedRounds, 1, 50);
        var clampedDelay = Math.Clamp(delayMilliseconds, 0, 30_000);

        if (!TryValidateSettings(settings, out var normalizedSettings, out var baseUri, out var error))
        {
            return new ProxyStabilityResult(
                DateTimeOffset.Now,
                settings.BaseUrl,
                clampedRounds,
                0,
                clampedDelay,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                null,
                null,
                0,
                "参数校验失败",
                error,
                Array.Empty<ProxyDiagnosticsResult>());
        }

        List<ProxyDiagnosticsResult> rounds = new(clampedRounds);
        for (var index = 0; index < clampedRounds; index++)
        {
            progress?.Report($"正在运行中转站稳定性第 {index + 1}/{clampedRounds} 轮...");
            var roundResult = await RunSingleCoreAsync(normalizedSettings, baseUri, null, cancellationToken);
            rounds.Add(roundResult);
            roundProgress?.Report(roundResult);

            if (index < clampedRounds - 1 && clampedDelay > 0)
            {
                await Task.Delay(clampedDelay, cancellationToken);
            }
        }

        return BuildStabilityResult(normalizedSettings, clampedRounds, clampedDelay, rounds);
    }

    public ProxyStabilityResult BuildStabilitySnapshot(
        ProxyEndpointSettings settings,
        int requestedRounds,
        int delayMilliseconds,
        IReadOnlyList<ProxyDiagnosticsResult> rounds)
    {
        var clampedDelay = Math.Clamp(delayMilliseconds, 0, 30_000);
        var effectiveRequestedRounds = Math.Max(Math.Clamp(requestedRounds, 1, 200), rounds.Count);

        if (!TryValidateSettings(settings, out var normalizedSettings, out _, out var error))
        {
            return new ProxyStabilityResult(
                DateTimeOffset.Now,
                settings.BaseUrl,
                effectiveRequestedRounds,
                rounds.Count,
                clampedDelay,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                null,
                null,
                0,
                "参数校验失败",
                error,
                rounds);
        }

        return BuildStabilityResult(normalizedSettings, effectiveRequestedRounds, clampedDelay, rounds);
    }

    private static bool TryValidateSettings(
        ProxyEndpointSettings settings,
        out ProxyEndpointSettings normalizedSettings,
        out Uri baseUri,
        out string error)
    {
        normalizedSettings = settings;
        baseUri = null!;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(settings.BaseUrl))
        {
            error = "必须填写中转站地址（Base URL）。";
            return false;
        }

        if (string.IsNullOrWhiteSpace(settings.ApiKey))
        {
            error = "必须填写接口密钥（API Key）。";
            return false;
        }

        if (!Uri.TryCreate(settings.BaseUrl.Trim(), UriKind.Absolute, out var parsedBaseUri))
        {
            error = "中转站地址（Base URL）不是有效的绝对 URI。";
            return false;
        }

        baseUri = EnsureTrailingSlash(parsedBaseUri);
        normalizedSettings = new ProxyEndpointSettings(
            baseUri.ToString(),
            settings.ApiKey.Trim(),
            string.IsNullOrWhiteSpace(settings.Model) ? string.Empty : settings.Model.Trim(),
            settings.IgnoreTlsErrors,
            Math.Clamp(settings.TimeoutSeconds, 5, 120));

        return true;
    }


    private static ProxyDiagnosticsResult BuildValidationFailure(ProxyEndpointSettings settings, string message)
    {
        var scenarioResults = new[]
        {
            new ProxyProbeScenarioResult(
                ProxyProbeScenarioKind.Models,
                "参数校验",
                "失败",
                false,
                null,
                null,
                null,
                null,
                false,
                0,
                null,
                message,
                null,
                ProxyFailureKind.ConfigurationInvalid,
                "参数校验",
                message)
        };

        return new ProxyDiagnosticsResult(
            DateTimeOffset.Now,
            settings.BaseUrl,
            settings.Model,
            null,
            false,
            null,
            0,
            Array.Empty<string>(),
            null,
            false,
            null,
            null,
            null,
            false,
            null,
            null,
            null,
            null,
            message,
            message,
            scenarioResults,
            ProxyFailureKind.ConfigurationInvalid,
            "参数校验",
            "不可用",
            "请先补全中转站地址、接口密钥和模型配置，再重新测试。",
            message,
            "本次未采集到响应头信息。");
    }

}
