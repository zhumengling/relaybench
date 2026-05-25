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
    private readonly ProxyEndpointProtocolProbeService _protocolProbeService;

    public AdvancedTestRunner()
        : this(AdvancedTestCatalog.CreateDefaultCases(), new AdvancedScoreCalculator(), new SensitiveDataRedactor(), new ProxyEndpointProtocolProbeService())
    {
    }

    public AdvancedTestRunner(ProxyEndpointProtocolProbeService protocolProbeService)
        : this(AdvancedTestCatalog.CreateDefaultCases(), new AdvancedScoreCalculator(), new SensitiveDataRedactor(), protocolProbeService)
    {
    }

    public AdvancedTestRunner(
        IReadOnlyList<IAdvancedTestCase> cases,
        IScoreCalculator scoreCalculator,
        ISensitiveDataRedactor redactor,
        ProxyEndpointProtocolProbeService? protocolProbeService = null)
    {
        _cases = cases;
        _scoreCalculator = scoreCalculator;
        _redactor = redactor;
        _protocolProbeService = protocolProbeService ?? new ProxyEndpointProtocolProbeService();
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
        if (plan.Options.AllowParallelSuites && plan.Options.MaxParallelism > 1 && runnableCases.Length > 1)
        {
            return await RunParallelAsync(
                    runnableCases,
                    context,
                    endpoint,
                    startedAt,
                    progress,
                    cancellationToken)
                .ConfigureAwait(false);
        }

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

            result = ApplyRunOptions(result, plan.Options);
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

    private async Task<AdvancedTestRunResult> RunParallelAsync(
        IReadOnlyList<IAdvancedTestCase> runnableCases,
        AdvancedTestRunContext context,
        AdvancedEndpoint endpoint,
        DateTimeOffset startedAt,
        IProgress<AdvancedTestProgress>? progress,
        CancellationToken cancellationToken)
    {
        var maxParallelism = Math.Clamp(context.Options.MaxParallelism, 1, 32);
        using var client = new HttpModelClient(endpoint);
        using var semaphore = new SemaphoreSlim(maxParallelism);
        var results = new AdvancedTestCaseResult?[runnableCases.Count];
        var completed = 0;

        var tasks = runnableCases.Select(async (testCase, index) =>
        {
            await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                progress?.Report(new AdvancedTestProgress(
                    testCase.Definition.TestId,
                    testCase.Definition.DisplayName,
                    AdvancedTestStatus.Running,
                    completed * 100d / runnableCases.Count,
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

                result = ApplyRunOptions(result, context.Options);
                results[index] = result;
                var done = Interlocked.Increment(ref completed);
                progress?.Report(new AdvancedTestProgress(
                    result.TestId,
                    result.DisplayName,
                    result.Status,
                    done * 100d / runnableCases.Count,
                    $"{result.DisplayName}：{ToStatusText(result.Status)}",
                    result));
            }
            finally
            {
                semaphore.Release();
            }
        }).ToArray();

        await Task.WhenAll(tasks).ConfigureAwait(false);
        var completedAt = DateTimeOffset.Now;
        var materialized = results.Where(static result => result is not null).Cast<AdvancedTestCaseResult>().ToList();
        return new AdvancedTestRunResult(
            startedAt,
            completedAt,
            endpoint,
            materialized,
            _scoreCalculator.Calculate(materialized));
    }

    private static AdvancedTestCaseResult ApplyRunOptions(
        AdvancedTestCaseResult result,
        AdvancedTestRunOptions options)
        => options.IncludeRawExchange
            ? result
            : result with
            {
                RawRequest = null,
                RawResponse = null
            };

    private async Task<AdvancedEndpoint> ResolveEndpointWireApiAsync(
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
            var resolution = await _protocolProbeService
                .ResolveAsync(
                    settings,
                    new ProxyEndpointProtocolProbeOptions(
                        ForceProbe: false,
                        UseCache: true,
                        SaveResult: true),
                    cancellationToken)
                .ConfigureAwait(false);
            var probe = resolution.Result;

            var preferredWireApi = ProxyWireApiProbeService.NormalizeWireApi(probe.PreferredWireApi) ??
                                   ProxyWireApiProbeService.ChatCompletionsWireApi;
            return endpoint with
            {
                PreferredWireApi = preferredWireApi,
                ProtocolHint = string.IsNullOrWhiteSpace(endpoint.ProtocolHint)
                    ? probe.Summary
                    : $"{endpoint.ProtocolHint} | 探测：{probe.Summary}"
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
