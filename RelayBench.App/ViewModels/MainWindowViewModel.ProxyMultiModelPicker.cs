using System.ComponentModel;
using RelayBench.Core.Models;

namespace RelayBench.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private async Task OpenProxyMultiModelPickerAsync()
    {
        IsProxyModelPickerOpen = false;
        ProxyMultiModelCatalogFilterText = string.Empty;

        if (ProxyCatalogModels.Count > 0)
        {
            IsProxyMultiModelPickerOpen = true;
            StatusMessage = _proxySelectedMultiModelNames.Count == 0
                ? "\u53EF\u4EE5\u5F00\u59CB\u52FE\u9009\u9700\u8981\u5BF9\u6BD4 tok/s \u7684\u6A21\u578B\u3002"
                : $"\u5DF2\u6253\u5F00\u591A\u6A21\u578B\u9009\u62E9\u5F39\u7A97\uff0c\u5F53\u524D{BuildProxyMultiModelSelectionSummary()}\u3002";
            return;
        }

        if (!TryBuildProxyModelCatalogSettings(ProxyModelPickerTarget.DefaultModel, out var settings, out var message))
        {
            StatusMessage = message;
            return;
        }

        await ExecuteBusyActionAsync(
            "\u6B63\u5728\u4E3A\u591A\u6A21\u578B\u6D4B\u901F\u62C9\u53D6\u53EF\u7528\u6A21\u578B\u5217\u8868...",
            async () =>
            {
                var result = await _proxyDiagnosticsService.FetchModelsAsync(settings);
                ApplyProxyModelCatalogResult(result);
                DashboardCards[3].Status = result.Success ? "\u6A21\u578B\u5DF2\u62C9\u53D6" : "\u6A21\u578B\u62C9\u53D6\u5931\u8D25";
                DashboardCards[3].Detail = result.Summary;
                StatusMessage = result.Summary;
                IsProxyModelPickerOpen = false;
                IsProxyMultiModelPickerOpen = true;
            });
    }

    private Task CloseProxyMultiModelPickerAsync()
    {
        IsProxyMultiModelPickerOpen = false;
        return Task.CompletedTask;
    }

    private Task ConfirmProxyMultiModelPickerAsync()
    {
        IsProxyMultiModelPickerOpen = false;
        StatusMessage = _proxySelectedMultiModelNames.Count == 0
            ? "\u5DF2\u5173\u95ED\u591A\u6A21\u578B\u9009\u62E9\u5F39\u7A97\uff0c\u672C\u6B21\u6DF1\u6D4B\u4E0D\u8FFD\u52A0\u591A\u6A21\u578B tok/s \u5BF9\u6BD4\u3002"
            : $"\u5DF2\u786E\u8BA4{BuildProxyMultiModelSelectionSummary()}\uFF0C\u6DF1\u6D4B\u6700\u540E\u4F1A\u8FFD\u52A0\u4E32\u884C tok/s \u5BF9\u6BD4\u3002";
        SaveState();
        return Task.CompletedTask;
    }

    private Task ClearProxyMultiModelSelectionAsync()
    {
        if (_proxySelectedMultiModelNames.Count == 0)
        {
            StatusMessage = "\u5F53\u524D\u6CA1\u6709\u5DF2\u9009\u7684\u591A\u6A21\u578B\u3002";
            return Task.CompletedTask;
        }

        _proxySelectedMultiModelNames.Clear();
        RefreshVisibleProxyMultiModelCatalogItems();
        NotifyProxyMultiModelSelectionStateChanged();
        SaveState();
        StatusMessage = "\u5DF2\u6E05\u7A7A\u591A\u6A21\u578B\u6D4B\u901F\u9009\u62E9\u3002";
        return Task.CompletedTask;
    }

    private void RefreshVisibleProxyMultiModelCatalogItems(bool trimToCatalog = false)
    {
        if (trimToCatalog)
        {
            TrimProxyMultiModelSelectionsToCatalog();
        }

        foreach (var item in VisibleProxyMultiModelCatalogItems)
        {
            item.PropertyChanged -= ProxyMultiModelCatalogItem_OnPropertyChanged;
        }

        VisibleProxyMultiModelCatalogItems.Clear();

        var keyword = ProxyMultiModelCatalogFilterText?.Trim() ?? string.Empty;
        IEnumerable<string> filtered = string.IsNullOrWhiteSpace(keyword)
            ? ProxyCatalogModels
            : ProxyCatalogModels
                .Where(item => item.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                .ToArray();

        foreach (var model in filtered)
        {
            var item = new ProxySelectableModelItemViewModel(
                model,
                _proxySelectedMultiModelNames.Any(selected =>
                    string.Equals(selected, model, StringComparison.OrdinalIgnoreCase)));
            item.PropertyChanged += ProxyMultiModelCatalogItem_OnPropertyChanged;
            VisibleProxyMultiModelCatalogItems.Add(item);
        }

        NotifyProxyMultiModelSelectionStateChanged();
    }

    private void ProxyMultiModelCatalogItem_OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!string.Equals(e.PropertyName, nameof(ProxySelectableModelItemViewModel.IsSelected), StringComparison.Ordinal) ||
            sender is not ProxySelectableModelItemViewModel item)
        {
            return;
        }

        var changed = item.IsSelected
            ? AddProxyMultiModelSelection(item.Name)
            : RemoveProxyMultiModelSelection(item.Name);

        if (!changed)
        {
            return;
        }

        NotifyProxyMultiModelSelectionStateChanged();
        SaveState();
    }

    private bool AddProxyMultiModelSelection(string? model)
    {
        var normalized = NormalizeProxyMultiModelName(model);
        if (normalized.Length == 0 ||
            _proxySelectedMultiModelNames.Any(existing =>
                string.Equals(existing, normalized, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        _proxySelectedMultiModelNames.Add(normalized);
        return true;
    }

    private bool RemoveProxyMultiModelSelection(string? model)
    {
        var normalized = NormalizeProxyMultiModelName(model);
        if (normalized.Length == 0)
        {
            return false;
        }

        var index = _proxySelectedMultiModelNames.FindIndex(existing =>
            string.Equals(existing, normalized, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            return false;
        }

        _proxySelectedMultiModelNames.RemoveAt(index);
        return true;
    }

    private void ReplaceProxyMultiModelSelections(IEnumerable<string>? models)
    {
        _proxySelectedMultiModelNames.Clear();

        if (models is null)
        {
            return;
        }

        foreach (var model in models)
        {
            AddProxyMultiModelSelection(model);
        }
    }

    private void TrimProxyMultiModelSelectionsToCatalog()
    {
        if (_proxySelectedMultiModelNames.Count == 0)
        {
            return;
        }

        var catalog = ProxyCatalogModels
            .Where(static model => !string.IsNullOrWhiteSpace(model))
            .Select(static model => model.Trim())
            .ToArray();
        var catalogSet = new HashSet<string>(catalog, StringComparer.OrdinalIgnoreCase);
        _proxySelectedMultiModelNames.RemoveAll(model => !catalogSet.Contains(model));
    }

    private string[] GetSelectedProxyMultiModelNames()
        => _proxySelectedMultiModelNames
            .Where(static model => !string.IsNullOrWhiteSpace(model))
            .Select(static model => model.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private string BuildProxyMultiModelSelectionSummary()
    {
        var count = _proxySelectedMultiModelNames.Count;
        if (count == 0)
        {
            return "\u672A\u9009\u6A21\u578B";
        }

        var preview = string.Join(", ", _proxySelectedMultiModelNames.Take(3));
        return count <= 3
            ? $"\u5DF2\u9009 {count} \u4E2A\uFF1A{preview}"
            : $"\u5DF2\u9009 {count} \u4E2A\uFF1A{preview} ...";
    }

    private string BuildProxyMultiModelSelectionDetail()
    {
        if (_proxySelectedMultiModelNames.Count == 0)
        {
            return "\u5C1A\u672A\u9009\u62E9\u591A\u6A21\u578B\u3002\n\u4E0D\u9009\u5219\u6DF1\u6D4B\u53EA\u4FDD\u7559\u57FA\u7840\u4E0E\u8865\u5145\u63A2\u9488\uff0c\u56FE\u8868\u672B\u5C3E\u4E0D\u4F1A\u8FFD\u52A0 tok/s \u5BF9\u6BD4\u5206\u533A\u3002";
        }

        return string.Join(
            Environment.NewLine,
            _proxySelectedMultiModelNames.Select((model, index) => $"{index + 1}. {model}"));
    }

    private string DescribeProxyMultiModelExecutionState()
        => _proxySelectedMultiModelNames.Count == 0
            ? "\u672A\u9009"
            : $"\u5DF2\u9009 {_proxySelectedMultiModelNames.Count} \u4E2A";

    private void NotifyProxyMultiModelSelectionStateChanged()
    {
        OnPropertyChanged(nameof(ProxyMultiModelSelectionSummary));
        OnPropertyChanged(nameof(ProxyMultiModelSelectionDetail));
        OnPropertyChanged(nameof(ProxyDiagnosticsExecutionSummary));
    }

    private static string NormalizeProxyMultiModelName(string? model)
        => model?.Trim() ?? string.Empty;

    private static string BuildProxyMultiModelSpeedDigest(IReadOnlyList<ProxyMultiModelSpeedTestResult>? results)
    {
        if (results is null || results.Count == 0)
        {
            return "\u672A\u6267\u884C";
        }

        return string.Join(
            Environment.NewLine,
            results
                .OrderByDescending(item => item.Success)
                .ThenByDescending(item => item.OutputTokensPerSecond ?? double.MinValue)
                .ThenBy(item => item.Model, StringComparer.OrdinalIgnoreCase)
                .Select(result =>
            {
                var metric = result.Success
                    ? FormatTokensPerSecond(result.OutputTokensPerSecond, result.OutputTokenCountEstimated)
                    : "--";
                var suffix = result.Success
                    ? string.Empty
                    : $" / {(result.Error ?? result.Summary)}";
                return $"{result.Model}\uFF1A{metric}{suffix}";
            }));
    }
}
