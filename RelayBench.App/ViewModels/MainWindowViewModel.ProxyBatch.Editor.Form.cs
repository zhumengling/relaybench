using System.Text;
using RelayBench.App.Infrastructure;
using RelayBench.Core.Models;

namespace RelayBench.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private void RebuildProxyBatchTargetsTextFromEditorItems(bool persistState = true)
    {
        StringBuilder builder = new();
        string? activeSiteGroupName = null;

        foreach (var item in ProxyBatchEditorItems)
        {
            if (IsEmptyProxyBatchEditorItem(item))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(item.SiteGroupName))
            {
                activeSiteGroupName = null;
                builder.AppendLine(BuildStandaloneEntryLine(item));
                continue;
            }

            if (!string.Equals(activeSiteGroupName, item.SiteGroupName, StringComparison.OrdinalIgnoreCase))
            {
                builder.AppendLine(BuildSiteGroupHeaderLine(item));
                activeSiteGroupName = item.SiteGroupName;
            }

            builder.AppendLine(BuildSiteGroupChildLine(item));
        }

        ProxyBatchTargetsText = builder.ToString().TrimEnd();
        OnPropertyChanged(nameof(ProxyBatchEditorListSummary));
        OnPropertyChanged(nameof(ProxyBatchEditorListSummaryDisplay));
        if (_lastProxySingleResult is not null)
        {
            RefreshProxyManagedEntryAssessment(_lastProxySingleResult);
            RefreshProxyUnifiedOutput();
        }

        if (persistState)
        {
            SaveState();
        }
    }

    private static bool IsEmptyProxyBatchEditorItem(ProxyBatchEditorItemViewModel item)
        => string.IsNullOrWhiteSpace(item.EntryName) &&
           string.IsNullOrWhiteSpace(item.BaseUrl) &&
           string.IsNullOrWhiteSpace(item.EntryApiKey) &&
           string.IsNullOrWhiteSpace(item.EntryModel) &&
           string.IsNullOrWhiteSpace(item.SiteGroupName) &&
           string.IsNullOrWhiteSpace(item.SiteGroupApiKey) &&
           string.IsNullOrWhiteSpace(item.SiteGroupModel);

    private ProxyBatchEditorItemViewModel BuildProxyBatchEditorItemFromForm()
    {
        var baseUrl = ProxyBatchFormBaseUrl.Trim();
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            throw new InvalidOperationException("请输入入口地址，例如 https://example.com/v1。");
        }

        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException($"入口地址格式不正确：{baseUrl}。请填写以 http:// 或 https:// 开头的完整地址。");
        }

        var entryName = NormalizeNullable(ProxyBatchFormEntryName) ?? BuildBatchDefaultName(baseUrl, ProxyBatchEditorItems.Count + 1);
        var effectiveMode = ResolveEffectiveProxyBatchEditorMode();
        string? siteGroupName;
        string? siteGroupApiKey;
        string? siteGroupModel;
        string? entryApiKey;
        string? entryModel;

        switch (effectiveMode)
        {
            case ProxyBatchEditorMode.SharedKeyGroup:
                siteGroupName = NormalizeNullable(ProxyBatchFormSiteGroupName) ?? BuildBatchSiteGroupName(baseUrl);

                var existingGroupReference = ResolveExistingSiteGroupReference(siteGroupName, SelectedProxyBatchEditorItem);
                siteGroupApiKey = NormalizeNullable(ProxyBatchFormSiteGroupApiKey) ?? existingGroupReference?.SiteGroupApiKey;
                siteGroupModel = NormalizeNullable(ProxyBatchFormSiteGroupModel) ?? existingGroupReference?.SiteGroupModel;
                entryApiKey = null;
                entryModel = null;
                break;

            case ProxyBatchEditorMode.MultiKey:
                siteGroupName = NormalizeNullable(ProxyBatchFormSiteGroupName);
                siteGroupApiKey = null;
                siteGroupModel = null;
                entryApiKey = NormalizeNullable(ProxyBatchFormApiKey);
                entryModel = NormalizeNullable(ProxyBatchFormModel);
                break;

            default:
                siteGroupName = null;
                siteGroupApiKey = null;
                siteGroupModel = null;
                entryApiKey = NormalizeNullable(ProxyBatchFormApiKey);
                entryModel = NormalizeNullable(ProxyBatchFormModel);
                break;
        }

        return new ProxyBatchEditorItemViewModel(
            entryName,
            baseUrl,
            entryApiKey,
            entryModel,
            siteGroupName,
            siteGroupApiKey,
            siteGroupModel,
            true);
    }

    private void LoadProxyBatchEditorForm(ProxyBatchEditorItemViewModel item)
    {
        SetProxyBatchEditorMode(ResolveProxyBatchEditorMode(item));
        ProxyBatchFormSiteGroupName = item.SiteGroupName ?? string.Empty;
        ProxyBatchFormSiteGroupApiKey = item.SiteGroupApiKey ?? string.Empty;
        ProxyBatchFormSiteGroupModel = item.SiteGroupModel ?? string.Empty;
        ProxyBatchFormEntryName = item.EntryName;
        ProxyBatchFormBaseUrl = item.BaseUrl;
        ProxyBatchFormApiKey = item.EntryApiKey ?? string.Empty;
        ProxyBatchFormModel = item.EntryModel ?? string.Empty;
    }

    private void LoadProxyBatchEntryFields(ProxyBatchEditorItemViewModel item)
    {
        ProxyBatchFormEntryName = item.EntryName;
        ProxyBatchFormBaseUrl = item.BaseUrl;
        ProxyBatchFormApiKey = item.EntryApiKey ?? string.Empty;
        ProxyBatchFormModel = item.EntryModel ?? string.Empty;
    }

    private void ClearProxyBatchEditorForm()
    {
        ProxyBatchFormSiteGroupName = string.Empty;
        ProxyBatchFormSiteGroupApiKey = string.Empty;
        ProxyBatchFormSiteGroupModel = string.Empty;
        ProxyBatchFormEntryName = string.Empty;
        ProxyBatchFormBaseUrl = string.Empty;
        ProxyBatchFormApiKey = string.Empty;
        ProxyBatchFormModel = string.Empty;
    }

    private void ClearProxyBatchEntryFields()
    {
        ProxyBatchFormEntryName = string.Empty;
        ProxyBatchFormBaseUrl = string.Empty;
        ProxyBatchFormApiKey = string.Empty;
        ProxyBatchFormModel = string.Empty;
    }

    private void LoadCurrentProxyConfigIntoBatchForm()
    {
        SelectedProxyBatchEditorItem = null;
        SetProxyBatchEditorMode(ProxyBatchEditorMode.SharedKeyGroup);

        var baseUrl = ProxyBaseUrl.Trim();

        ProxyBatchFormSiteGroupName = string.IsNullOrWhiteSpace(baseUrl)
            ? string.Empty
            : BuildBatchSiteGroupName(baseUrl);

        ProxyBatchFormSiteGroupApiKey = ProxyApiKey.Trim();
        ProxyBatchFormSiteGroupModel = ProxyModel.Trim();
        ProxyBatchFormEntryName = BuildBatchDefaultName(baseUrl, ProxyBatchEditorItems.Count + 1);
        ProxyBatchFormBaseUrl = baseUrl;
        ProxyBatchFormApiKey = string.Empty;
        ProxyBatchFormModel = string.Empty;
    }

    private void PrepareProxyBatchEditorFormForNextAdd(ProxyBatchEditorItemViewModel item, ProxyBatchEditorMode mode)
    {
        SelectedProxyBatchEditorItem = null;
        SetProxyBatchEditorMode(mode);

        switch (mode)
        {
            case ProxyBatchEditorMode.SharedKeyGroup:
                ProxyBatchFormSiteGroupName = item.SiteGroupName ?? string.Empty;
                ProxyBatchFormSiteGroupApiKey = item.SiteGroupApiKey ?? string.Empty;
                ProxyBatchFormSiteGroupModel = item.SiteGroupModel ?? string.Empty;
                ProxyBatchFormEntryName = string.Empty;
                ProxyBatchFormBaseUrl = string.Empty;
                ProxyBatchFormApiKey = string.Empty;
                ProxyBatchFormModel = string.Empty;
                break;

            case ProxyBatchEditorMode.MultiKey:
                ProxyBatchFormSiteGroupName = item.SiteGroupName ?? string.Empty;
                ProxyBatchFormSiteGroupApiKey = string.Empty;
                ProxyBatchFormSiteGroupModel = string.Empty;
                ProxyBatchFormEntryName = string.Empty;
                ProxyBatchFormBaseUrl = string.Empty;
                ProxyBatchFormApiKey = string.Empty;
                ProxyBatchFormModel = item.EntryModel ?? string.Empty;
                break;

            default:
                ProxyBatchFormSiteGroupName = string.Empty;
                ProxyBatchFormSiteGroupApiKey = string.Empty;
                ProxyBatchFormSiteGroupModel = string.Empty;
                ProxyBatchFormEntryName = string.Empty;
                ProxyBatchFormBaseUrl = string.Empty;
                ProxyBatchFormApiKey = item.EntryApiKey ?? string.Empty;
                ProxyBatchFormModel = item.EntryModel ?? string.Empty;
                break;
        }
    }
}
