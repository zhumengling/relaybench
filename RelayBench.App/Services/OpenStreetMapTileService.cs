using System.IO;
using System.Net.Http;
using System.Windows.Media.Imaging;
using RelayBench.App.Infrastructure;

namespace RelayBench.App.Services;

public sealed class OpenStreetMapTileService
{
    private static readonly HttpClient HttpClient = CreateHttpClient();
    private static readonly TimeSpan CacheTtl = TimeSpan.FromDays(7);
    private readonly string _tileRootDirectory;

    public OpenStreetMapTileService()
    {
        _tileRootDirectory = RelayBenchPaths.MapTilesDirectory;
        Directory.CreateDirectory(_tileRootDirectory);
    }

    public async Task<BitmapSource?> GetTileAsync(int zoom, int x, int y, CancellationToken cancellationToken = default)
    {
        var tileCount = 1 << zoom;
        if (y < 0 || y >= tileCount)
        {
            return null;
        }

        var wrappedX = ((x % tileCount) + tileCount) % tileCount;
        var tilePath = GetTilePath(zoom, wrappedX, y);

        if (File.Exists(tilePath) && DateTimeOffset.UtcNow - File.GetLastWriteTimeUtc(tilePath) < CacheTtl)
        {
            return LoadBitmap(tilePath);
        }

        try
        {
            var bytes = await HttpClient.GetByteArrayAsync($"https://tile.openstreetmap.org/{zoom}/{wrappedX}/{y}.png", cancellationToken);
            Directory.CreateDirectory(Path.GetDirectoryName(tilePath)!);
            await File.WriteAllBytesAsync(tilePath, bytes, cancellationToken);
            return LoadBitmap(bytes);
        }
        catch
        {
            return File.Exists(tilePath) ? LoadBitmap(tilePath) : null;
        }
    }

    private string GetTilePath(int zoom, int x, int y)
        => Path.Combine(_tileRootDirectory, zoom.ToString(), x.ToString(), $"{y}.png");

    private static BitmapSource LoadBitmap(string path)
    {
        var bytes = File.ReadAllBytes(path);
        return LoadBitmap(bytes);
    }

    private static BitmapSource LoadBitmap(byte[] bytes)
    {
        using MemoryStream stream = new(bytes);
        BitmapImage bitmap = new();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(10)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("RelayBenchSuite/0.2 (Windows desktop diagnostics)");
        return client;
    }
}
