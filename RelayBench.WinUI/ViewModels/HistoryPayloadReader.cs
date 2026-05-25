using System.Globalization;
using System.Text.Json;

namespace RelayBench.WinUI.ViewModels;

internal static class HistoryPayloadReader
{
    public static bool TryParse(string? payloadJson, out JsonDocument document)
    {
        document = null!;
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return false;
        }

        try
        {
            document = JsonDocument.Parse(payloadJson);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            value = default;
            return false;
        }

        if (element.TryGetProperty(name, out value))
        {
            return true;
        }

        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    public static bool TryGetPath(JsonElement element, out JsonElement value, params string[] path)
    {
        value = element;
        foreach (var segment in path)
        {
            if (!TryGetProperty(value, segment, out value))
            {
                value = default;
                return false;
            }
        }

        return true;
    }

    public static string? ReadString(JsonElement element, params string[] path)
    {
        if (!TryGetPath(element, out var value, path))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null
        };
    }

    public static double? ReadDouble(JsonElement element, params string[] path)
    {
        if (!TryGetPath(element, out var value, path))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number))
        {
            return number;
        }

        if (value.ValueKind == JsonValueKind.String &&
            double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number))
        {
            return number;
        }

        return null;
    }

    public static int? ReadInt(JsonElement element, params string[] path)
    {
        var value = ReadDouble(element, path);
        return value.HasValue ? (int)Math.Round(value.Value) : null;
    }

    public static bool? ReadBool(JsonElement element, params string[] path)
    {
        if (!TryGetPath(element, out var value, path))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(value.GetString(), out var parsed) => parsed,
            _ => null
        };
    }

    public static double? FirstDouble(JsonElement element, params string[][] paths)
    {
        foreach (var path in paths)
        {
            if (ReadDouble(element, path) is { } value)
            {
                return value;
            }
        }

        return null;
    }

    public static string? FirstString(JsonElement element, params string[][] paths)
    {
        foreach (var path in paths)
        {
            var value = ReadString(element, path);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    public static List<JsonElement> ReadArray(JsonElement element, params string[] path)
    {
        if (!TryGetPath(element, out var value, path) || value.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return value.EnumerateArray().ToList();
    }
}
