using NetTest.Core.Models;

namespace NetTest.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private Task FetchDefaultProxyModelsAsync()
        => FetchProxyModelsForTargetAsync(ProxyModelPickerTarget.DefaultModel);

    private Task FetchProxyBatchSharedModelsAsync()
        => FetchProxyModelsForTargetAsync(ProxyModelPickerTarget.BatchSharedModel);

    private Task FetchProxyBatchEntryModelsAsync()
        => FetchProxyModelsForTargetAsync(ProxyModelPickerTarget.BatchEntryModel);

    private Task RunProxyWithValidationAsync()
        => EnsureDefaultProxyModelSelected()
            ? RunBasicProxyAsync()
            : Task.CompletedTask;

    private Task RunProxyDeepWithValidationAsync()
        => EnsureDefaultProxyModelSelected()
            ? RunDeepProxyAsync()
            : Task.CompletedTask;

    private Task RunProxySeriesWithValidationAsync()
        => EnsureDefaultProxyModelSelected()
            ? RunProxySeriesAsync()
            : Task.CompletedTask;

    private Task RunProxyBatchWithValidationAsync()
    {
        if (!TryEnsureProxyBatchModelsReady())
        {
            return Task.CompletedTask;
        }

        return RunProxyBatchAsync();
    }

    private Task FetchProxyModelsForTargetAsync(ProxyModelPickerTarget target)
    {
        SetProxyModelPickerTarget(target);
        if (!TryBuildProxyModelCatalogSettings(target, out var settings, out var message))
        {
            StatusMessage = message;
            return Task.CompletedTask;
        }

        return ExecuteBusyActionAsync(
            $"正在拉取{GetProxyModelPickerTargetDisplayName()}可用模型...",
            () => FetchProxyModelsCoreAsync(settings));
    }

    private async Task FetchProxyModelsCoreAsync(ProxyEndpointSettings settings)
    {
        var result = await _proxyDiagnosticsService.FetchModelsAsync(settings);
        ApplyProxyModelCatalogResult(result);
        IsProxyModelPickerOpen = true;
        DashboardCards[3].Status = result.Success ? "模型已拉取" : "模型拉取失败";
        DashboardCards[3].Detail = result.Summary;
        StatusMessage = result.Summary;
        AppendHistory("中转站", "拉取模型列表", ProxyModelCatalogSummary);
    }

    private bool EnsureDefaultProxyModelSelected()
    {
        if (!string.IsNullOrWhiteSpace(ProxyModel))
        {
            return true;
        }

        SetProxyModelPickerTarget(ProxyModelPickerTarget.DefaultModel);
        if (ProxyCatalogModels.Count > 0)
        {
            IsProxyModelPickerOpen = true;
            StatusMessage = "请先选择模型后再运行中转站诊断，已为你打开模型选择弹窗。";
        }
        else
        {
            StatusMessage = "请先点击“拉取并选择模型”，选中一个模型后再运行中转站诊断。";
        }

        return false;
    }

    private bool TryEnsureProxyBatchModelsReady()
    {
        try
        {
            BuildProxyBatchPlan(requireRunnable: true);
            return true;
        }
        catch (InvalidOperationException ex)
        {
            StatusMessage = ex.Message;
            return false;
        }
    }

    private bool TryBuildProxyModelCatalogSettings(
        ProxyModelPickerTarget target,
        out ProxyEndpointSettings settings,
        out string message)
    {
        var context = ResolveProxyModelPickerContext(target);
        var targetName = GetProxyModelPickerTargetDisplayName(target);

        if (string.IsNullOrWhiteSpace(context.BaseUrl))
        {
            settings = default!;
            message = $"请先填写{targetName}对应的网址，再拉取模型列表。";
            return false;
        }

        if (string.IsNullOrWhiteSpace(context.ApiKey))
        {
            settings = default!;
            message = $"请先填写{targetName}对应的 API Key，再拉取模型列表。";
            return false;
        }

        settings = new ProxyEndpointSettings(
            context.BaseUrl,
            context.ApiKey,
            context.Model,
            ProxyIgnoreTlsErrors,
            ParseBoundedInt(ProxyTimeoutSecondsText, fallback: 20, min: 5, max: 120));
        message = string.Empty;
        return true;
    }

    private ProxyModelPickerContext ResolveProxyModelPickerContext(ProxyModelPickerTarget target)
    {
        var siteGroupReference = ResolveExistingSiteGroupReference(
            NormalizeNullable(ProxyBatchFormSiteGroupName),
            SelectedProxyBatchEditorItem);
        var templateRow = _proxyBatchTemplateModelTargetRow;

        return target switch
        {
            ProxyModelPickerTarget.DefaultModel => new ProxyModelPickerContext(
                NormalizeNullable(ProxyBaseUrl) ?? string.Empty,
                NormalizeNullable(ProxyApiKey) ?? string.Empty,
                NormalizeNullable(ProxyModel) ?? string.Empty),
            ProxyModelPickerTarget.BatchSharedModel => new ProxyModelPickerContext(
                FirstNonEmpty(
                    NormalizeNullable(ProxyBatchFormBaseUrl),
                    NormalizeNullable(siteGroupReference?.BaseUrl),
                    NormalizeNullable(ProxyBaseUrl)) ?? string.Empty,
                FirstNonEmpty(
                    NormalizeNullable(ProxyBatchFormSiteGroupApiKey),
                    NormalizeNullable(siteGroupReference?.SiteGroupApiKey),
                    NormalizeNullable(ProxyApiKey)) ?? string.Empty,
                NormalizeNullable(ProxyBatchFormSiteGroupModel) ?? string.Empty),
            ProxyModelPickerTarget.BatchEntryModel => new ProxyModelPickerContext(
                FirstNonEmpty(
                    NormalizeNullable(ProxyBatchFormBaseUrl),
                    NormalizeNullable(ProxyBaseUrl)) ?? string.Empty,
                FirstNonEmpty(
                    NormalizeNullable(ProxyBatchFormApiKey),
                    NormalizeNullable(ProxyBatchFormSiteGroupApiKey),
                    NormalizeNullable(siteGroupReference?.SiteGroupApiKey),
                    NormalizeNullable(ProxyApiKey)) ?? string.Empty,
                NormalizeNullable(ProxyBatchFormModel) ?? string.Empty),
            ProxyModelPickerTarget.BatchTemplateRowModel => new ProxyModelPickerContext(
                NormalizeNullable(templateRow?.BaseUrl) ?? string.Empty,
                ResolveProxyBatchTemplateRowApiKey(templateRow) ?? string.Empty,
                NormalizeNullable(templateRow?.EntryModel) ?? string.Empty),
            _ => new ProxyModelPickerContext(string.Empty, string.Empty, string.Empty)
        };
    }

    private void SetProxyModelPickerTarget(ProxyModelPickerTarget target)
    {
        if (_proxyModelPickerTarget == target)
        {
            SyncSelectedProxyCatalogModel(GetCurrentProxyModelPickerValue());
            return;
        }

        _proxyModelPickerTarget = target;
        OnPropertyChanged(nameof(ProxyModelPickerTargetSummary));
        OnPropertyChanged(nameof(ProxyModelPickerInstruction));
        SyncSelectedProxyCatalogModel(GetCurrentProxyModelPickerValue());
    }

    private string GetCurrentProxyModelPickerValue()
        => _proxyModelPickerTarget switch
        {
            ProxyModelPickerTarget.DefaultModel => ProxyModel,
            ProxyModelPickerTarget.BatchSharedModel => ProxyBatchFormSiteGroupModel,
            ProxyModelPickerTarget.BatchEntryModel => ProxyBatchFormModel,
            ProxyModelPickerTarget.BatchTemplateRowModel => _proxyBatchTemplateModelTargetRow?.EntryModel ?? string.Empty,
            _ => string.Empty
        };

    private void ApplyProxyCatalogSelection(string model)
    {
        var normalizedModel = model.Trim();

        switch (_proxyModelPickerTarget)
        {
            case ProxyModelPickerTarget.BatchSharedModel:
                ProxyBatchFormSiteGroupModel = normalizedModel;
                StatusMessage = $"已回填同站共用模型：{normalizedModel}";
                break;
            case ProxyModelPickerTarget.BatchEntryModel:
                ProxyBatchFormModel = normalizedModel;
                StatusMessage = $"已回填本条目模型：{normalizedModel}";
                break;
            case ProxyModelPickerTarget.BatchTemplateRowModel:
                if (_proxyBatchTemplateModelTargetRow is not null)
                {
                    _proxyBatchTemplateModelTargetRow.EntryModel = normalizedModel;
                    StatusMessage = $"已回填模板行模型：{normalizedModel}";
                }
                else
                {
                    StatusMessage = $"已选择模型：{normalizedModel}";
                }

                SaveState();
                break;
            default:
                ProxyModel = normalizedModel;
                StatusMessage = $"已选择默认模型：{normalizedModel}";
                SaveState();
                break;
        }
    }

    private string GetProxyModelPickerTargetDisplayName()
        => GetProxyModelPickerTargetDisplayName(_proxyModelPickerTarget);

    private static string GetProxyModelPickerTargetDisplayName(ProxyModelPickerTarget target)
        => target switch
        {
            ProxyModelPickerTarget.BatchSharedModel => "入口组的同站共用模型",
            ProxyModelPickerTarget.BatchEntryModel => "入口组的本条目模型",
            ProxyModelPickerTarget.BatchTemplateRowModel => "模板表当前行模型",
            _ => "主页默认模型"
        };

    private sealed record ProxyModelPickerContext(
        string BaseUrl,
        string ApiKey,
        string Model);

    private enum ProxyModelPickerTarget
    {
        DefaultModel,
        BatchSharedModel,
        BatchEntryModel,
        BatchTemplateRowModel
    }
}
