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
    private IReadOnlyList<BatchSiteEntry> BuildCommittedSitesFromDraft()
    {
        var sourceRows = DraftRows.Where(static row => row.HasContent).ToArray();
        if (sourceRows.Length == 0)
        {
            throw new InvalidOperationException("请先在草稿表里至少填写一行入口。");
        }

        List<BatchSiteEntry> normalizedRows = [];
        string? previousName = null;
        string? previousBaseUrl = null;
        string? previousApiKey = null;
        string? previousModel = null;

        for (var index = 0; index < sourceRows.Length; index++)
        {
            var row = sourceRows[index];
            var name = FirstNonEmpty(row.Name, previousName);
            var baseUrl = FirstNonEmpty(row.BaseUrl, previousBaseUrl);
            var apiKey = FirstNonEmpty(row.ApiKey, previousApiKey);
            var model = FirstNonEmpty(row.Model, previousModel);

            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                throw new InvalidOperationException($"第 {index + 1} 行缺少接口地址。");
            }

            var normalizedBaseUrl = BatchEndpointText.NormalizeBaseUrl(baseUrl);
            if (normalizedBaseUrl is null)
            {
                throw new InvalidOperationException($"第 {index + 1} 行接口地址无效：{baseUrl}");
            }

            var entry = new BatchSiteEntry(
                normalizedBaseUrl,
                apiKey ?? string.Empty,
                model ?? string.Empty,
                isIncluded: row.IsIncluded,
                name: name ?? BuildDefaultName(normalizedBaseUrl, index + 1));
            entry.ModelCatalogSummary = row.ModelCatalogSummary;
            entry.ProtocolSummary = string.IsNullOrWhiteSpace(row.ProtocolSummary) ? "未探测" : row.ProtocolSummary;
            foreach (var availableModel in row.AvailableModels)
            {
                entry.AvailableModels.Add(availableModel);
            }

            normalizedRows.Add(entry);

            previousName = name;
            previousBaseUrl = normalizedBaseUrl;
            previousApiKey = apiKey;
            previousModel = model;
        }

        var groupName = FirstNonEmpty(EditingGroupName, SharedGroupName, normalizedRows[0].Name, TryGetHost(normalizedRows[0].BaseUrl))
                        ?? $"Site {GroupCount + 1}";
        foreach (var row in normalizedRows)
        {
            row.GroupName = groupName;
        }

        return normalizedRows;
    }

    private static bool IsDraftFieldMissing(BatchSiteDraftRow row, ClipboardFieldKind kind)
        => kind switch
        {
            ClipboardFieldKind.Url => string.IsNullOrWhiteSpace(row.BaseUrl),
            ClipboardFieldKind.ApiKey => string.IsNullOrWhiteSpace(row.ApiKey),
            ClipboardFieldKind.Model => string.IsNullOrWhiteSpace(row.Model),
            ClipboardFieldKind.Name => string.IsNullOrWhiteSpace(row.Name),
            _ => false
        };

    private static int FindFirstDraftRowIndex(IReadOnlyList<BatchSiteDraftRow> rows, Func<BatchSiteDraftRow, bool> predicate)
    {
        for (var index = 0; index < rows.Count; index++)
        {
            if (predicate(rows[index]))
            {
                return index;
            }
        }

        return -1;
    }

    private static BatchSiteDraftRow? FindPreviousDraftMeaningfulRow(IReadOnlyList<BatchSiteDraftRow> rows, int startIndex)
    {
        for (var index = Math.Min(startIndex, rows.Count - 1); index >= 0; index--)
        {
            if (rows[index].HasContent)
            {
                return rows[index];
            }
        }

        return null;
    }

    private static BatchSiteDraftRow BuildNextDraftRow(
        BatchSiteDraftRow? previousRow,
        BatchSiteDraftRow? targetRow,
        ClassifiedClipboardValue classified)
    {
        var row = new BatchSiteDraftRow
        {
            Name = FirstNonEmpty(targetRow?.Name, previousRow?.Name) ?? string.Empty,
            BaseUrl = FirstNonEmpty(targetRow?.BaseUrl, previousRow?.BaseUrl) ?? string.Empty,
            ApiKey = FirstNonEmpty(targetRow?.ApiKey, previousRow?.ApiKey) ?? string.Empty,
            Model = FirstNonEmpty(targetRow?.Model, previousRow?.Model) ?? string.Empty,
            IsIncluded = targetRow?.IsIncluded ?? previousRow?.IsIncluded ?? true,
            ModelCatalogSummary = FirstNonEmpty(targetRow?.ModelCatalogSummary, previousRow?.ModelCatalogSummary) ?? "未拉取模型"
        };
        PreserveDraftModelState(row, targetRow?.AvailableModels.Count > 0 ? targetRow : previousRow);
        ApplyClassifiedValue(row, classified);
        return row;
    }

    private static void ApplyClassifiedValue(BatchSiteDraftRow row, ClassifiedClipboardValue classified)
    {
        var value = NormalizeNullable(classified.Value) ?? string.Empty;
        switch (classified.Kind)
        {
            case ClipboardFieldKind.Url:
                row.BaseUrl = value.Trim().TrimEnd('/');
                if (string.IsNullOrWhiteSpace(row.Name))
                {
                    row.Name = TryGetHost(value) ?? string.Empty;
                }
                break;
            case ClipboardFieldKind.ApiKey:
                row.ApiKey = value;
                break;
            case ClipboardFieldKind.Model:
                row.Model = value;
                break;
            case ClipboardFieldKind.Name:
                row.Name = value;
                break;
        }
    }

    private static ClassifiedClipboardValue ClassifyClipboardValue(string rawValue)
    {
        var value = NormalizeNullable(rawValue) ?? string.Empty;
        if (TrySplitKeyValueLabel(value, out var label, out var labeledValue))
        {
            var labelKind = ClassifyLabel(label);
            if (labelKind != ClipboardFieldKind.Unknown)
            {
                return new ClassifiedClipboardValue(labelKind, labeledValue);
            }
        }

        if (LooksLikeUrl(value))
        {
            return new ClassifiedClipboardValue(ClipboardFieldKind.Url, value);
        }

        if (LooksLikeModel(value))
        {
            return new ClassifiedClipboardValue(ClipboardFieldKind.Model, value);
        }

        return LooksLikeApiKey(value)
            ? new ClassifiedClipboardValue(ClipboardFieldKind.ApiKey, value)
            : new ClassifiedClipboardValue(ClipboardFieldKind.ApiKey, value);
    }

    private static bool TryClassifyUnlabeledClipboardValue(string rawValue, out ClassifiedClipboardValue classified)
    {
        var value = NormalizeNullable(rawValue) ?? string.Empty;
        if (LooksLikeUrl(value))
        {
            classified = new ClassifiedClipboardValue(ClipboardFieldKind.Url, value);
            return true;
        }

        if (LooksLikeModel(value))
        {
            classified = new ClassifiedClipboardValue(ClipboardFieldKind.Model, value);
            return true;
        }

        if (LooksLikeApiKey(value))
        {
            classified = new ClassifiedClipboardValue(ClipboardFieldKind.ApiKey, value);
            return true;
        }

        classified = default;
        return false;
    }

    private static bool TrySplitKeyValueLabel(string rawValue, out string label, out string value)
    {
        label = string.Empty;
        value = string.Empty;
        var text = NormalizeNullable(rawValue);
        if (text is null)
        {
            return false;
        }

        var separators = new[] { ':', '：', '=' };
        var index = text.IndexOfAny(separators);
        if (index <= 0 || index >= text.Length - 1)
        {
            return false;
        }

        label = text[..index].Trim();
        value = text[(index + 1)..].Trim().Trim('"', '\'', '`');
        return !string.IsNullOrWhiteSpace(label) && !string.IsNullOrWhiteSpace(value);
    }

    private static ClipboardFieldKind ClassifyLabel(string label)
    {
        var normalized = label.Trim().ToLowerInvariant().Replace("_", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal);
        return normalized switch
        {
            "url" or "baseurl" or "apiurl" or "endpoint" or "接口" or "接口地址" or "地址" or "网址" => ClipboardFieldKind.Url,
            "key" or "apikey" or "token" or "authorization" or "auth" or "密钥" or "秘钥" => ClipboardFieldKind.ApiKey,
            "model" or "models" or "模型" or "模型名" or "模型名称" => ClipboardFieldKind.Model,
            "name" or "site" or "Site" or "名称" or "名字" => ClipboardFieldKind.Name,
            _ => ClipboardFieldKind.Unknown
        };
    }

    private static bool LooksLikeApiKey(string value)
    {
        var text = value.Trim();
        if (text.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var lower = text.ToLowerInvariant();
        return lower.StartsWith("sk-", StringComparison.Ordinal) ||
               lower.StartsWith("sk_", StringComparison.Ordinal) ||
               lower.StartsWith("ak-", StringComparison.Ordinal) ||
               lower.StartsWith("rk-", StringComparison.Ordinal) ||
               lower.StartsWith("pk-", StringComparison.Ordinal) ||
               lower.StartsWith("key-", StringComparison.Ordinal) ||
               (text.Length >= 24 && !text.Any(char.IsWhiteSpace));
    }

    private static bool LooksLikeModel(string value)
    {
        var text = value.Trim();
        if (string.IsNullOrWhiteSpace(text) || LooksLikeUrl(text))
        {
            return false;
        }

        var lower = text.ToLowerInvariant();
        string[] keywords =
        [
            "gpt", "claude", "gemini", "qwen", "deepseek", "kimi", "mimo",
            "moonshot", "mistral", "llama", "mixtral", "sonnet", "opus", "haiku",
            "glm", "chatglm", "yi-", "baichuan", "ernie", "doubao", "hunyuan",
            "minimax", "grok", "turbo", "embedding", "embed", "rerank", "whisper",
            "tts", "dall-e", "flux", "sdxl", "o1", "o3", "o4"
        ];

        if (keywords.Any(keyword => lower.Contains(keyword, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return text.Contains('/') && text.Length <= 128 && !text.Any(char.IsWhiteSpace);
    }

    private static string GetClipboardFieldDisplayName(ClipboardFieldKind kind)
        => kind switch
        {
            ClipboardFieldKind.Url => "URL",
            ClipboardFieldKind.ApiKey => "Key",
            ClipboardFieldKind.Model => "模型",
            ClipboardFieldKind.Name => "名称",
            _ => "内容"
        };

    partial void OnImportTextChanged(string value) => RefreshImportPreview();
    partial void OnImportModeChanged(string value)
    {
        OnPropertyChanged(nameof(IsSharedImport));
        RefreshImportPreview();
    }
    partial void OnSharedGroupNameChanged(string value) => RefreshImportPreview();
    partial void OnSharedApiKeyChanged(string value) => RefreshImportPreview();
    partial void OnSharedModelChanged(string value) => RefreshImportPreview();
    partial void OnSelectedSiteChanged(BatchSiteEntry? value) => OnPropertyChanged(nameof(SelectedSiteSummary));

    private void SubscribeToChanges(BatchSiteEntry entry)
    {
        entry.PropertyChanged -= OnSiteEntryChanged;
        entry.PropertyChanged += OnSiteEntryChanged;
    }

    private void UnsubscribeFromChanges(BatchSiteEntry entry)
    {
        entry.PropertyChanged -= OnSiteEntryChanged;
    }

    private void OnSitesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is not null)
        {
            foreach (BatchSiteEntry entry in e.NewItems)
            {
                SubscribeToChanges(entry);
            }
        }

        if (e.OldItems is not null)
        {
            foreach (BatchSiteEntry entry in e.OldItems)
            {
                UnsubscribeFromChanges(entry);
            }
        }

        MarkDirty();
        RefreshEditorState();
    }

    private void OnSiteEntryChanged(object? sender, PropertyChangedEventArgs e)
    {
        MarkDirty();
        RefreshEditorState();
    }

    private void MarkDirty()
    {
        _isDirty = true;
        // Reset and start the debounce timer
        _autoSaveTimer?.Stop();
        _autoSaveTimer?.Start();
    }

    /// <summary>
    /// Saves the current site list to JSON on disk.
    /// </summary>
    internal void SaveToDisk()
    {
        if (!_isDirty) return;
        _isDirty = false;

        try
        {
            var data = Sites.Select(s => new BatchSiteEntryDto
            {
                Name = s.Name,
                BaseUrl = s.BaseUrl,
                ApiKey = s.ApiKey,
                Model = s.Model,
                AvailableModels = s.AvailableModels.ToList(),
                ModelCatalogSummary = s.ModelCatalogSummary,
                ProtocolSummary = s.ProtocolSummary,
                Timeout = s.Timeout,
                TlsIgnore = s.TlsIgnore,
                IsIncluded = s.IsIncluded,
                GroupName = s.GroupName,
            }).ToList();

            var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
            Directory.CreateDirectory(Path.GetDirectoryName(SavePath)!);
            File.WriteAllText(SavePath, json);
        }
        catch
        {
            // Silently ignore save failures (e.g., disk full, permissions)
        }
    }

    /// <summary>
    /// Loads site entries from the JSON file on disk.
    /// </summary>
    private void LoadFromDisk()
    {
        try
        {
            if (!File.Exists(SavePath)) return;

            var json = File.ReadAllText(SavePath);
            var data = JsonSerializer.Deserialize<List<BatchSiteEntryDto>>(json);
            if (data is null) return;

            foreach (var dto in data)
            {
                var entry = new BatchSiteEntry(
                    dto.BaseUrl ?? string.Empty,
                    dto.ApiKey ?? string.Empty,
                    dto.Model ?? string.Empty,
                    dto.Timeout,
                    dto.TlsIgnore,
                    dto.IsIncluded,
                    dto.GroupName ?? string.Empty,
                    dto.Name ?? string.Empty);
                entry.ModelCatalogSummary = string.IsNullOrWhiteSpace(dto.ModelCatalogSummary)
                    ? "未拉取模型"
                    : dto.ModelCatalogSummary;
                entry.ProtocolSummary = string.IsNullOrWhiteSpace(dto.ProtocolSummary)
                    ? "未探测"
                    : dto.ProtocolSummary;
                foreach (var model in dto.AvailableModels ?? [])
                {
                    if (!string.IsNullOrWhiteSpace(model) &&
                        !entry.AvailableModels.Contains(model, StringComparer.OrdinalIgnoreCase))
                    {
                        entry.AvailableModels.Add(model);
                    }
                }

                SubscribeToChanges(entry);
                Sites.Add(entry);
            }
        }
        catch
        {
            // Silently ignore load failures
        }
    }

    private void RefreshEditorState()
    {
        RefreshSiteGroups();
        OnPropertyChanged(nameof(TotalSiteCount));
        OnPropertyChanged(nameof(IncludedCount));
        OnPropertyChanged(nameof(ReadyIncludedCount));
        OnPropertyChanged(nameof(MissingUrlCount));
        OnPropertyChanged(nameof(MissingKeyCount));
        OnPropertyChanged(nameof(GroupCount));
        OnPropertyChanged(nameof(EditorSummary));
        OnPropertyChanged(nameof(EditorHealthSummary));
        OnPropertyChanged(nameof(SelectedSiteSummary));
    }

    private void RefreshSiteGroups()
    {
        SiteGroups.Clear();
        var selectedGroupName = NormalizeNullable(EditingGroupName);
        if (Sites.Count == 0)
        {
            SiteGroups.Add(new BatchSiteGroupSummary(
                "暂无入口组",
                "先新增一行或批量粘贴接口",
                "0 Site",
                "启用 0",
                "缺项 0",
                "未填写接口地址",
                "模型 0"));
            return;
        }

        foreach (var group in Sites.GroupBy(ResolveSiteGroupName, StringComparer.OrdinalIgnoreCase))
        {
            var rows = group.ToArray();
            var included = rows.Count(static row => row.IsIncluded);
            var ready = rows.Count(static row => row.IsIncluded && IsRunnableSite(row));
            var missing = rows.Count(static row => row.IsIncluded && (!LooksLikeUrl(row.BaseUrl) || string.IsNullOrWhiteSpace(row.ApiKey)));
            var first = rows.FirstOrDefault(static row => !string.IsNullOrWhiteSpace(row.BaseUrl))?.BaseUrl ?? "未填写接口地址";
            var model = rows.FirstOrDefault(static row => !string.IsNullOrWhiteSpace(row.Model))?.Model ?? "未指定模型";
            var protocol = BuildGroupProtocolSummary(rows);

            SiteGroups.Add(new BatchSiteGroupSummary(
                group.Key,
                $"{ready}/{included} 可测试",
                $"{rows.Length} Site",
                $"启用 {included}",
                $"缺项 {missing}",
                first,
                model,
                protocol,
                !string.IsNullOrWhiteSpace(selectedGroupName) &&
                string.Equals(group.Key, selectedGroupName, StringComparison.OrdinalIgnoreCase)));
        }
    }

    private static string BuildGroupProtocolSummary(IReadOnlyList<BatchSiteEntry> rows)
    {
        var summaries = rows
            .Select(static row => NormalizeNullable(row.ProtocolSummary))
            .Where(static value => !string.IsNullOrWhiteSpace(value) &&
                                   !string.Equals(value, "未探测", StringComparison.Ordinal))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return summaries.Length == 0 ? "未探测" : string.Join(" / ", summaries.Take(2));
    }

    private static bool IsRunnableSite(BatchSiteEntry site)
        => LooksLikeUrl(site.BaseUrl) && !string.IsNullOrWhiteSpace(site.ApiKey);

    /// <summary>
    /// DTO for JSON serialization of site entries.
    /// </summary>
    private sealed class BatchSiteEntryDto
    {
        public string? Name { get; set; }
        public string? BaseUrl { get; set; }
        public string? ApiKey { get; set; }
        public string? Model { get; set; }
        public List<string>? AvailableModels { get; set; }
        public string? ModelCatalogSummary { get; set; }
        public string? ProtocolSummary { get; set; }
        public int Timeout { get; set; } = 30;
        public bool TlsIgnore { get; set; }
        public bool IsIncluded { get; set; } = true;
        public string? GroupName { get; set; }
    }

    private readonly record struct IndependentImportContext(
        string? Name,
        string? BaseUrl,
        string? ApiKey,
        string? Model);

    private sealed record IndependentImportDraftRow(
        string? Name,
        string? BaseUrl,
        string? ApiKey,
        string? Model,
        bool HasName,
        bool HasBaseUrl,
        bool HasApiKey,
        bool HasModel);

    private readonly record struct ClassifiedClipboardValue(ClipboardFieldKind Kind, string Value);

    private enum ClipboardFieldKind
    {
        Unknown,
        Url,
        ApiKey,
        Model,
        Name
    }
}
