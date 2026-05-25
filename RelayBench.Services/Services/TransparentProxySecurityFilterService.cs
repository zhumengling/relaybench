using System.Text.Json;
using System.Text.Json.Nodes;

namespace RelayBench.Services;

internal sealed class TransparentProxySecurityFilterService
{
    private static readonly ISet<string> DefaultAllowedPrivateFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "_meta",
        "_metadata",
        "_stream_options"
    };

    private static readonly JsonSerializerOptions CompactJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    public byte[] FilterPrivateFields(byte[] requestBody, out int removedFields)
    {
        removedFields = 0;
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

            removedFields = RemovePrivateFields(node);
            return removedFields <= 0
                ? requestBody
                : JsonSerializer.SerializeToUtf8Bytes(node, CompactJsonOptions);
        }
        catch
        {
            removedFields = 0;
            return requestBody;
        }
    }

    private static int RemovePrivateFields(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                var removed = 0;
                foreach (var property in obj.ToArray())
                {
                    if (ShouldRemovePrivateField(property.Key))
                    {
                        obj.Remove(property.Key);
                        removed++;
                        continue;
                    }

                    removed += RemovePrivateFields(property.Value);
                }

                return removed;
            case JsonArray array:
                var arrayRemoved = 0;
                foreach (var item in array)
                {
                    arrayRemoved += RemovePrivateFields(item);
                }

                return arrayRemoved;
            default:
                return 0;
        }
    }

    private static bool ShouldRemovePrivateField(string propertyName)
        => propertyName.StartsWith('_') &&
           !DefaultAllowedPrivateFields.Contains(propertyName);
}
