using System.Text;
using NetTest.App.Infrastructure;
using NetTest.Core.Models;

namespace NetTest.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private void SyncProxyBatchEditorItemsFromText()
    {
        ProxyBatchEditorItems.Clear();
        var entries = ParseProxyBatchSourceEntries(ProxyBatchTargetsText, allowEmpty: true);
        foreach (var entry in entries)
        {
            ProxyBatchEditorItems.Add(new ProxyBatchEditorItemViewModel(
                entry.Name,
                entry.BaseUrl,
                entry.ApiKey,
                entry.Model,
                entry.SiteGroupName,
                entry.SiteGroupApiKey,
                entry.SiteGroupModel));
        }

        OnPropertyChanged(nameof(ProxyBatchEditorListSummary));
        OnPropertyChanged(nameof(ProxyBatchEditorListSummaryDisplay));
    }

    private void LoadProxyBatchState(AppStateSnapshot snapshot)
    {
        if (snapshot.ProxyBatchItems is { Count: > 0 })
        {
            ApplyProxyBatchEditorItems(snapshot.ProxyBatchItems);
            RebuildProxyBatchTargetsTextFromEditorItems(persistState: false);
        }
        else
        {
            ProxyBatchTargetsText = string.IsNullOrWhiteSpace(snapshot.ProxyBatchTargetsText)
                ? ProxyBatchTargetsText
                : snapshot.ProxyBatchTargetsText;
            SyncProxyBatchEditorItemsFromText();
        }

        LoadProxyBatchDraft(snapshot.ProxyBatchDraft);
    }

    private void ApplyProxyBatchStateToSnapshot(AppStateSnapshot snapshot)
    {
        snapshot.ProxyBatchTargetsText = ProxyBatchTargetsText;
        snapshot.ProxyBatchItems = ProxyBatchEditorItems
            .Select(CreateProxyBatchConfigItemSnapshot)
            .ToList();
        snapshot.ProxyBatchDraft = CreateProxyBatchDraftSnapshot();
    }

    private void ApplyProxyBatchEditorItems(IEnumerable<ProxyBatchConfigItemSnapshot> snapshots)
    {
        ProxyBatchEditorItems.Clear();
        foreach (var item in snapshots)
        {
            ProxyBatchEditorItems.Add(new ProxyBatchEditorItemViewModel(
                item.EntryName,
                item.BaseUrl,
                NormalizeNullable(item.EntryApiKey),
                NormalizeNullable(item.EntryModel),
                NormalizeNullable(item.SiteGroupName),
                NormalizeNullable(item.SiteGroupApiKey),
                NormalizeNullable(item.SiteGroupModel)));
        }

        OnPropertyChanged(nameof(ProxyBatchEditorListSummary));
        OnPropertyChanged(nameof(ProxyBatchEditorListSummaryDisplay));
    }

    private void LoadProxyBatchDraft(ProxyBatchDraftSnapshot? draft)
    {
        draft ??= new ProxyBatchDraftSnapshot();
        _suppressProxyBatchDraftAutoSave = true;
        try
        {
            SelectedProxyBatchEditorItem = null;
            SetProxyBatchEditorMode(ResolveProxyBatchEditorMode(draft));
            ProxyBatchFormSiteGroupName = draft.SiteGroupName;
            ProxyBatchFormSiteGroupApiKey = draft.SiteGroupApiKey;
            ProxyBatchFormSiteGroupModel = draft.SiteGroupModel;
            ProxyBatchFormEntryName = draft.EntryName;
            ProxyBatchFormBaseUrl = draft.BaseUrl;
            ProxyBatchFormApiKey = draft.EntryApiKey;
            ProxyBatchFormModel = draft.EntryModel;
        }
        finally
        {
            _suppressProxyBatchDraftAutoSave = false;
        }
    }

    private ProxyBatchDraftSnapshot CreateProxyBatchDraftSnapshot()
        => new()
        {
            EditorModeIndex = (int)ResolveEffectiveProxyBatchEditorMode(),
            SiteGroupName = ProxyBatchFormSiteGroupName.Trim(),
            SiteGroupApiKey = ProxyBatchFormSiteGroupApiKey.Trim(),
            SiteGroupModel = ProxyBatchFormSiteGroupModel.Trim(),
            EntryName = ProxyBatchFormEntryName.Trim(),
            BaseUrl = ProxyBatchFormBaseUrl.Trim(),
            EntryApiKey = ProxyBatchFormApiKey.Trim(),
            EntryModel = ProxyBatchFormModel.Trim()
        };

    private static ProxyBatchConfigItemSnapshot CreateProxyBatchConfigItemSnapshot(ProxyBatchEditorItemViewModel item)
        => new()
        {
            EntryName = item.EntryName.Trim(),
            BaseUrl = item.BaseUrl.Trim(),
            EntryApiKey = item.EntryApiKey?.Trim() ?? string.Empty,
            EntryModel = item.EntryModel?.Trim() ?? string.Empty,
            SiteGroupName = item.SiteGroupName?.Trim() ?? string.Empty,
            SiteGroupApiKey = item.SiteGroupApiKey?.Trim() ?? string.Empty,
            SiteGroupModel = item.SiteGroupModel?.Trim() ?? string.Empty
        };

}
