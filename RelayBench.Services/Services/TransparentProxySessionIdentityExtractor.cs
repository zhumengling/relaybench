using System.Text.Json;

namespace RelayBench.Services;

internal static class TransparentProxySessionIdentityExtractor
{
    public static string? ResolveExplicitSessionKey(
        byte[] requestBody,
        Func<string, string?>? readHeader = null)
    {
        var metadataUserId = TryReadNestedStringProperty(requestBody, "metadata", "user_id");
        var normalizedMetadataUserId = NormalizeMetadataUserId(metadataUserId);
        if (!string.IsNullOrWhiteSpace(normalizedMetadataUserId))
        {
            return normalizedMetadataUserId;
        }

        var metadataSession =
            TryReadNestedStringProperty(requestBody, "metadata", "session_id") ??
            TryReadNestedStringProperty(requestBody, "metadata", "conversation_id");
        if (!string.IsNullOrWhiteSpace(metadataSession))
        {
            return "metadata:" + metadataSession.Trim();
        }

        var headerSession =
            ReadHeader(readHeader, "X-Session-ID") is { } xSessionId
                ? "header:" + xSessionId
                : ReadHeader(readHeader, "Session_id") is { } codexSessionId
                    ? "codex:" + codexSessionId
                    : ReadHeader(readHeader, "Session-Id") is { } sessionId
                        ? "codex:" + sessionId
                        : ReadHeader(readHeader, "X-Amp-Thread-Id") is { } ampThreadId
                            ? "amp:" + ampThreadId
                            : ReadHeader(readHeader, "X-Client-Request-Id") is { } clientRequestId
                                ? "clientreq:" + clientRequestId
                                : ReadHeader(readHeader, "OpenAI-Conversation-ID") is { } openAiConversationId
                                    ? "conv:" + openAiConversationId
                                    : ReadHeader(readHeader, "X-Conversation-ID") is { } conversationHeader
                                        ? "conv:" + conversationHeader
                                        : null;
        if (!string.IsNullOrWhiteSpace(headerSession))
        {
            return headerSession;
        }

        var bodySession =
            TryReadStringProperty(requestBody, "conversation_id") is { } conversationId
                ? "conv:" + conversationId
                : TryReadStringProperty(requestBody, "thread_id") is { } threadId
                    ? "thread:" + threadId
                    : TryReadStringProperty(requestBody, "session_id") is { } bodySessionId
                        ? "session:" + bodySessionId
                        : TryReadStringProperty(requestBody, "user") is { } user
                            ? "user:" + user
                            : TryReadStringProperty(requestBody, "user_id") is { } userId
                                ? "user:" + userId
                                : null;
        if (!string.IsNullOrWhiteSpace(bodySession))
        {
            return bodySession;
        }

        return string.IsNullOrWhiteSpace(metadataUserId)
            ? null
            : "user:" + metadataUserId.Trim();
    }

    private static string? NormalizeMetadataUserId(string? metadataUserId)
    {
        var value = metadataUserId?.Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        const string sessionMarker = "_session_";
        var markerIndex = value.LastIndexOf(sessionMarker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex >= 0)
        {
            var sessionId = value[(markerIndex + sessionMarker.Length)..].Trim();
            if (!string.IsNullOrWhiteSpace(sessionId))
            {
                return "claude:" + sessionId;
            }
        }

        if (value.StartsWith("{", StringComparison.Ordinal))
        {
            try
            {
                using var document = JsonDocument.Parse(value);
                var sessionId =
                    TryReadStringProperty(document.RootElement, "session_id") ??
                    TryReadStringProperty(document.RootElement, "sessionId");
                if (!string.IsNullOrWhiteSpace(sessionId))
                {
                    return "claude:" + sessionId.Trim();
                }
            }
            catch
            {
                return null;
            }
        }

        return null;
    }

    private static string? ReadHeader(Func<string, string?>? readHeader, string name)
    {
        var value = readHeader?.Invoke(name)?.Trim();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

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

    private static string? TryReadNestedStringProperty(byte[] requestBody, string parentPropertyName, string propertyName)
    {
        if (requestBody.Length == 0)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(requestBody);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                   document.RootElement.TryGetProperty(parentPropertyName, out var parent)
                ? TryReadStringProperty(parent, propertyName)
                : null;
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
