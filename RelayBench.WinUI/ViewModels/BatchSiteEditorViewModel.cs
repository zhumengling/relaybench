using System.Collections.ObjectModel;
using System.Text.Json;
using System.Collections.Specialized;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using RelayBench.WinUI.Storage;
using Windows.ApplicationModel.DataTransfer;

namespace RelayBench.WinUI.ViewModels;

/// <summary>
/// Sub-ViewModel managing the batch site editor: CRUD, bulk import, template duplication, and auto-save.
/// </summary>
public sealed partial class BatchSiteEditorViewModel : ObservableObject
{
    private static readonly string SavePath = Path.Combine(StoragePaths.Root, "batch-sites.json");
    private const string IndependentImportMode = "独立入口";
    private const string SharedImportMode = "同站共享 Key";
    private const int MaxDraftRows = 64;

    private DispatcherQueueTimer? _autoSaveTimer;
    private bool _isDirty;

    /// <summary>
    /// The collection of site entries managed by the editor.
    /// </summary>
    public ObservableCollection<BatchSiteEntry> Sites { get; } = new();

    public ObservableCollection<BatchSiteGroupSummary> SiteGroups { get; } = new();

    public ObservableCollection<BatchSiteDraftRow> DraftRows { get; } = new();

    /// <summary>
    /// The currently selected site entry for editing or duplication.
    /// </summary>
    [ObservableProperty] public partial BatchSiteEntry? SelectedSite { get; set; }

    [ObservableProperty] public partial BatchSiteDraftRow? SelectedDraftRow { get; set; }

    [ObservableProperty] public partial string EditingGroupName { get; set; } = string.Empty;

    /// <summary>
    /// Whether the site editor panel is expanded.
    /// </summary>
    [ObservableProperty] public partial bool IsEditorExpanded { get; set; } = true;

    /// <summary>
    /// Bulk import text content (pipe or tab delimited).
    /// </summary>
    [ObservableProperty] public partial string ImportText { get; set; } = string.Empty;

    [ObservableProperty] public partial bool IsBulkImportOpen { get; set; } = true;
    [ObservableProperty] public partial string ImportMode { get; set; } = IndependentImportMode;
    [ObservableProperty] public partial string SharedGroupName { get; set; } = string.Empty;
    [ObservableProperty] public partial string SharedApiKey { get; set; } = string.Empty;
    [ObservableProperty] public partial string SharedModel { get; set; } = string.Empty;
    [ObservableProperty] public partial string ImportPreview { get; set; } = "粘贴入口后可先预览再导入。";
    [ObservableProperty] public partial string ImportStatusText { get; set; } = "支持 | 或 Tab 分列；空行和 #、// 注释会被忽略。";
    [ObservableProperty] public partial bool HasImportError { get; set; }
    [ObservableProperty] public partial string DraftStatusText { get; set; } = "从剪贴板粘贴 URL、Key、模型或表格，系统会自动识别并补齐到草稿行。";

    public IReadOnlyList<string> ImportModes { get; } = [IndependentImportMode, SharedImportMode];
    public bool IsSharedImport => string.Equals(ImportMode, SharedImportMode, StringComparison.Ordinal);
    public int TotalSiteCount => Sites.Count;
    public int IncludedCount => Sites.Count(static site => site.IsIncluded);
    public int ReadyIncludedCount => Sites.Count(static site => site.IsIncluded && IsRunnableSite(site));
    public int MissingUrlCount => Sites.Count(static site => site.IsIncluded && string.IsNullOrWhiteSpace(site.BaseUrl));
    public int MissingKeyCount => Sites.Count(static site => site.IsIncluded && !string.IsNullOrWhiteSpace(site.BaseUrl) && string.IsNullOrWhiteSpace(site.ApiKey));
    public int GroupCount => Sites
        .Select(ResolveSiteGroupName)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Count();

    public string EditorSummary => $"已录入 {TotalSiteCount} 个Site，启用 {IncludedCount} 个，可测试 {ReadyIncludedCount} 个";
    public string EditorHealthSummary => $"分组 {GroupCount} 个，缺 URL {MissingUrlCount} 行，缺 Key {MissingKeyCount} 行";
    public bool IsEditingExistingGroup => !string.IsNullOrWhiteSpace(EditingGroupName);
    public string DraftPrimaryActionText => IsEditingExistingGroup ? "保存Site" : "加入入口组";
    public string SelectedSiteSummary => SelectedSite is null
        ? IsEditingExistingGroup
            ? $"正在编辑Site组“{EditingGroupName}”，保存后会覆盖该Site组。"
            : "未选中入口。点击左侧Site组可载入右侧继续编辑。"
        : IsEditingExistingGroup
            ? $"正在编辑Site组“{EditingGroupName}” · {SelectedSite.EntryStatusText} · {SelectedSite.EndpointDisplay}"
        : $"{SelectedSite.DisplayName} · {SelectedSite.EntryStatusText} · {SelectedSite.EndpointDisplay}";
    public int DraftRowCount => DraftRows.Count;
    public int DraftFilledCount => DraftRows.Count(static row => row.HasContent);
    public int DraftIncludedCount => DraftRows.Count(static row => row.HasContent && row.IsIncluded);
    public int DraftMissingUrlCount => DraftRows.Count(static row => row.HasContent && string.IsNullOrWhiteSpace(row.BaseUrl));
    public int DraftInvalidUrlCount => DraftRows.Count(static row => row.HasContent && !string.IsNullOrWhiteSpace(row.BaseUrl) && !LooksLikeUrl(row.BaseUrl));
    public string DraftSummary => $"草稿 {DraftRowCount} 行，已填 {DraftFilledCount} 行，加入测试 {DraftIncludedCount} 行，缺 URL {DraftMissingUrlCount} 行，无效 URL {DraftInvalidUrlCount} 行";

    public BatchSiteEditorViewModel()
    {
        LoadFromDisk();
        Sites.CollectionChanged += OnSitesChanged;
        DraftRows.CollectionChanged += OnDraftRowsChanged;
        EnsureDraftRow();
        RefreshEditorState();
        RefreshDraftState();
        RefreshImportPreview();
    }

    partial void OnEditingGroupNameChanged(string value)
    {
        OnPropertyChanged(nameof(IsEditingExistingGroup));
        OnPropertyChanged(nameof(DraftPrimaryActionText));
        OnPropertyChanged(nameof(SelectedSiteSummary));
        RefreshSiteGroups();
    }

    /// <summary>
    /// Initializes the auto-save timer. Must be called from the UI thread.
    /// </summary>
    public void InitializeAutoSave(DispatcherQueue dispatcherQueue)
    {
        _autoSaveTimer = dispatcherQueue.CreateTimer();
        _autoSaveTimer.Interval = TimeSpan.FromSeconds(2);
        _autoSaveTimer.IsRepeating = false;
        _autoSaveTimer.Tick += (_, _) => SaveToDisk();
    }

    public int AddGeneratedCandidates(IEnumerable<BatchSiteEntry> candidates)
    {
        var added = 0;
        BatchSiteEntry? lastAdded = null;

        foreach (var candidate in candidates)
        {
            if (candidate is null ||
                string.IsNullOrWhiteSpace(candidate.BaseUrl) ||
                FindSiteIndex(candidate.BaseUrl) >= 0)
            {
                continue;
            }

            SubscribeToChanges(candidate);
            Sites.Add(candidate);
            lastAdded = candidate;
            added++;
        }

        if (lastAdded is not null)
        {
            SelectedSite = lastAdded;
            SaveToDisk();
        }

        return added;
    }

    public (int Added, int Updated) UpsertGeneratedCandidates(IEnumerable<BatchSiteEntry> candidates)
    {
        var added = 0;
        var updated = 0;
        BatchSiteEntry? lastTouched = null;

        foreach (var candidate in candidates)
        {
            if (candidate is null ||
                string.IsNullOrWhiteSpace(candidate.BaseUrl) ||
                BatchEndpointText.NormalizeBaseUrl(candidate.BaseUrl) is not { } normalizedUrl)
            {
                continue;
            }

            candidate.BaseUrl = normalizedUrl;
            var existingIndex = FindSiteIndex(normalizedUrl);
            if (existingIndex >= 0)
            {
                var existing = Sites[existingIndex];
                existing.Name = candidate.Name;
                existing.BaseUrl = candidate.BaseUrl;
                existing.ApiKey = candidate.ApiKey;
                existing.Model = candidate.Model;
                existing.Timeout = candidate.Timeout;
                existing.TlsIgnore = candidate.TlsIgnore;
                existing.IsIncluded = candidate.IsIncluded;
                existing.GroupName = candidate.GroupName;
                existing.ModelCatalogSummary = candidate.ModelCatalogSummary;
                existing.ProtocolSummary = candidate.ProtocolSummary;
                ReplaceAvailableModels(existing.AvailableModels, candidate.AvailableModels);
                lastTouched = existing;
                updated++;
            }
            else
            {
                SubscribeToChanges(candidate);
                Sites.Add(candidate);
                lastTouched = candidate;
                added++;
            }
        }

        if (lastTouched is not null)
        {
            SelectedSite = lastTouched;
            MarkDirty();
            SaveToDisk();
            ImportStatusText = $"已从透明代理路由同步到入口组：新增 {added} 条，更新 {updated} 条。";
        }

        return (added, updated);
    }

    /// <summary>
    /// Adds a new empty site entry.
    /// </summary>
}
