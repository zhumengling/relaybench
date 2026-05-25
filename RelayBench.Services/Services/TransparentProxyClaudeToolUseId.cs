using System.Text;

namespace RelayBench.Services;

internal static class TransparentProxyClaudeToolUseId
{
    public static string Normalize(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return Generate();
        }

        var builder = new StringBuilder(id.Length);
        foreach (var ch in id)
        {
            builder.Append(IsAllowed(ch) ? ch : '_');
        }

        return builder.Length == 0 ? Generate() : builder.ToString();
    }

    private static string Generate()
        => $"toolu_{Guid.NewGuid():N}";

    private static bool IsAllowed(char ch)
        => (ch >= 'a' && ch <= 'z') ||
           (ch >= 'A' && ch <= 'Z') ||
           (ch >= '0' && ch <= '9') ||
           ch == '_' ||
           ch == '-';
}
