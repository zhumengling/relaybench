using System.Text;
using NetTest.App.Infrastructure;
using NetTest.Core.Models;

namespace NetTest.App.ViewModels;

public sealed partial class MainWindowViewModel
{

    private ProxyBatchEditorItemViewModel? ResolveExistingSiteGroupReference(
        string? siteGroupName,
        ProxyBatchEditorItemViewModel? currentSelection)
    {
        if (string.IsNullOrWhiteSpace(siteGroupName))
        {
            return null;
        }

        return ProxyBatchEditorItems.FirstOrDefault(item =>
            !ReferenceEquals(item, currentSelection) &&
            string.Equals(item.SiteGroupName, siteGroupName, StringComparison.OrdinalIgnoreCase));
    }

    private void SetProxyBatchEditorMode(ProxyBatchEditorMode mode)
    {
        if (_proxyBatchEditorMode == mode)
        {
            return;
        }

        _proxyBatchEditorMode = mode;
        OnPropertyChanged(nameof(ProxyBatchEditorModeIndex));
        OnPropertyChanged(nameof(ProxyBatchGuideSummary));
        OnPropertyChanged(nameof(ProxyBatchEditorFormModeSummary));
    }

    private void PersistProxyBatchDraftState()
    {
        if (_suppressProxyBatchDraftAutoSave)
        {
            return;
        }

        SaveState();
    }

    private ProxyBatchEditorMode ResolveEffectiveProxyBatchEditorMode()
    {
        var hasSharedKeyOrModel =
            !string.IsNullOrWhiteSpace(NormalizeNullable(ProxyBatchFormSiteGroupApiKey)) ||
            !string.IsNullOrWhiteSpace(NormalizeNullable(ProxyBatchFormSiteGroupModel));
        var hasEntryKeyOrModel =
            !string.IsNullOrWhiteSpace(NormalizeNullable(ProxyBatchFormApiKey)) ||
            !string.IsNullOrWhiteSpace(NormalizeNullable(ProxyBatchFormModel));

        if (hasSharedKeyOrModel && !hasEntryKeyOrModel)
        {
            return ProxyBatchEditorMode.SharedKeyGroup;
        }

        if (hasEntryKeyOrModel)
        {
            return ProxyBatchEditorMode.MultiKey;
        }

        return _proxyBatchEditorMode;
    }

    private static ProxyBatchEditorMode ResolveProxyBatchEditorMode(ProxyBatchDraftSnapshot draft)
    {
        var hasSharedKeyOrModel =
            !string.IsNullOrWhiteSpace(NormalizeNullable(draft.SiteGroupApiKey)) ||
            !string.IsNullOrWhiteSpace(NormalizeNullable(draft.SiteGroupModel));
        var hasEntryKeyOrModel =
            !string.IsNullOrWhiteSpace(NormalizeNullable(draft.EntryApiKey)) ||
            !string.IsNullOrWhiteSpace(NormalizeNullable(draft.EntryModel));

        if (hasSharedKeyOrModel && !hasEntryKeyOrModel)
        {
            return ProxyBatchEditorMode.SharedKeyGroup;
        }

        if (hasEntryKeyOrModel)
        {
            return ProxyBatchEditorMode.MultiKey;
        }

        return (ProxyBatchEditorMode)Math.Clamp(draft.EditorModeIndex, 0, 1);
    }

    private ProxyBatchEditorMode ResolveProxyBatchEditorMode(ProxyBatchEditorItemViewModel item)
    {
        if (!string.IsNullOrWhiteSpace(item.SiteGroupName) &&
            string.IsNullOrWhiteSpace(item.EntryApiKey))
        {
            return ProxyBatchEditorMode.SharedKeyGroup;
        }

        return ProxyBatchEditorMode.MultiKey;
    }

    private static string BuildBatchSiteGroupName(string baseUrl)
    {
        if (Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) &&
            !string.IsNullOrWhiteSpace(uri.Host))
        {
            return uri.Host;
        }

        return "default-group";
    }

    private void NormalizeSiteGroupConsistency(string? siteGroupName)
    {
        if (string.IsNullOrWhiteSpace(siteGroupName))
        {
            return;
        }

        var items = ProxyBatchEditorItems
            .Where(item => string.Equals(item.SiteGroupName, siteGroupName, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (items.Length == 0)
        {
            return;
        }

        var sharedApiKey = items
            .Select(item => NormalizeNullable(item.SiteGroupApiKey))
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        var sharedModel = items
            .Select(item => NormalizeNullable(item.SiteGroupModel))
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

        foreach (var item in items)
        {
            item.SiteGroupApiKey = sharedApiKey;
            item.SiteGroupModel = sharedModel;
        }
    }

}
