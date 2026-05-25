using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace RelayBench.Core.Services;

internal static class ClientApiConfigPatterns
{
    private static readonly Regex UrlRegex = new(@"https?://[^\s""'<>]+", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static string NormalizeEndpoint(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.Trim().Trim('"', '\'').TrimEnd('/');

    public static bool IsLocalEndpoint(string? value)
    {
        if (!Uri.TryCreate(NormalizeEndpoint(value), UriKind.Absolute, out var uri))
        {
            return false;
        }

        return uri.Host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
               uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
               uri.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsOfficialEndpoint(string? candidate, string officialBaseUrl)
    {
        if (!Uri.TryCreate(NormalizeEndpoint(candidate), UriKind.Absolute, out var candidateUri) ||
            !Uri.TryCreate(NormalizeEndpoint(officialBaseUrl), UriKind.Absolute, out var officialUri))
        {
            return false;
        }

        return candidateUri.Host.Equals(officialUri.Host, StringComparison.OrdinalIgnoreCase);
    }

    public static int? TryGetPort(string? candidate)
    {
        if (!Uri.TryCreate(NormalizeEndpoint(candidate), UriKind.Absolute, out var uri))
        {
            return null;
        }

        return uri.IsDefaultPort ? null : uri.Port;
    }

    public static bool LooksLikeProxyManagedValue(string? value)
        => string.Equals(value?.Trim(), "PROXY_MANAGED", StringComparison.OrdinalIgnoreCase);

    public static bool LooksLikeEndpointKey(string? propertyName)
    {
        if (string.IsNullOrWhiteSpace(propertyName))
        {
            return false;
        }

        var normalized = propertyName.Replace("_", string.Empty, StringComparison.OrdinalIgnoreCase);
        return normalized.Contains("baseurl", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("apibaseurl", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("endpoint", StringComparison.OrdinalIgnoreCase);
    }

    public static IReadOnlyList<string> ExtractUrlCandidates(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Array.Empty<string>();
        }

        return UrlRegex.Matches(text)
            .Select(match => NormalizeEndpoint(match.Value))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static JsonObject? TryParseJsonObject(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(text) as JsonObject;
        }
        catch
        {
            return null;
        }
    }

    public static string SerializeJson(JsonObject root)
        => root.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true
        });

    public static bool RemoveJsonOverrides(JsonObject root)
        => RemoveJsonOverridesRecursive(root);

    private static bool RemoveJsonOverridesRecursive(JsonObject root)
    {
        var changed = false;
        foreach (var property in root.ToList())
        {
            if (property.Value is JsonObject childObject)
            {
                changed |= RemoveJsonOverridesRecursive(childObject);
                if (childObject.Count == 0)
                {
                    root.Remove(property.Key);
                    changed = true;
                }

                continue;
            }

            if (property.Value is JsonArray childArray)
            {
                foreach (var item in childArray.OfType<JsonObject>())
                {
                    changed |= RemoveJsonOverridesRecursive(item);
                }

                continue;
            }

            var value = property.Value is JsonValue jsonValue
                ? jsonValue.TryGetValue<string>(out var stringValue) ? stringValue : jsonValue.ToJsonString()
                : property.Value?.ToJsonString();
            if (LooksLikeEndpointKey(property.Key))
            {
                root.Remove(property.Key);
                changed = true;
                continue;
            }

            if (LooksLikeProxyManagedValue(value))
            {
                root.Remove(property.Key);
                changed = true;
            }
        }

        return changed;
    }

    public static string RemoveLineBasedOverrides(string content, out bool changed)
    {
        changed = false;
        List<string> keptLines = [];

        foreach (var line in content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var trimmed = line.Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                keptLines.Add(line);
                continue;
            }

            var assignmentIndex = trimmed.IndexOf('=');
            if (assignmentIndex > 0)
            {
                var key = trimmed[..assignmentIndex].Trim();
                var value = trimmed[(assignmentIndex + 1)..].Trim().Trim('"', '\'');
                if (LooksLikeEndpointKey(key) || LooksLikeProxyManagedValue(value))
                {
                    changed = true;
                    continue;
                }
            }

            keptLines.Add(line);
        }

        if (!changed)
        {
            return content;
        }

        return string.Join(Environment.NewLine, keptLines)
            .Replace($"{Environment.NewLine}{Environment.NewLine}{Environment.NewLine}", $"{Environment.NewLine}{Environment.NewLine}", StringComparison.Ordinal)
            .TrimEnd() + Environment.NewLine;
    }
}
