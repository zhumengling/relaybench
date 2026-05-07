using System.Text;
using System.Text.Json;

namespace RelayBench.App.Services;

internal static class CodexOAuthJwtParser
{
    public static CodexOAuthJwtInfo Parse(string idToken)
    {
        if (string.IsNullOrWhiteSpace(idToken))
        {
            return CodexOAuthJwtInfo.Empty;
        }

        try
        {
            var parts = idToken.Split('.');
            if (parts.Length != 3)
            {
                return CodexOAuthJwtInfo.Empty;
            }

            var payload = Encoding.UTF8.GetString(Base64UrlDecode(parts[1]));
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            var email = TryReadString(root, "email");
            var auth = TryReadObject(root, "https://api.openai.com/auth");
            var accountId = auth.HasValue ? TryReadString(auth.Value, "chatgpt_account_id") : string.Empty;
            var planType = auth.HasValue ? TryReadString(auth.Value, "chatgpt_plan_type") : string.Empty;
            return new CodexOAuthJwtInfo(email, accountId, planType);
        }
        catch
        {
            return CodexOAuthJwtInfo.Empty;
        }
    }

    private static JsonElement? TryReadObject(JsonElement root, string propertyName)
        => root.ValueKind == JsonValueKind.Object &&
           root.TryGetProperty(propertyName, out var value) &&
           value.ValueKind == JsonValueKind.Object
            ? value
            : null;

    private static string TryReadString(JsonElement root, string propertyName)
        => root.ValueKind == JsonValueKind.Object &&
           root.TryGetProperty(propertyName, out var value) &&
           value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static byte[] Base64UrlDecode(string value)
    {
        var normalized = value.Replace('-', '+').Replace('_', '/');
        normalized = normalized.PadRight(normalized.Length + (4 - normalized.Length % 4) % 4, '=');
        return Convert.FromBase64String(normalized);
    }
}

internal sealed record CodexOAuthJwtInfo(string Email, string AccountId, string PlanType)
{
    public static CodexOAuthJwtInfo Empty { get; } = new(string.Empty, string.Empty, string.Empty);
}
