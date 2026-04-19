using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace NetTest.Core.Support;

public sealed class OpenAiSupportedRegionCatalog
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly Lazy<CatalogData> _catalog;

    public OpenAiSupportedRegionCatalog()
    {
        _catalog = new Lazy<CatalogData>(LoadCatalog);
    }

    public string SourceUrl => _catalog.Value.SourceUrl;

    public string SnapshotDate => _catalog.Value.SnapshotDate;

    public bool IsSupported(string? countryCode)
    {
        if (string.IsNullOrWhiteSpace(countryCode))
        {
            return false;
        }

        return _catalog.Value.CountryCodes.Contains(countryCode.ToUpperInvariant());
    }

    public string? TryGetRegionName(string? countryCode)
    {
        if (string.IsNullOrWhiteSpace(countryCode))
        {
            return null;
        }

        try
        {
            return new RegionInfo(countryCode).DisplayName;
        }
        catch (ArgumentException)
        {
            return countryCode.ToUpperInvariant();
        }
    }

    private static CatalogData LoadCatalog()
    {
        var assembly = Assembly.GetExecutingAssembly();
        const string resourceName = "NetTest.Core.Resources.openai_supported_regions.json";

        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"缺少内嵌资源：{resourceName}");
        using var reader = new StreamReader(stream);
        var json = reader.ReadToEnd();
        var payload = JsonSerializer.Deserialize<CatalogPayload>(json, SerializerOptions)
            ?? throw new InvalidOperationException("无法解析内置的支持地区目录。");

        return new CatalogData(
            payload.SourceUrl,
            payload.SnapshotDate,
            new HashSet<string>(payload.SupportedCountryCodes, StringComparer.OrdinalIgnoreCase));
    }

    private sealed record CatalogPayload(string SourceUrl, string SnapshotDate, List<string> SupportedCountryCodes);

    private sealed record CatalogData(string SourceUrl, string SnapshotDate, HashSet<string> CountryCodes);
}
