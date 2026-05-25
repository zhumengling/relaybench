namespace RelayBench.Services.Infrastructure;

public static class SecretProtector
{
    public static string Protect(string? value)
        => value ?? string.Empty;

    public static string Unprotect(string? value)
        => value ?? string.Empty;
}
