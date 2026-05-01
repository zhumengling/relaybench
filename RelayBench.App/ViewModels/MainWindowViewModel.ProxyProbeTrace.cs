using System.Text;
using System.Text.Json;
using RelayBench.Core.Models;

namespace RelayBench.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private Task OpenProbeTraceAsync(ProxySingleCapabilityChartRowViewModel? row)
    {
        if (row?.Trace is null)
        {
            return Task.CompletedTask;
        }

        return OpenOfficialApiTraceDialogAsync($"{row.Name} · 探针详情", BuildProbeTraceDialogContent(row.Trace));
    }

    private static string BuildProbeTraceDialogContent(ProxyProbeTrace trace)
    {
        StringBuilder builder = new();
        AppendProbeTraceInterpretation(builder, trace);
        builder.AppendLine();
        AppendProbeTraceEvidenceSummary(builder, trace);
        builder.AppendLine();
        AppendRawProbeTrace(builder, trace);
        return builder.ToString();
    }

    private static void AppendProbeTraceInterpretation(StringBuilder builder, ProxyProbeTrace trace)
    {
        var isSuccess = IsTraceSuccessful(trace);
        var passedChecks = trace.Checks.Count(check => check.Passed);
        var failedChecks = trace.Checks.Where(check => !check.Passed).ToArray();

        builder.AppendLine("[判定解读]");
        builder.AppendLine($"结论: {trace.Verdict}");
        builder.AppendLine($"探针: {trace.DisplayName} ({trace.Scenario})");
        builder.AppendLine($"目标: {trace.BaseUrl}/{trace.Path}");
        builder.AppendLine($"协议: {trace.WireApi}");
        builder.AppendLine($"模型: {trace.Model}");
        builder.AppendLine($"耗时: {FormatTraceDuration(trace)}");
        builder.AppendLine();

        if (isSuccess)
        {
            builder.AppendLine("为什么通过:");
            foreach (var reason in BuildSuccessReasons(trace, passedChecks))
            {
                builder.AppendLine($"- {reason}");
            }
            return;
        }

        builder.AppendLine("为什么失败:");
        foreach (var reason in BuildFailureReasons(trace, failedChecks))
        {
            builder.AppendLine($"- {reason}");
        }

        builder.AppendLine();
        builder.AppendLine("建议怎么判断:");
        foreach (var suggestion in BuildProbeTraceSuggestions(trace, failedChecks))
        {
            builder.AppendLine($"- {suggestion}");
        }
    }

    private static void AppendProbeTraceEvidenceSummary(StringBuilder builder, ProxyProbeTrace trace)
    {
        builder.AppendLine("[关键证据]");
        builder.AppendLine($"HTTP 状态码: {trace.StatusCode?.ToString() ?? "-"}");
        builder.AppendLine($"RequestId: {trace.RequestId ?? "-"}");
        builder.AppendLine($"TraceId: {trace.TraceId ?? "-"}");
        builder.AppendLine($"提取输出: {FormatCompactTraceValue(trace.ExtractedOutput)}");

        if (trace.Checks.Count == 0)
        {
            builder.AppendLine("自动检查: 该探针没有附加语义检查，只根据 HTTP / SSE / 内容提取结果判断。");
            return;
        }

        builder.AppendLine("自动检查:");
        foreach (var check in trace.Checks)
        {
            builder.AppendLine($"- {(check.Passed ? "通过" : "失败")} · {check.Name}");
            builder.AppendLine($"  期望: {FormatCompactTraceValue(check.Expected)}");
            builder.AppendLine($"  实际: {FormatCompactTraceValue(check.Actual)}");
            if (!string.IsNullOrWhiteSpace(check.Detail))
            {
                builder.AppendLine($"  说明: {check.Detail}");
            }
        }
    }

    private static void AppendRawProbeTrace(StringBuilder builder, ProxyProbeTrace trace)
    {
        builder.AppendLine("[原始 Trace]");
        builder.AppendLine("下面是脱敏后的原始请求、响应和完整 Trace JSON，主要给排查接口兼容、上游返回和转发细节时使用。");
        builder.AppendLine();

        builder.AppendLine("[概览]");
        builder.AppendLine($"探针: {trace.DisplayName} ({trace.Scenario})");
        builder.AppendLine($"判定: {trace.Verdict}");
        builder.AppendLine($"模型: {trace.Model}");
        builder.AppendLine($"协议: {trace.WireApi}");
        builder.AppendLine($"地址: {trace.BaseUrl}/{trace.Path}");
        builder.AppendLine($"状态码: {trace.StatusCode?.ToString() ?? "-"}");
        builder.AppendLine($"耗时: {trace.LatencyMilliseconds?.ToString() ?? "-"} ms");
        builder.AppendLine($"RequestId: {trace.RequestId ?? "-"}");
        builder.AppendLine($"TraceId: {trace.TraceId ?? "-"}");
        if (!string.IsNullOrWhiteSpace(trace.FailureReason))
        {
            builder.AppendLine($"失败原因: {trace.FailureReason}");
        }

        builder.AppendLine();
        builder.AppendLine("[请求头]");
        AppendLines(builder, trace.RequestHeaders);

        builder.AppendLine();
        builder.AppendLine("[请求体]");
        builder.AppendLine(FormatJsonOrRaw(trace.RequestBody));

        builder.AppendLine();
        builder.AppendLine("[响应头]");
        AppendLines(builder, trace.ResponseHeaders);

        builder.AppendLine();
        builder.AppendLine("[原始响应]");
        builder.AppendLine(FormatJsonOrRaw(trace.ResponseBody));

        builder.AppendLine();
        builder.AppendLine("[提取输出]");
        builder.AppendLine(string.IsNullOrWhiteSpace(trace.ExtractedOutput) ? "-" : trace.ExtractedOutput);

        builder.AppendLine();
        builder.AppendLine("[判定证据]");
        if (trace.Checks.Count == 0)
        {
            builder.AppendLine("-");
        }
        else
        {
            foreach (var check in trace.Checks)
            {
                builder.AppendLine($"{(check.Passed ? "PASS" : "FAIL")} {check.Name}");
                builder.AppendLine($"  Expected: {check.Expected}");
                builder.AppendLine($"  Actual: {check.Actual}");
                builder.AppendLine($"  Detail: {check.Detail}");
            }
        }

        builder.AppendLine();
        builder.AppendLine("[完整 Trace JSON]");
        builder.AppendLine(JsonSerializer.Serialize(trace, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static bool IsTraceSuccessful(ProxyProbeTrace trace)
        => string.Equals(trace.Verdict, "通过", StringComparison.Ordinal) &&
           trace.Checks.All(check => check.Passed) &&
           (trace.StatusCode is null or >= 200 and < 300);

    private static IReadOnlyList<string> BuildSuccessReasons(ProxyProbeTrace trace, int passedChecks)
    {
        List<string> reasons = [];

        if (trace.StatusCode is >= 200 and < 300)
        {
            reasons.Add($"接口返回 HTTP {trace.StatusCode}，说明请求已经被上游正常接受并返回。");
        }
        else if (trace.StatusCode is null)
        {
            reasons.Add("该探针没有记录 HTTP 状态码，但未发现请求层异常。");
        }

        if (!string.IsNullOrWhiteSpace(trace.ExtractedOutput))
        {
            reasons.Add($"程序成功从响应中提取到可判定输出：{FormatCompactTraceValue(trace.ExtractedOutput)}。");
        }

        reasons.Add(trace.Checks.Count == 0
            ? "该探针没有额外语义检查，基础连通、协议和响应提取均未报错。"
            : $"所有自动判定项均通过（{passedChecks}/{trace.Checks.Count}）：{string.Join("、", trace.Checks.Select(check => check.Name))}。");

        return reasons;
    }

    private static IReadOnlyList<string> BuildFailureReasons(
        ProxyProbeTrace trace,
        IReadOnlyList<ProxyProbeEvaluationCheck> failedChecks)
    {
        List<string> reasons = [];
        var isInvalidToolSchema = IsInvalidToolSchemaError(trace);

        if (isInvalidToolSchema)
        {
            reasons.Add("请求被服务端在执行前拒绝：工具 schema 不合法（例如 function.parameters 缺少 properties）。这类失败发生在模型生成前，不能直接判定模型不支持 ToolCall。");
        }

        if (trace.StatusCode is < 200 or >= 300)
        {
            reasons.Add($"接口返回 HTTP {trace.StatusCode}，不是正常成功状态码，优先检查 URL、Key、模型名或中转站路由。");
        }

        if (!string.IsNullOrWhiteSpace(trace.FailureReason))
        {
            reasons.Add(trace.FailureReason!.Trim());
        }

        foreach (var check in failedChecks)
        {
            reasons.Add($"{check.Name} 未通过：期望 {FormatCompactTraceValue(check.Expected)}，实际 {FormatCompactTraceValue(check.Actual)}。{check.Detail}");
        }

        if (failedChecks.Count == 0 && string.IsNullOrWhiteSpace(trace.FailureReason))
        {
            reasons.Add("自动判定结果不是“通过”，但没有记录到更具体的失败原因，需要结合下方原始响应继续核对。");
        }

        if (string.IsNullOrWhiteSpace(trace.ExtractedOutput))
        {
            reasons.Add("程序没有从响应里提取到可判定输出，可能是响应格式、流式片段或工具调用结构不符合预期。");
        }

        return reasons;
    }

    private static IReadOnlyList<string> BuildProbeTraceSuggestions(
        ProxyProbeTrace trace,
        IReadOnlyList<ProxyProbeEvaluationCheck> failedChecks)
    {
        List<string> suggestions = [];
        var isInvalidToolSchema = IsInvalidToolSchemaError(trace);

        if (isInvalidToolSchema)
        {
            suggestions.Add("优先检查探针请求里的 tools[].function.parameters；严格 OpenAI 兼容端通常要求 object schema 带 properties、required 和 additionalProperties。");
            suggestions.Add("如果修正 schema 后能返回 tool_calls，说明接口具备工具调用能力，之前的失败应归类为探针请求模板问题。");
        }

        if (trace.StatusCode is < 200 or >= 300)
        {
            suggestions.Add("先看原始响应里的 error/message 字段，确认是鉴权、模型不存在、额度限制还是协议路径不兼容。");
        }

        if (failedChecks.Count > 0)
        {
            suggestions.Add("重点对照“自动检查”里的期望和实际值；如果实际输出接近但格式不一致，通常是模型没有严格按测试提示输出。");
        }

        if (!string.IsNullOrWhiteSpace(trace.ExtractedOutput))
        {
            suggestions.Add("如果你认为模型输出其实可接受，可以复制下方原始 Trace 给开发者判断是否需要放宽探针规则。");
        }
        else
        {
            suggestions.Add("如果原始响应里明明有内容但这里没有提取输出，优先检查该中转站返回结构是否偏离 OpenAI 兼容格式。");
        }

        return suggestions.Count == 0
            ? ["结合下方原始请求和响应，核对接口是否按该探针要求返回。"]
            : suggestions;
    }

    private static bool IsInvalidToolSchemaError(ProxyProbeTrace trace)
    {
        var text = string.Concat(trace.FailureReason, "\n", trace.ResponseBody);
        return text.Contains("invalid_function_parameters", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("Invalid schema for function", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("object schema missing properties", StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatTraceDuration(ProxyProbeTrace trace)
    {
        List<string> parts = [];
        if (trace.LatencyMilliseconds is { } latency)
        {
            parts.Add($"{latency} ms");
        }

        if (trace.FirstTokenLatencyMilliseconds is { } firstTokenLatency)
        {
            parts.Add($"首 token {firstTokenLatency} ms");
        }

        if (trace.DurationMilliseconds is { } duration &&
            duration != trace.LatencyMilliseconds)
        {
            parts.Add($"总时长 {duration} ms");
        }

        return parts.Count == 0 ? "-" : string.Join(" / ", parts);
    }

    private static string FormatCompactTraceValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "-";
        }

        const int maximumLength = 260;
        var compact = value
            .Trim()
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);
        return compact.Length <= maximumLength
            ? compact
            : string.Concat(compact.AsSpan(0, maximumLength), "...");
    }

    private static void AppendLines(StringBuilder builder, IReadOnlyList<string> lines)
    {
        if (lines.Count == 0)
        {
            builder.AppendLine("-");
            return;
        }

        foreach (var line in lines)
        {
            builder.AppendLine(line);
        }
    }

    private static string FormatJsonOrRaw(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "-";
        }

        try
        {
            using var document = JsonDocument.Parse(value);
            return JsonSerializer.Serialize(document.RootElement, new JsonSerializerOptions { WriteIndented = true });
        }
        catch
        {
            return value.Trim();
        }
    }
}
