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

public sealed partial class BatchSiteEditorViewModel : ObservableObject
{
    private void EnsureDraftRow()
    {
        if (DraftRows.Count > 0)
        {
            return;
        }

        var row = new BatchSiteDraftRow();
        SubscribeToDraftChanges(row);
        DraftRows.Add(row);
        SelectedDraftRow = row;
    }

    private void ReplaceDraftRows(IEnumerable<BatchSiteDraftRow> rows)
    {
        foreach (var row in DraftRows)
        {
            UnsubscribeFromDraftChanges(row);
        }

        DraftRows.Clear();
        foreach (var row in rows)
        {
            SubscribeToDraftChanges(row);
            DraftRows.Add(row);
        }

        EnsureDraftRow();
        SelectedDraftRow = DraftRows.FirstOrDefault(static row => row.HasContent) ?? DraftRows.FirstOrDefault();
        RefreshDraftState();
    }

    private void RefreshDraftState()
    {
        OnPropertyChanged(nameof(DraftRowCount));
        OnPropertyChanged(nameof(DraftFilledCount));
        OnPropertyChanged(nameof(DraftIncludedCount));
        OnPropertyChanged(nameof(DraftMissingUrlCount));
        OnPropertyChanged(nameof(DraftInvalidUrlCount));
        OnPropertyChanged(nameof(DraftSummary));
    }

    private void SubscribeToDraftChanges(BatchSiteDraftRow row)
    {
        row.PropertyChanged -= OnDraftRowChanged;
        row.PropertyChanged += OnDraftRowChanged;
    }

    private void UnsubscribeFromDraftChanges(BatchSiteDraftRow row)
    {
        row.PropertyChanged -= OnDraftRowChanged;
    }

    private void OnDraftRowsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is not null)
        {
            foreach (BatchSiteDraftRow row in e.NewItems)
            {
                SubscribeToDraftChanges(row);
            }
        }

        if (e.OldItems is not null)
        {
            foreach (BatchSiteDraftRow row in e.OldItems)
            {
                UnsubscribeFromDraftChanges(row);
            }
        }

        RefreshDraftState();
    }

    private void OnDraftRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is BatchSiteDraftRow row &&
            (string.Equals(e.PropertyName, nameof(BatchSiteDraftRow.BaseUrl), StringComparison.Ordinal) ||
             string.Equals(e.PropertyName, nameof(BatchSiteDraftRow.ApiKey), StringComparison.Ordinal)) &&
            row.HasContent &&
            (string.IsNullOrWhiteSpace(row.Model) || row.AvailableModels.Count == 0))
        {
            var rows = DraftRows.ToList();
            var index = rows.IndexOf(row);
            if (index > 0)
            {
                PreserveDraftModelState(row, FindPreviousDraftMeaningfulRow(rows, index - 1));
            }
        }

        RefreshDraftState();
    }

    private bool TryApplySingleDraftClipboardValue(string clipboardText)
    {
        var singleValue = NormalizeSingleDraftClipboardValue(clipboardText);
        if (singleValue is null)
        {
            return false;
        }

        var classified = ClassifyClipboardValue(singleValue);
        var draftRows = DraftRows.Select(static row => row.Duplicate()).ToList();
        if (draftRows.Count == 0)
        {
            draftRows.Add(new BatchSiteDraftRow());
        }

        var missingFieldIndex = FindFirstDraftRowIndex(
            draftRows,
            row => row.HasContent && IsDraftFieldMissing(row, classified.Kind));

        if (missingFieldIndex >= 0)
        {
            ApplyClassifiedValue(draftRows[missingFieldIndex], classified);
            ReplaceDraftRows(draftRows);
            DraftStatusText = $"已识别为单个 {GetClipboardFieldDisplayName(classified.Kind)}，已补到第 {missingFieldIndex + 1} 行。";
            return true;
        }

        var emptyRowIndex = FindFirstDraftRowIndex(draftRows, static row => !row.HasContent);
        var previousRow = FindPreviousDraftMeaningfulRow(draftRows, emptyRowIndex >= 0 ? emptyRowIndex - 1 : draftRows.Count - 1);
        if (emptyRowIndex >= 0)
        {
            draftRows[emptyRowIndex] = BuildNextDraftRow(previousRow, draftRows[emptyRowIndex], classified);
            ReplaceDraftRows(draftRows);
            DraftStatusText = previousRow is null
                ? $"已识别为单个 {GetClipboardFieldDisplayName(classified.Kind)}，已写入第 {emptyRowIndex + 1} 行。"
                : $"已识别为单个 {GetClipboardFieldDisplayName(classified.Kind)}，第 {emptyRowIndex + 1} 行已沿用上一行其他内容。";
            return true;
        }

        if (draftRows.Count >= MaxDraftRows)
        {
            DraftStatusText = $"粘贴后会超过 {MaxDraftRows} 行，请先删除不需要的行。";
            return true;
        }

        draftRows.Add(BuildNextDraftRow(previousRow, null, classified));
        ReplaceDraftRows(draftRows);
        DraftStatusText = previousRow is null
            ? $"已识别为单个 {GetClipboardFieldDisplayName(classified.Kind)}，已新增第 {draftRows.Count} 行。"
            : $"已识别为单个 {GetClipboardFieldDisplayName(classified.Kind)}，已新增第 {draftRows.Count} 行并沿用上一行其他内容。";
        return true;
    }

    private static string? NormalizeSingleDraftClipboardValue(string? clipboardText)
    {
        var normalized = NormalizeNullable(
            (clipboardText ?? string.Empty)
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace('\r', '\n'));

        if (string.IsNullOrWhiteSpace(normalized) ||
            normalized.Contains('\n') ||
            normalized.Contains('\t') ||
            normalized.Contains('|'))
        {
            return null;
        }

        return normalized;
    }

    private static IReadOnlyList<BatchSiteDraftRow> ParseDraftRows(string rawText)
    {
        var lines = EnumerateContentLines(rawText).ToList();
        if (lines.Count == 0)
        {
            return [];
        }

        if (TryParseKeyValueBlock(lines, out var blockRow))
        {
            return [blockRow];
        }

        List<BatchSiteDraftRow> rows = [];
        foreach (var line in lines)
        {
            var columns = SplitColumns(line);
            var row = BuildDraftRowFromColumns(columns);
            if (row.HasContent)
            {
                rows.Add(row);
            }
        }

        return rows;
    }

    private static bool TryParseKeyValueBlock(IReadOnlyList<string> lines, out BatchSiteDraftRow row)
    {
        row = new BatchSiteDraftRow();
        var recognized = 0;
        foreach (var line in lines)
        {
            if (!TrySplitKeyValueLabel(line, out var label, out var value))
            {
                if (TryClassifyUnlabeledClipboardValue(line, out var classified))
                {
                    ApplyClassifiedValue(row, classified);
                    recognized++;
                }

                continue;
            }

            var kind = ClassifyLabel(label);
            if (kind == ClipboardFieldKind.Unknown)
            {
                continue;
            }

            ApplyClassifiedValue(row, new ClassifiedClipboardValue(kind, value));
            recognized++;
        }

        return recognized > 0 && row.HasContent;
    }

    private static BatchSiteDraftRow BuildDraftRowFromColumns(string[] columns)
    {
        var row = new BatchSiteDraftRow();
        if (columns.Length == 0)
        {
            return row;
        }

        if (columns.Length is 1)
        {
            ApplyClassifiedValue(row, ClassifyClipboardValue(columns[0]));
            return row;
        }

        var urlIndex = Array.FindIndex(columns, LooksLikeUrl);
        if (urlIndex >= 0)
        {
            row.BaseUrl = columns[urlIndex];
            if (urlIndex > 0)
            {
                row.Name = columns[0];
            }

            var remaining = columns
                .Where((_, index) => index != urlIndex && index != 0)
                .ToArray();
            foreach (var value in remaining)
            {
                ApplyClassifiedValue(row, ClassifyClipboardValue(value));
            }

            return row;
        }

        foreach (var value in columns)
        {
            ApplyClassifiedValue(row, ClassifyClipboardValue(value));
        }

        return row;
    }

    private static IReadOnlyList<BatchSiteDraftRow> MergeDraftRows(
        IEnumerable<BatchSiteDraftRow> existingRows,
        IReadOnlyList<BatchSiteDraftRow> pastedRows)
    {
        var merged = existingRows
            .Where(static row => row.HasContent)
            .Select(static row => row.Duplicate())
            .ToList();

        foreach (var row in pastedRows)
        {
            var pasted = row.Duplicate();
            PreserveDraftModelState(pasted, FindDraftModelStateSource(merged, pasted));
            merged.Add(pasted);
        }

        if (merged.Count == 0)
        {
            merged.Add(new BatchSiteDraftRow());
        }

        return merged;
    }

    private static BatchSiteDraftRow? FindDraftModelStateSource(
        IReadOnlyList<BatchSiteDraftRow> existingRows,
        BatchSiteDraftRow pastedRow)
    {
        var pastedUrl = BatchEndpointText.NormalizeBaseUrl(pastedRow.BaseUrl);
        if (pastedUrl is not null)
        {
            var matched = existingRows.LastOrDefault(row =>
            {
                var existingUrl = BatchEndpointText.NormalizeBaseUrl(row.BaseUrl);
                return string.Equals(existingUrl, pastedUrl, StringComparison.OrdinalIgnoreCase);
            });

            if (matched is not null)
            {
                return matched;
            }
        }

        return existingRows.LastOrDefault(static row =>
            !string.IsNullOrWhiteSpace(row.Model) || row.AvailableModels.Count > 0);
    }

    private static void PreserveDraftModelState(BatchSiteDraftRow target, BatchSiteDraftRow? source)
    {
        if (source is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(target.Model) && !string.IsNullOrWhiteSpace(source.Model))
        {
            target.Model = source.Model;
        }

        if (target.AvailableModels.Count == 0 && source.AvailableModels.Count > 0)
        {
            foreach (var model in source.AvailableModels)
            {
                target.AvailableModels.Add(model);
            }
        }

        if ((string.IsNullOrWhiteSpace(target.ModelCatalogSummary) ||
             string.Equals(target.ModelCatalogSummary, "未拉取模型", StringComparison.Ordinal)) &&
            !string.IsNullOrWhiteSpace(source.ModelCatalogSummary))
        {
            target.ModelCatalogSummary = source.ModelCatalogSummary;
        }

        if ((string.IsNullOrWhiteSpace(target.ProtocolSummary) ||
             string.Equals(target.ProtocolSummary, "未探测", StringComparison.Ordinal)) &&
            !string.IsNullOrWhiteSpace(source.ProtocolSummary))
        {
            target.ProtocolSummary = source.ProtocolSummary;
        }
    }

}
