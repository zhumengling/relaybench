using System.Collections.ObjectModel;
using RelayBench.App.Infrastructure;

namespace RelayBench.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private const int MaxProxyEndpointHistoryEntries = 80;
    private bool _isProxyEndpointHistoryOpen;
    private ProxyEndpointHistoryItemViewModel? _selectedProxyEndpointHistoryItem;
    private ProxyEndpointHistoryApplyTarget _proxyEndpointHistoryApplyTarget = ProxyEndpointHistoryApplyTarget.SingleStation;

    public ObservableCollection<ProxyEndpointHistoryItemViewModel> ProxyEndpointHistoryItems { get; } = [];

    public bool IsProxyEndpointHistoryOpen
    {
        get => _isProxyEndpointHistoryOpen;
        private set => SetProperty(ref _isProxyEndpointHistoryOpen, value);
    }

    public ProxyEndpointHistoryItemViewModel? SelectedProxyEndpointHistoryItem
    {
        get => _selectedProxyEndpointHistoryItem;
        set => SetProperty(ref _selectedProxyEndpointHistoryItem, value);
    }

    public bool HasProxyEndpointHistoryItems
        => ProxyEndpointHistoryItems.Count > 0;

    public string ProxyEndpointHistorySummary
        => ProxyEndpointHistoryItems.Count == 0
            ? "还没有保存过接口。填入接口并运行、拉取模型、加入入口组或关闭程序后会自动记录。"
            : $"已保存 {ProxyEndpointHistoryItems.Count} 组接口，按最近使用时间倒序显示；点击任一项即可回填{ProxyEndpointHistoryTargetDisplayName}。";

    public string ProxyEndpointHistoryTargetDisplayName
        => _proxyEndpointHistoryApplyTarget == ProxyEndpointHistoryApplyTarget.ApplicationCenter
            ? "应用接入"
            : "单站测试";

    public string ProxyEndpointHistoryApplyHint
        => _proxyEndpointHistoryApplyTarget == ProxyEndpointHistoryApplyTarget.ApplicationCenter
            ? "点击“使用选中项”后，会把接口地址、API Key、模型回填到应用接入的当前接口；单站测试里的当前接口不会被改动。"
            : "点击“使用选中项”后，会把接口地址、API Key、模型回填到单站测试输入框；应用接入里的当前接口不会被改动。";

    private Task OpenProxyEndpointHistoryAsync()
        => OpenProxyEndpointHistoryAsync(ProxyEndpointHistoryApplyTarget.SingleStation);

    private Task OpenApplicationCenterProxyEndpointHistoryAsync()
        => OpenProxyEndpointHistoryAsync(ProxyEndpointHistoryApplyTarget.ApplicationCenter);

    private Task OpenProxyEndpointHistoryAsync(ProxyEndpointHistoryApplyTarget applyTarget)
    {
        _proxyEndpointHistoryApplyTarget = applyTarget;
        RememberKnownProxyEndpoints(countUse: false);
        SelectedProxyEndpointHistoryItem = ProxyEndpointHistoryItems.FirstOrDefault();
        IsProxyEndpointHistoryOpen = true;
        OnPropertyChanged(nameof(ProxyEndpointHistorySummary));
        OnPropertyChanged(nameof(ProxyEndpointHistoryTargetDisplayName));
        OnPropertyChanged(nameof(ProxyEndpointHistoryApplyHint));
        return Task.CompletedTask;
    }

    private Task CloseProxyEndpointHistoryAsync()
    {
        IsProxyEndpointHistoryOpen = false;
        return Task.CompletedTask;
    }

    private Task ApplyProxyEndpointHistoryItemAsync(ProxyEndpointHistoryItemViewModel? item)
    {
        item ??= SelectedProxyEndpointHistoryItem;
        if (item is null)
        {
            return Task.CompletedTask;
        }

        if (_proxyEndpointHistoryApplyTarget == ProxyEndpointHistoryApplyTarget.ApplicationCenter)
        {
            ApplicationCenterBaseUrl = item.BaseUrl;
            ApplicationCenterApiKey = item.ApiKey;
            ApplicationCenterModel = item.Model;
        }
        else
        {
            ProxyBaseUrl = item.BaseUrl;
            ProxyApiKey = item.ApiKey;
            ProxyModel = item.Model;
        }

        RememberProxyEndpoint(item.BaseUrl, item.ApiKey, item.Model);
        IsProxyEndpointHistoryOpen = false;
        StatusMessage = $"已回填历史接口到{ProxyEndpointHistoryTargetDisplayName}：{item.BaseUrl}";
        SaveState();
        return Task.CompletedTask;
    }

    private Task ClearProxyEndpointHistoryAsync()
    {
        ProxyEndpointHistoryItems.Clear();
        SelectedProxyEndpointHistoryItem = null;
        NotifyProxyEndpointHistoryChanged();
        SaveState();
        return Task.CompletedTask;
    }

    private void LoadProxyEndpointHistoryState(AppStateSnapshot snapshot)
    {
        ProxyEndpointHistoryItems.Clear();
        foreach (var entry in (snapshot.ProxyEndpointHistoryEntries ?? [])
                 .Where(entry => !string.IsNullOrWhiteSpace(entry.BaseUrl))
                 .OrderByDescending(entry => entry.LastUsedAt)
                 .Take(MaxProxyEndpointHistoryEntries))
        {
            ProxyEndpointHistoryItems.Add(CreateProxyEndpointHistoryItem(entry));
        }

        SelectedProxyEndpointHistoryItem = ProxyEndpointHistoryItems.FirstOrDefault();
        NotifyProxyEndpointHistoryChanged();
    }

    private void ApplyProxyEndpointHistoryStateToSnapshot(AppStateSnapshot snapshot)
    {
        RememberKnownProxyEndpoints(countUse: false);
        snapshot.ProxyEndpointHistoryEntries = ProxyEndpointHistoryItems
            .OrderByDescending(item => item.LastUsedAt)
            .Take(MaxProxyEndpointHistoryEntries)
            .Select(item => new ProxyEndpointHistoryEntrySnapshot
            {
                BaseUrl = item.BaseUrl,
                ApiKey = item.ApiKey,
                Model = item.Model,
                FirstSeenAt = item.FirstSeenAt,
                LastUsedAt = item.LastUsedAt,
                UseCount = item.UseCount
            })
            .ToList();
    }

    private void RememberKnownProxyEndpoints(bool countUse = true)
    {
        RememberProxyEndpoint(ProxyBaseUrl, ProxyApiKey, ProxyModel, countUse);
        RememberProxyEndpoint(ApplicationCenterBaseUrl, ApplicationCenterApiKey, ApplicationCenterModel, countUse);

        foreach (var item in ProxyBatchEditorItems)
        {
            var apiKey = string.IsNullOrWhiteSpace(item.EntryApiKey)
                ? item.SiteGroupApiKey
                : item.EntryApiKey;
            var model = string.IsNullOrWhiteSpace(item.EntryModel)
                ? item.SiteGroupModel
                : item.EntryModel;
            RememberProxyEndpoint(item.BaseUrl, apiKey, model, countUse);
        }

        foreach (var row in ProxyBatchRankingRows)
        {
            RememberProxyEndpoint(row.BaseUrl, row.ApiKey, row.Model, countUse);
        }
    }

    private void RememberProxyEndpoint(string? baseUrl, string? apiKey, string? model, bool countUse = true)
    {
        var normalizedBaseUrl = NormalizeProxyEndpointBaseUrl(baseUrl);
        if (string.IsNullOrWhiteSpace(normalizedBaseUrl))
        {
            return;
        }

        var normalizedApiKey = apiKey?.Trim() ?? string.Empty;
        var normalizedModel = NormalizeHistoryModel(model);
        var key = BuildProxyEndpointHistoryKey(normalizedBaseUrl, normalizedApiKey, normalizedModel);
        var existing = ProxyEndpointHistoryItems
            .FirstOrDefault(item => string.Equals(
                BuildProxyEndpointHistoryKey(item.BaseUrl, item.ApiKey, item.Model),
                key,
                StringComparison.OrdinalIgnoreCase));
        if (existing is not null && !countUse)
        {
            return;
        }

        var updated = new ProxyEndpointHistoryItemViewModel
        {
            BaseUrl = normalizedBaseUrl,
            ApiKey = normalizedApiKey,
            Model = normalizedModel,
            FirstSeenAt = existing?.FirstSeenAt ?? DateTimeOffset.Now,
            LastUsedAt = countUse || existing is null ? DateTimeOffset.Now : existing.LastUsedAt,
            UseCount = countUse ? Math.Max(0, existing?.UseCount ?? 0) + 1 : Math.Max(1, existing?.UseCount ?? 1)
        };

        if (existing is not null)
        {
            ProxyEndpointHistoryItems.Remove(existing);
        }

        ProxyEndpointHistoryItems.Insert(0, updated);
        while (ProxyEndpointHistoryItems.Count > MaxProxyEndpointHistoryEntries)
        {
            ProxyEndpointHistoryItems.RemoveAt(ProxyEndpointHistoryItems.Count - 1);
        }

        SelectedProxyEndpointHistoryItem ??= updated;
        NotifyProxyEndpointHistoryChanged();
    }

    private static ProxyEndpointHistoryItemViewModel CreateProxyEndpointHistoryItem(ProxyEndpointHistoryEntrySnapshot entry)
        => new()
        {
            BaseUrl = NormalizeProxyEndpointBaseUrl(entry.BaseUrl),
            ApiKey = entry.ApiKey?.Trim() ?? string.Empty,
            Model = NormalizeHistoryModel(entry.Model),
            FirstSeenAt = entry.FirstSeenAt == DateTimeOffset.MinValue ? DateTimeOffset.Now : entry.FirstSeenAt,
            LastUsedAt = entry.LastUsedAt == DateTimeOffset.MinValue ? DateTimeOffset.Now : entry.LastUsedAt,
            UseCount = Math.Max(1, entry.UseCount)
        };

    private static string NormalizeProxyEndpointBaseUrl(string? value)
        => (value ?? string.Empty).Trim().TrimEnd('/');

    private static string NormalizeHistoryModel(string? value)
    {
        var normalized = (value ?? string.Empty).Trim();
        return string.Equals(normalized, "（沿用当前模型）", StringComparison.Ordinal)
            ? string.Empty
            : normalized;
    }

    private static string BuildProxyEndpointHistoryKey(string baseUrl, string apiKey, string model)
        => string.Join("|", baseUrl.Trim().TrimEnd('/'), apiKey.Trim(), model.Trim());

    private void NotifyProxyEndpointHistoryChanged()
    {
        OnPropertyChanged(nameof(HasProxyEndpointHistoryItems));
        OnPropertyChanged(nameof(ProxyEndpointHistorySummary));
    }

    private enum ProxyEndpointHistoryApplyTarget
    {
        SingleStation,
        ApplicationCenter
    }
}
