using System.Collections.ObjectModel;
using System.Text.Json;
using RelayBench.Core.Models;
using RelayBench.Core.Services;

namespace RelayBench.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private const string CodexConfigModelKey = "model";
    private const string CodexConfigModelProviderKey = "model_provider";
    private const string CodexConfigContextWindowKey = "model_context_window";
    private const string CodexConfigAutoCompactKey = "model_auto_compact_token_limit";
    private const string CodexConfigProviderNameKey = "model_providers.relaybench.name";
    private const string CodexConfigBaseUrlKey = "model_providers.relaybench.base_url";
    private const string CodexConfigWireApiKey = "model_providers.relaybench.wire_api";
    private const string CodexConfigBearerTokenKey = "model_providers.relaybench.experimental_bearer_token";
    private const string CodexConfigHttpHeadersKey = "model_providers.relaybench.http_headers";
    private const string CodexConfigRequestRetriesKey = "model_providers.relaybench.request_max_retries";
    private const string CodexConfigStreamRetriesKey = "model_providers.relaybench.stream_max_retries";
    private const string CodexConfigStreamIdleTimeoutKey = "model_providers.relaybench.stream_idle_timeout_ms";

    private static readonly HashSet<string> BuiltInCodexTemplateKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        CodexConfigModelKey,
        CodexConfigModelProviderKey,
        CodexConfigContextWindowKey,
        CodexConfigAutoCompactKey,
        CodexConfigProviderNameKey,
        CodexConfigBaseUrlKey,
        CodexConfigWireApiKey,
        CodexConfigBearerTokenKey,
        CodexConfigHttpHeadersKey,
        CodexConfigRequestRetriesKey,
        CodexConfigStreamRetriesKey,
        CodexConfigStreamIdleTimeoutKey
    };

    private bool _isCodexConfigTemplateDialogOpen;
    private string _codexConfigTemplateDialogSummary = "查看并调整本次将写入 ~/.codex/config.toml 的 RelayBench provider 模板。";
    private ClientApplyTargetItemViewModel? _editingCodexConfigTemplateTarget;

    public ObservableCollection<CodexConfigTemplateRowViewModel> CodexConfigTemplateRows { get; } = [];

    public bool IsCodexConfigTemplateDialogOpen
    {
        get => _isCodexConfigTemplateDialogOpen;
        private set
        {
            if (SetProperty(ref _isCodexConfigTemplateDialogOpen, value))
            {
                SaveCodexConfigTemplateCommand?.RaiseCanExecuteChanged();
                ResetCodexConfigTemplateCommand?.RaiseCanExecuteChanged();
            }
        }
    }

    public string CodexConfigTemplateDialogTitle => "Codex config.toml 模板";

    public string CodexConfigTemplateDialogSummary
    {
        get => _codexConfigTemplateDialogSummary;
        private set => SetProperty(ref _codexConfigTemplateDialogSummary, value);
    }

    private Task OpenCodexConfigTemplateDialogAsync(ClientApplyTargetItemViewModel? item)
    {
        if (item?.HasSettings != true)
        {
            return Task.CompletedTask;
        }

        _editingCodexConfigTemplateTarget = item;
        LoadCodexConfigTemplateRows(item.CodexConfigTemplate ?? item.DefaultCodexConfigTemplate);
        CodexConfigTemplateDialogSummary =
            "表格覆盖 Codex 文档中明确可调的 config.toml 项；空值不会写入，wire_api 固定为 responses。";
        IsCodexConfigTemplateDialogOpen = true;
        return Task.CompletedTask;
    }

    private Task SaveCodexConfigTemplateDialogAsync()
    {
        if (_editingCodexConfigTemplateTarget is null)
        {
            IsCodexConfigTemplateDialogOpen = false;
            return Task.CompletedTask;
        }

        if (!TryBuildCodexConfigTemplateFromRows(out var template, out var error))
        {
            StatusMessage = $"Codex config.toml 模板保存失败：{error}";
            return Task.CompletedTask;
        }

        _editingCodexConfigTemplateTarget.CodexConfigTemplate = template;
        StatusMessage = "Codex config.toml 模板已更新，本次应用时写入。";
        IsCodexConfigTemplateDialogOpen = false;
        return Task.CompletedTask;
    }

    private Task ResetCodexConfigTemplateDialogAsync()
    {
        if (_editingCodexConfigTemplateTarget is not null)
        {
            LoadCodexConfigTemplateRows(_editingCodexConfigTemplateTarget.DefaultCodexConfigTemplate);
            StatusMessage = "已恢复 Codex config.toml 默认模板。";
        }

        return Task.CompletedTask;
    }

    private Task CloseCodexConfigTemplateDialogAsync()
    {
        IsCodexConfigTemplateDialogOpen = false;
        _editingCodexConfigTemplateTarget = null;
        return Task.CompletedTask;
    }

    private void LoadCodexConfigTemplateRows(CodexConfigTemplate? template)
    {
        template ??= CodexFamilyConfigApplyService.CreateDefaultTemplate(
            string.Empty,
            string.Empty,
            string.Empty,
            "RelayBench",
            modelContextWindow: null,
            preferredWireApi: ProxyWireApiProbeService.ResponsesWireApi);

        CodexConfigTemplateRows.Clear();
        AddCoreRows(template);
        AddDocumentedOptionalRows(template);
    }

    private void AddCoreRows(CodexConfigTemplate template)
    {
        AddRow(CodexConfigModelKey, template.Model, "默认模型名称；应用当前接口时会同步为当前模型。", true, "text");
        AddRow(CodexConfigModelProviderKey, "relaybench", "固定指向 RelayBench 维护的 provider，避免写入后找不到对应 section。", false, "text");
        AddRow(CodexConfigContextWindowKey, FormatNullableInteger(template.ModelContextWindow), "模型上下文窗口；留空则不写入该项。", true, "integer");
        AddRow(CodexConfigAutoCompactKey, FormatNullableInteger(template.ModelAutoCompactTokenLimit), "自动压缩触发 token 阈值；留空则不写入该项。", true, "integer");
        AddRow(CodexConfigProviderNameKey, template.ProviderName, "Codex 配置中显示的 provider 名称。", true, "text");
        AddRow(CodexConfigBaseUrlKey, template.BaseUrl, "Responses 接口地址，通常以 /v1 结尾。", true, "url");
        AddRow(CodexConfigWireApiKey, "responses", "Codex 当前只使用 Responses 协议，此项固定不可改。", false, "text");
        AddRow(CodexConfigBearerTokenKey, template.ExperimentalBearerToken, "写入 provider 的 bearer token。", true, "secret");
        AddRow(CodexConfigHttpHeadersKey, template.HttpHeaders, "TOML inline table 格式的 HTTP 头，例如 { \"Content-Type\" = \"application/json\" }。", true, "raw");
        AddRow(CodexConfigRequestRetriesKey, FormatNullableInteger(template.RequestMaxRetries), "provider 普通请求最大重试次数；留空使用 Codex 默认值。", true, "integer");
        AddRow(CodexConfigStreamRetriesKey, FormatNullableInteger(template.StreamMaxRetries), "provider 流式请求最大重试次数；留空使用 Codex 默认值。", true, "integer");
        AddRow(CodexConfigStreamIdleTimeoutKey, FormatNullableInteger(template.StreamIdleTimeoutMs), "provider 流式响应空闲超时毫秒数；留空使用 Codex 默认值。", true, "integer");
    }

    private void AddDocumentedOptionalRows(CodexConfigTemplate template)
    {
        var additional = template.AdditionalRawSettings ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        AddOptional("profile", "启动时默认使用的配置 profile 名称。", "text");
        AddOptional("personality", "选择内置人格，例如 chatgpt、codex、default、none。", "text");
        AddOptional("review_model", "代码审查时使用的模型。", "text");
        AddOptional("oss_provider", "开源模型 provider 选择，例如 ollama、lmstudio。", "text");
        AddOptional("service_tier", "OpenAI 服务等级，例如 auto、default、flex、priority。", "text");
        AddOptional("tool_output_token_limit", "工具输出最大 token 数；留空使用默认限制。", "integer");
        AddOptional("model_catalog_json", "自定义模型目录 JSON 文件路径。", "path");
        AddOptional("background_terminal_max_timeout", "后台终端最长等待时间，毫秒。", "integer");
        AddOptional("log_dir", "Codex 日志目录路径。", "path");
        AddOptional("sqlite_home", "Codex SQLite 数据目录路径。", "path");

        AddOptional("model_reasoning_effort", "推理强度，例如 low、medium、high、xhigh。", "text");
        AddOptional("plan_mode_reasoning_effort", "Plan 模式推理强度，未设置时使用模型默认值。", "text");
        AddOptional("model_reasoning_summary", "推理摘要模式，例如 auto、concise、detailed、none。", "text");
        AddOptional("model_verbosity", "输出详略程度，例如 low、medium、high。", "text");
        AddOptional("model_supports_reasoning_summaries", "显式声明模型是否支持 reasoning summary。", "boolean");

        AddOptional("developer_instructions", "追加给模型的开发者指令文本。", "text");
        AddOptional("compact_prompt", "手动压缩上下文时使用的提示词。", "text");
        AddOptional("experimental_compact_prompt_file", "从文件读取压缩提示词。", "path");

        AddOptional("approval_policy", "命令审批策略，例如 untrusted、on-failure、on-request、never。", "text");
        AddOptional("approvals_reviewer", "审批 reviewer 模式或 granular 策略；复杂配置可填 TOML inline table。", "stringOrRaw");
        AddOptional("sandbox_mode", "沙箱模式，例如 read-only、workspace-write、danger-full-access。", "text");
        AddOptional("allow_login_shell", "是否允许以 login shell 语义执行命令。", "boolean");
        AddOptional("default_permissions", "默认权限策略；复杂配置可填 TOML inline table。", "stringOrRaw");

        AddOptional("cli_auth_credentials_store", "凭据存储方式，例如 keychain、file。", "text");
        AddOptional("chatgpt_base_url", "ChatGPT 登录/OAuth 流程使用的基础地址。", "url");
        AddOptional("openai_base_url", "简单代理路由入口，可直接指向 RelayBench 本地 /v1。", "url");

        AddOptional("project_doc_max_bytes", "项目文档最大读取字节数。", "integer");
        AddOptional("project_doc_fallback_filenames", "项目说明文件候选名数组，例如 [\"AGENTS.md\", \"README.md\"]。", "raw");
        AddOptional("project_root_markers", "项目根目录标记文件数组，例如 [\".git\", \"package.json\"]。", "raw");

        AddOptional("file_opener", "点击文件时使用的打开器，例如 vscode、cursor、none。", "text");
        AddOptional("hide_agent_reasoning", "是否隐藏 agent reasoning。", "boolean");
        AddOptional("show_raw_agent_reasoning", "是否显示原始 reasoning 事件。", "boolean");
        AddOptional("disable_paste_burst", "是否禁用粘贴爆发保护。", "boolean");
        AddOptional("windows_wsl_setup_acknowledged", "Windows WSL 设置提示是否已确认。", "boolean");
        AddOptional("check_for_update_on_startup", "启动时是否检查更新。", "boolean");
        AddOptional("web_search", "是否启用 Web search。", "boolean");
        AddOptional("notify", "通知命令数组，例如 [\"notify-send\", \"Codex\"]。", "raw");

        AddOptional("history.persistence", "历史记录持久化策略，例如 save-all、none。", "text");
        AddOptional("history.max_bytes", "history 文件最大字节数。", "integer");

        AddOptional("tui.notifications", "TUI 通知开关或通知数组。", "raw");
        AddOptional("tui.notification_method", "通知方式，例如 toast、terminal。", "text");
        AddOptional("tui.notification_condition", "通知触发条件，例如 always、on-failure。", "text");
        AddOptional("tui.animations", "是否启用 TUI 动画。", "boolean");
        AddOptional("tui.show_tooltips", "是否显示 TUI 工具提示。", "boolean");
        AddOptional("tui.alternate_screen", "是否使用 alternate screen。", "boolean");
        AddOptional("tui.status_line", "自定义状态栏模板。", "text");

        AddOptional("analytics.enabled", "是否启用 analytics。", "boolean");
        AddOptional("feedback.enabled", "是否启用反馈入口。", "boolean");

        AddOptional("features.shell_tool", "是否启用 shell tool。", "boolean");
        AddOptional("features.apps", "是否启用 app connector / apps 能力。", "boolean");
        AddOptional("features.codex_hooks", "是否启用 Codex hooks。", "boolean");
        AddOptional("features.unified_exec", "是否启用统一 exec。", "boolean");
        AddOptional("features.shell_snapshot", "是否启用 shell snapshot。", "boolean");
        AddOptional("features.multi_agent", "是否启用多 agent。", "boolean");
        AddOptional("features.personality", "是否启用 personality 功能。", "boolean");
        AddOptional("features.fast_mode", "是否启用 fast mode。", "boolean");
        AddOptional("features.enable_request_compression", "是否启用请求压缩。", "boolean");
        AddOptional("features.skill_mcp_dependency_install", "是否允许 skill MCP 依赖安装。", "boolean");
        AddOptional("features.prevent_idle_sleep", "运行时是否阻止系统空闲睡眠。", "boolean");
        AddOptional("features.memories", "是否启用 memories。", "boolean");
        AddOptional("features.undo", "是否启用 undo。", "boolean");

        AddOptional("memories.generate_memories", "是否自动生成 memories。", "boolean");
        AddOptional("memories.use_memories", "是否使用已有 memories。", "boolean");
        AddOptional("memories.disable_on_external_context", "外部上下文存在时是否禁用 memories。", "boolean");

        AddOptional("shell_environment_policy.inherit", "是否继承父进程环境变量。", "boolean");
        AddOptional("shell_environment_policy.ignore_default_excludes", "是否忽略默认环境变量排除列表。", "boolean");
        AddOptional("shell_environment_policy.exclude", "要排除的环境变量数组，例如 [\"AWS_SECRET_ACCESS_KEY\"]。", "raw");
        AddOptional("shell_environment_policy.set", "强制设置环境变量的 TOML inline table。", "raw");
        AddOptional("shell_environment_policy.include_only", "只允许继承的环境变量数组。", "raw");
        AddOptional("shell_environment_policy.experimental_use_profile", "是否使用 shell profile 生成环境。", "boolean");

        AddOptional("permissions.workspace.network.enabled", "workspace 网络权限开关。", "boolean");
        AddOptional("permissions.workspace.network.proxy_url", "workspace 网络代理地址。", "url");
        AddOptional("permissions.workspace.network.admin_url", "workspace 网络管理地址。", "url");
        AddOptional("permissions.workspace.network.enable_socks5", "是否启用 SOCKS5。", "boolean");
        AddOptional("permissions.workspace.network.socks_url", "SOCKS5 地址。", "url");
        AddOptional("permissions.workspace.network.enable_socks5_udp", "是否启用 SOCKS5 UDP。", "boolean");
        AddOptional("permissions.workspace.network.allow_upstream_proxy", "是否允许继续使用上游代理。", "boolean");
        AddOptional("permissions.workspace.network.danger_allow_upstream_proxy_without_network_access", "无网络权限时是否仍允许上游代理，风险项。", "boolean");
        AddOptional("permissions.workspace.network.mode", "网络权限模式。", "text");
        AddOptional("permissions.workspace.network.allow_local_binding", "是否允许本地端口绑定。", "boolean");

        AddOptional("model_providers.relaybench.env_key", "从环境变量读取 API Key 的变量名；设置后可不用明文 token。", "text");
        AddOptional("model_providers.relaybench.env_key_instructions", "缺少 env_key 时展示给用户的说明。", "text");
        AddOptional("model_providers.relaybench.env_http_headers", "从环境变量读取额外 HTTP 头的 TOML inline table。", "raw");
        AddOptional("model_providers.relaybench.query_params", "追加到请求 URL 的 query params，TOML inline table。", "raw");
        AddOptional("model_providers.relaybench.requires_openai_auth", "provider 是否需要 OpenAI auth。", "boolean");
        AddOptional("model_providers.relaybench.supports_websockets", "provider 是否支持 websocket。", "boolean");
        AddOptional("model_providers.relaybench.auth.command", "动态获取认证信息的命令。", "text");
        AddOptional("model_providers.relaybench.auth.args", "认证命令参数数组。", "raw");
        AddOptional("model_providers.relaybench.auth.timeout_ms", "认证命令超时毫秒数。", "integer");
        AddOptional("model_providers.relaybench.auth.refresh_interval_ms", "认证刷新间隔毫秒数。", "integer");
        AddOptional("model_providers.relaybench.auth.cwd", "认证命令工作目录。", "path");

        AddOptional("mcp_servers.docs.enabled", "示例 MCP：是否启用 docs server，可把 docs 作为固定 ID 使用。", "boolean");
        AddOptional("mcp_servers.docs.required", "示例 MCP：是否要求启动成功。", "boolean");
        AddOptional("mcp_servers.docs.command", "示例 MCP：启动命令，例如 npx。", "text");
        AddOptional("mcp_servers.docs.args", "示例 MCP：命令参数数组。", "raw");
        AddOptional("mcp_servers.docs.env", "示例 MCP：环境变量 inline table。", "raw");
        AddOptional("mcp_servers.docs.env_vars", "示例 MCP：允许透传的环境变量数组。", "raw");
        AddOptional("mcp_servers.docs.cwd", "示例 MCP：工作目录。", "path");
        AddOptional("mcp_servers.docs.experimental_environment", "示例 MCP：实验环境标记。", "text");
        AddOptional("mcp_servers.docs.startup_timeout_sec", "示例 MCP：启动超时秒数。", "integer");
        AddOptional("mcp_servers.docs.tool_timeout_sec", "示例 MCP：工具调用超时秒数。", "integer");
        AddOptional("mcp_servers.docs.enabled_tools", "示例 MCP：启用工具数组。", "raw");
        AddOptional("mcp_servers.docs.disabled_tools", "示例 MCP：禁用工具数组。", "raw");
        AddOptional("mcp_servers.docs.scopes", "示例 MCP：scope 数组。", "raw");
        AddOptional("mcp_servers.docs.oauth_resource", "示例 MCP：OAuth resource。", "text");

        AddOptional("apps._default.enabled", "默认 app connector 是否启用。", "boolean");
        AddOptional("apps._default.destructive_enabled", "默认是否允许破坏性 app 操作。", "boolean");
        AddOptional("apps._default.open_world_enabled", "默认是否允许 open-world app 操作。", "boolean");
        AddOptional("apps.google_drive.enabled", "Google Drive app 是否启用。", "boolean");
        AddOptional("apps.google_drive.destructive_enabled", "Google Drive 是否允许破坏性操作。", "boolean");
        AddOptional("apps.google_drive.default_tools_enabled", "Google Drive 默认工具是否启用。", "boolean");
        AddOptional("apps.google_drive.default_tools_approval_mode", "Google Drive 默认工具审批模式。", "text");
        AddOptional("apps.google_drive.open_world_enabled", "Google Drive 是否允许 open-world 操作。", "boolean");
        AddOptional("apps.google_drive.tools.\"files/delete\".enabled", "示例工具：是否启用 files/delete。", "boolean");
        AddOptional("apps.google_drive.tools.\"files/delete\".approval_mode", "示例工具：files/delete 的审批模式。", "text");

        AddOptional("tool_suggest.discoverables", "工具推荐候选数组。", "raw");
        AddOptional("tool_suggest.disabled_tools", "禁用工具推荐的工具数组。", "raw");

        AddOptional("profiles.default.model", "default profile 的模型。", "text");
        AddOptional("profiles.default.model_provider", "default profile 的 provider。", "text");
        AddOptional("profiles.default.approval_policy", "default profile 的审批策略。", "text");
        AddOptional("profiles.default.sandbox_mode", "default profile 的沙箱模式。", "text");
        AddOptional("profiles.default.service_tier", "default profile 的服务等级。", "text");
        AddOptional("profiles.default.oss_provider", "default profile 的 OSS provider。", "text");
        AddOptional("profiles.default.model_reasoning_effort", "default profile 的推理强度。", "text");
        AddOptional("profiles.default.plan_mode_reasoning_effort", "default profile 的 Plan 模式推理强度。", "text");
        AddOptional("profiles.default.model_reasoning_summary", "default profile 的推理摘要模式。", "text");
        AddOptional("profiles.default.model_verbosity", "default profile 的输出详略程度。", "text");
        AddOptional("profiles.default.personality", "default profile 的人格。", "text");
        AddOptional("profiles.default.chatgpt_base_url", "default profile 的 ChatGPT base URL。", "url");
        AddOptional("profiles.default.model_catalog_json", "default profile 的模型目录 JSON 路径。", "path");
        AddOptional("profiles.default.model_instructions_file", "default profile 的模型指令文件。", "path");
        AddOptional("profiles.default.experimental_compact_prompt_file", "default profile 的压缩提示词文件。", "path");
        AddOptional("profiles.default.tools_view_image", "default profile 是否允许 view image 工具。", "boolean");
        AddOptional("profiles.default.features", "default profile 的 features inline table。", "raw");

        void AddOptional(string parameter, string description, string valueKind)
        {
            additional.TryGetValue(parameter, out var value);
            AddRow(parameter, value ?? string.Empty, description, true, valueKind);
        }
    }

    private void AddRow(
        string parameter,
        string value,
        string description,
        bool isEditable,
        string valueKind)
        => CodexConfigTemplateRows.Add(new(parameter, value, description, isEditable, valueKind));

    private bool TryBuildCodexConfigTemplateFromRows(out CodexConfigTemplate template, out string error)
    {
        template = new CodexConfigTemplate(string.Empty, string.Empty, null, null, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, null, null, null);
        error = string.Empty;

        var model = GetCodexTemplateRowValue(CodexConfigModelKey).Trim();
        var providerName = GetCodexTemplateRowValue(CodexConfigProviderNameKey).Trim();
        var baseUrl = GetCodexTemplateRowValue(CodexConfigBaseUrlKey).Trim();
        var apiKey = GetCodexTemplateRowValue(CodexConfigBearerTokenKey).Trim();
        var httpHeaders = GetCodexTemplateRowValue(CodexConfigHttpHeadersKey).Trim();

        if (string.IsNullOrWhiteSpace(model))
        {
            error = "model 不能为空。";
            return false;
        }

        if (string.IsNullOrWhiteSpace(providerName))
        {
            error = "provider 名称不能为空。";
            return false;
        }

        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            error = "base_url 不能为空。";
            return false;
        }

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            error = "experimental_bearer_token 不能为空。";
            return false;
        }

        if (!TryReadOptionalInteger(CodexConfigContextWindowKey, out var contextWindow, out error) ||
            !TryReadOptionalInteger(CodexConfigAutoCompactKey, out var autoCompactTokenLimit, out error) ||
            !TryReadOptionalInteger(CodexConfigRequestRetriesKey, out var requestMaxRetries, out error) ||
            !TryReadOptionalInteger(CodexConfigStreamRetriesKey, out var streamMaxRetries, out error) ||
            !TryReadOptionalInteger(CodexConfigStreamIdleTimeoutKey, out var streamIdleTimeoutMs, out error) ||
            !TryBuildAdditionalSettings(out var additionalSettings, out error))
        {
            return false;
        }

        template = new CodexConfigTemplate(
            model,
            "relaybench",
            contextWindow,
            autoCompactTokenLimit,
            providerName,
            baseUrl,
            ProxyWireApiProbeService.ResponsesWireApi,
            apiKey,
            string.IsNullOrWhiteSpace(httpHeaders)
                ? "{ \"Content-Type\" = \"application/json\" }"
                : httpHeaders,
            requestMaxRetries,
            streamMaxRetries,
            streamIdleTimeoutMs,
            additionalSettings);
        return true;
    }

    private bool TryBuildAdditionalSettings(
        out IReadOnlyDictionary<string, string> additionalSettings,
        out string error)
    {
        Dictionary<string, string> values = new(StringComparer.OrdinalIgnoreCase);
        foreach (var row in CodexConfigTemplateRows)
        {
            if (!row.IsEditable ||
                BuiltInCodexTemplateKeys.Contains(row.Parameter) ||
                string.IsNullOrWhiteSpace(row.Value))
            {
                continue;
            }

            if (!TryConvertCodexTemplateValueToToml(row, out var rawValue, out error))
            {
                additionalSettings = values;
                return false;
            }

            values[row.Parameter] = rawValue;
        }

        additionalSettings = values;
        error = string.Empty;
        return true;
    }

    private static bool TryConvertCodexTemplateValueToToml(
        CodexConfigTemplateRowViewModel row,
        out string rawValue,
        out string error)
    {
        var value = row.Value.Trim();
        rawValue = string.Empty;
        error = string.Empty;

        switch (row.ValueKind)
        {
            case "integer":
                if (!int.TryParse(value, out var parsedInteger) || parsedInteger < 0)
                {
                    error = $"{row.Parameter} 必须是大于等于 0 的整数，或留空。";
                    return false;
                }

                rawValue = parsedInteger.ToString();
                return true;
            case "boolean":
                if (!bool.TryParse(value, out var parsedBoolean))
                {
                    error = $"{row.Parameter} 必须填写 true 或 false，或留空。";
                    return false;
                }

                rawValue = parsedBoolean ? "true" : "false";
                return true;
            case "raw":
                rawValue = value;
                return true;
            case "stringOrRaw":
                rawValue = value.StartsWith('{') || value.StartsWith('[')
                    ? value
                    : JsonSerializer.Serialize(value);
                return true;
            default:
                rawValue = JsonSerializer.Serialize(value);
                return true;
        }
    }

    private string GetCodexTemplateRowValue(string parameter)
        => CodexConfigTemplateRows.FirstOrDefault(row => string.Equals(row.Parameter, parameter, StringComparison.OrdinalIgnoreCase))?.Value ?? string.Empty;

    private bool TryReadOptionalInteger(string parameter, out int? value, out string error)
    {
        value = null;
        error = string.Empty;
        var text = GetCodexTemplateRowValue(parameter).Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        if (!int.TryParse(text, out var parsed) || parsed < 0)
        {
            error = $"{parameter} 必须是大于等于 0 的整数，或留空。";
            return false;
        }

        value = parsed;
        return true;
    }

    private static string FormatNullableInteger(int? value)
        => value?.ToString() ?? string.Empty;
}
