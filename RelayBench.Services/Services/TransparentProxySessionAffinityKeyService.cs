using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RelayBench.Services;

internal sealed class TransparentProxySessionAffinityKeyService
{
    public string? Build(HttpListenerRequest request, byte[] requestBody)
        => Build(headerName => request.Headers[headerName], requestBody);

    public string? Build(Func<string, string?> readHeader, byte[] requestBody)
    {
        var model = TryReadRequestModel(requestBody);
        var explicitSession = TransparentProxySessionIdentityExtractor.ResolveExplicitSessionKey(requestBody, readHeader);
        if (!string.IsNullOrWhiteSpace(explicitSession))
        {
            return BuildScopedSessionKey(model, explicitSession);
        }

        if (TryBuildConversationFingerprint(requestBody, out var conversationFingerprint))
        {
            return BuildScopedSessionKey(model, conversationFingerprint);
        }

        var bodyHash = SHA256.HashData(requestBody);
        return BuildScopedSessionKey(
            model,
            Convert.ToHexString(bodyHash[..8]));
    }

    private static string BuildScopedSessionKey(string? model, string sessionId)
    {
        var cleanSession = sessionId.Trim();
        if (string.IsNullOrWhiteSpace(model))
        {
            return cleanSession;
        }

        return $"{model.Trim()}\u001F{cleanSession}";
    }

    private static bool TryBuildConversationFingerprint(byte[] requestBody, out string fingerprint)
    {
        fingerprint = string.Empty;
        if (requestBody.Length == 0 || requestBody.Length > 512 * 1024)
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(requestBody);
            List<string> parts = [];
            if (document.RootElement.TryGetProperty("messages", out var messages) &&
                messages.ValueKind == JsonValueKind.Array)
            {
                foreach (var message in messages.EnumerateArray().Take(4))
                {
                    var role = TryReadStringProperty(message, "role") ?? "-";
                    var content = ReadConversationContentPreview(message, "content");
                    if (!string.IsNullOrWhiteSpace(content))
                    {
                        parts.Add($"{role}:{content}");
                    }
                }
            }
            else if (document.RootElement.TryGetProperty("input", out var input))
            {
                if (input.ValueKind == JsonValueKind.String)
                {
                    parts.Add("input:" + TruncateFingerprintPart(input.GetString()));
                }
                else if (input.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in input.EnumerateArray().Take(4))
                    {
                        var role = TryReadStringProperty(item, "role") ?? "-";
                        var content = ReadConversationContentPreview(item, "content");
                        if (!string.IsNullOrWhiteSpace(content))
                        {
                            parts.Add($"{role}:{content}");
                        }
                    }
                }
            }

            if (parts.Count == 0)
            {
                return false;
            }

            var source = string.Join("\u001F", parts);
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(source));
            fingerprint = Convert.ToHexString(hash[..10]);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string ReadConversationContentPreview(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var content))
        {
            return string.Empty;
        }

        if (content.ValueKind == JsonValueKind.String)
        {
            return TruncateFingerprintPart(content.GetString());
        }

        if (content.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        List<string> parts = [];
        foreach (var item in content.EnumerateArray().Take(4))
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                parts.Add(TruncateFingerprintPart(item.GetString()));
                continue;
            }

            if (item.ValueKind == JsonValueKind.Object &&
                item.TryGetProperty("text", out var text) &&
                text.ValueKind == JsonValueKind.String)
            {
                parts.Add(TruncateFingerprintPart(text.GetString()));
            }
        }

        return string.Join(" ", parts.Where(static item => !string.IsNullOrWhiteSpace(item)));
    }

    private static string TruncateFingerprintPart(string? value)
    {
        var normalized = (value ?? string.Empty).Trim();
        return normalized.Length <= 240 ? normalized : normalized[..240];
    }

    private static string? TryReadRequestModel(byte[] requestBody)
        => TryReadStringProperty(requestBody, "model");

    private static string? TryReadStringProperty(byte[] requestBody, string propertyName)
    {
        if (requestBody.Length == 0)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(requestBody);
            return TryReadStringProperty(document.RootElement, propertyName);
        }
        catch
        {
            return null;
        }
    }
    private static string? TryReadStringProperty(JsonElement element, string propertyName)
        => element.ValueKind == JsonValueKind.Object &&
           element.TryGetProperty(propertyName, out var property) &&
           property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
}
