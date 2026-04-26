using System.IO;

namespace RelayBench.App.Infrastructure;

public static class RelayBenchPaths
{
    private const string WorkspaceRootEnvironmentVariable = "RELAYBENCH_WORKSPACE_ROOT";
    private const string ProductDirectoryName = "RelayBench";
    private static readonly Lazy<string> ResolvedRootDirectory = new(ResolveRootDirectory);

    public static string RootDirectory => ResolvedRootDirectory.Value;

    public static string DataDirectory => EnsureDirectory(Path.Combine(RootDirectory, "data"));

    public static string ConfigDirectory => EnsureDirectory(Path.Combine(RootDirectory, "config"));

    public static string ReportsDirectory => EnsureDirectory(Path.Combine(DataDirectory, "reports"));

    public static string ExportsDirectory => EnsureDirectory(Path.Combine(DataDirectory, "exports"));

    public static string PortScanExportsDirectory => EnsureDirectory(Path.Combine(ExportsDirectory, "port-scan"));

    public static string MapTilesDirectory => EnsureDirectory(Path.Combine(DataDirectory, "map-tiles"));

    public static string AppStatePath => Path.Combine(DataDirectory, "app-state.json");

    public static string ProxyTrendsPath => Path.Combine(DataDirectory, "proxy-trends.json");

    public static string ProxyRelayConfigPath => Path.Combine(ConfigDirectory, "proxy-relay.json");

    public static string GeoIpCachePath => Path.Combine(DataDirectory, "geoip-cache.json");

    public static string StartupLogPath => Path.Combine(RootDirectory, "app-startup.log");

    private static string EnsureDirectory(string path)
    {
        Directory.CreateDirectory(path);
        return path;
    }

    private static string ResolveRootDirectory()
    {
        var configuredRoot = Environment.GetEnvironmentVariable(WorkspaceRootEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configuredRoot))
        {
            if (TryPrepareRootDirectory(configuredRoot, out var configuredRootDirectory))
            {
                return configuredRootDirectory;
            }

            return ResolveFallbackRootDirectory();
        }

        if (TryPrepareRootDirectory(AppContext.BaseDirectory, out var rootDirectory))
        {
            return rootDirectory;
        }

        return ResolveFallbackRootDirectory();
    }

    private static string ResolveFallbackRootDirectory()
    {
        if (TryPrepareRootDirectory(GetLocalAppDataRootDirectory(), out var rootDirectory))
        {
            return rootDirectory;
        }

        if (TryPrepareRootDirectory(GetTempRootDirectory(), out rootDirectory))
        {
            return rootDirectory;
        }

        if (TryPrepareRootDirectory(AppContext.BaseDirectory, out rootDirectory))
        {
            return rootDirectory;
        }

        return AppContext.BaseDirectory;
    }

    private static string? GetLocalAppDataRootDirectory()
    {
        try
        {
            var localAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA");
            if (string.IsNullOrWhiteSpace(localAppData))
            {
                localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            }

            return string.IsNullOrWhiteSpace(localAppData)
                ? null
                : Path.Combine(localAppData, ProductDirectoryName);
        }
        catch
        {
            return null;
        }
    }

    private static string? GetTempRootDirectory()
    {
        try
        {
            return Path.Combine(Path.GetTempPath(), ProductDirectoryName);
        }
        catch
        {
            return null;
        }
    }

    private static bool TryPrepareRootDirectory(string? candidateDirectory, out string rootDirectory)
    {
        rootDirectory = string.Empty;
        if (string.IsNullOrWhiteSpace(candidateDirectory))
        {
            return false;
        }

        string? probePath = null;
        try
        {
            var fullPath = Path.GetFullPath(candidateDirectory);
            Directory.CreateDirectory(fullPath);

            probePath = Path.Combine(fullPath, $".relaybench-write-test-{Guid.NewGuid():N}.tmp");
            File.WriteAllText(probePath, string.Empty);
            File.Delete(probePath);

            rootDirectory = fullPath;
            return true;
        }
        catch
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(probePath) && File.Exists(probePath))
                {
                    File.Delete(probePath);
                }
            }
            catch
            {
            }

            rootDirectory = string.Empty;
            return false;
        }
    }
}
