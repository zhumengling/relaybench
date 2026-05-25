using System.Net;
using System.Text.Json;

namespace RelayBench.Services;

internal static class TransparentProxyResponsesStreamError
{
    public static byte[] BuildChunk(int statusCode, string? errorText, int sequenceNumber = 0)
    {
        if (statusCode <= 0)
        {
            statusCode = (int)HttpStatusCode.InternalServerError;
        }

        sequenceNumber = Math.Max(0, sequenceNumber);
        var code = ResolveCode(statusCode);
        var message = string.IsNullOrWhiteSpace(errorText)
            ? ReasonPhrase(statusCode)
            : errorText.Trim();

        TryReadErrorPayload(message, ref code, ref message, ref sequenceNumber);

        if (string.IsNullOrWhiteSpace(code))
        {
            code = "unknown_error";
        }

        return JsonSerializer.SerializeToUtf8Bytes(new
        {
            type = "error",
            code,
            message,
            sequence_number = sequenceNumber
        });
    }

    private static string ResolveCode(int statusCode)
        => statusCode switch
        {
            (int)HttpStatusCode.Unauthorized => "invalid_api_key",
            (int)HttpStatusCode.Forbidden => "insufficient_quota",
            (int)HttpStatusCode.TooManyRequests => "rate_limit_exceeded",
            (int)HttpStatusCode.NotFound => "model_not_found",
            (int)HttpStatusCode.RequestTimeout => "request_timeout",
            >= 500 => "internal_server_error",
            >= 400 => "invalid_request_error",
            _ => "unknown_error"
        };

    private static void TryReadErrorPayload(
        string rawText,
        ref string code,
        ref string message,
        ref int sequenceNumber)
    {
        if (string.IsNullOrWhiteSpace(rawText))
        {
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(rawText);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            if (TryReadString(root, "type")?.Equals("error", StringComparison.OrdinalIgnoreCase) == true)
            {
                message = TryReadString(root, "message") ?? message;
                code = TryReadString(root, "code") ?? code;
                if (root.TryGetProperty("sequence_number", out var sequence) &&
                    sequence.ValueKind == JsonValueKind.Number &&
                    sequence.TryGetInt32(out var parsed) &&
                    sequenceNumber == 0)
                {
                    sequenceNumber = Math.Max(0, parsed);
                }
            }

            if (root.TryGetProperty("error", out var error) &&
                error.ValueKind == JsonValueKind.Object)
            {
                message = TryReadString(error, "message") ?? message;
                code = TryReadString(error, "code") ?? code;
            }
        }
        catch
        {
        }
    }

    private static string? TryReadString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        if (property.ValueKind == JsonValueKind.String)
        {
            var value = property.GetString()?.Trim();
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }

        return property.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
            ? null
            : property.ToString();
    }

    private static string ReasonPhrase(int statusCode)
        => Enum.IsDefined(typeof(HttpStatusCode), statusCode)
            ? ((HttpStatusCode)statusCode).ToString()
            : "upstream error";
}
