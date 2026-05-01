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
            var protocol = ResolveTargetProtocol(definition.Protocol, context);
            var protocolSupported = IsProtocolSupported(protocol, context);
            var hasRequiredFields =
                !string.IsNullOrWhiteSpace(context.BaseUrl) &&
                !string.IsNullOrWhiteSpace(context.ApiKey) &&
                !string.IsNullOrWhiteSpace(context.Model);
            var selectable = installed && hasRequiredFields;
            var defaultSelected = selectable && protocolSupported;

            targets.Add(new ClientApplyTarget(
                definition.Id,
                definition.DisplayName,
                protocol,
                installed,
                selectable,
                protocolSupported,
                defaultSelected,
                definition.ConfigSummary,
                BuildDisabledReason(installed, protocolSupported, hasRequiredFields, protocol)));
        }

        return targets;
    }

    private static ClientApplyProtocolKind ResolveTargetProtocol(
        ClientApplyProtocolKind protocol,
        ClientAppApplyPlanContext context)
    {
        if (protocol == ClientApplyProtocolKind.Responses &&
            !context.ResponsesSupported &&
            context.OpenAiCompatibleSupported)
        {
            return ClientApplyProtocolKind.OpenAiCompatible;
        }

        return protocol;
    }

    private static bool IsProtocolSupported(ClientApplyProtocolKind protocol, ClientAppApplyPlanContext context)
        => protocol switch
        {
            ClientApplyProtocolKind.Responses => context.ResponsesSupported,
            ClientApplyProtocolKind.OpenAiCompatible => context.OpenAiCompatibleSupported,
            ClientApplyProtocolKind.Anthropic => context.AnthropicSupported,
            _ => false
        };

    private static string? BuildDisabledReason(
        bool installed,
        bool protocolSupported,
        bool hasRequiredFields,
        ClientApplyProtocolKind protocol)
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
            return protocol switch
            {
                ClientApplyProtocolKind.Responses => "当前模型未通过 Responses 或 OpenAI Chat 探测。",
                ClientApplyProtocolKind.OpenAiCompatible => "当前模型未通过 OpenAI 兼容探测。",
                ClientApplyProtocolKind.Anthropic => "当前模型未通过 Anthropic Messages 探测。",
                _ => "当前模型不支持该协议。"
            };
        }

        return null;
    }

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
