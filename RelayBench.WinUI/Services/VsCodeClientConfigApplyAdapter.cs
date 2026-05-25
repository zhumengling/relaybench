using RelayBench.Core.Models;
using RelayBench.Core.Services;
using RelayBench.Services;

namespace RelayBench.WinUI.Services;

internal sealed class VsCodeClientConfigApplyAdapter : IVsCodeClientConfigApplyAdapter
{
    private readonly TransparentProxyVsCodeSettingsService _settingsService;

    public VsCodeClientConfigApplyAdapter(TransparentProxyVsCodeSettingsService settingsService)
    {
        _settingsService = settingsService;
    }

    public Task<ClientAppApplyResult> ApplyAsync(
        ClientApplyEndpoint endpoint,
        IReadOnlyList<ClientApplyTargetSelection> targetSelections,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var result = _settingsService.Apply(endpoint.BaseUrl);
        foreach (var changedFile in result.ChangedFiles)
        {
            ConfigBackupRetention.EnsureRetained(changedFile, 8);
        }

        var targetResults = targetSelections
            .Select(target => new ClientAppTargetApplyResult(
                target.TargetId,
                "VS Code",
                target.Protocol,
                result.Succeeded,
                result.ChangedFiles,
                result.BackupFiles,
                result.Succeeded ? null : result.Summary))
            .ToArray();

        return Task.FromResult(new ClientAppApplyResult(
            result.Succeeded,
            result.Summary,
            result.ChangedFiles,
            result.BackupFiles,
            result.Succeeded ? ["VS Code"] : [],
            result.Succeeded ? null : result.Summary)
        {
            TargetResults = targetResults
        });
    }
}
