using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using RelayBench.Core.Models;
using RelayBench.Core.Services;
using RelayBench.Services.Infrastructure;
using SkiaSharp;

namespace RelayBench.WinUI.Services;

public sealed class RouteMapRenderService
{
    private const int MapWidth = 960;
    private const int MapHeight = 420;
    private const int MapPadding = 52;

    private static readonly HttpClient OriginClient = CreateOriginClient();
    private readonly GeoIpLookupService _geoIpLookupService = new(RelayBenchPaths.GeoIpCachePath);
    private readonly OpenStreetMapTileService _tileService = new();

    public async Task<RouteMapRenderResult> RenderAsync(
        RouteDiagnosticsResult routeResult,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var orderedHops = routeResult.Hops
            .OrderBy(static hop => hop.HopNumber)
            .ToArray();
        if (orderedHops.Length == 0)
        {
            return new RouteMapRenderResult(
                false,
                "\u6ca1\u6709\u91c7\u96c6\u5230 hop \u6570\u636e\uff0c\u6682\u65f6\u65e0\u6cd5\u7ed8\u5236\u5730\u7406\u8def\u5f84\u56fe\u3002",
                "\u6682\u65e0 hop \u5730\u7406\u5b9a\u4f4d\u7ed3\u679c\u3002",
                null,
                null);
        }

        progress?.Report("\u6b63\u5728\u89e3\u6790\u8def\u7531 hop \u5750\u6807...");
        var geoHops = await BuildGeoHopLookupAsync(orderedHops, cancellationToken).ConfigureAwait(false);
        var originGeo = await LookupCurrentPublicOriginAsync(cancellationToken).ConfigureAwait(false);
        var routeMapPoints = BuildRouteMapPoints(originGeo, orderedHops, geoHops);
        if (routeMapPoints.Count == 0)
        {
            return new RouteMapRenderResult(
                false,
                "\u8def\u7531\u8ffd\u8e2a\u5df2\u5b8c\u6210\uff0c\u4f46\u6ca1\u6709\u53ef\u7528\u4e8e\u7ed8\u5236\u5730\u56fe\u7684\u5b9a\u4f4d\u70b9\u3002",
                "\u5f53\u524d hop \u7f3a\u5c11\u53ef\u5b9a\u4f4d\u7684\u516c\u7f51\u5750\u6807\uff0c\u5df2\u4fdd\u7559\u6587\u672c\u8def\u7531\u660e\u7ec6\u3002",
                null,
                null);
        }

        progress?.Report("\u6b63\u5728\u52a0\u8f7d OpenStreetMap \u74e6\u7247\u5e76\u7ed8\u5236\u8def\u5f84...");
        var zoom = PickZoom(routeMapPoints);
        var projectedPoints = routeMapPoints
            .Select(point => new ProjectedHop(point, ProjectToPixel(point.Latitude, point.Longitude, zoom)))
            .ToArray();

        var minX = projectedPoints.Min(static item => item.Pixel.X);
        var maxX = projectedPoints.Max(static item => item.Pixel.X);
        var minY = projectedPoints.Min(static item => item.Pixel.Y);
        var maxY = projectedPoints.Max(static item => item.Pixel.Y);
        var offsetX = ((MapWidth - (maxX - minX)) / 2d) - minX;
        var offsetY = ((MapHeight - (maxY - minY)) / 2d) - minY;
        var viewportMinX = -offsetX;
        var viewportMinY = -offsetY;

        var imagePath = BuildMapImagePath(routeResult.Target);
        Directory.CreateDirectory(Path.GetDirectoryName(imagePath)!);

        using var bitmap = new SKBitmap(MapWidth, MapHeight, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(new SKColor(231, 238, 245));
        await DrawTilesAsync(canvas, zoom, viewportMinX, viewportMinY, cancellationToken).ConfigureAwait(false);
        DrawRouteOverlay(canvas, projectedPoints, offsetX, offsetY);
        DrawAttribution(canvas);

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 92);
        await using (var stream = File.Open(imagePath, FileMode.Create, FileAccess.Write, FileShare.Read))
        {
            data.SaveTo(stream);
        }

        var exactPointCount = routeMapPoints.Count(static point => !point.IsApproximated);
        var approximatedPointCount = routeMapPoints.Count(static point => point.IsApproximated);
        var countryCount = routeMapPoints
            .Select(static point => point.Country)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        var asnCount = routeMapPoints
            .Select(static point => point.Asn)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

        var originSummary = originGeo is null
            ? "\u672a\u80fd\u83b7\u53d6\u5f53\u524d\u516c\u7f51\u51fa\u53e3\u4f4d\u7f6e\uff0c\u5730\u56fe\u4ece\u9996\u4e2a\u5df2\u5b9a\u4f4d hop \u5f00\u59cb\u7ed8\u5236\u3002"
            : $"\u5df2\u4ece\u5f53\u524d\u516c\u7f51\u51fa\u53e3 {FormatLocation(originGeo.City, originGeo.Region, originGeo.Country)} [{originGeo.Address}] \u5f00\u59cb\u7ed8\u5236\u3002";

        var summary =
            $"\u5df2\u7ed8\u5236 {routeMapPoints.Count} \u4e2a\u8def\u5f84\u70b9\uff08\u7cbe\u786e\u5b9a\u4f4d {exactPointCount} \u4e2a\uff0c\u4f4d\u7f6e\u63a8\u6d4b {approximatedPointCount} \u4e2a\uff09\uff0c\u8986\u76d6 {orderedHops.Length} \u4e2a hop\u3002\n" +
            $"{originSummary}\n" +
            $"\u5730\u56fe\u7f29\u653e\u7ea7\u522b {zoom}\uff0c\u8986\u76d6\u56fd\u5bb6/\u5730\u533a {countryCount}\uff0c\u4e0d\u540c ASN {asnCount}\u3002\n" +
            "\u7ea2\u7ebf\u6309 hop \u987a\u5e8f\u8fde\u63a5\uff1b\u79c1\u7f51\u6216\u65e0\u54cd\u5e94 hop \u5982\u7f3a\u5c11\u5750\u6807\uff0c\u4f1a\u6309\u524d\u540e\u5df2\u5b9a\u4f4d\u8282\u70b9\u505a\u8fd1\u4f3c\u8865\u70b9\u3002";

        var geoSummary = string.Join(Environment.NewLine, routeMapPoints.Select(static point => point.Summary));
        return new RouteMapRenderResult(true, summary, geoSummary, imagePath, null);
    }

    private async Task<Dictionary<int, RouteGeoPoint>> BuildGeoHopLookupAsync(
        IReadOnlyList<RouteHopResult> orderedHops,
        CancellationToken cancellationToken)
    {
        Dictionary<int, RouteGeoPoint> lookup = [];
        foreach (var hop in orderedHops)
        {
            if (TryBuildInlineGeoPoint(hop) is { } inlinePoint)
            {
                lookup[hop.HopNumber] = inlinePoint;
                continue;
            }

            if (string.IsNullOrWhiteSpace(hop.Address) || IsPrivateOrSpecialAddress(hop.Address))
            {
                continue;
            }

            var geo = await _geoIpLookupService.LookupAsync(hop.Address, cancellationToken).ConfigureAwait(false);
            if (geo is null || geo.Latitude == 0 || geo.Longitude == 0)
            {
                continue;
            }

            lookup[hop.HopNumber] = new RouteGeoPoint(
                hop.Address,
                geo.City,
                null,
                geo.Country,
                geo.Asn,
                geo.Organization,
                geo.Latitude,
                geo.Longitude);
        }

        return lookup;
    }

    private static IReadOnlyList<RouteMapPoint> BuildRouteMapPoints(
        RouteGeoPoint? originGeo,
        IReadOnlyList<RouteHopResult> orderedHops,
        IReadOnlyDictionary<int, RouteGeoPoint> hopGeoLookup)
    {
        List<RouteMapPointSeed> seeds = [];
        if (originGeo is not null)
        {
            seeds.Add(new RouteMapPointSeed(
                0,
                null,
                originGeo.Address,
                $"\u5f53\u524d\u4f4d\u7f6e - {FormatLocation(originGeo.City, originGeo.Region, originGeo.Country)}",
                BuildNetworkLabel(originGeo.Asn, originGeo.Organization, null),
                originGeo.Country,
                originGeo.Asn,
                originGeo.Latitude,
                originGeo.Longitude,
                true));
        }

        foreach (var hop in orderedHops)
        {
            hopGeoLookup.TryGetValue(hop.HopNumber, out var geoHop);
            var displayAddress = string.IsNullOrWhiteSpace(hop.Address) ? "*" : hop.Address!;
            var locationText = geoHop is null
                ? displayAddress
                : FormatLocation(geoHop.City, geoHop.Region, geoHop.Country);
            var networkLabel = geoHop is null
                ? hop.NetworkLabel
                : BuildNetworkLabel(geoHop.Asn, geoHop.Organization, hop.Hostname);

            seeds.Add(new RouteMapPointSeed(
                hop.HopNumber,
                hop.HopNumber,
                displayAddress,
                $"Hop {hop.HopNumber} - {locationText}",
                string.IsNullOrWhiteSpace(networkLabel) ? "--" : networkLabel,
                geoHop?.Country ?? hop.Country,
                geoHop?.Asn ?? hop.AutonomousSystem,
                geoHop?.Latitude,
                geoHop?.Longitude,
                false));
        }

        if (seeds.Count == 0 || seeds.All(static seed => !seed.HasCoordinates))
        {
            return [];
        }

        List<RouteMapPoint> points = [];
        for (var index = 0; index < seeds.Count; index++)
        {
            var seed = seeds[index];
            if (seed.HasCoordinates)
            {
                points.Add(BuildMapPoint(seed, seed.Latitude!.Value, seed.Longitude!.Value, false));
                continue;
            }

            if (TryInferCoordinates(seeds, index, out var latitude, out var longitude))
            {
                points.Add(BuildMapPoint(seed, latitude, longitude, true));
            }
        }

        return points.OrderBy(static point => point.Sequence).ToArray();
    }

    private static RouteMapPoint BuildMapPoint(RouteMapPointSeed seed, double latitude, double longitude, bool approximated)
    {
        var prefix = seed.IsOrigin ? "\u5f53\u524d\u4f4d\u7f6e" : $"Hop {seed.HopNumber}";
        var locationText = approximated
            ? "\u4f4d\u7f6e\u63a8\u6d4b"
            : "\u5df2\u5b9a\u4f4d";
        return new RouteMapPoint(
            seed.Sequence,
            seed.HopNumber,
            seed.Address,
            approximated ? $"{seed.DisplayLabel} \u00b7 \u4f4d\u7f6e\u63a8\u6d4b" : seed.DisplayLabel,
            seed.NetworkLabel,
            seed.Country,
            seed.Asn,
            latitude,
            longitude,
            seed.IsOrigin,
            approximated,
            $"{prefix} [{seed.Address}]  {locationText}  {seed.NetworkLabel}  lat={latitude:F4} lon={longitude:F4}");
    }

    private async Task<RouteGeoPoint?> LookupCurrentPublicOriginAsync(CancellationToken cancellationToken)
    {
        try
        {
            var address = (await OriginClient.GetStringAsync("https://api.ipify.org", cancellationToken).ConfigureAwait(false)).Trim();
            if (string.IsNullOrWhiteSpace(address) || IsPrivateOrSpecialAddress(address))
            {
                return null;
            }

            var geo = await _geoIpLookupService.LookupAsync(address, cancellationToken).ConfigureAwait(false);
            if (geo is null || geo.Latitude == 0 || geo.Longitude == 0)
            {
                return null;
            }

            return new RouteGeoPoint(address, geo.City, null, geo.Country, geo.Asn, geo.Organization, geo.Latitude, geo.Longitude);
        }
        catch
        {
            return null;
        }
    }

    private async Task DrawTilesAsync(
        SKCanvas canvas,
        int zoom,
        double viewportMinX,
        double viewportMinY,
        CancellationToken cancellationToken)
    {
        var startTileX = (int)Math.Floor(viewportMinX / 256d);
        var endTileX = (int)Math.Floor((viewportMinX + MapWidth - 1) / 256d);
        var startTileY = (int)Math.Floor(viewportMinY / 256d);
        var endTileY = (int)Math.Floor((viewportMinY + MapHeight - 1) / 256d);

        using var fallbackFill = new SKPaint { Color = new SKColor(211, 221, 231), IsAntialias = true };
        using var fallbackStroke = new SKPaint
        {
            Color = SKColors.White,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1,
            IsAntialias = true
        };

        for (var tileY = startTileY; tileY <= endTileY; tileY++)
        {
            for (var tileX = startTileX; tileX <= endTileX; tileX++)
            {
                var destination = new SKRect(
                    (float)(tileX * 256d - viewportMinX),
                    (float)(tileY * 256d - viewportMinY),
                    (float)(tileX * 256d - viewportMinX + 256d),
                    (float)(tileY * 256d - viewportMinY + 256d));
                using var tile = await _tileService.GetTileAsync(zoom, tileX, tileY, cancellationToken).ConfigureAwait(false);
                if (tile is null)
                {
                    canvas.DrawRect(destination, fallbackFill);
                    canvas.DrawRect(destination, fallbackStroke);
                    continue;
                }

                canvas.DrawBitmap(tile, destination);
            }
        }
    }

    private static void DrawRouteOverlay(SKCanvas canvas, IReadOnlyList<ProjectedHop> points, double offsetX, double offsetY)
    {
        if (points.Count == 0)
        {
            return;
        }

        if (points.Count > 1)
        {
            using SKPath path = new();
            var first = Translate(points[0].Pixel, offsetX, offsetY);
            path.MoveTo((float)first.X, (float)first.Y);
            foreach (var point in points.Skip(1))
            {
                var location = Translate(point.Pixel, offsetX, offsetY);
                path.LineTo((float)location.X, (float)location.Y);
            }

            using var shadowPaint = new SKPaint
            {
                Color = new SKColor(0, 0, 0, 140),
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 7,
                StrokeCap = SKStrokeCap.Round,
                StrokeJoin = SKStrokeJoin.Round
            };
            using var routePaint = new SKPaint
            {
                Color = new SKColor(232, 59, 70, 228),
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 4,
                StrokeCap = SKStrokeCap.Round,
                StrokeJoin = SKStrokeJoin.Round
            };
            canvas.DrawPath(path, shadowPaint);
            canvas.DrawPath(path, routePaint);
        }

        for (var index = 0; index < points.Count; index++)
        {
            var point = points[index];
            var location = Translate(point.Pixel, offsetX, offsetY);
            var fill = point.Hop.IsOrigin
                ? new SKColor(220, 53, 69)
                : point.Hop.IsApproximated
                    ? new SKColor(255, 193, 7)
                    : new SKColor(255, 165, 0);
            var radius = point.Hop.IsOrigin ? 8.5f : point.Hop.IsApproximated ? 6.5f : 7.5f;

            using var fillPaint = new SKPaint { Color = fill, IsAntialias = true };
            using var outlinePaint = new SKPaint
            {
                Color = SKColors.White,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 2,
                IsAntialias = true
            };
            canvas.DrawCircle((float)location.X, (float)location.Y, radius, fillPaint);
            canvas.DrawCircle((float)location.X, (float)location.Y, radius, outlinePaint);

            if (ShouldDrawMarkerLabel(points, index))
            {
                DrawMarkerLabel(canvas, point.Hop.DisplayLabel, location, point.Hop.IsApproximated);
            }
        }
    }

    private static bool ShouldDrawMarkerLabel(IReadOnlyList<ProjectedHop> points, int index)
    {
        if (points.Count <= 8)
        {
            return true;
        }

        return index == 0 || index == points.Count - 1 || points[index].Hop.IsOrigin || !points[index].Hop.IsApproximated;
    }

    private static void DrawMarkerLabel(SKCanvas canvas, string label, MapPixel anchor, bool approximated)
    {
        var text = TrimLabel(label, 34);
        using var typeface = SKTypeface.FromFamilyName("Segoe UI");
        using var font = new SKFont(typeface, 12);
        using var textPaint = new SKPaint
        {
            Color = SKColors.White,
            IsAntialias = true
        };
        var textWidth = font.MeasureText(text);
        var background = new SKRect(
            (float)anchor.X + 10,
            (float)anchor.Y - 17,
            (float)anchor.X + 10 + textWidth + 14,
            (float)anchor.Y + 6);
        using var backgroundPaint = new SKPaint
        {
            Color = approximated ? new SKColor(82, 60, 15, 225) : new SKColor(7, 17, 31, 215),
            IsAntialias = true
        };
        canvas.DrawRoundRect(background, 6, 6, backgroundPaint);
        canvas.DrawText(text, background.Left + 7, background.Top + 15, SKTextAlign.Left, font, textPaint);
    }

    private static void DrawAttribution(SKCanvas canvas)
    {
        const string text = "Map tiles: OpenStreetMap contributors";
        using var typeface = SKTypeface.FromFamilyName("Segoe UI");
        using var font = new SKFont(typeface, 11);
        using var textPaint = new SKPaint
        {
            Color = SKColors.White,
            IsAntialias = true
        };
        var textWidth = font.MeasureText(text);
        var x = MapWidth - textWidth - 14;
        var y = MapHeight - 11;
        var background = new SKRect(x - 6, y - 16, x + textWidth + 6, y + 5);
        using var backgroundPaint = new SKPaint
        {
            Color = new SKColor(7, 17, 31, 185),
            IsAntialias = true
        };
        canvas.DrawRoundRect(background, 4, 4, backgroundPaint);
        canvas.DrawText(text, x, y, SKTextAlign.Left, font, textPaint);
    }

    private static int PickZoom(IReadOnlyList<RouteMapPoint> points)
    {
        for (var zoom = 6; zoom >= 1; zoom--)
        {
            var projected = points.Select(point => ProjectToPixel(point.Latitude, point.Longitude, zoom)).ToArray();
            var width = projected.Max(static point => point.X) - projected.Min(static point => point.X);
            var height = projected.Max(static point => point.Y) - projected.Min(static point => point.Y);

            if (width <= MapWidth - (MapPadding * 2) && height <= MapHeight - (MapPadding * 2))
            {
                return zoom;
            }
        }

        return 1;
    }

    private static MapPixel ProjectToPixel(double latitude, double longitude, int zoom)
    {
        var clampedLatitude = Math.Clamp(latitude, -85.05112878d, 85.05112878d);
        var sinLatitude = Math.Sin(clampedLatitude * Math.PI / 180d);
        var worldSize = 256d * Math.Pow(2, zoom);
        var x = ((longitude + 180d) / 360d) * worldSize;
        var y = (0.5d - Math.Log((1d + sinLatitude) / (1d - sinLatitude)) / (4d * Math.PI)) * worldSize;
        return new MapPixel(x, y);
    }

    private static MapPixel Translate(MapPixel point, double offsetX, double offsetY)
        => new(point.X + offsetX, point.Y + offsetY);

    private static bool TryInferCoordinates(
        IReadOnlyList<RouteMapPointSeed> seeds,
        int index,
        out double latitude,
        out double longitude)
    {
        var previousIndex = -1;
        for (var i = index - 1; i >= 0; i--)
        {
            if (seeds[i].HasCoordinates)
            {
                previousIndex = i;
                break;
            }
        }

        var nextIndex = -1;
        for (var i = index + 1; i < seeds.Count; i++)
        {
            if (seeds[i].HasCoordinates)
            {
                nextIndex = i;
                break;
            }
        }

        if (previousIndex >= 0 && nextIndex >= 0)
        {
            var previous = seeds[previousIndex];
            var next = seeds[nextIndex];
            var ratio = (index - previousIndex) / (double)(nextIndex - previousIndex);
            latitude = Lerp(previous.Latitude!.Value, next.Latitude!.Value, ratio);
            longitude = Lerp(previous.Longitude!.Value, next.Longitude!.Value, ratio);
            return true;
        }

        if (previousIndex >= 0)
        {
            latitude = seeds[previousIndex].Latitude!.Value;
            longitude = seeds[previousIndex].Longitude!.Value;
            return true;
        }

        if (nextIndex >= 0)
        {
            latitude = seeds[nextIndex].Latitude!.Value;
            longitude = seeds[nextIndex].Longitude!.Value;
            return true;
        }

        latitude = default;
        longitude = default;
        return false;
    }

    private static double Lerp(double start, double end, double ratio)
        => start + ((end - start) * ratio);

    private static RouteGeoPoint? TryBuildInlineGeoPoint(RouteHopResult hop)
    {
        if (string.IsNullOrWhiteSpace(hop.Address) || !hop.HasCoordinates)
        {
            return null;
        }

        return new RouteGeoPoint(
            hop.Address,
            hop.City,
            hop.Region,
            hop.Country,
            hop.AutonomousSystem,
            hop.Organization,
            hop.Latitude!.Value,
            hop.Longitude!.Value);
    }

    private static string FormatLocation(string? city, string? region, string? country)
    {
        var location = string.Join(
            ", ",
            new[] { city, region, country }.Where(static value => !string.IsNullOrWhiteSpace(value)));
        return string.IsNullOrWhiteSpace(location) ? "\u4f4d\u7f6e\u672a\u77e5" : location;
    }

    private static string BuildNetworkLabel(string? asn, string? organization, string? hostname)
    {
        var parts = new[] { asn, organization, hostname }
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return parts.Length == 0 ? "--" : string.Join(" | ", parts);
    }

    private static string TrimLabel(string value, int maxLength)
        => value.Length <= maxLength ? value : string.Concat(value.AsSpan(0, Math.Max(0, maxLength - 1)), "...");

    private static bool IsPrivateOrSpecialAddress(string value)
    {
        if (!IPAddress.TryParse(value, out var address))
        {
            return false;
        }

        if (IPAddress.IsLoopback(address))
        {
            return true;
        }

        var bytes = address.GetAddressBytes();
        return address.AddressFamily switch
        {
            System.Net.Sockets.AddressFamily.InterNetwork => bytes[0] == 10 ||
                (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) ||
                (bytes[0] == 192 && bytes[1] == 168) ||
                (bytes[0] == 169 && bytes[1] == 254) ||
                bytes[0] == 127,
            System.Net.Sockets.AddressFamily.InterNetworkV6 => address.IsIPv6LinkLocal ||
                address.IsIPv6SiteLocal ||
                address.Equals(IPAddress.IPv6Loopback),
            _ => false
        };
    }

    private static string BuildMapImagePath(string target)
    {
        var safeTarget = string.Concat((string.IsNullOrWhiteSpace(target) ? "route" : target)
            .Select(static ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '-'));
        safeTarget = string.IsNullOrWhiteSpace(safeTarget) ? "route" : safeTarget;
        if (safeTarget.Length > 48)
        {
            safeTarget = safeTarget[..48];
        }

        var fileName = $"route-map-{DateTimeOffset.Now:yyyyMMdd-HHmmss}-{safeTarget}.png";
        return Path.Combine(RelayBenchPaths.DataDirectory, "route-maps", fileName);
    }

    private static HttpClient CreateOriginClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(5)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("RelayBenchSuite/0.2 (WinUI desktop diagnostics)");
        return client;
    }

    private sealed record RouteGeoPoint(
        string Address,
        string? City,
        string? Region,
        string? Country,
        string? Asn,
        string? Organization,
        double Latitude,
        double Longitude);

    private sealed record RouteMapPointSeed(
        int Sequence,
        int? HopNumber,
        string Address,
        string DisplayLabel,
        string NetworkLabel,
        string? Country,
        string? Asn,
        double? Latitude,
        double? Longitude,
        bool IsOrigin)
    {
        public bool HasCoordinates => Latitude.HasValue && Longitude.HasValue;
    }

    private sealed record RouteMapPoint(
        int Sequence,
        int? HopNumber,
        string Address,
        string DisplayLabel,
        string NetworkLabel,
        string? Country,
        string? Asn,
        double Latitude,
        double Longitude,
        bool IsOrigin,
        bool IsApproximated,
        string Summary);

    private sealed record ProjectedHop(RouteMapPoint Hop, MapPixel Pixel);

    private readonly record struct MapPixel(double X, double Y);
}
