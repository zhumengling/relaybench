using NetTest.Core.Models;
using NetTest.Core.Support;

namespace NetTest.Core.Services;

public sealed class ChatGptTraceService
{
    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(8)
    };

    private readonly OpenAiSupportedRegionCatalog _regionCatalog = new();

    public async Task<ChatGptTraceResult> RunAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await HttpClient.GetAsync("https://chatgpt.com/cdn-cgi/trace", cancellationToken);
            response.EnsureSuccessStatusCode();

            var rawTrace = await response.Content.ReadAsStringAsync(cancellationToken);
            var values = TraceDocumentParser.Parse(rawTrace);
            values.TryGetValue("ip", out var publicIp);
            values.TryGetValue("loc", out var locationCode);
            values.TryGetValue("colo", out var colo);

            var isSupportedRegion = _regionCatalog.IsSupported(locationCode);
            var locationName = _regionCatalog.TryGetRegionName(locationCode);
            var supportSummary = locationCode is null
                ? "Trace 未返回 loc 字段，因此暂时无法明确判断地区可用性。"
                : isSupportedRegion
                    ? $"{locationName ?? locationCode}（{locationCode}）在内置支持地区列表中。"
                    : $"{locationName ?? locationCode}（{locationCode}）不在内置支持地区列表中。";

            return new ChatGptTraceResult(
                DateTimeOffset.Now,
                rawTrace,
                values,
                publicIp,
                locationCode,
                locationName,
                colo,
                isSupportedRegion,
                supportSummary,
                null);
        }
        catch (Exception ex)
        {
            return new ChatGptTraceResult(
                DateTimeOffset.Now,
                string.Empty,
                new Dictionary<string, string>(),
                null,
                null,
                null,
                null,
                false,
                "Trace 请求失败，无法判断当前解锁地区。",
                ex.Message);
        }
    }
}
