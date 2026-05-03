using System.Text;
using System.Text.Json;

namespace RelayBench.Core.Services;

public static class ProbeTraceRedactor
{
    private static readonly HashSet<string> SensitiveNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "authorization",
        "api-key",
        "x-api-key",
        "apikey",
        "api_key",
        "key",
        "token",
        "access_token",
        "refresh_token",
        "password",
        "secret",
        "client_secret",
        "cookie",
        "set-cookie",
        "openai_api_key",
        "anthropic_api_key"
    };

    public static IReadOnlyList<string> RedactHeaders(IEnumerable<string>? headers)
        => headers?.Select(RedactHeader).ToArray() ?? Array.Empty<string>();

    public static string RedactHeader(string? header)
    {
        if (string.IsNullOrWhiteSpace(header))
        {
            return string.Empty;
        }

        var separatorIndex = header.IndexOf(':');
        if (separatorIndex < 0)
        {
            return RedactText(header);
        }

        var name = header[..separatorIndex].Trim();
        var value = header[(separatorIndex + 1)..].Trim();
        if (!IsSensitiveName(name))
        {
            return $"{name}: {RedactText(value)}";
        }

        if (name.Equals("authorization", StringComparison.OrdinalIgnoreCase) &&
            value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return $"{name}: {MaskBearer(value)}";
        }

        return $"{name}: ***";
    }

    public static string RedactUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return string.Empty;
        }

        var queryIndex = url.IndexOf('?');
        if (queryIndex < 0 || queryIndex == url.Length - 1)
        {
            return RedactText(url);
        }

        var prefix = url[..(queryIndex + 1)];
        var query = url[(queryIndex + 1)..];
        var fragments = query.Split('&', StringSplitOptions.None);
        for (var i = 0; i < fragments.Length; i++)
        {
            var fragment = fragments[i];
            var equalsIndex = fragment.IndexOf('=');
            if (equalsIndex <= 0)
            {
                fragments[i] = RedactText(fragment);
                continue;
            }

            var key = fragment[..equalsIndex];
            var value = fragment[(equalsIndex + 1)..];
            fragments[i] = IsSensitiveName(Uri.UnescapeDataString(key))
                ? $"{key}=***"
                : $"{key}={RedactText(value)}";
        }

        return prefix + string.Join("&", fragments);
    }

    public static string RedactJsonBody(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return string.Empty;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
            {
                WriteRedactedJsonValue(writer, document.RootElement, propertyName: null);
            }

            return Encoding.UTF8.GetString(stream.ToArray());
        }
        catch
        {
            return RedactText(json);
        }
    }

    public static string RedactText(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var normalized = text;
        normalized = RedactBearerTokens(normalized);
        normalized = RedactInlineAssignments(normalized, "api_key");
        normalized = RedactInlineAssignments(normalized, "apikey");
        normalized = RedactInlineAssignments(normalized, "token");
        normalized = RedactInlineAssignments(normalized, "access_token");
        normalized = RedactInlineAssignments(normalized, "refresh_token");
        normalized = RedactInlineAssignments(normalized, "password");
        normalized = RedactInlineAssignments(normalized, "secret");
        normalized = RedactInlineAssignments(normalized, "client_secret");
        return normalized;
    }

    private static void WriteRedactedJsonValue(Utf8JsonWriter writer, JsonElement element, string? propertyName)
    {
        if (propertyName is not null && IsSensitiveName(propertyName))
        {
            writer.WriteStringValue("***");
            return;
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject())
                {
                    writer.WritePropertyName(property.Name);
                    WriteRedactedJsonValue(writer, property.Value, property.Name);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteRedactedJsonValue(writer, item, propertyName: null);
                }
                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                var value = element.GetString() ?? string.Empty;
                writer.WriteStringValue(IsLikelyLargeBinary(value) ? SummarizeBinary(value) : RedactText(value));
                break;
            case JsonValueKind.Number:
                writer.WriteRawValue(element.GetRawText());
                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
            default:
                writer.WriteNullValue();
                break;
        }
    }

    private static bool IsSensitiveName(string name)
        => SensitiveNames.Contains(name.Replace("_", "-", StringComparison.OrdinalIgnoreCase)) ||
           SensitiveNames.Contains(name);

    private static string MaskBearer(string value)
    {
        const string prefix = "Bearer ";
        var token = value[prefix.Length..].Trim();
        if (token.Length <= 8)
        {
            return "Bearer ***";
        }

        return $"Bearer {token[..Math.Min(3, token.Length)]}-...{token[^Math.Min(4, token.Length)..]}";
    }

    private static string RedactBearerTokens(string value)
    {
        const string marker = "Bearer ";
        var index = value.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return value;
        }

        var end = value.IndexOfAny([' ', '\r', '\n', '\t', '"', '\''], index + marker.Length);
        if (end < 0)
        {
            end = value.Length;
        }

        var token = value[(index + marker.Length)..end];
        return value[..index] + MaskBearer(marker + token) + value[end..];
    }

    private static string RedactInlineAssignments(string value, string key)
    {
        var search = key + "=";
        var index = value.IndexOf(search, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return value;
        }

        var start = index + search.Length;
        var end = value.IndexOfAny(['&', ' ', '\r', '\n', '\t', '"', '\''], start);
        if (end < 0)
        {
            end = value.Length;
        }

        return value[..start] + "***" + value[end..];
    }

    private static bool IsLikelyLargeBinary(string value)
        => value.Length > 512 &&
           value.All(ch => char.IsLetterOrDigit(ch) || ch is '+' or '/' or '=' or '-' or '_');

    private static string SummarizeBinary(string value)
        => $"<binary:{value.Length} chars:{value[..Math.Min(8, value.Length)]}...>";
}
