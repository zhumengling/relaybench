using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using NetTest.Core.Models;

namespace NetTest.Core.Services;

public sealed partial class ProxyDiagnosticsService
{
    private static ProxyProbeScenarioResult? SelectPrimaryFailure(IReadOnlyList<ProxyProbeScenarioResult> scenarioResults)
        => scenarioResults
            .Where(result => !result.Success)
            .OrderBy(result => GetFailurePriority(result.FailureKind))
            .FirstOrDefault();

    private static int GetFailurePriority(ProxyFailureKind? kind)
        => kind switch
        {
            ProxyFailureKind.AuthRejected => 0,
            ProxyFailureKind.TlsHandshakeFailure => 1,
            ProxyFailureKind.DnsFailure => 2,
            ProxyFailureKind.TcpConnectFailure => 3,
            ProxyFailureKind.Timeout => 4,
            ProxyFailureKind.UnsupportedEndpoint => 5,
            ProxyFailureKind.RateLimited => 6,
            ProxyFailureKind.ModelNotFound => 7,
            ProxyFailureKind.SemanticMismatch => 8,
            ProxyFailureKind.StreamNoFirstToken => 9,
            ProxyFailureKind.StreamNoDone => 10,
            ProxyFailureKind.ProtocolMismatch => 11,
            ProxyFailureKind.Http5xx => 12,
            ProxyFailureKind.Http4xx => 13,
            _ => 14
        };

    private static string BuildVerdict(IReadOnlyList<ProxyProbeScenarioResult> scenarioResults)
    {
        var models = GetScenario(scenarioResults, ProxyProbeScenarioKind.Models);
        var chat = GetScenario(scenarioResults, ProxyProbeScenarioKind.ChatCompletions);
        var stream = GetScenario(scenarioResults, ProxyProbeScenarioKind.ChatCompletionsStream);
        var responses = GetScenario(scenarioResults, ProxyProbeScenarioKind.Responses);
        var structuredOutput = GetScenario(scenarioResults, ProxyProbeScenarioKind.StructuredOutput);

        var baseFivePassed =
            models?.Success == true &&
            chat?.Success == true &&
            stream?.Success == true &&
            responses?.Success == true &&
            structuredOutput?.Success == true;

        var advancedScenarios = scenarioResults
            .Where(result => result.Scenario is ProxyProbeScenarioKind.SystemPromptMapping or
                                           ProxyProbeScenarioKind.FunctionCalling or
                                           ProxyProbeScenarioKind.ErrorTransparency or
                                           ProxyProbeScenarioKind.StreamingIntegrity or
                                           ProxyProbeScenarioKind.OfficialReferenceIntegrity or
                                           ProxyProbeScenarioKind.MultiModal or
                                           ProxyProbeScenarioKind.CacheIsolation)
            .ToArray();
        var advancedExecuted = advancedScenarios.Length > 0;
        var advancedAllPassed = advancedExecuted && advancedScenarios.All(result => result.Success);

        if (baseFivePassed && (!advancedExecuted || advancedAllPassed))
        {
            return "适合长期挂载";
        }

        if (baseFivePassed && advancedExecuted)
        {
            return "基础可用，高级兼容待复核";
        }

        if (models?.Success == true &&
            chat?.Success == true &&
            stream?.Success == true &&
            (responses?.Success == true || structuredOutput?.Success == true))
        {
            return "适合日常使用";
        }

        if (models?.Success == true && chat?.Success == true && stream?.Success == true)
        {
            return "基础可用，高级能力不完整";
        }

        if (chat?.Success == true && stream?.Success != true)
        {
            return "可用但流式不稳";
        }

        if (models?.Success == true || chat?.Success == true || stream?.Success == true)
        {
            return "勉强可用，建议复核";
        }

        return "不建议使用";
    }

    private static string BuildRecommendation(IReadOnlyList<ProxyProbeScenarioResult> scenarioResults)
    {
        var models = GetScenario(scenarioResults, ProxyProbeScenarioKind.Models);
        var chat = GetScenario(scenarioResults, ProxyProbeScenarioKind.ChatCompletions);
        var stream = GetScenario(scenarioResults, ProxyProbeScenarioKind.ChatCompletionsStream);
        var responses = GetScenario(scenarioResults, ProxyProbeScenarioKind.Responses);
        var structuredOutput = GetScenario(scenarioResults, ProxyProbeScenarioKind.StructuredOutput);

        var baseFivePassed =
            models?.Success == true &&
            chat?.Success == true &&
            stream?.Success == true &&
            responses?.Success == true &&
            structuredOutput?.Success == true;

        var advancedScenarios = scenarioResults
            .Where(result => result.Scenario is ProxyProbeScenarioKind.SystemPromptMapping or
                                           ProxyProbeScenarioKind.FunctionCalling or
                                           ProxyProbeScenarioKind.ErrorTransparency or
                                           ProxyProbeScenarioKind.StreamingIntegrity or
                                           ProxyProbeScenarioKind.OfficialReferenceIntegrity or
                                           ProxyProbeScenarioKind.MultiModal or
                                           ProxyProbeScenarioKind.CacheIsolation)
            .ToArray();
        var advancedExecuted = advancedScenarios.Length > 0;
        var advancedPassed = !advancedExecuted || advancedScenarios.All(result => result.Success);

        if (baseFivePassed && (!advancedExecuted || advancedPassed))
        {
            return "适合网页聊天、API 调用、Responses、结构化输出以及高级协议场景，可长期挂载。";
        }

        if (baseFivePassed && advancedExecuted)
        {
            return "基础五项已经可用，但高级兼容探针里至少有一项待复核，建议继续补测后再长期使用。";
        }

        if (models?.Success == true && chat?.Success == true && stream?.Success == true && responses?.Success == true)
        {
            return "适合基础聊天、流式输出和 Responses；结构化输出或高级协议转换仍建议继续复核。";
        }

        if (models?.Success == true && chat?.Success == true && stream?.Success == true && structuredOutput?.Success == true)
        {
            return "适合基础聊天、流式输出和结构化输出；如果业务依赖 Responses 或工具调用，建议继续复核。";
        }

        if (models?.Success == true && chat?.Success == true && stream?.Success == true)
        {
            return "适合基础聊天和流式输出，但不建议依赖 Responses、结构化输出或高级协议兼容能力。";
        }

        if (chat?.Success == true)
        {
            return "仅适合轻量非流式使用，正式接入前建议继续跑稳定性与协议兼容测试。";
        }

        return "建议更换中转站，或先排查鉴权、TLS、线路与接口兼容问题。";
    }

    private static string BuildPrimaryIssue(IReadOnlyList<ProxyProbeScenarioResult> scenarioResults)
    {
        var primaryFailure = SelectPrimaryFailure(scenarioResults);
        if (primaryFailure is null)
        {
            return "单次探测未发现明显问题。";
        }

        return $"{primaryFailure.DisplayName}：{primaryFailure.Error ?? primaryFailure.Summary}";
    }

    private static string BuildHeadersSummary(IReadOnlyList<ProxyProbeScenarioResult> scenarioResults)
    {
        StringBuilder builder = new();
        foreach (var scenario in scenarioResults)
        {
            builder.AppendLine($"[{scenario.DisplayName}]");
            if (scenario.ResponseHeaders is null || scenario.ResponseHeaders.Count == 0)
            {
                builder.AppendLine("未采集到关键响应头。");
            }
            else
            {
                foreach (var line in scenario.ResponseHeaders)
                {
                    builder.AppendLine(line);
                }
            }

            builder.AppendLine();
        }

        return builder.ToString().TrimEnd();
    }

    private static string BuildOverallSummary(
        string verdict,
        string recommendation,
        IReadOnlyList<ProxyProbeScenarioResult> scenarioResults,
        string primaryIssue,
        string? cdnSummary)
    {
        var capabilityLines = string.Join(
            "；",
            scenarioResults.Select(result => $"{result.DisplayName}{result.CapabilityStatus}"));

        var cdnPart = string.IsNullOrWhiteSpace(cdnSummary) ? string.Empty : $"；边缘观察：{cdnSummary}";
        return $"总判定：{verdict}；能力矩阵：{capabilityLines}；建议：{recommendation}；主要问题：{primaryIssue}{cdnPart}";
    }

    private static ProxyProbeScenarioResult? GetScenario(
        IReadOnlyList<ProxyProbeScenarioResult> scenarioResults,
        ProxyProbeScenarioKind scenario)
        => scenarioResults.FirstOrDefault(result => result.Scenario == scenario);

    private static ProxyFailureKind ClassifyResponseFailure(
        ProxyProbeScenarioKind scenario,
        int statusCode,
        string? body)
    {
        if (statusCode is 401 or 403)
        {
            return ProxyFailureKind.AuthRejected;
        }

        if (statusCode == 429)
        {
            return ProxyFailureKind.RateLimited;
        }

        if (statusCode == 404)
        {
            return scenario is ProxyProbeScenarioKind.Responses or
                       ProxyProbeScenarioKind.StructuredOutput or
                       ProxyProbeScenarioKind.FunctionCalling or
                       ProxyProbeScenarioKind.MultiModal
                ? ProxyFailureKind.UnsupportedEndpoint
                : ProxyFailureKind.Http4xx;
        }

        if ((scenario is ProxyProbeScenarioKind.Responses or
             ProxyProbeScenarioKind.StructuredOutput or
             ProxyProbeScenarioKind.FunctionCalling or
             ProxyProbeScenarioKind.MultiModal) &&
            LooksLikeUnsupportedFeature(body))
        {
            return ProxyFailureKind.UnsupportedEndpoint;
        }

        if (statusCode >= 500)
        {
            return ProxyFailureKind.Http5xx;
        }

        if (LooksLikeModelMissing(body))
        {
            return ProxyFailureKind.ModelNotFound;
        }

        return statusCode >= 400 ? ProxyFailureKind.Http4xx : ProxyFailureKind.Unknown;
    }

    private static bool LooksLikeModelMissing(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return false;
        }

        return body.Contains("model", StringComparison.OrdinalIgnoreCase) &&
               (body.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
                body.Contains("does not exist", StringComparison.OrdinalIgnoreCase));
    }

    private static bool LooksLikeUnsupportedFeature(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return false;
        }

        return body.Contains("unsupported", StringComparison.OrdinalIgnoreCase) ||
               body.Contains("not supported", StringComparison.OrdinalIgnoreCase) ||
               body.Contains("json_schema", StringComparison.OrdinalIgnoreCase) ||
               body.Contains("structured output", StringComparison.OrdinalIgnoreCase) ||
               body.Contains("text.format", StringComparison.OrdinalIgnoreCase) ||
               body.Contains("unknown parameter", StringComparison.OrdinalIgnoreCase) ||
               body.Contains("tool_choice", StringComparison.OrdinalIgnoreCase) ||
               body.Contains("tool_calls", StringComparison.OrdinalIgnoreCase) ||
               body.Contains("function_call", StringComparison.OrdinalIgnoreCase) ||
               body.Contains("\"tools\"", StringComparison.OrdinalIgnoreCase) ||
               body.Contains("image_url", StringComparison.OrdinalIgnoreCase) ||
               body.Contains("input_image", StringComparison.OrdinalIgnoreCase) ||
               body.Contains("vision", StringComparison.OrdinalIgnoreCase) ||
               body.Contains("multimodal", StringComparison.OrdinalIgnoreCase);
    }

    private static ProxyFailureKind ClassifyException(Exception ex)
    {
        if (ex is TaskCanceledException or OperationCanceledException)
        {
            return ProxyFailureKind.Timeout;
        }

        if (ex is HttpRequestException httpRequestException)
        {
            if (httpRequestException.InnerException is AuthenticationException)
            {
                return ProxyFailureKind.TlsHandshakeFailure;
            }

            if (httpRequestException.InnerException is SocketException socketException)
            {
                return ClassifySocketFailure(socketException);
            }

            var message = httpRequestException.Message;
            if (message.Contains("SSL", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("TLS", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("certificate", StringComparison.OrdinalIgnoreCase))
            {
                return ProxyFailureKind.TlsHandshakeFailure;
            }

            if (message.Contains("name or service not known", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("no such host", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("host not found", StringComparison.OrdinalIgnoreCase))
            {
                return ProxyFailureKind.DnsFailure;
            }

            if (message.Contains("connection", StringComparison.OrdinalIgnoreCase))
            {
                return ProxyFailureKind.TcpConnectFailure;
            }
        }

        return ProxyFailureKind.Unknown;
    }

    private static ProxyFailureKind ClassifySocketFailure(SocketException socketException)
        => socketException.SocketErrorCode switch
        {
            SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain => ProxyFailureKind.DnsFailure,
            SocketError.TimedOut => ProxyFailureKind.Timeout,
            SocketError.ConnectionAborted or SocketError.ConnectionRefused or SocketError.ConnectionReset or SocketError.NotConnected => ProxyFailureKind.TcpConnectFailure,
            _ => ProxyFailureKind.TcpConnectFailure
        };

    private static string DescribeCapability(ProxyFailureKind? failureKind, bool success)
    {
        if (success)
        {
            return "支持";
        }

        return failureKind switch
        {
            ProxyFailureKind.UnsupportedEndpoint => "不支持",
            ProxyFailureKind.AuthRejected => "鉴权失败",
            ProxyFailureKind.RateLimited => "限流",
            ProxyFailureKind.SemanticMismatch or
            ProxyFailureKind.StreamNoDone or
            ProxyFailureKind.StreamNoFirstToken or
            ProxyFailureKind.StreamBroken => "异常",
            ProxyFailureKind.ProtocolMismatch => "待复核",
            _ => "失败"
        };
    }

    private static string DescribeFailureKind(ProxyFailureKind? failureKind)
        => failureKind switch
        {
            ProxyFailureKind.ConfigurationInvalid => "参数无效",
            ProxyFailureKind.DnsFailure => "DNS 解析失败",
            ProxyFailureKind.TcpConnectFailure => "TCP 连接失败",
            ProxyFailureKind.TlsHandshakeFailure => "TLS 握手失败",
            ProxyFailureKind.Timeout => "请求超时",
            ProxyFailureKind.AuthRejected => "鉴权被拒绝",
            ProxyFailureKind.RateLimited => "触发限流",
            ProxyFailureKind.ModelNotFound => "模型不存在",
            ProxyFailureKind.UnsupportedEndpoint => "接口或能力不支持",
            ProxyFailureKind.Http4xx => "HTTP 4xx 错误",
            ProxyFailureKind.Http5xx => "HTTP 5xx 错误",
            ProxyFailureKind.ProtocolMismatch => "协议不兼容",
            ProxyFailureKind.StreamNoFirstToken => "流式无首 Token",
            ProxyFailureKind.StreamNoDone => "流式未正常结束",
            ProxyFailureKind.StreamBroken => "流式中途断开",
            ProxyFailureKind.SemanticMismatch => "语义校验不通过",
            _ => "未知错误"
        };
}
