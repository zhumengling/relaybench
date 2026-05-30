using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using RelayBench.Core.Models;
using RelayBench.Core.Services;
using RelayBench.Services;
using RelayBench.WinUI.Services;
using RelayBench.WinUI.Storage;

namespace RelayBench.WinUI.ViewModels;

public sealed partial class ApplicationCenterViewModel : ObservableObject
{
    private void RefreshTargets()
    {
        var selectedIds = Targets
            .Where(static target => target.IsSelected)
            .Select(static target => target.TargetId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var detectedApps = _appDetector.Detect();
        var installedNames = detectedApps
            .Where(IsDetected)
            .Select(static app => app.DisplayName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (detectedApps.Any(static app => app.Id is "codex-cli" or "codex-desktop" && IsDetected(app)))
        {
            installedNames.Add("Codex");
        }

        var protocolKnown = _lastProbeResult is not null;
        var inferredResponses = !protocolKnown && IsPreferredProtocol("responses");
        var inferredChat = !protocolKnown && IsPreferredProtocol("chat");
        var inferredAnthropic = !protocolKnown && IsPreferredProtocol("anthropic");
        var effectiveCodexTemplate = _customCodexTemplate ?? BuildDefaultCodexTemplate();
        var context = new ClientAppApplyPlanContext(
            BaseUrl.Trim(),
            ApiKey.Trim(),
            Model.Trim(),
            protocolKnown ? ResponsesSupported : inferredResponses,
            protocolKnown ? ChatSupported : inferredChat,
            protocolKnown ? AnthropicSupported : inferredAnthropic,
            installedNames)
        {
            CandidateModels = AvailableModels
                .Where(static model => !string.IsNullOrWhiteSpace(model))
                .ToHashSet(StringComparer.OrdinalIgnoreCase)
        };

        Targets.Clear();
        foreach (var target in _applyPlanner.BuildTargets(context))
        {
            var detected = ResolveDetectedApp(target.Id, detectedApps);
            var item = AppTargetItem.FromClientTarget(
                target,
                detected,
                BaseUrl,
                protocolKnown,
                selectedIds.Contains(target.Id),
                string.Equals(target.Id, "codex", StringComparison.OrdinalIgnoreCase)
                    ? effectiveCodexTemplate
                    : null,
                NotifyTargetSelectionChanged);
            Targets.Add(item);
        }

        NotifyTargetSelectionChanged();
    }

    private void NotifyTargetSelectionChanged()
    {
        OnPropertyChanged(nameof(SelectedTargetCount));
        OnPropertyChanged(nameof(SelectedRestoreTargetCount));
        OnPropertyChanged(nameof(ApplySelectedButtonText));
        OnPropertyChanged(nameof(RestoreSelectedButtonText));
        RefreshAccessOverview();
    }

    private void RefreshAccessOverview()
    {
        var targetCount = Targets.Count;
        var installedCount = Targets.Count(static target => target.Installed);
        var selectableCount = Targets.Count(static target => target.IsSelectable);
        var selectedCount = SelectedRestoreTargetCount;
        var selectedWritableCount = SelectedTargetCount;

        TargetOverviewText = $"{targetCount} targets | {selectableCount} writable";
        InstalledTargetOverviewText = $"已检测 {installedCount}/{targetCount}";
        SelectedTargetOverviewText = $"已选 {selectedCount} | 可写 {selectedWritableCount}";
        BackupOverviewText = $"近期备份 {LastBackupFileCount} | 文件变更 {LastChangedFileCount}";
        EndpointTakeoverText = BuildEndpointTakeoverText(BaseUrl, AvailableModels.Count);
        ProtocolCoverageText = BuildProtocolCoverageText(_lastProbeResult);
    }

    private static string BuildEndpointTakeoverText(string baseUrl, int modelCount)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return "--";
        }

        var endpoint = TrimForDisplay(baseUrl.Trim(), 42);
        var mode = IsLocalEndpoint(baseUrl) ? "本地透明代理" : "外部入口";
        return $"{mode} | {endpoint} | 模型 {Math.Max(0, modelCount)}";
    }

    private static string BuildProtocolCoverageText(ProxyEndpointProtocolProbeResult? probeResult)
    {
        if (probeResult is null)
        {
            return "未复核";
        }

        var responses = probeResult.ResponsesSupported ? "R 通过" : "R --";
        var chat = probeResult.ChatCompletionsSupported ? "C 通过" : "C --";
        var anthropic = probeResult.AnthropicMessagesSupported ? "A 通过" : "A --";
        return $"{responses} | {chat} | {anthropic} | 优先 {probeResult.PreferredWireApi ?? "--"}";
    }

    private static string TrimForDisplay(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..Math.Max(0, maxLength - 3)] + "...";

    private void UpdateLastFileOperationCounts(IReadOnlyList<ApplicationAccessOperationReceipt> receipts)
        => UpdateLastFileOperationCounts(
            receipts.Sum(static item => item.ChangedFileCount),
            receipts.Sum(static item => item.BackupFileCount));

    private void UpdateLastFileOperationCounts(int changedFiles, int backupFiles)
    {
        LastChangedFileCount = Math.Max(0, changedFiles);
        LastBackupFileCount = Math.Max(0, backupFiles);
        RefreshAccessOverview();
    }

    private static TransparentProxyDetectedApp? ResolveDetectedApp(
        string targetId,
        IReadOnlyList<TransparentProxyDetectedApp> detectedApps)
        => targetId switch
        {
            "codex" => detectedApps.FirstOrDefault(static app => app.Id is "codex-cli" or "codex-desktop"),
            "claude-cli" => detectedApps.FirstOrDefault(static app => app.Id == "claude-cli"),
            "antigravity" => detectedApps.FirstOrDefault(static app => app.Id == "antigravity"),
            "vscode-codex" => detectedApps.FirstOrDefault(static app => app.Id == "vs-codex"),
            _ => null
        };

    private static bool IsDetected(TransparentProxyDetectedApp app)
        => app.IsDetected;

    private static bool IsLocalEndpoint(string? value)
    {
        if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri))
        {
            return false;
        }

        return uri.Host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
               uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
               uri.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSameEndpoint(string? left, string? right)
    {
        if (!Uri.TryCreate(left?.Trim(), UriKind.Absolute, out var leftUri) ||
            !Uri.TryCreate(right?.Trim(), UriKind.Absolute, out var rightUri))
        {
            return false;
        }

        return leftUri.Port == rightUri.Port &&
               (leftUri.Host.Equals(rightUri.Host, StringComparison.OrdinalIgnoreCase) ||
                IsLoopbackHost(leftUri.Host) && IsLoopbackHost(rightUri.Host));
    }

    private static bool IsLoopbackHost(string host)
        => host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
           host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
           host.Equals("::1", StringComparison.OrdinalIgnoreCase);

    private static bool IsVsCodeTarget(AppTargetItem target)
        => string.Equals(target.TargetId, "vscode-codex", StringComparison.OrdinalIgnoreCase);

    private void ResetProbeRows()
    {
        ProtocolProbeRows.Clear();
        ProtocolProbeRows.Add(new ProtocolProbeItem("Responses API", "POST /v1/responses", "等待探测", "--", ToneAccent));
        ProtocolProbeRows.Add(new ProtocolProbeItem("Anthropic Messages", "POST /v1/messages", "等待探测", "--", ToneAccent));
        ProtocolProbeRows.Add(new ProtocolProbeItem("OpenAI Chat", "POST /v1/chat/completions", "等待探测", "--", ToneAccent));
    }

    private void UpdateProbeRows(ProxyEndpointProtocolProbeResult result)
    {
        ProtocolProbeRows.Clear();
        ProtocolProbeRows.Add(BuildProbeRow(
            "Responses API",
            "POST /v1/responses",
            result.ResponsesSupported,
            FindScenario(result, ProxyProbeScenarioKind.Responses)));
        ProtocolProbeRows.Add(BuildProbeRow(
            "Anthropic Messages",
            "POST /v1/messages",
            result.AnthropicMessagesSupported,
            FindScenario(result, ProxyProbeScenarioKind.AnthropicMessages)));
        ProtocolProbeRows.Add(BuildProbeRow(
            "OpenAI Chat",
            "POST /v1/chat/completions",
            result.ChatCompletionsSupported,
            FindScenario(result, ProxyProbeScenarioKind.ChatCompletions)));
    }

    private static ProxyProbeScenarioResult? FindScenario(
        ProxyEndpointProtocolProbeResult result,
        ProxyProbeScenarioKind kind)
        => result.ScenarioResults?.FirstOrDefault(scenario => scenario.Scenario == kind);

    private static ProtocolProbeItem BuildProbeRow(
        string protocol,
        string request,
        bool supported,
        ProxyProbeScenarioResult? scenario)
    {
        if (scenario is null)
        {
            return new ProtocolProbeItem(
                protocol,
                request,
                supported ? "缓存通过" : "缓存失败",
                "--",
                supported ? ToneHealthy : ToneDanger);
        }

        return new ProtocolProbeItem(
            protocol,
            request,
            supported ? "探测通过" : "失败",
            scenario.Latency is null ? "--" : $"{scenario.Latency.Value.TotalMilliseconds:F0} ms",
            supported ? ToneHealthy : ToneDanger);
    }

    private static string BuildProbeSummary(ProxyEndpointProtocolProbeResult result, bool fromCache)
    {
        var parts = new List<string>();
        parts.Add(result.ResponsesSupported ? "Responses: 通过" : "Responses: 失败");
        parts.Add(result.AnthropicMessagesSupported ? "Anthropic: 通过" : "Anthropic: 失败");
        parts.Add(result.ChatCompletionsSupported ? "Chat: 通过" : "Chat: 失败");
        var source = fromCache ? "缓存" : "实测";
        return $"{source} | {string.Join(" | ", parts)}";
    }

    private void RefreshTemplateRows(AppTargetItem? target = null)
    {
        var template = GetCurrentCodexTemplate(target);
        var useOpenAiBaseUrlMode = CodexFamilyConfigApplyService.ShouldUseOpenAiBaseUrlMode(template.BaseUrl);
        var commonOptionCount = CountCommonCodexOptions(template.AdditionalRawSettings);

        TemplateRows.Clear();
        TemplateRows.Add(new ConfigTemplateRow("model", string.IsNullOrWhiteSpace(Model) ? "--" : Model.Trim(), "默认模型 ID"));
        TemplateRows.Add(useOpenAiBaseUrlMode
            ? new ConfigTemplateRow("openai_base_url", string.IsNullOrWhiteSpace(template.BaseUrl) ? "--" : template.BaseUrl, "Codex 文档推荐入口")
            : new ConfigTemplateRow("model_provider", "relaybench", "Codex 自定义 provider"));
        TemplateRows.Add(new ConfigTemplateRow("base_url", string.IsNullOrWhiteSpace(BaseUrl) ? "--" : BaseUrl.Trim(), "目标入口"));
        TemplateRows.Add(new ConfigTemplateRow("protocol", "responses", "Codex 固定使用 Responses API"));
        TemplateRows.Add(new ConfigTemplateRow("target", target?.Name ?? "所选目标", "预览或写入目标"));
        TemplateRows.Add(new ConfigTemplateRow(
            "api_key",
            MaskApiKey(ApiKey),
            useOpenAiBaseUrlMode ? "本地透明代理模式不写入 config.toml" : "用于 provider bearer token"));
        TemplateRows.Add(new ConfigTemplateRow("provider_name", template.ProviderName, "Codex 提供方名称"));
        TemplateRows.Add(new ConfigTemplateRow("model_context_window", template.ModelContextWindow?.ToString() ?? "--", "上下文窗口"));
        TemplateRows.Add(new ConfigTemplateRow("model_auto_compact_token_limit", template.ModelAutoCompactTokenLimit?.ToString() ?? "--", "自动压缩"));
        TemplateRows.Add(new ConfigTemplateRow("request_max_retries", template.RequestMaxRetries?.ToString() ?? "--", "请求重试"));
        TemplateRows.Add(new ConfigTemplateRow("stream_max_retries", template.StreamMaxRetries?.ToString() ?? "--", "流式重试"));
        TemplateRows.Add(new ConfigTemplateRow("stream_idle_timeout_ms", template.StreamIdleTimeoutMs?.ToString() ?? "--", "流式空闲超时"));
        TemplateRows.Add(new ConfigTemplateRow("common_options", commonOptionCount.ToString(), "推理、权限、工具等常用项"));
        TemplateRows.Add(new ConfigTemplateRow("additional", template.AdditionalRawSettings?.Count.ToString() ?? "0", "原始设置"));
    }

    private static int CountCommonCodexOptions(IReadOnlyDictionary<string, string>? settings)
    {
        if (settings is null || settings.Count == 0)
        {
            return 0;
        }

        return settings.Keys.Count(static key => key is
            "model_reasoning_effort" or
            "model_reasoning_summary" or
            "model_verbosity" or
            "tools.web_search" or
            "approval_policy" or
            "sandbox_mode" or
            "sandbox_workspace_write.network_access" or
            "personality" or
            "features.hooks" or
            "features.shell_snapshot");
    }

    private void ResetTraceSteps()
    {
        WriteTraceSteps.Clear();
        WriteTraceSteps.Add(new WriteTraceItem("1", "等待目标", "先拉取模型或探测协议", "待处理", ToneAccent));
        WriteTraceSteps.Add(new WriteTraceItem("2", "预览写入", "在目标行使用设置操作", "待处理", ToneAccent));
        WriteTraceSteps.Add(new WriteTraceItem("3", "写入配置", "会先备份原始配置", "待处理", ToneAccent));
        WriteTraceSteps.Add(new WriteTraceItem("4", "验证结果", "显示变更文件和错误", "待处理", ToneAccent));
        WriteTraceSteps.Add(new WriteTraceItem("5", "需要重启", "下次启动客户端后生效", "待处理", ToneAccent));
    }

    private void SetTracePreview(AppTargetItem target)
    {
        WriteTraceSteps.Clear();
        WriteTraceSteps.Add(new WriteTraceItem("1", "定位配置", target.ConfigFile, "完成", ToneHealthy));
        WriteTraceSteps.Add(new WriteTraceItem("2", "生成模板", target.Protocol, "完成", ToneHealthy));
        WriteTraceSteps.Add(new WriteTraceItem("3", "等待确认", target.Name, "就绪", ToneAccent));
        WriteTraceSteps.Add(new WriteTraceItem("4", "备份原始配置", "写入时自动执行", "待处理", ToneAccent));
        WriteTraceSteps.Add(new WriteTraceItem("5", "应用", "下次启动 / 重载后生效", "待处理", ToneAccent));
    }

    private void SetTraceFromApplyResult(AppTargetItem target, ClientAppApplyResult result)
    {
        var changed = string.Join(", ", result.ChangedFiles.DefaultIfEmpty("无文件变更"));
        var backups = string.Join(", ", result.BackupFiles.DefaultIfEmpty("无备份"));
        WriteTraceSteps.Clear();
        WriteTraceSteps.Add(new WriteTraceItem("1", "备份当前配置", backups, result.BackupFiles.Count > 0 ? "完成" : "无变化", ToneHealthy));
        WriteTraceSteps.Add(new WriteTraceItem("2", "写入入口", changed, result.Succeeded ? "完成" : "失败", result.Succeeded ? ToneHealthy : ToneDanger));
        WriteTraceSteps.Add(new WriteTraceItem("3", "目标客户端", target.Name, result.Succeeded ? "完成" : "检查", result.Succeeded ? ToneHealthy : ToneWarning));
        WriteTraceSteps.Add(new WriteTraceItem("4", "协议", PreferredProtocol, result.Succeeded ? "完成" : "待处理", result.Succeeded ? ToneHealthy : ToneAccent));
        WriteTraceSteps.Add(new WriteTraceItem("5", "应用", "下次启动 / 重载后生效", result.Succeeded ? "就绪" : "待处理", result.Succeeded ? ToneAccent : ToneWarning));
    }

    private void SetTraceFromRestoreResult(AppTargetItem target, ClientApiConfigRestoreResult result)
    {
        var changed = string.Join(", ", result.ChangedFiles.DefaultIfEmpty("无文件变更"));
        SetTraceState(target.Name, changed, result.Succeeded, result.Summary);
    }

    private void SetTraceFromVsCodeResult(
        AppTargetItem target,
        bool succeeded,
        string summary,
        IReadOnlyList<string> changedFiles)
    {
        var changed = string.Join(", ", changedFiles.DefaultIfEmpty("无文件变更"));
        SetTraceState(target.Name, changed, succeeded, summary);
    }

    private void SetTraceState(string targetName, string path, bool succeeded, string detail)
    {
        WriteTraceSteps.Clear();
        WriteTraceSteps.Add(new WriteTraceItem("1", "目标客户端", targetName, "完成", ToneHealthy));
        WriteTraceSteps.Add(new WriteTraceItem("2", "配置文件", string.IsNullOrWhiteSpace(path) ? "无文件变更" : path, succeeded ? "完成" : "失败", succeeded ? ToneHealthy : ToneDanger));
        WriteTraceSteps.Add(new WriteTraceItem("3", "结果", detail, succeeded ? "完成" : "检查", succeeded ? ToneHealthy : ToneWarning));
        WriteTraceSteps.Add(new WriteTraceItem("4", "协议", PreferredProtocol, succeeded ? "完成" : "待处理", succeeded ? ToneHealthy : ToneAccent));
        WriteTraceSteps.Add(new WriteTraceItem("5", "应用", "下次启动 / 重载后生效", succeeded ? "就绪" : "待处理", succeeded ? ToneAccent : ToneWarning));
    }

    private CodexConfigTemplate GetCurrentCodexTemplate(AppTargetItem? target = null)
        => target?.CodexConfigTemplate ?? _customCodexTemplate ?? BuildDefaultCodexTemplate();

    private CodexConfigTemplate BuildDefaultCodexTemplate()
        => CodexFamilyConfigApplyService.CreateDefaultTemplate(
            BaseUrl,
            ApiKey,
            Model,
            "RelayBench",
            modelContextWindow: null,
            preferredWireApi: _lastProbeResult?.PreferredWireApi ?? PreferredProtocol);

    private void SyncCustomCodexTemplateCoreFields()
    {
        if (_customCodexTemplate is null)
        {
            return;
        }

        var fallback = BuildDefaultCodexTemplate();
        _customCodexTemplate = _customCodexTemplate with
        {
            Model = fallback.Model,
            BaseUrl = fallback.BaseUrl,
            ExperimentalBearerToken = fallback.ExperimentalBearerToken,
            ModelContextWindow = _customCodexTemplate.ModelContextWindow ?? fallback.ModelContextWindow,
            ModelAutoCompactTokenLimit = _customCodexTemplate.ModelAutoCompactTokenLimit ?? fallback.ModelAutoCompactTokenLimit
        };
    }

    private string BuildCodexPreview()
    {
        var template = GetCurrentCodexTemplate();
        if (CodexFamilyConfigApplyService.ShouldUseOpenAiBaseUrlMode(template.BaseUrl))
        {
            return string.Join(
                Environment.NewLine,
                [
                    "目标文件: %USERPROFILE%\\.codex\\config.toml",
                    "操作: 按 Codex 文档推荐写入 openai_base_url，并清理旧版 RelayBench provider/profile 残留。",
                    $"openai_base_url = \"{template.BaseUrl}\"",
                    $"model = \"{template.Model}\"",
                    "不会改动: MCP servers、hooks、权限策略、项目级 .codex/config.toml。"
                ]);
        }

        return string.Join(
            Environment.NewLine,
            [
                "目标文件: %USERPROFILE%\\.codex\\config.toml",
                "操作: 写入 RelayBench 自定义 provider，并在变更前保留备份。",
                $"model = \"{template.Model}\"",
                "model_provider = \"relaybench\"",
                $"base_url = \"{template.BaseUrl}\"",
                $"wire_api = \"{template.WireApi}\"",
                $"experimental_bearer_token = \"{MaskApiKey(template.ExperimentalBearerToken)}\""
            ]);
    }

    private string BuildClaudePreview()
        => string.Join(
            Environment.NewLine,
            [
                "目标文件: %USERPROFILE%\\.claude\\settings.json",
                "辅助文件: %USERPROFILE%\\.claude.json",
                "操作: 写入 Anthropic 环境变量；本地统一入口使用 RelayBench 转换。",
                $"ANTHROPIC_BASE_URL = {BaseUrl.Trim()}",
                $"ANTHROPIC_MODEL = {Model.Trim()}",
                $"ANTHROPIC_DEFAULT_OPUS_MODEL = {Model.Trim()}",
                $"ANTHROPIC_DEFAULT_SONNET_MODEL = {Model.Trim()}",
                $"ANTHROPIC_DEFAULT_HAIKU_MODEL = {Model.Trim()}",
                $"API Key = {MaskApiKey(ApiKey)}"
            ]);

    private string BuildAntigravityPreview()
        => string.Join(
            Environment.NewLine,
            [
                "Target file: %USERPROFILE%\\.gemini\\.env + %USERPROFILE%\\.gemini\\settings.json",
                "Action: write Gemini environment variables and switch auth to gemini-api-key.",
                $"GOOGLE_GEMINI_BASE_URL = {BaseUrl.Trim()}",
                $"GEMINI_MODEL = {Model.Trim()}",
                $"GEMINI_API_KEY = {MaskApiKey(ApiKey)}"
            ]);

    private static string MaskApiKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "--";
        }

        var key = value.Trim();
        return key.Length <= 8 ? new string('*', key.Length) : $"{key[..4]}...{key[^4..]}";
    }

    internal sealed record ApplicationAccessOperationReceipt(
        string TargetId,
        string TargetName,
        bool Succeeded,
        string Summary,
        int ChangedFileCount,
        int BackupFileCount,
        string Error,
        IReadOnlyList<string>? ChangedFiles = null,
        IReadOnlyList<string>? BackupFiles = null)
    {
        public static ApplicationAccessOperationReceipt FromFiles(
            AppTargetItem target,
            bool succeeded,
            string summary,
            IReadOnlyCollection<string> changedFiles,
            IReadOnlyCollection<string> backupFiles,
            string? error)
            => new(
                target.TargetId,
                target.Name,
                succeeded,
                summary,
                Math.Max(0, changedFiles.Count),
                Math.Max(0, backupFiles.Count),
                error ?? string.Empty,
                changedFiles.Where(static path => !string.IsNullOrWhiteSpace(path)).ToArray(),
                backupFiles.Where(static path => !string.IsNullOrWhiteSpace(path)).ToArray());

        public static ApplicationAccessOperationReceipt Failed(AppTargetItem target, string summary, string? error)
            => new(target.TargetId, target.Name, false, summary, 0, 0, error ?? summary, [], []);
    }
}
