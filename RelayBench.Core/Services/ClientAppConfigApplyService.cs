using RelayBench.Core.Models;

namespace RelayBench.Core.Services;

public sealed class ClientAppConfigApplyService
{
    private readonly CodexFamilyConfigApplyService _codexService;
    private readonly AnthropicClientConfigApplyAdapter _anthropicAdapter;

    public ClientAppConfigApplyService(IClientApiConfigMutationEnvironment? environment = null)
    {
        _codexService = new CodexFamilyConfigApplyService(environment);
        _anthropicAdapter = new AnthropicClientConfigApplyAdapter(environment);
    }

    public async Task<ClientAppApplyResult> ApplyAsync(
        ClientApplyEndpoint endpoint,
        IReadOnlyList<ClientApplyTargetSelection> targetSelections,
        CancellationToken cancellationToken = default)
    {
        if (targetSelections.Count == 0)
        {
            return new ClientAppApplyResult(
                false,
                "没有选择要应用的软件。",
                [],
                [],
                [],
                "empty-selection")
            {
                TargetResults = []
            };
        }

        List<ClientAppApplyResult> results = [];

        var codexSelections = targetSelections
            .Where(target => target.Protocol is ClientApplyProtocolKind.Responses or ClientApplyProtocolKind.OpenAiCompatible)
            .ToArray();
        if (codexSelections.Length > 0)
        {
            var codexPreferredWireApi = ResolveCodexPreferredWireApi(
                endpoint.PreferredWireApi,
                codexSelections);
            results.Add(await _codexService.ApplyAsync(
                endpoint.BaseUrl,
                endpoint.ApiKey,
                endpoint.Model,
                endpoint.DisplayName,
                endpoint.ContextWindow,
                codexPreferredWireApi,
                codexSelections,
                cancellationToken));
        }

        var anthropicSelections = targetSelections
            .Where(target => target.Protocol == ClientApplyProtocolKind.Anthropic)
            .ToArray();
        if (anthropicSelections.Length > 0)
        {
            results.Add(await _anthropicAdapter.ApplyAsync(endpoint, anthropicSelections, cancellationToken));
        }

        var targetResults = results.SelectMany(result => result.TargetResults).ToArray();
        var changedFiles = results.SelectMany(result => result.ChangedFiles).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var backupFiles = results.SelectMany(result => result.BackupFiles).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var appliedTargets = targetResults
            .Where(result => result.Succeeded)
            .Select(result => result.DisplayName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var failures = targetResults.Where(result => !result.Succeeded).ToArray();
        var succeeded = targetResults.Length > 0 && failures.Length == 0 && appliedTargets.Length > 0;
        var summary = BuildSummary(targetResults);

        return new ClientAppApplyResult(
            succeeded,
            summary,
            changedFiles,
            backupFiles,
            appliedTargets,
            failures.Length == 0 ? null : string.Join("；", failures.Select(failure => $"{failure.DisplayName}: {failure.Error ?? "失败"}")))
        {
            TargetResults = targetResults
        };
    }

    private static string? ResolveCodexPreferredWireApi(
        string? preferredWireApi,
        IReadOnlyList<ClientApplyTargetSelection> codexSelections)
    {
        var hasResponsesTarget = codexSelections.Any(target => target.Protocol == ClientApplyProtocolKind.Responses);
        if (hasResponsesTarget)
        {
            return "responses";
        }

        var hasChatFallbackTarget = codexSelections.Any(target => target.Protocol == ClientApplyProtocolKind.OpenAiCompatible);
        if (hasChatFallbackTarget)
        {
            return "chat";
        }

        return preferredWireApi;
    }

    private static string BuildSummary(IReadOnlyList<ClientAppTargetApplyResult> targetResults)
    {
        if (targetResults.Count == 0)
        {
            return "没有软件被应用。";
        }

        var successTargets = targetResults.Where(target => target.Succeeded).Select(target => target.DisplayName).ToArray();
        var failedTargets = targetResults.Where(target => !target.Succeeded).Select(target => target.DisplayName).ToArray();
        if (failedTargets.Length == 0)
        {
            return $"已应用到：{string.Join("、", successTargets)}";
        }

        if (successTargets.Length == 0)
        {
            return $"应用失败：{string.Join("、", failedTargets)}";
        }

        return $"部分软件应用成功：{string.Join("、", successTargets)}；失败：{string.Join("、", failedTargets)}";
    }
}
