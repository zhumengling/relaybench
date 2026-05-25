using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using RelayBench.Services.Infrastructure;

namespace RelayBench.Services;

internal sealed partial class TransparentProxyResponseCacheService
{
    private static bool LooksUnsafeToCache(byte[] requestBody)
    {
        if (requestBody.Length == 0)
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(requestBody);
            return ContainsUnsafeCacheNode(document.RootElement);
        }
        catch
        {
            return false;
        }
    }

    private static bool ContainsUnsafeCacheNode(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (IsUnsafeCacheProperty(property.Name))
                    {
                        return true;
                    }

                    if (ContainsUnsafeCacheNode(property.Value))
                    {
                        return true;
                    }
                }

                return false;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    if (ContainsUnsafeCacheNode(item))
                    {
                        return true;
                    }
                }

                return false;
            case JsonValueKind.String:
                var value = element.GetString();
                return value is not null &&
                       (value.Contains("data:image/", StringComparison.OrdinalIgnoreCase) ||
                        value.Contains("\"type\":\"input_image\"", StringComparison.OrdinalIgnoreCase));
            default:
                return false;
        }
    }

    private static bool IsUnsafeCacheProperty(string name)
        => name.Equals("tools", StringComparison.OrdinalIgnoreCase) ||
           name.Equals("tool_choice", StringComparison.OrdinalIgnoreCase) ||
           name.Equals("tool_calls", StringComparison.OrdinalIgnoreCase) ||
           name.Equals("function_call", StringComparison.OrdinalIgnoreCase) ||
           name.Equals("files", StringComparison.OrdinalIgnoreCase) ||
           name.Equals("file_ids", StringComparison.OrdinalIgnoreCase) ||
           name.Equals("attachments", StringComparison.OrdinalIgnoreCase) ||
           name.Equals("image_url", StringComparison.OrdinalIgnoreCase) ||
           name.Equals("input_image", StringComparison.OrdinalIgnoreCase);

    private static byte[] BuildCanonicalCacheBody(byte[] requestBody)
    {
        if (requestBody.Length == 0 || requestBody.Length > 1024 * 1024)
        {
            return requestBody;
        }

        try
        {
            var node = JsonNode.Parse(requestBody);
            if (node is null)
            {
                return requestBody;
            }

            using MemoryStream stream = new();
            using (Utf8JsonWriter writer = new(stream))
            {
                WriteCanonicalJsonNode(writer, node, depth: 0);
            }

            return stream.ToArray();
        }
        catch
        {
            return requestBody;
        }
    }

    private static void WriteCanonicalJsonNode(Utf8JsonWriter writer, JsonNode? node, int depth)
    {
        switch (node)
        {
            case null:
                writer.WriteNullValue();
                break;
            case JsonObject obj:
                writer.WriteStartObject();
                foreach (var property in obj.OrderBy(static item => item.Key, StringComparer.Ordinal))
                {
                    if (ShouldSkipCacheProperty(property.Key, depth))
                    {
                        continue;
                    }

                    writer.WritePropertyName(property.Key);
                    WriteCanonicalJsonNode(writer, property.Value, depth + 1);
                }

                writer.WriteEndObject();
                break;
            case JsonArray array:
                writer.WriteStartArray();
                foreach (var item in array)
                {
                    WriteCanonicalJsonNode(writer, item, depth + 1);
                }

                writer.WriteEndArray();
                break;
            default:
                node.WriteTo(writer);
                break;
        }
    }

    private static bool ShouldSkipCacheProperty(string name, int depth)
        => ShouldSkipVolatileCacheProperty(name) ||
           depth == 0 &&
           (name.Equals("stream", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("metadata", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("user", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("store", StringComparison.OrdinalIgnoreCase));

    private static bool ShouldSkipVolatileCacheProperty(string name)
        => name.Equals("idempotency_key", StringComparison.OrdinalIgnoreCase) ||
           name.Equals("idempotencyKey", StringComparison.OrdinalIgnoreCase) ||
           name.Equals("request_id", StringComparison.OrdinalIgnoreCase) ||
           name.Equals("requestId", StringComparison.OrdinalIgnoreCase) ||
           name.Equals("client_request_id", StringComparison.OrdinalIgnoreCase) ||
           name.Equals("clientRequestId", StringComparison.OrdinalIgnoreCase) ||
           name.Equals("x_request_id", StringComparison.OrdinalIgnoreCase) ||
           name.Equals("trace_id", StringComparison.OrdinalIgnoreCase) ||
           name.Equals("traceId", StringComparison.OrdinalIgnoreCase) ||
           name.Equals("span_id", StringComparison.OrdinalIgnoreCase) ||
           name.Equals("spanId", StringComparison.OrdinalIgnoreCase) ||
           name.Equals("session_id", StringComparison.OrdinalIgnoreCase) ||
           name.Equals("sessionId", StringComparison.OrdinalIgnoreCase) ||
           name.Equals("prompt_cache_key", StringComparison.OrdinalIgnoreCase) ||
           name.Equals("promptCacheKey", StringComparison.OrdinalIgnoreCase) ||
           name.Equals("prompt_cache_retention", StringComparison.OrdinalIgnoreCase) ||
           name.Equals("promptCacheRetention", StringComparison.OrdinalIgnoreCase) ||
           name.Equals("cache_control", StringComparison.OrdinalIgnoreCase) ||
           name.Equals("cacheControl", StringComparison.OrdinalIgnoreCase) ||
           name.Equals("nonce", StringComparison.OrdinalIgnoreCase);

    private static string BuildCanonicalCachePath(string pathAndQuery)
    {
        var value = string.IsNullOrWhiteSpace(pathAndQuery)
            ? "/"
            : pathAndQuery.Trim();
        var question = value.IndexOf('?');
        if (question < 0)
        {
            return value;
        }

        var path = question == 0 ? "/" : value[..question];
        var query = question + 1 < value.Length ? value[(question + 1)..] : string.Empty;
        if (string.IsNullOrWhiteSpace(query))
        {
            return path;
        }

        var parameters = query
            .Split('&', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(static part =>
            {
                var equals = part.IndexOf('=');
                var rawName = equals >= 0 ? part[..equals] : part;
                var rawValue = equals >= 0 ? part[(equals + 1)..] : string.Empty;
                var name = DecodeQueryPart(rawName);
                var value = DecodeQueryPart(rawValue);
                return (Name: name, Value: value);
            })
            .Where(static item => !string.IsNullOrWhiteSpace(item.Name) && !ShouldSkipVolatileCacheQueryParameter(item.Name))
            .OrderBy(static item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static item => item.Value, StringComparer.Ordinal)
            .Select(static item => $"{Uri.EscapeDataString(item.Name)}={Uri.EscapeDataString(item.Value)}")
            .ToArray();

        return parameters.Length == 0
            ? path
            : path + "?" + string.Join("&", parameters);
    }

    private static bool ShouldSkipVolatileCacheQueryParameter(string name)
        => ShouldSkipVolatileCacheProperty(name) ||
           name.Equals("_", StringComparison.OrdinalIgnoreCase) ||
           name.Equals("ts", StringComparison.OrdinalIgnoreCase) ||
           name.Equals("timestamp", StringComparison.OrdinalIgnoreCase) ||
           name.Equals("api_key", StringComparison.OrdinalIgnoreCase) ||
           name.Equals("apikey", StringComparison.OrdinalIgnoreCase) ||
           name.Equals("key", StringComparison.OrdinalIgnoreCase) ||
           name.Equals("access_token", StringComparison.OrdinalIgnoreCase);

    private static string DecodeQueryPart(string value)
    {
        try
        {
            return Uri.UnescapeDataString(value.Replace("+", " ", StringComparison.Ordinal)).Trim();
        }
        catch
        {
            return value.Trim();
        }
    }

    private static string BuildModelsListCacheKey(string pathAndQuery, TransparentProxyServerConfig config)
    {
        StringBuilder builder = new();
        foreach (var route in config.Routes)
        {
            AppendHashPart(builder, route.Id);
            AppendHashPart(builder, route.BaseUrl);
            AppendHashPart(builder, route.Prefix);
            AppendHashPart(builder, route.ApiKey);
            AppendHashPart(builder, route.Model);
            AppendHashPart(builder, route.OutboundProxy);
            AppendHashPart(builder, route.PayloadRulesText);
            foreach (var mapping in route.ModelMappings.OrderBy(static item => item.Name, StringComparer.OrdinalIgnoreCase))
            {
                AppendHashPart(builder, mapping.Name);
                AppendHashPart(builder, mapping.Alias);
            }

            foreach (var pattern in route.ExcludedModelPatterns.Order(StringComparer.OrdinalIgnoreCase))
            {
                AppendHashPart(builder, pattern);
            }

            foreach (var header in route.Headers.OrderBy(static item => item.Key, StringComparer.OrdinalIgnoreCase))
            {
                AppendHashPart(builder, header.Key);
                AppendHashPart(builder, header.Value);
            }
        }

        AppendHashPart(builder, pathAndQuery);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return Convert.ToHexString(hash);
    }

    private static void AppendHashPart(StringBuilder builder, string? value)
        => builder.Append(value?.Trim() ?? string.Empty).Append('\u001F');
}
