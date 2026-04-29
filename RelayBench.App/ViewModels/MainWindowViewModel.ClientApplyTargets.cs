using System.Text;
using RelayBench.Core.Models;
using RelayBench.Core.Services;

namespace RelayBench.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private readonly ClientAppApplyPlanner _clientAppApplyPlanner = new();
    private readonly ClientAppConfigApplyService _clientAppConfigApplyService = new();

    private IReadOnlyList<ClientApplyTarget> BuildClientApplyTargets(
        ProxyEndpointSettings settings,
        ProxyEndpointProtocolProbeResult protocolProbeResult)
    {
        var installedNames = _lastClientApiDiagnosticsResult?.Checks
            .Where(check => check.Installed)
            .Select(check => check.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase) ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        return _clientAppApplyPlanner.BuildTargets(new ClientAppApplyPlanContext(
            settings.BaseUrl,
            settings.ApiKey,
            settings.Model,
            protocolProbeResult.ResponsesSupported,
            protocolProbeResult.ChatCompletionsSupported,
            protocolProbeResult.AnthropicMessagesSupported,
            installedNames));
    }

    private async Task<IReadOnlyList<ClientApplyTargetSelection>> ChooseClientApplyTargetsAsync(
        string title,
        ProxyEndpointSettings settings,
        ProxyEndpointProtocolProbeResult protocolProbeResult)
    {
        var targets = BuildClientApplyTargets(settings, protocolProbeResult);
        var summary =
            BuildProtocolProbeDetail(protocolProbeResult) +
            "\n\n\u8bf7\u9009\u62e9\u672c\u6b21\u8981\u5199\u5165\u914d\u7f6e\u7684\u8f6f\u4ef6\u3002\u672a\u53d1\u73b0\u8f6f\u4ef6\u6216\u7f3a\u5c11\u63a5\u53e3\u4fe1\u606f\u65f6\u4f1a\u7981\u6b62\u52fe\u9009\uff1b\u4ec5\u534f\u8bae\u4e0d\u4e00\u5b9a\u517c\u5bb9\u7684\u8f6f\u4ef6\u53ef\u4ee5\u52fe\u9009\uff0c\u4f46\u9700\u8981\u4f60\u518d\u786e\u8ba4\u4e00\u6b21\u3002";
        return await ShowClientApplyTargetDialogAsync(title, summary, targets);
    }

    private static bool HasSucceededCodexTarget(ClientAppApplyResult result)
        => result.TargetResults.Any(target => target.Protocol == ClientApplyProtocolKind.Responses && target.Succeeded);

    private static bool ShouldAskCodexChatMerge(
        IReadOnlyList<ClientApplyTargetSelection> selectedTargets,
        ClientAppApplyResult result)
        => selectedTargets.Any(target => target.Protocol == ClientApplyProtocolKind.Responses) &&
           HasSucceededCodexTarget(result);

    private static bool HasSucceededTarget(ClientAppApplyResult result)
        => result.TargetResults.Any(target => target.Succeeded) || result.AppliedTargets.Count > 0;

    private static string BuildClientApplyResultSummary(
        ClientAppApplyResult result,
        CodexChatMergeResult? mergeResult)
    {
        StringBuilder builder = new();
        builder.AppendLine(result.Summary);
        if (result.TargetResults.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("逐软件结果：");
            foreach (var target in result.TargetResults)
            {
                builder.AppendLine(target.Succeeded
                    ? $"- {target.DisplayName}：成功"
                    : $"- {target.DisplayName}：失败（{target.Error ?? "未知错误"}）");
            }
        }

        builder.Append($"聊天记录：{mergeResult?.Summary ?? "保持原样"}");
        return builder.ToString();
    }

    private static string BuildClientApplyResultDetail(
        string sourceTitle,
        string baseUrl,
        string apiKey,
        string model,
        ClientAppApplyResult result,
        CodexChatMergeResult? mergeResult)
    {
        StringBuilder builder = new();
        builder.AppendLine(sourceTitle);
        builder.AppendLine($"地址：{baseUrl}");
        builder.AppendLine($"密钥：{MaskApiKey(apiKey)}");
        builder.AppendLine($"模型：{model}");
        builder.AppendLine();
        builder.AppendLine("逐软件结果：");
        if (result.TargetResults.Count == 0)
        {
            builder.AppendLine("无");
        }
        else
        {
            foreach (var target in result.TargetResults)
            {
                builder.AppendLine(target.Succeeded
                    ? $"- {target.DisplayName} [{FormatClientApplyProtocol(target.Protocol)}]：成功"
                    : $"- {target.DisplayName} [{FormatClientApplyProtocol(target.Protocol)}]：失败（{target.Error ?? "未知错误"}）");
            }
        }

        builder.AppendLine();
        builder.AppendLine($"更新文件：{(result.ChangedFiles.Count == 0 ? "无" : string.Join("\n", result.ChangedFiles))}");
        builder.AppendLine($"备份文件：{(result.BackupFiles.Count == 0 ? "无" : string.Join("\n", result.BackupFiles))}");
        builder.AppendLine($"聊天记录：{mergeResult?.Summary ?? "保持原样"}");
        if (mergeResult is not null)
        {
            builder.AppendLine(BuildCodexChatMergeDetail(mergeResult));
        }

        builder.Append($"错误：{result.Error ?? "无"}");
        return builder.ToString();
    }

    private static string FormatClientApplyProtocol(ClientApplyProtocolKind protocol)
        => protocol switch
        {
            ClientApplyProtocolKind.Responses => "Responses",
            ClientApplyProtocolKind.OpenAiCompatible => "OpenAI 兼容",
            ClientApplyProtocolKind.Anthropic => "Anthropic",
            _ => protocol.ToString()
        };

    private static string BuildClientApplyStatusMessage(
        ClientAppApplyResult result,
        CodexChatMergeResult? mergeResult)
    {
        var hasSucceededTarget = HasSucceededTarget(result);
        if (!hasSucceededTarget)
        {
            return $"应用失败：{result.Error ?? result.Summary}";
        }

        return result.Succeeded
            ? mergeResult is { Succeeded: false }
                ? $"配置已更新，但聊天整理失败：{mergeResult.Error ?? mergeResult.Summary}"
                : mergeResult is { Succeeded: true }
                    ? $"{result.Summary}；{mergeResult.Summary}"
                    : result.Summary
            : result.Summary;
    }
}
