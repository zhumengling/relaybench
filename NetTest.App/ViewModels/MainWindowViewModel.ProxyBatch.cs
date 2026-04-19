using System.Text;
using NetTest.App.Infrastructure;
using NetTest.Core.Models;

namespace NetTest.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private const int MaxProxyBatchSourceEntries = 16;
    private const int MaxProxyBatchProbeTargets = 24;
    private string _proxyBatchTargetsText =
        "# 已切换为结构化编辑器模式；这里保留为内部存储。" + Environment.NewLine +
        "# 支持独立入口与同站共用 key 的子入口写法。";
    private string _proxyBatchSummary = "填写入口组后，这里会显示入口对比摘要。";
    private string _proxyBatchDetail = "尚无入口组对比结果。";
    private ProxyBatchEditorItemViewModel? _selectedProxyBatchEditorItem;
    private ProxyBatchSiteGroupViewModel? _selectedProxyBatchSiteGroup;
    private ProxyBatchEditorMode _proxyBatchEditorMode = ProxyBatchEditorMode.BulkImport;
    private bool _suppressProxyBatchDraftAutoSave;
    private bool _suppressProxyBatchEditorItemChangeHandling;
    private bool _suppressProxyBatchTemplateDraftChangeHandling;
    private string _proxyBatchFormSiteGroupName = string.Empty;
    private string _proxyBatchFormSiteGroupApiKey = string.Empty;
    private string _proxyBatchFormSiteGroupModel = string.Empty;
    private string _proxyBatchFormEntryName = string.Empty;
    private string _proxyBatchFormBaseUrl = string.Empty;
    private string _proxyBatchFormApiKey = string.Empty;
    private string _proxyBatchFormModel = string.Empty;
    private string _proxyBatchBulkImportSharedText = string.Empty;
    private string _proxyBatchBulkImportIndependentText = string.Empty;
    private string _proxyBatchBulkImportPreview = "批量导入支持竖线 | 和表格粘贴的 Tab 分列格式；可先预览，再决定是否追加到左侧入口组。";

    public string ProxyBatchTargetsText
    {
        get => _proxyBatchTargetsText;
        set
        {
            if (SetProperty(ref _proxyBatchTargetsText, value))
            {
                OnPropertyChanged(nameof(ProxyBatchTargetDigestSummary));
                OnPropertyChanged(nameof(ProxyBatchTargetPreviewSummary));
                OnPropertyChanged(nameof(ProxyBatchExecutionPlanSummary));
                OnPropertyChanged(nameof(ProxyBatchEditorListSummary));
                OnPropertyChanged(nameof(ProxyBatchEditorListSummaryDisplay));
                OnPropertyChanged(nameof(ProxyBatchTemplateSummary));
            }
        }
    }

    public string ProxyBatchSummary
    {
        get => _proxyBatchSummary;
        private set => SetProperty(ref _proxyBatchSummary, value);
    }

    public string ProxyBatchDetail
    {
        get => _proxyBatchDetail;
        private set => SetProperty(ref _proxyBatchDetail, value);
    }

    public ProxyBatchEditorItemViewModel? SelectedProxyBatchEditorItem
    {
        get => _selectedProxyBatchEditorItem;
        set
        {
            if (SetProperty(ref _selectedProxyBatchEditorItem, value))
            {
                OnPropertyChanged(nameof(ProxyBatchEditorSelectionSummary));
            }
        }
    }

    public ProxyBatchSiteGroupViewModel? SelectedProxyBatchSiteGroup
    {
        get => _selectedProxyBatchSiteGroup;
        set
        {
            if (!SetProperty(ref _selectedProxyBatchSiteGroup, value))
            {
                return;
            }

            OnPropertyChanged(nameof(IsProxyBatchEditorItemSelected));
            OnPropertyChanged(nameof(ProxyBatchPrimaryActionText));
            OnPropertyChanged(nameof(ProxyBatchEditorSelectionSummary));

            if (value is null)
            {
                return;
            }

            LoadProxyBatchTemplateDraftFromSiteGroup(value);
            StatusMessage = $"已载入站点：{value.DisplayTitle}";
        }
    }

    public int ProxyBatchEditorModeIndex
    {
        get => (int)_proxyBatchEditorMode;
        set => SetProxyBatchEditorMode((ProxyBatchEditorMode)Math.Clamp(value, 0, 2));
    }

    public string ProxyBatchFormSiteGroupName
    {
        get => _proxyBatchFormSiteGroupName;
        set
        {
            if (SetProperty(ref _proxyBatchFormSiteGroupName, value))
            {
                OnPropertyChanged(nameof(ProxyBatchEditorFormModeSummary));
                PersistProxyBatchDraftState();
            }
        }
    }

    public string ProxyBatchFormSiteGroupApiKey
    {
        get => _proxyBatchFormSiteGroupApiKey;
        set
        {
            if (SetProperty(ref _proxyBatchFormSiteGroupApiKey, value))
            {
                OnPropertyChanged(nameof(ProxyBatchEditorFormModeSummary));
                PersistProxyBatchDraftState();
            }
        }
    }

    public string ProxyBatchFormSiteGroupModel
    {
        get => _proxyBatchFormSiteGroupModel;
        set
        {
            var changed = SetProperty(ref _proxyBatchFormSiteGroupModel, value);
            if (changed &&
                _proxyModelPickerTarget == ProxyModelPickerTarget.BatchSharedModel)
            {
                SyncSelectedProxyCatalogModel(value);
            }

            if (changed)
            {
                PersistProxyBatchDraftState();
            }
        }
    }

    public string ProxyBatchFormEntryName
    {
        get => _proxyBatchFormEntryName;
        set
        {
            if (SetProperty(ref _proxyBatchFormEntryName, value))
            {
                PersistProxyBatchDraftState();
            }
        }
    }

    public string ProxyBatchFormBaseUrl
    {
        get => _proxyBatchFormBaseUrl;
        set
        {
            if (SetProperty(ref _proxyBatchFormBaseUrl, value))
            {
                PersistProxyBatchDraftState();
            }
        }
    }

    public string ProxyBatchFormApiKey
    {
        get => _proxyBatchFormApiKey;
        set
        {
            if (SetProperty(ref _proxyBatchFormApiKey, value))
            {
                OnPropertyChanged(nameof(ProxyBatchEditorFormModeSummary));
                PersistProxyBatchDraftState();
            }
        }
    }

    public string ProxyBatchFormModel
    {
        get => _proxyBatchFormModel;
        set
        {
            var changed = SetProperty(ref _proxyBatchFormModel, value);
            if (changed &&
                _proxyModelPickerTarget == ProxyModelPickerTarget.BatchEntryModel)
            {
                SyncSelectedProxyCatalogModel(value);
            }

            if (changed)
            {
                PersistProxyBatchDraftState();
            }
        }
    }

    public string ProxyBatchTargetDigestSummary
        => BuildProxyBatchTargetDigestSummary();

    public string ProxyBatchTargetPreviewSummary
        => BuildProxyBatchTargetPreviewSummary();

    public string ProxyBatchExecutionPlanSummary
        => BuildProxyBatchExecutionPlanSummary();

    public string ProxyBatchGuideSummary
        => BuildProxyBatchGuideSummaryByMode();

    public string ProxyBatchTemplateSummary
        => BuildProxyBatchTemplateSummary();

    public string ProxyBatchBulkImportSharedText
    {
        get => _proxyBatchBulkImportSharedText;
        set => SetProperty(ref _proxyBatchBulkImportSharedText, value);
    }

    public string ProxyBatchBulkImportIndependentText
    {
        get => _proxyBatchBulkImportIndependentText;
        set => SetProperty(ref _proxyBatchBulkImportIndependentText, value);
    }

    public string ProxyBatchBulkImportPreview
    {
        get => _proxyBatchBulkImportPreview;
        private set => SetProperty(ref _proxyBatchBulkImportPreview, value);
    }

    public bool IsProxyBatchEditorItemSelected
        => SelectedProxyBatchSiteGroup is not null;

    public string ProxyBatchPrimaryActionText
        => SelectedProxyBatchSiteGroup is null ? "加入入口组" : "保存站点";

    public string ProxyBatchEditorListSummary
        => ProxyBatchSiteGroups.Count == 0
            ? "当前还没有已录入站点。右侧填好一个站点后点“加入入口组”。"
            : $"当前已录入 {ProxyBatchSiteGroups.Count} 个站点，共 {ProxyBatchEditorItems.Count} 个网址。";

    public string ProxyBatchEditorListSummaryDisplay
        => BuildProxyBatchEditorListSummaryDisplay();

    public string ProxyBatchEditorSelectionSummary
        => SelectedProxyBatchSiteGroup is null
            ? "当前正在录入新站点。点“加入入口组”后，会把右侧所有行当成同一个站点保存，然后自动清空表格等待下一个站点。"
            : $"当前正在编辑站点：{SelectedProxyBatchSiteGroup.DisplayTitle}。保存后会覆盖这个站点，并把右侧表格重置为下一站点的空白状态。";

    public string ProxyBatchEditorFormModeSummary
        => BuildProxyBatchEditorFormModeSummaryByMode();
}
