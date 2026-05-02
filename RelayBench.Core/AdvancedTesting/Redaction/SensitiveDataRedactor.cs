using System.Text.Json;
using System.Text.RegularExpressions;
using RelayBench.Core.AdvancedTesting.Models;

namespace RelayBench.Core.AdvancedTesting.Redaction;

public sealed partial class SensitiveDataRedactor : ISensitiveDataRedactor
{
    private static readonly HashSet<string> SensitiveHeaderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "authorization",
        "x-api-key",
        "api-key",
        "cookie",
        "set-cookie"
    };

    private static readonly HashSet<string> SensitiveJsonNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "api_key",
        "apikey",
        "key",
        "token",
        "access_token",
        "authorization",
        "secret",
        "password",
        "cookie"
    };

    private static readonly HashSet<string> SensitiveQueryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "key",
        "api_key",
        "apikey",
        "token",
        "access_token",
        "authorization",
        "secret",
        "password"
    };

    public string Redact(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var redacted = RedactAuthorizationRegex().Replace(value, "$1***");
        redacted = RedactKeyValueRegex().Replace(redacted, "$1***");
        redacted = RedactUrlQuery(redacted);
        redacted = TryRedactJson(redacted);
        return redacted;
    }

    public AdvancedRawExchange Redact(AdvancedRawExchange exchange)
        => exchange with
        {
            Url = Redact(exchange.Url),
            RequestHeaders = RedactHeaders(exchange.RequestHeaders),
            RequestBody = Redact(exchange.RequestBody),
            ResponseHeaders = RedactHeaders(exchange.ResponseHeaders),
            ResponseBody = Redact(exchange.ResponseBody)
        };

    private static IReadOnlyDictionary<string, string> RedactHeaders(IReadOnlyDictionary<string, string> headers)
        => headers.ToDictionary(
            static pair => pair.Key,
            pair => SensitiveHeaderNames.Contains(pair.Key) ? "***" : pair.Value,
            StringComparer.OrdinalIgnoreCase);

    private static string RedactUrlQuery(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Query))
        {
            return value;
        }

        var query = uri.Query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part =>
            {
                var index = part.IndexOf('=');
                if (index <= 0)
                {
                    return part;
                }

                var name = Uri.UnescapeDataString(part[..index]);
                return SensitiveQueryNames.Contains(name)
                    ? $"{part[..(index + 1)]}***"
                    : part;
            });

        var builder = new UriBuilder(uri)
        {
            Query = string.Join("&", query)
        };

        return builder.Uri.ToString();
    }

    private static string TryRedactJson(string value)
    {
        var trimmed = value.Trim();
        if (!trimmed.StartsWith('{') && !trimmed.StartsWith('['))
        {
            return value;
        }

        try
        {
            using var document = JsonDocument.Parse(value);
            using MemoryStream stream = new();
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
            {
                WriteRedactedJsonElement(writer, document.RootElement, null);
            }

            return System.Text.Encoding.UTF8.GetString(stream.ToArray());
        }
        catch
        {
            return value;
        }
    }

    private static void WriteRedactedJsonElement(Utf8JsonWriter writer, JsonElement element, string? propertyName)
    {
        if (propertyName is not null && SensitiveJsonNames.Contains(propertyName))
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
                    WriteRedactedJsonElement(writer, property.Value, property.Name);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteRedactedJsonElement(writer, item, null);
                }
                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(RedactInlineSecret(element.GetString()));
                break;
            case JsonValueKind.Number:
                element.WriteTo(writer);
                break;
            case JsonValueKind.True:
            case JsonValueKind.False:
                writer.WriteBooleanValue(element.GetBoolean());
                break;
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
            default:
                writer.WriteNullValue();
                break;
        }
    }

    private static string RedactInlineSecret(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : RedactAuthorizationRegex().Replace(value, "$1***");

    [GeneratedRegex(@"(?i)\b(authorization\s*[:=]\s*(?:bearer\s+)?)['""]?[^'""\s,;]+")]
    private static partial Regex RedactAuthorizationRegex();

    [GeneratedRegex(@"(?i)\b((?:api[_-]?key|access[_-]?token|token|secret|password)\s*[:=]\s*)['""]?[^'""\s,;&]+")]
    private static partial Regex RedactKeyValueRegex();
}
