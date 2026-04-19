using System.IO;
using System.Text.Json;

namespace NetTest.App.Infrastructure;

public sealed class ProxyTrendStore
{
    private const int MaxEntries = 1200;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _filePath;
    private readonly string _dataDirectory;

    public ProxyTrendStore()
    {
        _dataDirectory = NetTestPaths.DataDirectory;
        Directory.CreateDirectory(_dataDirectory);
        _filePath = NetTestPaths.ProxyTrendsPath;
    }

    public IReadOnlyList<ProxyTrendEntry> GetRecentEntries(string baseUrl, int limit = 20)
        => GetEntries(baseUrl)
            .OrderByDescending(entry => entry.Timestamp)
            .Take(Math.Max(1, limit))
            .ToArray();

    public IReadOnlyList<ProxyTrendEntry> GetEntries(string baseUrl)
    {
        var normalized = NormalizeBaseUrl(baseUrl);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return Array.Empty<ProxyTrendEntry>();
        }

        return LoadAll()
            .Where(entry => string.Equals(NormalizeBaseUrl(entry.BaseUrl), normalized, StringComparison.OrdinalIgnoreCase))
            .OrderBy(entry => entry.Timestamp)
            .ToArray();
    }

    public IReadOnlyList<ProxyTrendEntry> GetEntriesSince(string baseUrl, DateTimeOffset sinceInclusive, int? limit = null)
    {
        var filtered = GetEntries(baseUrl)
            .Where(entry => entry.Timestamp >= sinceInclusive)
            .OrderBy(entry => entry.Timestamp);

        if (limit is int boundedLimit && boundedLimit > 0)
        {
            return filtered
                .TakeLast(boundedLimit)
                .ToArray();
        }

        return filtered.ToArray();
    }

    public void Append(ProxyTrendEntry entry)
        => AppendRange([entry]);

    public void AppendRange(IEnumerable<ProxyTrendEntry> entries)
    {
        EnsureDataDirectory();
        var allEntries = LoadAll().ToList();
        allEntries.AddRange(entries);

        if (allEntries.Count > MaxEntries)
        {
            allEntries = allEntries
                .OrderByDescending(entry => entry.Timestamp)
                .Take(MaxEntries)
                .OrderBy(entry => entry.Timestamp)
                .ToList();
        }

        File.WriteAllText(_filePath, JsonSerializer.Serialize(allEntries, SerializerOptions));
    }

    private IReadOnlyList<ProxyTrendEntry> LoadAll()
    {
        try
        {
            EnsureDataDirectory();
            if (!File.Exists(_filePath))
            {
                return Array.Empty<ProxyTrendEntry>();
            }

            var json = File.ReadAllText(_filePath);
            return JsonSerializer.Deserialize<List<ProxyTrendEntry>>(json, SerializerOptions) ?? [];
        }
        catch
        {
            return Array.Empty<ProxyTrendEntry>();
        }
    }

    private void EnsureDataDirectory()
        => Directory.CreateDirectory(_dataDirectory);

    public static string NormalizeBaseUrl(string? baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return string.Empty;
        }

        if (!Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out var uri))
        {
            return baseUrl.Trim();
        }

        var builder = new UriBuilder(uri)
        {
            Host = uri.Host.ToLowerInvariant(),
            Path = uri.AbsolutePath.TrimEnd('/')
        };

        return builder.Uri.ToString().TrimEnd('/');
    }
}
