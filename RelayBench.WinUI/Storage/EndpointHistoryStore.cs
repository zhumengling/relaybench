using System.Text;
using System.Text.Json;

namespace RelayBench.WinUI.Storage;

/// <summary>
/// Represents a single endpoint history entry.
/// </summary>
public sealed record EndpointHistoryItem(
    string BaseUrl,
    string ApiKey,
    string Model,
    DateTime UsedAt,
    List<string>? Models = null)
{
    public DateTime FirstSeenAt { get; init; } = UsedAt;

    public DateTime LastUsedAt { get; init; } = UsedAt;

    public int UseCount { get; init; } = 1;
}

/// <summary>
/// Persists recent endpoint configurations to a JSON file.
/// Max 80 entries, newest first. Full API keys are stored for restoration.
/// </summary>
public sealed class EndpointHistoryStore
{
    private const int MaxEntries = 80;
    private static readonly string s_filePath = Path.Combine(StoragePaths.Root, "endpoint-history.json");

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static readonly SemaphoreSlim s_mutex = new(1, 1);

    /// <summary>
    /// Loads all history entries from disk. Returns empty list if file is missing or corrupt.
    /// </summary>
    public async Task<List<EndpointHistoryItem>> LoadAsync(CancellationToken ct = default)
    {
        if (!File.Exists(s_filePath))
            return [];

        await s_mutex.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var json = await ReadAllTextSharedAsync(s_filePath, ct).ConfigureAwait(false);
            var items = JsonSerializer.Deserialize<List<EndpointHistoryItem>>(json, s_jsonOptions);
            return NormalizeItems(items ?? []).ToList();
        }
        catch
        {
            return [];
        }
        finally
        {
            s_mutex.Release();
        }
    }

    /// <summary>
    /// Records an endpoint usage. Deduplicates by BaseUrl+ApiKey+Model, keeps max 80 entries.
    /// </summary>
    public async Task RecordAsync(string baseUrl, string apiKey, string model, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
            return;

        await s_mutex.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var items = await LoadInternalAsync(ct);
            Upsert(items, baseUrl, apiKey, model, models: null);
            await SaveInternalAsync(items, ct);
        }
        finally
        {
            s_mutex.Release();
        }
    }

    /// <summary>
    /// Records an endpoint usage with the list of fetched models.
    /// </summary>
    public async Task RecordWithModelsAsync(string baseUrl, string apiKey, string model, List<string> models, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
            return;

        await s_mutex.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var items = await LoadInternalAsync(ct);
            Upsert(items, baseUrl, apiKey, model, models);
            await SaveInternalAsync(items, ct);
        }
        finally
        {
            s_mutex.Release();
        }
    }

    /// <summary>
    /// Clears all history entries.
    /// </summary>
    public async Task ClearAsync(CancellationToken ct = default)
    {
        await s_mutex.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (File.Exists(s_filePath))
                File.Delete(s_filePath);
        }
        finally
        {
            s_mutex.Release();
        }
    }

    /// <summary>
    /// Masks an API key for safe storage. Shows first 4 and last 4 chars.
    /// </summary>
    public static string MaskApiKey(string apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            return "";
        if (apiKey.Length <= 8)
            return new string('*', apiKey.Length);
        return $"{apiKey[..4]}...{apiKey[^4..]}";
    }

    private async Task<List<EndpointHistoryItem>> LoadInternalAsync(CancellationToken ct)
    {
        if (!File.Exists(s_filePath))
            return [];

        try
        {
            var json = await ReadAllTextSharedAsync(s_filePath, ct).ConfigureAwait(false);
            var items = JsonSerializer.Deserialize<List<EndpointHistoryItem>>(json, s_jsonOptions);
            return NormalizeItems(items ?? []).ToList();
        }
        catch
        {
            return [];
        }
    }

    private static async Task SaveInternalAsync(List<EndpointHistoryItem> items, CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(s_filePath);
        if (dir is not null)
            Directory.CreateDirectory(dir);

        var normalized = NormalizeItems(items).ToList();
        items.Clear();
        items.AddRange(normalized);

        var json = JsonSerializer.Serialize(items, s_jsonOptions);
        await WriteAllTextWithRetryAsync(s_filePath, json, ct).ConfigureAwait(false);
    }

    private static void Upsert(
        List<EndpointHistoryItem> items,
        string baseUrl,
        string apiKey,
        string model,
        List<string>? models)
    {
        var normalizedBaseUrl = NormalizeBaseUrl(baseUrl);
        if (string.IsNullOrWhiteSpace(normalizedBaseUrl))
            return;

        var normalizedApiKey = (apiKey ?? string.Empty).Trim();
        var normalizedModel = (model ?? string.Empty).Trim();
        var existing = items.FirstOrDefault(item =>
            string.Equals(item.BaseUrl, normalizedBaseUrl, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(item.ApiKey, normalizedApiKey, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(item.Model, normalizedModel, StringComparison.OrdinalIgnoreCase));

        var now = DateTime.UtcNow;
        var firstSeenAt = existing?.FirstSeenAt ?? now;
        var useCount = Math.Max(0, existing?.UseCount ?? 0) + 1;
        var updated = new EndpointHistoryItem(
            normalizedBaseUrl,
            normalizedApiKey,
            normalizedModel,
            now,
            NormalizeModelList(models ?? existing?.Models))
        {
            FirstSeenAt = NormalizeTimestamp(firstSeenAt, now),
            LastUsedAt = now,
            UseCount = useCount
        };

        if (existing is not null)
            items.Remove(existing);

        items.Insert(0, updated);
    }

    private static IEnumerable<EndpointHistoryItem> NormalizeItems(IEnumerable<EndpointHistoryItem> items)
    {
        var now = DateTime.UtcNow;
        return items
            .Select(item => NormalizeItem(item, now))
            .Where(static item => !string.IsNullOrWhiteSpace(item.BaseUrl))
            .GroupBy(
                static item => BuildHistoryKey(item.BaseUrl, item.ApiKey, item.Model),
                StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.OrderByDescending(item => item.LastUsedAt).First())
            .OrderByDescending(static item => item.LastUsedAt)
            .Take(MaxEntries);
    }

    private static EndpointHistoryItem NormalizeItem(EndpointHistoryItem item, DateTime now)
    {
        var usedAt = NormalizeTimestamp(item.UsedAt, now);
        var lastUsedAt = NormalizeTimestamp(item.LastUsedAt, usedAt);
        var firstSeenAt = NormalizeTimestamp(item.FirstSeenAt, lastUsedAt);
        if (firstSeenAt > lastUsedAt)
            firstSeenAt = lastUsedAt;

        return item with
        {
            BaseUrl = NormalizeBaseUrl(item.BaseUrl),
            ApiKey = (item.ApiKey ?? string.Empty).Trim(),
            Model = (item.Model ?? string.Empty).Trim(),
            Models = NormalizeModelList(item.Models),
            UsedAt = lastUsedAt,
            FirstSeenAt = firstSeenAt,
            LastUsedAt = lastUsedAt,
            UseCount = Math.Max(1, item.UseCount)
        };
    }

    private static string NormalizeBaseUrl(string? value)
        => (value ?? string.Empty).Trim().TrimEnd('/');

    private static List<string>? NormalizeModelList(IEnumerable<string>? models)
    {
        var normalized = models?
            .Select(static model => model?.Trim() ?? string.Empty)
            .Where(static model => model.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return normalized is { Count: > 0 } ? normalized : null;
    }

    private static DateTime NormalizeTimestamp(DateTime value, DateTime fallback)
    {
        if (value == default)
            return fallback;

        return value.Kind == DateTimeKind.Utc
            ? value
            : value.ToUniversalTime();
    }

    private static string BuildHistoryKey(string baseUrl, string apiKey, string model)
        => string.Join("|", baseUrl.Trim().TrimEnd('/'), apiKey.Trim(), model.Trim());

    private static async Task<string> ReadAllTextSharedAsync(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(
            path,
            new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.ReadWrite | FileShare.Delete,
                Options = FileOptions.Asynchronous
            });
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return await reader.ReadToEndAsync(ct).ConfigureAwait(false);
    }

    private static async Task WriteAllTextWithRetryAsync(string path, string contents, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await using var stream = new FileStream(
                    path,
                    new FileStreamOptions
                    {
                        Mode = FileMode.Create,
                        Access = FileAccess.Write,
                        Share = FileShare.ReadWrite | FileShare.Delete,
                        Options = FileOptions.Asynchronous
                    });
                await using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                await writer.WriteAsync(contents.AsMemory(), ct).ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (attempt < 6 && ex is IOException or UnauthorizedAccessException)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50 * attempt), ct).ConfigureAwait(false);
            }
        }
    }
}
