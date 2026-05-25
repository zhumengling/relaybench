using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using RelayBench.Core.Models;

namespace RelayBench.Core.Services;

public sealed partial class ExitIpRiskReviewService
{
    private static JsonElement GetProperty(JsonElement root, string propertyName)
        => root.ValueKind == JsonValueKind.Object &&
           root.TryGetProperty(propertyName, out var property)
            ? property
            : default;

    private static string? GetString(JsonElement root, string propertyName)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.String => property.GetString(),
            JsonValueKind.Number => property.ToString(),
            JsonValueKind.True => bool.TrueString,
            JsonValueKind.False => bool.FalseString,
            _ => null
        };
    }

    private static bool? GetBool(JsonElement root, string propertyName)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(property.GetString(), out var parsedBool) => parsedBool,
            JsonValueKind.String when string.Equals(property.GetString(), "yes", StringComparison.OrdinalIgnoreCase) => true,
            JsonValueKind.String when string.Equals(property.GetString(), "no", StringComparison.OrdinalIgnoreCase) => false,
            JsonValueKind.Number when property.TryGetInt32(out var parsedInt) => parsedInt != 0,
            _ => null
        };
    }

    private static double? GetDouble(JsonElement root, string propertyName)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.Number when property.TryGetDouble(out var parsedDouble) => parsedDouble,
            JsonValueKind.String when double.TryParse(property.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedDouble) => parsedDouble,
            _ => null
        };
    }

    private static string BuildCoordinateText(JsonElement root)
    {
        var latitude = GetDouble(root, "latitude");
        var longitude = GetDouble(root, "longitude");
        if (latitude is null || longitude is null)
        {
            return "--";
        }

        return $"{latitude.Value.ToString("F4", CultureInfo.InvariantCulture)}, {longitude.Value.ToString("F4", CultureInfo.InvariantCulture)}";
    }

    private static bool ArrayContainsString(JsonElement root, string propertyName, string expected)
    {
        if (root.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var item in root.EnumerateArray())
        {
            var value = GetString(item, propertyName);
            if (string.Equals(value, expected, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string[] GetArrayValues(JsonElement root, string propertyName)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return property
            .EnumerateArray()
            .Select(static item => item.ValueKind switch
            {
                JsonValueKind.String => item.GetString(),
                JsonValueKind.Number => item.ToString(),
                JsonValueKind.True => bool.TrueString,
                JsonValueKind.False => bool.FalseString,
                _ => null
            })
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .ToArray()!;
    }
}
