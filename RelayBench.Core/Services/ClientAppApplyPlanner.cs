using RelayBench.Core.Models;

namespace RelayBench.Core.Services;

public sealed class ClientAppApplyPlanner
{
    private static readonly ClientApplyTargetDefinition[] TargetDefinitions =
    [
        new("codex-cli", "Codex CLI", ClientApplyProtocolKind.Responses, "~/.codex/config.toml（Codex 系列共用配置）"),
        new("codex-desktop", "Codex Desktop", ClientApplyProtocolKind.Responses, "~/.codex/config.toml（Codex 系列共用配置）"),
        new("vscode-codex", "VSCode Codex", ClientApplyProtocolKind.Responses, "~/.codex/config.toml（Codex 系列共用配置）"),
        new("claude-cli", "Claude CLI", ClientApplyProtocolKind.Anthropic, "~/.claude/settings.json")
    ];

    public IReadOnlyList<ClientApplyTarget> BuildTargets(ClientAppApplyPlanContext context)
    {
        var installedNames = new HashSet<string>(context.InstalledClientNames, StringComparer.OrdinalIgnoreCase);
        List<ClientApplyTarget> targets = [];

        foreach (var definition in TargetDefinitions)
        {
            var installed = installedNames.Count == 0 || installedNames.Contains(definition.DisplayName);
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
                BuildDisabledReason(installed, protocolSupported, hasRequiredFields, definition)));
        }

        return targets;
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
            return "本机未发现该软件。";
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
                ClientApplyProtocolKind.Responses => "Codex 系列只支持 Responses，当前 /v1/responses 探测未通过。",
                ClientApplyProtocolKind.OpenAiCompatible => "当前模型未通过 OpenAI 兼容探测。",
                ClientApplyProtocolKind.Anthropic => "当前模型未通过 Anthropic Messages 探测。",
                _ => "当前模型不支持该协议。"
            };
        }

        return null;
    }

    private static bool IsClaudeTarget(ClientApplyTargetDefinition definition)
        => string.Equals(definition.Id, "claude-cli", StringComparison.OrdinalIgnoreCase);

    private sealed record ClientApplyTargetDefinition(
        string Id,
        string DisplayName,
        ClientApplyProtocolKind Protocol,
        string ConfigSummary);
}

public sealed record ClientAppApplyPlanContext(
    string BaseUrl,
    string ApiKey,
    string Model,
    bool ResponsesSupported,
    bool OpenAiCompatibleSupported,
    bool AnthropicSupported,
    IReadOnlySet<string> InstalledClientNames);
