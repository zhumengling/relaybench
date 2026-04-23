using System.Text;
using System.Windows;
using RelayBench.Core.Models;

namespace RelayBench.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    public string ApplicationCenterApplyTargetSummary
    {
        get
        {
            var missing = GetApplicationCenterMissingContextFields();
            if (missing.Count == 0)
            {
                return "接口已就绪，可写入 Codex CLI / Desktop / VSCode Codex；写入前会自动备份。";
            }

            return $"还缺 {string.Join("、", missing)}，补齐后可写入 Codex 系列。";
        }
    }

    public string ApplicationCenterApplyPreviewDetail
    {
        get
        {
            StringBuilder builder = new();
            builder.AppendLine($"\u5F53\u524D Base URL\uFF1A{FormatPreviewValue(ProxyBaseUrl)}");
            builder.AppendLine($"\u5F53\u524D\u6A21\u578B\uFF1A{FormatPreviewValue(ProxyModel)}");
            builder.AppendLine($"\u5F53\u524D API Key\uFF1A{FormatPreviewApiKey(ProxyApiKey)}");
            builder.AppendLine($"\u914D\u7F6E\u540D\u79F0\uFF1A{ResolveCurrentProxyDisplayName() ?? "\u5C06\u4F7F\u7528\u9ED8\u8BA4 Custom OpenAI-Compatible"}");
            builder.AppendLine();
            builder.AppendLine("\u5199\u5165\u76EE\u6807\uFF1A");
            builder.AppendLine("- ~/.codex/config.toml");
            builder.AppendLine("- ~/.codex/auth.json");
            builder.AppendLine("- ~/.codex/settings.json\uFF08\u4EC5\u5728\u8FD8\u539F/\u57FA\u7EBF\u573A\u666F\u4E2D\u7528\u5230\uFF09");
            builder.AppendLine();
            builder.AppendLine("\u9002\u7528\u8F6F\u4EF6\uFF1A");
            builder.AppendLine("- Codex CLI");
            builder.AppendLine("- Codex Desktop");
            builder.AppendLine("- VSCode Codex");
            builder.AppendLine();
            builder.AppendLine("\u8BF4\u660E\uFF1A");
            builder.AppendLine("- \u4E0D\u4F1A\u542F\u7528\u6216\u4FEE\u6539\u672C\u5730\u4EE3\u7406");
            builder.AppendLine("- \u4E0D\u4F1A\u52A8 Claude CLI / Antigravity \u7684\u914D\u7F6E");
            builder.AppendLine("- \u5B8C\u6210\u5199\u5165\u540E\u4F1A\u7ACB\u5373\u91CD\u65B0\u626B\u63CF\u672C\u5730\u5E94\u7528\u72B6\u6001");

            var missing = GetApplicationCenterMissingContextFields();
            if (missing.Count > 0)
            {
                builder.AppendLine();
                builder.AppendLine($"\u5F85\u8865\u5168\u9879\uFF1A{string.Join("\u3001", missing)}");
            }

            return builder.ToString().TrimEnd();
        }
    }

    private bool CanApplyCurrentInterfaceToCodexApps()
        => !IsBusy && GetApplicationCenterMissingContextFields().Count == 0;

    private Task ApplyCurrentInterfaceToCodexAppsAsync()
    {
        var missing = GetApplicationCenterMissingContextFields();
        if (missing.Count > 0)
        {
            StatusMessage = $"\u5E94\u7528\u5931\u8D25\uFF1A\u8FD8\u7F3A {string.Join("\u3001", missing)}\u3002";
            return Task.CompletedTask;
        }

        var confirmed = MessageBox.Show(
            "\u786E\u5B9A\u8981\u5C06\u5F53\u524D\u63A5\u53E3\u5E94\u7528\u5230 Codex \u7CFB\u5217\u8F6F\u4EF6\u5417\uFF1F\n\n" +
            "\u672C\u6B21\u4F1A\u5199\u5165 Codex CLI / Codex Desktop / VSCode Codex \u5171\u7528\u7684 .codex \u914D\u7F6E\uFF0C\u4FEE\u6539\u524D\u4F1A\u81EA\u52A8\u521B\u5EFA\u5907\u4EFD\u3002",
            "\u786E\u8BA4\u5E94\u7528\u5230 Codex \u7CFB\u5217",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.No);

        if (confirmed != MessageBoxResult.Yes)
        {
            StatusMessage = "\u5DF2\u53D6\u6D88\u5C06\u5F53\u524D\u63A5\u53E3\u5E94\u7528\u5230 Codex \u7CFB\u5217\u3002";
            return Task.CompletedTask;
        }

        return ExecuteBusyActionAsync(
            "\u6B63\u5728\u5E94\u7528\u5F53\u524D\u63A5\u53E3\u5230 Codex \u7CFB\u5217...",
            async () =>
            {
                var result = await _codexFamilyConfigApplyService.ApplyAsync(
                    ProxyBaseUrl,
                    ProxyApiKey,
                    ProxyModel,
                    ResolveCurrentProxyDisplayName());

                StatusMessage = result.Succeeded
                    ? result.Summary
                    : $"\u5E94\u7528\u5931\u8D25\uFF1A{result.Error ?? result.Summary}";

                AppendModuleOutput(
                    "\u5E94\u7528\u5F53\u524D\u63A5\u53E3\u5230 Codex \u7CFB\u5217",
                    BuildApplicationCenterApplySummary(result),
                    BuildApplicationCenterApplyDetail(result));

                if (result.Succeeded)
                {
                    await RunClientApiDiagnosticsCoreAsync();
                }
            });
    }

    private static string BuildApplicationCenterApplySummary(ClientAppApplyResult result)
        => $"\u76EE\u6807\uFF1A{(result.AppliedTargets.Count == 0 ? "Codex \u7CFB\u5217" : string.Join(" / ", result.AppliedTargets))}\n\u7ED3\u679C\uFF1A{result.Summary}";

    private string BuildApplicationCenterApplyDetail(ClientAppApplyResult result)
    {
        StringBuilder builder = new();
        builder.AppendLine($"\u5F53\u524D Base URL\uFF1A{ProxyBaseUrl}");
        builder.AppendLine($"\u5F53\u524D\u6A21\u578B\uFF1A{ProxyModel}");
        builder.AppendLine($"\u5F53\u524D API Key\uFF1A{MaskApiKey(ProxyApiKey)}");
        builder.AppendLine($"\u914D\u7F6E\u540D\u79F0\uFF1A{ResolveCurrentProxyDisplayName() ?? "Custom OpenAI-Compatible"}");
        builder.AppendLine($"\u5E94\u7528\u76EE\u6807\uFF1A{(result.AppliedTargets.Count == 0 ? "\u65E0" : string.Join(" / ", result.AppliedTargets))}");
        builder.AppendLine($"\u5DF2\u5904\u7406\u6587\u4EF6\uFF1A{(result.ChangedFiles.Count == 0 ? "\u65E0" : string.Join("\n", result.ChangedFiles))}");
        builder.AppendLine($"\u5907\u4EFD\u6587\u4EF6\uFF1A{(result.BackupFiles.Count == 0 ? "\u65E0" : string.Join("\n", result.BackupFiles))}");
        builder.Append($"\u9519\u8BEF\uFF1A{result.Error ?? "\u65E0"}");
        return builder.ToString();
    }

    private void NotifyApplicationCenterProxyContextChanged()
    {
        OnPropertyChanged(nameof(ApplicationCenterApplyTargetSummary));
        OnPropertyChanged(nameof(ApplicationCenterApplyPreviewDetail));
        ApplyCurrentInterfaceToCodexAppsCommand?.RaiseCanExecuteChanged();
    }

    private List<string> GetApplicationCenterMissingContextFields()
    {
        List<string> missing = [];

        if (string.IsNullOrWhiteSpace(ProxyBaseUrl))
        {
            missing.Add("Base URL");
        }

        if (string.IsNullOrWhiteSpace(ProxyModel))
        {
            missing.Add("\u6A21\u578B");
        }

        if (string.IsNullOrWhiteSpace(ProxyApiKey))
        {
            missing.Add("API Key");
        }

        return missing;
    }

    private string? ResolveCurrentProxyDisplayName()
    {
        if (Uri.TryCreate(ProxyBaseUrl?.Trim(), UriKind.Absolute, out var uri) &&
            !string.IsNullOrWhiteSpace(uri.Host))
        {
            return uri.Host;
        }

        return null;
    }

    private static string FormatPreviewValue(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? "\uFF08\u672A\u586B\u5199\uFF09"
            : value.Trim();

    private static string FormatPreviewApiKey(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? "\uFF08\u672A\u586B\u5199\uFF09"
            : MaskApiKey(value);
}
