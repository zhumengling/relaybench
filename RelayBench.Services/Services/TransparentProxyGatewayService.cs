namespace RelayBench.Services;

internal sealed class TransparentProxyGatewayService
{
    public string CreateRequestId()
        => $"rb-{Guid.NewGuid():N}";

    public bool IsCorsPreflight(string method)
        => string.Equals(method, "OPTIONS", StringComparison.OrdinalIgnoreCase);

    public bool TryResolveManagementEndpoint(string pathAndQuery, out TransparentProxyManagementEndpoint endpoint)
    {
        var path = NormalizePath(pathAndQuery);
        if (IsPathPrefix(path, "/relaybench/health"))
        {
            endpoint = TransparentProxyManagementEndpoint.Health;
            return true;
        }

        if (IsPathPrefix(path, "/relaybench/metrics"))
        {
            endpoint = TransparentProxyManagementEndpoint.Metrics;
            return true;
        }

        if (IsPathPrefix(path, "/relaybench/usage"))
        {
            endpoint = TransparentProxyManagementEndpoint.Usage;
            return true;
        }

        if (IsPathPrefix(path, "/relaybench/ingress"))
        {
            endpoint = TransparentProxyManagementEndpoint.Ingress;
            return true;
        }

        if (IsPathPrefix(path, "/relaybench/capture/apps"))
        {
            endpoint = TransparentProxyManagementEndpoint.CaptureApps;
            return true;
        }

        if (IsPathPrefix(path, "/relaybench/capture/diagnostics"))
        {
            endpoint = TransparentProxyManagementEndpoint.CaptureDiagnostics;
            return true;
        }

        if (IsPathPrefix(path, "/relaybench/capture/recovery"))
        {
            endpoint = TransparentProxyManagementEndpoint.CaptureRecovery;
            return true;
        }

        if (IsPathPrefix(path, "/relaybench/logs"))
        {
            endpoint = TransparentProxyManagementEndpoint.Logs;
            return true;
        }

        if (IsPathPrefix(path, "/relaybench/models"))
        {
            endpoint = TransparentProxyManagementEndpoint.Models;
            return true;
        }

        if (IsPathPrefix(path, "/relaybench/cache"))
        {
            endpoint = TransparentProxyManagementEndpoint.Cache;
            return true;
        }

        if (IsPathPrefix(path, "/relaybench/routes"))
        {
            endpoint = TransparentProxyManagementEndpoint.Routes;
            return true;
        }

        if (IsPathPrefix(path, "/relaybench/scheduler"))
        {
            endpoint = TransparentProxyManagementEndpoint.Scheduler;
            return true;
        }

        if (IsPathPrefix(path, "/relaybench/protocols"))
        {
            endpoint = TransparentProxyManagementEndpoint.Protocols;
            return true;
        }

        endpoint = TransparentProxyManagementEndpoint.None;
        return false;
    }

    private static string NormalizePath(string pathAndQuery)
    {
        var queryStart = pathAndQuery.IndexOf('?');
        var path = queryStart >= 0 ? pathAndQuery[..queryStart] : pathAndQuery;
        return string.IsNullOrWhiteSpace(path) ? "/" : path.TrimEnd('/');
    }

    private static bool IsPathPrefix(string path, string prefix)
        => path.Equals(prefix, StringComparison.OrdinalIgnoreCase) ||
           path.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase);
}

internal enum TransparentProxyManagementEndpoint
{
    None,
    Health,
    Metrics,
    Usage,
    Ingress,
    CaptureApps,
    CaptureDiagnostics,
    CaptureRecovery,
    Logs,
    Models,
    Cache,
    Routes,
    Scheduler,
    Protocols
}
