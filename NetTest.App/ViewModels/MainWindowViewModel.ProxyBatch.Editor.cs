using System.ComponentModel;
using NetTest.App.Infrastructure;
using NetTest.Core.Models;

namespace NetTest.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private void SyncProxyBatchEditorItemsFromText()
    {
        var entries = ParseProxyBatchSourceEntries(ProxyBatchTargetsText, allowEmpty: true);
        ReplaceProxyBatchEditorItems(entries.Select(entry =>
            new ProxyBatchEditorItemViewModel(
                entry.Name,
                entry.BaseUrl,
                entry.ApiKey,
                entry.Model,
                entry.SiteGroupName,
                entry.SiteGroupApiKey,
                entry.SiteGroupModel)));
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
        ResetProxyBatchTemplateDraft(clearSelection: true);
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
        => ReplaceProxyBatchEditorItems(snapshots.Select(item =>
            new ProxyBatchEditorItemViewModel(
                item.EntryName,
                item.BaseUrl,
                NormalizeNullable(item.EntryApiKey),
                NormalizeNullable(item.EntryModel),
                NormalizeNullable(item.SiteGroupName),
                NormalizeNullable(item.SiteGroupApiKey),
                NormalizeNullable(item.SiteGroupModel))));

    private void ReplaceProxyBatchEditorItems(IEnumerable<ProxyBatchEditorItemViewModel> items)
    {
        ExecuteWithoutProxyBatchEditorItemSync(() =>
        {
            foreach (var existing in ProxyBatchEditorItems.ToArray())
            {
                DetachProxyBatchEditorItem(existing);
            }

            ProxyBatchEditorItems.Clear();
            foreach (var item in items)
            {
                AttachProxyBatchEditorItem(item);
                ProxyBatchEditorItems.Add(item);
            }
        });

        RefreshProxyBatchSiteGroups();
        RefreshProxyBatchEditorCollectionState();
    }

    private void ReplaceProxyBatchTemplateDraftItems(IEnumerable<ProxyBatchEditorItemViewModel> items)
    {
        _proxyBatchTemplateModelTargetRow = null;

        ExecuteWithoutProxyBatchTemplateDraftSync(() =>
        {
            foreach (var existing in ProxyBatchTemplateDraftItems.ToArray())
            {
                DetachProxyBatchTemplateDraftItem(existing);
            }

            ProxyBatchTemplateDraftItems.Clear();
            foreach (var item in items)
            {
                AttachProxyBatchTemplateDraftItem(item);
                ProxyBatchTemplateDraftItems.Add(item);
            }
        });

        EnsureProxyBatchTemplateDraftPlaceholder();
        RefreshProxyBatchTemplateDraftState();
    }

    private void AttachProxyBatchEditorItem(ProxyBatchEditorItemViewModel item)
        => item.PropertyChanged += HandleProxyBatchEditorItemPropertyChanged;

    private void DetachProxyBatchEditorItem(ProxyBatchEditorItemViewModel item)
        => item.PropertyChanged -= HandleProxyBatchEditorItemPropertyChanged;

    private void AttachProxyBatchTemplateDraftItem(ProxyBatchEditorItemViewModel item)
        => item.PropertyChanged += HandleProxyBatchTemplateDraftItemPropertyChanged;

    private void DetachProxyBatchTemplateDraftItem(ProxyBatchEditorItemViewModel item)
        => item.PropertyChanged -= HandleProxyBatchTemplateDraftItemPropertyChanged;

    private void HandleProxyBatchEditorItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_suppressProxyBatchEditorItemChangeHandling)
        {
            return;
        }

        if (e.PropertyName is nameof(ProxyBatchEditorItemViewModel.DisplayTitle) or
            nameof(ProxyBatchEditorItemViewModel.KeyDisplay) or
            nameof(ProxyBatchEditorItemViewModel.ModelDisplay) or
            nameof(ProxyBatchEditorItemViewModel.SiteGroupDisplay) or
            nameof(ProxyBatchEditorItemViewModel.TemplateStatus) or
            nameof(ProxyBatchEditorItemViewModel.ResolvedEntryName))
        {
            return;
        }

        RebuildProxyBatchTargetsTextFromEditorItems(persistState: false);
        RefreshProxyBatchSiteGroups();
        RefreshProxyBatchEditorCollectionState();
        SaveState();
    }

    private void HandleProxyBatchTemplateDraftItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_suppressProxyBatchTemplateDraftChangeHandling)
        {
            return;
        }

        if (e.PropertyName is nameof(ProxyBatchEditorItemViewModel.DisplayTitle) or
            nameof(ProxyBatchEditorItemViewModel.KeyDisplay) or
            nameof(ProxyBatchEditorItemViewModel.ModelDisplay) or
            nameof(ProxyBatchEditorItemViewModel.SiteGroupDisplay))
        {
            return;
        }

        RefreshProxyBatchTemplateDraftState();
    }

    private void ExecuteWithoutProxyBatchEditorItemSync(Action action)
    {
        _suppressProxyBatchEditorItemChangeHandling = true;
        try
        {
            action();
        }
        finally
        {
            _suppressProxyBatchEditorItemChangeHandling = false;
        }
    }

    private void ExecuteWithoutProxyBatchTemplateDraftSync(Action action)
    {
        _suppressProxyBatchTemplateDraftChangeHandling = true;
        try
        {
            action();
        }
        finally
        {
            _suppressProxyBatchTemplateDraftChangeHandling = false;
        }
    }

    private void RefreshProxyBatchEditorCollectionState()
    {
        OnPropertyChanged(nameof(ProxyBatchEditorListSummary));
        OnPropertyChanged(nameof(ProxyBatchEditorListSummaryDisplay));
        OnPropertyChanged(nameof(ProxyBatchTemplateSummary));
        OnPropertyChanged(nameof(ProxyBatchPrimaryActionText));
        OnPropertyChanged(nameof(ProxyBatchEditorSelectionSummary));
        OnPropertyChanged(nameof(IsProxyBatchEditorItemSelected));
    }

    private void RefreshProxyBatchTemplateDraftState()
    {
        OnPropertyChanged(nameof(ProxyBatchTemplateSummary));
        OnPropertyChanged(nameof(ProxyBatchEditorSelectionSummary));
    }

    private void RefreshProxyBatchSiteGroups()
    {
        var selectedGroupName = SelectedProxyBatchSiteGroup?.GroupName;

        ProxyBatchSiteGroups.Clear();
        foreach (var group in ProxyBatchEditorItems
                     .GroupBy(item => ResolveProxyBatchSiteGroupName(item), StringComparer.OrdinalIgnoreCase)
                     .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
        {
            var items = group.ToArray();
            var urlPreview = items.Length == 1
                ? items[0].BaseUrl
                : $"{items[0].BaseUrl}\n+ 另有 {items.Length - 1} 个网址";
            var keyValues = items
                .Select(item => NormalizeNullable(item.EntryApiKey) ?? NormalizeNullable(item.SiteGroupApiKey))
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var modelValues = items
                .Select(item => NormalizeNullable(item.EntryModel) ?? NormalizeNullable(item.SiteGroupModel))
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            ProxyBatchSiteGroups.Add(new ProxyBatchSiteGroupViewModel(
                group.Key,
                items.Length,
                urlPreview,
                BuildProxyBatchSiteKeySummary(keyValues),
                BuildProxyBatchSiteModelSummary(modelValues)));
        }

        if (string.IsNullOrWhiteSpace(selectedGroupName))
        {
            return;
        }

        SelectedProxyBatchSiteGroup = ProxyBatchSiteGroups
            .FirstOrDefault(item => string.Equals(item.GroupName, selectedGroupName, StringComparison.OrdinalIgnoreCase));
    }

    private void LoadProxyBatchDraft(ProxyBatchDraftSnapshot? draft)
    {
        draft ??= new ProxyBatchDraftSnapshot();
        _suppressProxyBatchDraftAutoSave = true;
        try
        {
            SelectedProxyBatchEditorItem = null;
            SelectedProxyBatchSiteGroup = null;
            SetProxyBatchEditorMode(ProxyBatchEditorMode.BulkImport);
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
            EditorModeIndex = (int)_proxyBatchEditorMode,
            SiteGroupName = string.Empty,
            SiteGroupApiKey = string.Empty,
            SiteGroupModel = string.Empty,
            EntryName = string.Empty,
            BaseUrl = string.Empty,
            EntryApiKey = string.Empty,
            EntryModel = string.Empty
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

    private void ResetProxyBatchTemplateDraft(bool clearSelection)
    {
        _proxyBatchTemplateModelTargetRow = null;

        if (clearSelection)
        {
            SelectedProxyBatchSiteGroup = null;
        }

        SetProxyBatchEditorMode(ProxyBatchEditorMode.BulkImport);
        ReplaceProxyBatchTemplateDraftItems(
        [
            new ProxyBatchEditorItemViewModel(
                string.Empty,
                string.Empty,
                null,
                null,
                null,
                null,
                null)
        ]);
    }

    private void EnsureProxyBatchTemplateDraftPlaceholder()
    {
        if (ProxyBatchTemplateDraftItems.Count == 0)
        {
            var item = new ProxyBatchEditorItemViewModel(string.Empty, string.Empty, null, null, null, null, null);
            AttachProxyBatchTemplateDraftItem(item);
            ProxyBatchTemplateDraftItems.Add(item);
        }
    }

    private void LoadProxyBatchTemplateDraftFromSiteGroup(ProxyBatchSiteGroupViewModel siteGroup)
    {
        var items = ProxyBatchEditorItems
            .Where(item => string.Equals(ResolveProxyBatchSiteGroupName(item), siteGroup.GroupName, StringComparison.OrdinalIgnoreCase))
            .Select(item => new ProxyBatchEditorItemViewModel(
                item.EntryName,
                item.BaseUrl,
                item.EntryApiKey,
                item.EntryModel,
                null,
                null,
                null))
            .ToArray();

        ReplaceProxyBatchTemplateDraftItems(items);
        SetProxyBatchEditorMode(ProxyBatchEditorMode.BulkImport);
    }

    private static string ResolveProxyBatchSiteGroupName(ProxyBatchEditorItemViewModel item)
        => NormalizeNullable(item.SiteGroupName)
           ?? NormalizeNullable(item.EntryName)
           ?? TryGetHost(item.BaseUrl)
           ?? "未命名站点";

    private static string BuildProxyBatchSiteKeySummary(IReadOnlyList<string?> keyValues)
    {
        if (keyValues.Count == 0)
        {
            return "未填写 key";
        }

        return keyValues.Count == 1
            ? $"Key：{MaskApiKey(keyValues[0]!)}"
            : $"包含 {keyValues.Count} 组不同 key";
    }

    private static string BuildProxyBatchSiteModelSummary(IReadOnlyList<string?> modelValues)
    {
        if (modelValues.Count == 0)
        {
            return "未填写模型";
        }

        return modelValues.Count == 1
            ? $"模型：{modelValues[0]}"
            : $"包含 {modelValues.Count} 个模型";
    }

    private static string? TryGetHost(string? baseUrl)
        => Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) && !string.IsNullOrWhiteSpace(uri.Host)
            ? uri.Host
            : null;
}
