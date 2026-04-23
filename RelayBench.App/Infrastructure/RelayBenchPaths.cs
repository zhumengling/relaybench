using System.IO;

namespace RelayBench.App.Infrastructure;

public static class RelayBenchPaths
{
    private const string WorkspaceRootEnvironmentVariable = "RELAYBENCH_WORKSPACE_ROOT";

    public static string RootDirectory
    {
        get
        {
            var configuredRoot = Environment.GetEnvironmentVariable(WorkspaceRootEnvironmentVariable);
            if (!string.IsNullOrWhiteSpace(configuredRoot))
            {
                var fullPath = Path.GetFullPath(configuredRoot);
                Directory.CreateDirectory(fullPath);
                return fullPath;
            }

            return AppContext.BaseDirectory;
        }
    }

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
}
