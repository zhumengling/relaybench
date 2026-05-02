using RelayBench.Core.AdvancedTesting.Clients;
using RelayBench.Core.AdvancedTesting.Models;
using RelayBench.Core.AdvancedTesting.Redaction;
using RelayBench.Core.AdvancedTesting.Scoring;
using RelayBench.Core.Models;
using RelayBench.Core.Services;

namespace RelayBench.Core.AdvancedTesting.Runners;

public sealed class AdvancedTestRunner : IAdvancedTestRunner
{
    private readonly IReadOnlyList<IAdvancedTestCase> _cases;
    private readonly IScoreCalculator _scoreCalculator;
    private readonly ISensitiveDataRedactor _redactor;

    public AdvancedTestRunner()
        : this(AdvancedTestCatalog.CreateDefaultCases(), new AdvancedScoreCalculator(), new SensitiveDataRedactor())
    {
    }

    public AdvancedTestRunner(
        IReadOnlyList<IAdvancedTestCase> cases,
        IScoreCalculator scoreCalculator,
        ISensitiveDataRedactor redactor)
    {
        _cases = cases;
        _scoreCalculator = scoreCalculator;
        _redactor = redactor;
        Suites = AdvancedTestCatalog.CreateDefaultSuites(_cases);
    }

    public IReadOnlyList<AdvancedTestSuiteDefinition> Suites { get; }

    public async Task<AdvancedTestRunResult> RunAsync(
        AdvancedTestPlan plan,
        IProgress<AdvancedTestProgress>? progress,
        CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.Now;
        var selectedIds = new HashSet<string>(plan.SelectedTestIds, StringComparer.OrdinalIgnoreCase);
        var runnableCases = _cases.Where(item => selectedIds.Contains(item.Definition.TestId)).ToArray();
        List<AdvancedTestCaseResult> results = [];

        if (runnableCases.Length == 0)
        {
            return new AdvancedTestRunResult(startedAt, DateTimeOffset.Now, plan.Endpoint, results, _scoreCalculator.Calculate(results));
        }

        var endpoint = await ResolveEndpointWireApiAsync(plan.Endpoint, progress, cancellationToken).ConfigureAwait(false);
        var context = new AdvancedTestRunContext(endpoint, plan.Options, startedAt);
        using var client = new HttpModelClient(endpoint);
        for (var index = 0; index < runnableCases.Length; index++)
        {
            var testCase = runnableCases[index];
            cancellationToken.ThrowIfCancellationRequested();

            var startPercent = index * 100d / runnableCases.Length;
            progress?.Report(new AdvancedTestProgress(
                testCase.Definition.TestId,
                testCase.Definition.DisplayName,
                AdvancedTestStatus.Running,
                startPercent,
                $"正在运行 {testCase.Definition.DisplayName}..."));

            AdvancedTestCaseResult result;
            try
            {
                result = await testCase.RunAsync(context, client, _redactor, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                result = new AdvancedTestCaseResult(
                    testCase.Definition.TestId,
                    testCase.Definition.DisplayName,
                    testCase.Definition.Category,
                    AdvancedTestStatus.Stopped,
                    0,
                    testCase.Definition.Weight,
                    TimeSpan.Zero,
                    "用户停止",
                    "测试已停止。",
                    null,
                    null,
                    AdvancedErrorKind.None,
                    string.Empty,
                    "测试已停止。",
                    AdvancedRiskLevel.Medium,
                    new[] { "可点击重试失败项继续未完成的测试。" },
                    Array.Empty<AdvancedCheckResult>());
            }

            results.Add(result);
            var percent = (index + 1) * 100d / runnableCases.Length;
            progress?.Report(new AdvancedTestProgress(
                result.TestId,
                result.DisplayName,
                result.Status,
                percent,
                $"{result.DisplayName}：{ToStatusText(result.Status)}",
                result));

            if (result.Status == AdvancedTestStatus.Stopped)
            {
                break;
            }
        }

        var completedAt = DateTimeOffset.Now;
        return new AdvancedTestRunResult(
            startedAt,
            completedAt,
            endpoint,
            results,
            _scoreCalculator.Calculate(results));
    }

    private static async Task<AdvancedEndpoint> ResolveEndpointWireApiAsync(
        AdvancedEndpoint endpoint,
        IProgress<AdvancedTestProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (ProxyWireApiProbeService.NormalizeWireApi(endpoint.PreferredWireApi) is { } configuredWireApi)
        {
            return endpoint with
            {
                PreferredWireApi = configuredWireApi,
                ProtocolHint = endpoint.ProtocolHint ?? $"Using configured wire_api={configuredWireApi}."
            };
        }

        progress?.Report(new AdvancedTestProgress(
            "protocol_probe",
            "协议探测",
            AdvancedTestStatus.Running,
            0,
            "正在探测当前模型支持的 responses / messages / chat 协议..."));

        try
        {
            var settings = new ProxyEndpointSettings(
                endpoint.BaseUrl,
                endpoint.ApiKey,
                endpoint.Model,
                endpoint.IgnoreTlsErrors,
                endpoint.TimeoutSeconds);
            var probe = await new ProxyDiagnosticsService()
                .ProbeProtocolAsync(settings, cancellationToken)
                .ConfigureAwait(false);

            var preferredWireApi = ProxyWireApiProbeService.NormalizeWireApi(probe.PreferredWireApi) ??
                                   ProxyWireApiProbeService.ChatCompletionsWireApi;
            return endpoint with
            {
                PreferredWireApi = preferredWireApi,
                ProtocolHint = probe.Summary
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return endpoint with
            {
                PreferredWireApi = ProxyWireApiProbeService.ChatCompletionsWireApi,
                ProtocolHint = $"Protocol probe failed before advanced tests, fallback to chat/completions. {ex.Message}"
            };
        }
    }

    private static string ToStatusText(AdvancedTestStatus status)
        => status switch
        {
            AdvancedTestStatus.Passed => "通过",
            AdvancedTestStatus.Partial => "部分通过",
            AdvancedTestStatus.Failed => "失败",
            AdvancedTestStatus.Skipped => "跳过",
            AdvancedTestStatus.Stopped => "已停止",
            _ => "运行中"
        };
}
