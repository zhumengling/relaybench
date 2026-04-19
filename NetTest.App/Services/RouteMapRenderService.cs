using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NetTest.Core.Models;

namespace NetTest.App.Services;

public sealed class RouteMapRenderService
{
    private const int MapWidth = 960;
    private const int MapHeight = 420;
    private const int MapPadding = 52;

    private readonly GeoIpLookupService _geoIpLookupService = new();
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
                "没有采集到 hop 数据，暂时无法绘制地理路径图。",
                "暂无 hop 地理定位结果。",
                null,
                null);
        }

        var routableHops = orderedHops
            .Where(hop => !string.IsNullOrWhiteSpace(hop.Address))
            .ToArray();

        var inlineGeoHops = routableHops
            .Select(TryBuildInlineRouteGeoHop)
            .Where(static hop => hop is not null)
            .Cast<RouteGeoHopResult>()
            .ToArray();
        var inlineGeoLookup = inlineGeoHops.ToDictionary(hop => hop.Address, StringComparer.OrdinalIgnoreCase);

        var addressesToLookup = routableHops
            .Where(hop => !inlineGeoLookup.ContainsKey(hop.Address!))
            .Select(hop => hop.Address!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var remoteGeoHops = addressesToLookup.Length == 0
            ? Array.Empty<RouteGeoHopResult>()
            : await _geoIpLookupService.LookupRouteHopsAsync(addressesToLookup, progress, cancellationToken);
        var remoteGeoLookup = remoteGeoHops.ToDictionary(hop => hop.Address, StringComparer.OrdinalIgnoreCase);

        Dictionary<int, RouteGeoHopResult> hopGeoLookup = new();
        foreach (var hop in routableHops)
        {
            if (inlineGeoLookup.TryGetValue(hop.Address!, out var inlineGeoHop))
            {
                hopGeoLookup[hop.HopNumber] = inlineGeoHop with { HopNumber = hop.HopNumber };
                continue;
            }

            if (remoteGeoLookup.TryGetValue(hop.Address!, out var remoteGeoHop))
            {
                hopGeoLookup[hop.HopNumber] = remoteGeoHop with { HopNumber = hop.HopNumber };
            }
        }

        var originGeo = await _geoIpLookupService.LookupCurrentPublicOriginAsync(progress, cancellationToken);
        var routeMapPoints = BuildRouteMapPoints(originGeo, orderedHops, hopGeoLookup);
        if (routeMapPoints.Count == 0)
        {
            return new RouteMapRenderResult(
                false,
                "路由检测已完成，但没有可用于绘制地图的定位点。",
                "当前 hop 中缺少可定位的公网坐标；如前几跳为私网 / CGNAT，软件只能在后续获取到公网 hop 后继续连线。",
                null,
                null);
        }

        progress?.Report("正在使用 OpenStreetMap 底图绘制路由地图...");

        var zoom = PickZoom(routeMapPoints);
        var projectedPoints = routeMapPoints
            .Select(point => new ProjectedHop(point, ProjectToPixel(point.Latitude, point.Longitude, zoom)))
            .ToArray();

        var minX = projectedPoints.Min(item => item.Pixel.X);
        var maxX = projectedPoints.Max(item => item.Pixel.X);
        var minY = projectedPoints.Min(item => item.Pixel.Y);
        var maxY = projectedPoints.Max(item => item.Pixel.Y);
        var offsetX = ((MapWidth - (maxX - minX)) / 2d) - minX;
        var offsetY = ((MapHeight - (maxY - minY)) / 2d) - minY;
        var viewportMinX = -offsetX;
        var viewportMinY = -offsetY;

        DrawingVisual visual = new();
        using (var context = visual.RenderOpen())
        {
            context.DrawRectangle(new SolidColorBrush(Color.FromRgb(231, 238, 245)), null, new Rect(0, 0, MapWidth, MapHeight));
            await DrawTilesAsync(context, zoom, viewportMinX, viewportMinY, cancellationToken);
            DrawRouteOverlay(context, projectedPoints, offsetX, offsetY);
            DrawAttribution(context);
        }

        RenderTargetBitmap bitmap = new(MapWidth, MapHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();

        var exactPointCount = routeMapPoints.Count(static point => !point.IsApproximated);
        var approximatedPointCount = routeMapPoints.Count(static point => point.IsApproximated);
        var inlineGeoHopCount = hopGeoLookup.Values.Count(IsInlineTraceMetadataHop);
        var countryCount = routeMapPoints
            .Select(point => point.CountryCode)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        var asnCount = routeMapPoints
            .Where(static point => point.Asn is not null)
            .Select(static point => point.Asn)
            .Distinct()
            .Count();

        var originSummary = originGeo is null
            ? "未能获取当前公网出口位置，路径从首个已定位 hop 开始绘制。"
            : $"已从当前公网出口位置 {FormatPointLocation(originGeo.City, originGeo.Region, originGeo.Country)} [{originGeo.Address}] 开始绘制。";

        var summary =
            $"已绘制 {routeMapPoints.Count} 个路径点（精确定位 {exactPointCount} 个，位置推测 {approximatedPointCount} 个），覆盖 {orderedHops.Length} 个 hop。\n" +
            $"{originSummary}\n" +
            $"其中 {inlineGeoHopCount} 个 hop 直接使用追踪返回的内联地理信息，其余按需回退到 Geo-IP 查询。\n" +
            $"地图缩放级别 {zoom}，覆盖国家数 {countryCount}，不同 ASN 数 {asnCount}。\n" +
            "红线按 hop 顺序持续连线；私网 / 无响应 hop 若缺少真实坐标，会按前后已定位节点做近似补点。";

        var geoSummary = string.Join(Environment.NewLine, routeMapPoints.Select(static point => point.Summary));
        return new RouteMapRenderResult(true, summary, geoSummary, bitmap, null);
    }

    private static IReadOnlyList<RouteMapPoint> BuildRouteMapPoints(
        RouteGeoHopResult? originGeo,
        IReadOnlyList<RouteHopResult> orderedHops,
        IReadOnlyDictionary<int, RouteGeoHopResult> hopGeoLookup)
    {
        List<RouteMapPointSeed> seeds = [];
        if (originGeo is not null)
        {
            seeds.Add(new RouteMapPointSeed(
                Sequence: 0,
                HopNumber: null,
                Address: originGeo.Address,
                DisplayLabel: $"当前位置 - {FormatPointLocation(originGeo.City, originGeo.Region, originGeo.Country)}",
                NetworkLabel: originGeo.NetworkLabel,
                CountryCode: originGeo.CountryCode,
                Asn: originGeo.Asn,
                Latitude: originGeo.Latitude,
                Longitude: originGeo.Longitude,
                IsOrigin: true));
        }

        foreach (var hop in orderedHops)
        {
            hopGeoLookup.TryGetValue(hop.HopNumber, out var geoHop);
            var hasExactCoordinates = geoHop is not null;
            var displayAddress = string.IsNullOrWhiteSpace(hop.Address) ? "*" : hop.Address!;
            var locationText = hasExactCoordinates
                ? FormatPointLocation(geoHop!.City, geoHop.Region, geoHop.Country)
                : displayAddress;
            var networkLabel = hasExactCoordinates
                ? geoHop!.NetworkLabel
                : hop.HasTraceMetadata
                    ? hop.NetworkLabel
                    : "位置未知";

            seeds.Add(new RouteMapPointSeed(
                Sequence: hop.HopNumber,
                HopNumber: hop.HopNumber,
                Address: displayAddress,
                DisplayLabel: $"第 {hop.HopNumber} 跳 - {locationText}",
                NetworkLabel: networkLabel,
                CountryCode: geoHop?.CountryCode,
                Asn: geoHop?.Asn,
                Latitude: geoHop?.Latitude,
                Longitude: geoHop?.Longitude,
                IsOrigin: false));
        }

        if (seeds.Count == 0 || seeds.All(static seed => !seed.HasCoordinates))
        {
            return Array.Empty<RouteMapPoint>();
        }

        List<RouteMapPoint> points = [];
        for (var index = 0; index < seeds.Count; index++)
        {
            var seed = seeds[index];
            if (seed.HasCoordinates)
            {
                points.Add(new RouteMapPoint(
                    seed.Sequence,
                    seed.HopNumber,
                    seed.Address,
                    seed.DisplayLabel,
                    seed.NetworkLabel,
                    seed.CountryCode,
                    seed.Asn,
                    seed.Latitude!.Value,
                    seed.Longitude!.Value,
                    seed.IsOrigin,
                    IsApproximated: false,
                    Summary: BuildPointSummary(
                        seed.IsOrigin ? "当前位置" : $"第 {seed.HopNumber} 跳",
                        seed.Address,
                        "已定位",
                        seed.NetworkLabel,
                        seed.Latitude.Value,
                        seed.Longitude.Value)));
                continue;
            }

            if (!TryInferCoordinates(seeds, index, out var latitude, out var longitude))
            {
                continue;
            }

            var prefix = seed.IsOrigin ? "当前位置" : $"第 {seed.HopNumber} 跳";
            points.Add(new RouteMapPoint(
                seed.Sequence,
                seed.HopNumber,
                seed.Address,
                seed.DisplayLabel + "（位置推测）",
                seed.NetworkLabel,
                seed.CountryCode,
                seed.Asn,
                latitude,
                longitude,
                seed.IsOrigin,
                IsApproximated: true,
                Summary: BuildPointSummary(
                    prefix,
                    seed.Address,
                    "位置推测（依据前后已定位节点插值）",
                    seed.NetworkLabel,
                    latitude,
                    longitude)));
        }

        return points
            .OrderBy(static point => point.Sequence)
            .ToArray();
    }

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

    private static string BuildPointSummary(
        string prefix,
        string address,
        string locationText,
        string networkLabel,
        double latitude,
        double longitude)
        => $"{prefix} [{address}]  位置={locationText}  网络归属={networkLabel}  纬度={latitude:F4}  经度={longitude:F4}";

    private static string FormatPointLocation(string? city, string? region, string? country)
    {
        var location = string.Join(
            ", ",
            new[] { city, region, country }
                .Where(static value => !string.IsNullOrWhiteSpace(value)));

        return string.IsNullOrWhiteSpace(location) ? "位置未知" : location;
    }

    private static RouteGeoHopResult? TryBuildInlineRouteGeoHop(RouteHopResult hop)
    {
        if (string.IsNullOrWhiteSpace(hop.Address) || !hop.HasCoordinates)
        {
            return null;
        }

        return new RouteGeoHopResult(
            hop.HopNumber,
            hop.Address,
            hop.City,
            hop.Region,
            hop.Country,
            TryNormalizeCountryCode(hop.Country),
            null,
            null,
            TryParseAutonomousSystemNumber(hop.AutonomousSystem),
            hop.Organization,
            hop.Organization,
            hop.Hostname,
            "内联路由元信息",
            hop.Latitude!.Value,
            hop.Longitude!.Value);
    }

    private static bool IsInlineTraceMetadataHop(RouteGeoHopResult hop)
        => string.Equals(hop.NetworkRole, "内联路由元信息", StringComparison.OrdinalIgnoreCase);

    private static int? TryParseAutonomousSystemNumber(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim();
        if (normalized.StartsWith("AS", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[2..];
        }

        return int.TryParse(normalized, out var asn) ? asn : null;
    }

    private static string? TryNormalizeCountryCode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Length is 2 or 3 ? trimmed.ToUpperInvariant() : null;
    }

    private static int PickZoom(IReadOnlyList<RouteMapPoint> points)
    {
        for (var zoom = 5; zoom >= 1; zoom--)
        {
            var projected = points.Select(point => ProjectToPixel(point.Latitude, point.Longitude, zoom)).ToArray();
            var width = projected.Max(point => point.X) - projected.Min(point => point.X);
            var height = projected.Max(point => point.Y) - projected.Min(point => point.Y);

            if (width <= MapWidth - (MapPadding * 2) && height <= MapHeight - (MapPadding * 2))
            {
                return zoom;
            }
        }

        return 1;
    }

    private async Task DrawTilesAsync(
        DrawingContext context,
        int zoom,
        double viewportMinX,
        double viewportMinY,
        CancellationToken cancellationToken)
    {
        var startTileX = (int)Math.Floor(viewportMinX / 256d);
        var endTileX = (int)Math.Floor((viewportMinX + MapWidth - 1) / 256d);
        var startTileY = (int)Math.Floor(viewportMinY / 256d);
        var endTileY = (int)Math.Floor((viewportMinY + MapHeight - 1) / 256d);

        for (var tileY = startTileY; tileY <= endTileY; tileY++)
        {
            for (var tileX = startTileX; tileX <= endTileX; tileX++)
            {
                var tile = await _tileService.GetTileAsync(zoom, tileX, tileY, cancellationToken);
                var destination = new Rect(
                    tileX * 256d - viewportMinX,
                    tileY * 256d - viewportMinY,
                    256d,
                    256d);

                if (tile is not null)
                {
                    context.DrawImage(tile, destination);
                }
                else
                {
                    context.DrawRectangle(new SolidColorBrush(Color.FromRgb(211, 221, 231)), new Pen(Brushes.White, 1), destination);
                }
            }
        }
    }

    private static void DrawRouteOverlay(DrawingContext context, IReadOnlyList<ProjectedHop> points, double offsetX, double offsetY)
    {
        if (points.Count == 0)
        {
            return;
        }

        if (points.Count > 1)
        {
            var pathGeometry = new StreamGeometry();
            using (var geometryContext = pathGeometry.Open())
            {
                geometryContext.BeginFigure(Translate(points[0].Pixel, offsetX, offsetY), false, false);
                geometryContext.PolyLineTo(points.Skip(1).Select(item => Translate(item.Pixel, offsetX, offsetY)).ToList(), true, true);
            }

            pathGeometry.Freeze();
            var shadowPen = new Pen(new SolidColorBrush(Color.FromArgb(140, 0, 0, 0)), 6)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round,
                LineJoin = PenLineJoin.Round
            };
            var routePen = new Pen(new SolidColorBrush(Color.FromArgb(228, 232, 59, 70)), 4)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round,
                LineJoin = PenLineJoin.Round
            };
            shadowPen.Freeze();
            routePen.Freeze();

            context.DrawGeometry(null, shadowPen, pathGeometry);
            context.DrawGeometry(null, routePen, pathGeometry);
        }

        for (var index = 0; index < points.Count; index++)
        {
            var point = points[index];
            var location = Translate(point.Pixel, offsetX, offsetY);
            var fill = point.Hop.IsOrigin
                ? new SolidColorBrush(Color.FromRgb(220, 53, 69))
                : point.Hop.IsApproximated
                    ? new SolidColorBrush(Color.FromRgb(255, 193, 7))
                    : new SolidColorBrush(Color.FromRgb(255, 165, 0));
            var radius = point.Hop.IsOrigin ? 8.5 : point.Hop.IsApproximated ? 6.5 : 7.5;
            context.DrawEllipse(fill, new Pen(Brushes.White, 2), location, radius, radius);

            if (ShouldDrawMarkerLabel(points, index))
            {
                DrawMarkerLabel(context, point.Hop.DisplayLabel, location, point.Hop.IsApproximated);
            }
        }
    }

    private static bool ShouldDrawMarkerLabel(IReadOnlyList<ProjectedHop> points, int index)
    {
        if (points.Count <= 8)
        {
            return true;
        }

        if (index == 0 || index == points.Count - 1)
        {
            return true;
        }

        return points[index].Hop.IsOrigin || !points[index].Hop.IsApproximated;
    }

    private static void DrawMarkerLabel(DrawingContext context, string label, Point anchor, bool isApproximated)
    {
        var text = new FormattedText(
            label,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface("Segoe UI"),
            12,
            Brushes.White,
            1.0);

        var backgroundBrush = isApproximated
            ? new SolidColorBrush(Color.FromArgb(225, 82, 60, 15))
            : new SolidColorBrush(Color.FromArgb(215, 7, 17, 31));
        var background = new Rect(anchor.X + 10, anchor.Y - 12, text.Width + 12, text.Height + 6);
        context.DrawRoundedRectangle(backgroundBrush, null, background, 6, 6);
        context.DrawText(text, new Point(background.X + 6, background.Y + 3));
    }

    private static void DrawAttribution(DrawingContext context)
    {
        var text = new FormattedText(
            "地图底图：OpenStreetMap 贡献者",
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface("Segoe UI"),
            11,
            Brushes.White,
            1.0);

        var x = MapWidth - text.Width - 14;
        var y = MapHeight - text.Height - 10;
        var background = new Rect(x - 6, y - 3, text.Width + 12, text.Height + 6);
        context.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(185, 7, 17, 31)), null, background, 4, 4);
        context.DrawText(text, new Point(x, y));
    }

    private static Point ProjectToPixel(double latitude, double longitude, int zoom)
    {
        var sinLatitude = Math.Sin(latitude * Math.PI / 180d);
        var worldSize = 256d * Math.Pow(2, zoom);
        var x = ((longitude + 180d) / 360d) * worldSize;
        var y = (0.5d - Math.Log((1d + sinLatitude) / (1d - sinLatitude)) / (4d * Math.PI)) * worldSize;
        return new Point(x, y);
    }

    private static Point Translate(Point point, double offsetX, double offsetY)
        => new(point.X + offsetX, point.Y + offsetY);

    private sealed record ProjectedHop(RouteMapPoint Hop, Point Pixel);

    private sealed record RouteMapPointSeed(
        int Sequence,
        int? HopNumber,
        string Address,
        string DisplayLabel,
        string NetworkLabel,
        string? CountryCode,
        int? Asn,
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
        string? CountryCode,
        int? Asn,
        double Latitude,
        double Longitude,
        bool IsOrigin,
        bool IsApproximated,
        string Summary);
}
