using System.IO;
using System.Net.Http;
using RelayBench.Services.Infrastructure;
using SkiaSharp;

namespace RelayBench.WinUI.Services;

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

    public async Task<SKBitmap?> GetTileAsync(int zoom, int x, int y, CancellationToken cancellationToken = default)
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
            var bytes = await HttpClient.GetByteArrayAsync(
                $"https://tile.openstreetmap.org/{zoom}/{wrappedX}/{y}.png",
                cancellationToken).ConfigureAwait(false);
            Directory.CreateDirectory(Path.GetDirectoryName(tilePath)!);
            await File.WriteAllBytesAsync(tilePath, bytes, cancellationToken).ConfigureAwait(false);
            return LoadBitmap(bytes);
        }
        catch
        {
            return File.Exists(tilePath) ? LoadBitmap(tilePath) : null;
        }
    }

    private string GetTilePath(int zoom, int x, int y)
        => Path.Combine(_tileRootDirectory, zoom.ToString(), x.ToString(), $"{y}.png");

    private static SKBitmap? LoadBitmap(string path)
    {
        try
        {
            return SKBitmap.Decode(path);
        }
        catch
        {
            return null;
        }
    }

    private static SKBitmap? LoadBitmap(byte[] bytes)
    {
        try
        {
            return SKBitmap.Decode(bytes);
        }
        catch
        {
            return null;
        }
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(10)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("RelayBenchSuite/0.2 (WinUI desktop diagnostics)");
        return client;
    }
}
