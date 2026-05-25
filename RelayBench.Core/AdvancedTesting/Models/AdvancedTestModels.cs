using System.Collections.ObjectModel;

namespace RelayBench.Core.AdvancedTesting.Models;

public enum AdvancedTestCategory
{
    BasicCompatibility,
    AgentCompatibility,
    StructuredOutput,
    ReasoningCompatibility,
    LongContext,
    Stability,
    Concurrency,
    Rag,
    ModelConsistency,
    SecurityRedTeam
}

public enum AdvancedTestStatus
{
    Queued,
    Running,
    Passed,
    Partial,
    Failed,
    Skipped,
    Stopped
}

public enum AdvancedRiskLevel
{
    Low,
    Medium,
    High,
    Critical
}

public enum AdvancedErrorKind
{
    None,
    NetworkTimeout,
    DnsFailure,
    TlsFailure,
    Unauthorized,
    RateLimited,
    InvalidRequest,
    ServerError,
    BadGateway,
    StreamBroken,
    StreamMalformed,
    JsonMalformed,
    ToolCallMalformed,
    ReasoningProtocolIncompatible,
    ContextOverflow,
    UsageMissing,
    UsageSuspicious,
    ModelMismatchSuspected,
    PromptInjectionSuspected,
    SystemPromptLeak,
    SensitiveDataLeak,
    UnauthorizedToolCall,
    RagPoisoningSuspected,
    UnsafeUrlOrCommand,
    JailbreakSuspected,
    Unknown
}

public sealed record AdvancedEndpoint(
    string BaseUrl,
    string ApiKey,
    string Model,
    bool IgnoreTlsErrors,
    int TimeoutSeconds,
    string? PreferredWireApi = null,
    string? DisplayModelName = null,
    string? ProtocolHint = null)
{
    public bool IsComplete
        => !string.IsNullOrWhiteSpace(BaseUrl) &&
           !string.IsNullOrWhiteSpace(ApiKey) &&
           !string.IsNullOrWhiteSpace(Model);
}

public sealed record AdvancedWireApiState(
    bool ChatSupported,
    bool ResponsesSupported,
    bool AnthropicSupported,
    string PreferredWireApi,
    string ProbeSummary);

public sealed record AdvancedTestRunOptions(
    bool AllowParallelSuites = false,
    int MaxParallelism = 2,
    bool IncludeRawExchange = true);

public sealed record AdvancedTestCaseDefinition(
    string TestId,
    string DisplayName,
    AdvancedTestCategory Category,
    double Weight,
    string Description,
    bool IsEnabledByDefault = true);

public sealed record AdvancedTestSuiteDefinition(
    string SuiteId,
    string DisplayName,
    string Description,
    AdvancedRiskLevel RiskLevel,
    IReadOnlyList<AdvancedTestCaseDefinition> Cases);

public sealed record AdvancedTestPlan(
    AdvancedEndpoint Endpoint,
    IReadOnlyList<string> SelectedTestIds,
    AdvancedTestRunOptions Options);

public sealed record AdvancedTestRunContext(
    AdvancedEndpoint Endpoint,
    AdvancedTestRunOptions Options,
    DateTimeOffset StartedAt);

public sealed record AdvancedRawExchange(
    string Method,
    string Url,
    IReadOnlyDictionary<string, string> RequestHeaders,
    string? RequestBody,
    int? StatusCode,
    IReadOnlyDictionary<string, string> ResponseHeaders,
    string? ResponseBody);

public sealed record AdvancedCheckResult(
    string Name,
    bool Passed,
    string Expected,
    string Actual,
    string Detail);

public sealed record AdvancedTestCaseResult(
    string TestId,
    string DisplayName,
    AdvancedTestCategory Category,
    AdvancedTestStatus Status,
    double Score,
    double Weight,
    TimeSpan Duration,
    string RequestSummary,
    string ResponseSummary,
    string? RawRequest,
    string? RawResponse,
    AdvancedErrorKind ErrorKind,
    string ErrorCode,
    string ErrorMessage,
    AdvancedRiskLevel RiskLevel,
    IReadOnlyList<string> Suggestions,
    IReadOnlyList<AdvancedCheckResult> Checks)
{
    public static AdvancedTestCaseResult Skipped(AdvancedTestCaseDefinition definition, string reason)
        => new(
            definition.TestId,
            definition.DisplayName,
            definition.Category,
            AdvancedTestStatus.Skipped,
            0,
            definition.Weight,
            TimeSpan.Zero,
            "未发起请求",
            reason,
            null,
            null,
            AdvancedErrorKind.None,
            string.Empty,
            reason,
            AdvancedRiskLevel.Medium,
            new[] { "确认当前接口是否需要该能力；如果不需要，可在测试套件中取消勾选。" },
            Array.Empty<AdvancedCheckResult>());
}

public sealed record AdvancedScenarioScores(
    double Overall,
    double CodexFit,
    double AgentFit,
    double RagFit,
    double ChatExperience);

public sealed record AdvancedTestRunResult(
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    AdvancedEndpoint Endpoint,
    IReadOnlyList<AdvancedTestCaseResult> Results,
    AdvancedScenarioScores Scores)
{
    public int PassedCount => Results.Count(static item => item.Status == AdvancedTestStatus.Passed);

    public int FailedCount => Results.Count(static item => item.Status == AdvancedTestStatus.Failed);

    public int PartialCount => Results.Count(static item => item.Status == AdvancedTestStatus.Partial);

    public int SkippedCount => Results.Count(static item => item.Status == AdvancedTestStatus.Skipped);
}

public sealed record AdvancedTestProgress(
    string TestId,
    string DisplayName,
    AdvancedTestStatus Status,
    double Percent,
    string Message,
    AdvancedTestCaseResult? Result = null);

public sealed class AdvancedErrorDescriptor
{
    public AdvancedErrorDescriptor(
        AdvancedErrorKind kind,
        string userMessage,
        string technicalDetail,
        string possibleCause,
        string suggestion,
        AdvancedRiskLevel riskLevel)
    {
        Kind = kind;
        UserMessage = userMessage;
        TechnicalDetail = technicalDetail;
        PossibleCause = possibleCause;
        Suggestion = suggestion;
        RiskLevel = riskLevel;
    }

    public AdvancedErrorKind Kind { get; }

    public string UserMessage { get; }

    public string TechnicalDetail { get; }

    public string PossibleCause { get; }

    public string Suggestion { get; }

    public AdvancedRiskLevel RiskLevel { get; }
}

public sealed class AdvancedTestCaseResultCollection : Collection<AdvancedTestCaseResult>
{
}
