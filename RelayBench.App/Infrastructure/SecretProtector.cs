using System.Security.Cryptography;
using System.Text;

namespace RelayBench.App.Infrastructure;

public static class SecretProtector
{
    public const string Prefix = "dpapi:v1:";
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("RelayBench.AppStateSecrets.v1");

    public static string Protect(string? value)
        => value ?? string.Empty;

    public static string Unprotect(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        if (!IsProtected(value))
        {
            return value;
        }

        try
        {
            var payload = value[Prefix.Length..];
            var protectedBytes = Convert.FromBase64String(payload);
            var plainBytes = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plainBytes);
        }
        catch
        {
            return string.Empty;
        }
    }

    public static bool IsProtected(string value)
        => value.StartsWith(Prefix, StringComparison.Ordinal);
}
