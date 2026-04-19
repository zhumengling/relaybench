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
    private ProxyBatchEditorMode _proxyBatchEditorMode = ProxyBatchEditorMode.SharedKeyGroup;
    private bool _suppressProxyBatchDraftAutoSave;
    private string _proxyBatchFormSiteGroupName = string.Empty;
    private string _proxyBatchFormSiteGroupApiKey = string.Empty;
    private string _proxyBatchFormSiteGroupModel = string.Empty;
    private string _proxyBatchFormEntryName = string.Empty;
    private string _proxyBatchFormBaseUrl = string.Empty;
    private string _proxyBatchFormApiKey = string.Empty;
    private string _proxyBatchFormModel = string.Empty;

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
                OnPropertyChanged(nameof(IsProxyBatchEditorItemSelected));
                OnPropertyChanged(nameof(ProxyBatchPrimaryActionText));
                OnPropertyChanged(nameof(ProxyBatchEditorSelectionSummary));

                if (value is null)
                {
                    ClearProxyBatchEditorForm();
                }
                else
                {
                    LoadProxyBatchEditorForm(value);
                }
            }
        }
    }

    public int ProxyBatchEditorModeIndex
    {
        get => (int)_proxyBatchEditorMode;
        set
        {
            SetProxyBatchEditorMode((ProxyBatchEditorMode)Math.Clamp(value, 0, 1));
            PersistProxyBatchDraftState();
        }
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

    public bool IsProxyBatchEditorItemSelected
        => SelectedProxyBatchEditorItem is not null;

    public string ProxyBatchPrimaryActionText
        => SelectedProxyBatchEditorItem is null ? "加入入口组" : "保存修改";

    public string ProxyBatchEditorListSummary
        => ProxyBatchEditorItems.Count == 0
            ? "当前还没有已填写条目。右侧填完后点击“加入入口组”。"
            : $"当前已填写 {ProxyBatchEditorItems.Count} 条记录。点击左侧任一条目，可回填到右侧小表单继续修改。";

    public string ProxyBatchEditorListSummaryDisplay
        => BuildProxyBatchEditorListSummaryDisplay();

    public string ProxyBatchEditorSelectionSummary
        => SelectedProxyBatchEditorItem is null
            ? "当前是新增模式。右侧填完一条后点“加入入口组”，左侧会立刻出现新记录。"
            : $"当前正在编辑：{SelectedProxyBatchEditorItem.DisplayTitle}。点“保存修改”会覆盖左侧选中项。";

    public string ProxyBatchEditorFormModeSummary
        => BuildProxyBatchEditorFormModeSummaryByMode();
}
