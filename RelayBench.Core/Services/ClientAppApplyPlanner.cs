using RelayBench.Core.Models;

namespace RelayBench.Core.Services;

public sealed class ClientAppApplyPlanner
{
    private static readonly ClientApplyTargetDefinition[] TargetDefinitions =
    [
        new("codex", "Codex", ClientApplyProtocolKind.Responses, "~/.codex/config.toml（Codex 共享配置）", true),
        new("claude-cli", "Claude CLI", ClientApplyProtocolKind.Anthropic, "~/.claude/settings.json", false)
    ];

    private static readonly string[] CodexClientNames =
    [
        "Codex",
        "Codex CLI",
        "Codex Desktop",
        "VSCode Codex"
    ];

    public IReadOnlyList<ClientApplyTarget> BuildTargets(ClientAppApplyPlanContext context)
    {
        var installedNames = new HashSet<string>(context.InstalledClientNames, StringComparer.OrdinalIgnoreCase);
        var codexTemplate = CodexFamilyConfigApplyService.CreateDefaultTemplate(
            context.BaseUrl,
            context.ApiKey,
            context.Model,
            "RelayBench",
            modelContextWindow: null,
            preferredWireApi: ProxyWireApiProbeService.ResponsesWireApi);
        List<ClientApplyTarget> targets = [];

        foreach (var definition in TargetDefinitions)
        {
            var installed = IsInstalled(definition, installedNames);
            var protocol = definition.Protocol;
            var protocolSupported = IsProtocolSupported(definition, context);
            var hasRequiredFields =
                !string.IsNullOrWhiteSpace(context.BaseUrl) &&
                !string.IsNullOrWhiteSpace(context.ApiKey) &&
                !string.IsNullOrWhiteSpace(context.Model);
            var selectable = installed && hasRequiredFields && protocolSupported;
            var defaultSelected = selectable && protocolSupported;

            targets.Add(new ClientApplyTarget(
                definition.Id,
                definition.DisplayName,
                protocol,
                installed,
                selectable,
                protocolSupported,
                defaultSelected,
                BuildConfigSummary(definition, context),
                BuildDisabledReason(installed, protocolSupported, hasRequiredFields, definition),
                definition.HasSettings,
                definition.HasSettings ? codexTemplate : null));
        }

        return targets;
    }

    private static bool IsInstalled(ClientApplyTargetDefinition definition, HashSet<string> installedNames)
    {
        if (installedNames.Count == 0)
        {
            return true;
        }

        if (IsCodexTarget(definition))
        {
            return CodexClientNames.Any(installedNames.Contains);
        }

        return installedNames.Contains(definition.DisplayName);
    }

    private static bool IsProtocolSupported(ClientApplyTargetDefinition definition, ClientAppApplyPlanContext context)
        => definition.Protocol switch
        {
            ClientApplyProtocolKind.Responses => context.ResponsesSupported,
            ClientApplyProtocolKind.OpenAiCompatible => context.OpenAiCompatibleSupported,
            ClientApplyProtocolKind.Anthropic when IsClaudeTarget(definition) =>
                context.AnthropicSupported || context.OpenAiCompatibleSupported,
            ClientApplyProtocolKind.Anthropic => context.AnthropicSupported,
            _ => false
        };

    private static string BuildConfigSummary(ClientApplyTargetDefinition definition, ClientAppApplyPlanContext context)
    {
        if (IsClaudeTarget(definition))
        {
            if (context.AnthropicSupported)
            {
                return "~/.claude/settings.json（Anthropic Messages 直连）";
            }

            if (context.OpenAiCompatibleSupported)
            {
                return "~/.claude/settings.json（通过 RelayBench 本地统一出口转换）";
            }
        }

        return definition.ConfigSummary;
    }

    private static string? BuildDisabledReason(
        bool installed,
        bool protocolSupported,
        bool hasRequiredFields,
        ClientApplyTargetDefinition definition)
    {
        if (!installed)
        {
            return IsCodexTarget(definition)
                ? "本机未发现 Codex CLI、Codex Desktop 或 VSCode Codex 线索。"
                : "本机未发现该软件。";
        }

        if (!hasRequiredFields)
        {
            return "当前接口缺少地址、密钥或模型。";
        }

        if (!protocolSupported)
        {
            if (IsClaudeTarget(definition))
            {
                return "Claude CLI 未通过 Anthropic Messages 或 OpenAI Chat 探测；Anthropic 可直连，OpenAI Chat 只能通过 RelayBench 本地统一出口转换。";
            }

            return definition.Protocol switch
            {
                ClientApplyProtocolKind.Responses => "Codex 只支持 Responses，当前 /v1/responses 探测未通过。",
                ClientApplyProtocolKind.OpenAiCompatible => "当前模型未通过 OpenAI 兼容探测。",
                ClientApplyProtocolKind.Anthropic => "当前模型未通过 Anthropic Messages 探测。",
                _ => "当前模型不支持该协议。"
            };
        }

        return null;
    }

    private static bool IsCodexTarget(ClientApplyTargetDefinition definition)
        => string.Equals(definition.Id, "codex", StringComparison.OrdinalIgnoreCase);

    private static bool IsClaudeTarget(ClientApplyTargetDefinition definition)
        => string.Equals(definition.Id, "claude-cli", StringComparison.OrdinalIgnoreCase);

    private sealed record ClientApplyTargetDefinition(
        string Id,
        string DisplayName,
        ClientApplyProtocolKind Protocol,
        string ConfigSummary,
        bool HasSettings);
}

public sealed record ClientAppApplyPlanContext(
    string BaseUrl,
    string ApiKey,
    string Model,
    bool ResponsesSupported,
    bool OpenAiCompatibleSupported,
    bool AnthropicSupported,
    IReadOnlySet<string> InstalledClientNames);
