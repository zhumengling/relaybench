namespace RelayBench.Services;

public static class TransparentProxyRouteAuthModes
{
    public const string ApiKey = "ApiKey";
    public const string CodexOAuth = "CodexOAuth";

    public static string Normalize(string? value)
        => string.Equals(value?.Trim(), CodexOAuth, StringComparison.OrdinalIgnoreCase)
            ? CodexOAuth
            : ApiKey;
}

internal sealed record TransparentProxyRouteAuthMaterial(
    string Mode,
    string BearerToken,
    IReadOnlyDictionary<string, string> Headers,
    string AccountLabel)
{
    public bool IsCodexOAuth
        => string.Equals(Mode, TransparentProxyRouteAuthModes.CodexOAuth, StringComparison.OrdinalIgnoreCase);
}
