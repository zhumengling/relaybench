using System.Net;
using System.Text.Json;

namespace RelayBench.Services;

internal sealed class TransparentProxyRequestClassifier
{
    public TransparentProxyRequestClassification Classify(
        HttpListenerRequest request,
        string method,
        string pathAndQuery,
        byte[] body)
    {
        var relativePath = ExtractRelativePath(pathAndQuery);
        var kind = ResolveKind(method, relativePath);
        return new TransparentProxyRequestClassification(
            kind,
            relativePath,
            TryReadRequestModel(body) ?? TryReadGeminiModelFromPath(relativePath),
            IsStreamingRequest(request, body));
    }

    private static TransparentProxyRequestKind ResolveKind(string method, string relativePath)
    {
        if (string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase) &&
            (relativePath.Equals("models", StringComparison.OrdinalIgnoreCase) ||
             relativePath.Equals("v1/models", StringComparison.OrdinalIgnoreCase)))
        {
            return TransparentProxyRequestKind.ModelsList;
        }

        if (IsResponsesPath(relativePath))
        {
            return TransparentProxyRequestKind.OpenAiResponses;
        }

        if (IsAnthropicMessagesPath(relativePath))
        {
            return TransparentProxyRequestKind.AnthropicMessages;
        }

        if (IsAnthropicCountTokensPath(relativePath))
        {
            return TransparentProxyRequestKind.AnthropicCountTokens;
        }

        if (relativePath.EndsWith("chat/completions", StringComparison.OrdinalIgnoreCase))
        {
            return TransparentProxyRequestKind.OpenAiChatCompletions;
        }

        if (relativePath.EndsWith("embeddings", StringComparison.OrdinalIgnoreCase))
        {
            return TransparentProxyRequestKind.Embeddings;
        }

        if (IsGeminiNativePath(relativePath))
        {
            return TransparentProxyRequestKind.GeminiNative;
        }

        return TransparentProxyRequestKind.PassThrough;
    }

    private static bool IsGeminiNativePath(string relativePath)
        => IsGeminiCliInternalPath(relativePath) ||
           relativePath.Equals("v1beta/models", StringComparison.OrdinalIgnoreCase) ||
           relativePath.StartsWith("v1beta/models/", StringComparison.OrdinalIgnoreCase) ||
           relativePath.StartsWith("models/", StringComparison.OrdinalIgnoreCase);

    private static bool IsGeminiCliInternalPath(string relativePath)
        => relativePath.Equals("v1internal", StringComparison.OrdinalIgnoreCase) ||
           relativePath.StartsWith("v1internal:", StringComparison.OrdinalIgnoreCase) ||
           relativePath.StartsWith("v1internal/", StringComparison.OrdinalIgnoreCase);

    private static bool IsAnthropicMessagesPath(string relativePath)
        => relativePath.Equals("messages", StringComparison.OrdinalIgnoreCase) ||
           relativePath.EndsWith("/messages", StringComparison.OrdinalIgnoreCase);

    private static bool IsAnthropicCountTokensPath(string relativePath)
        => relativePath.Equals("messages/count_tokens", StringComparison.OrdinalIgnoreCase) ||
           relativePath.EndsWith("/messages/count_tokens", StringComparison.OrdinalIgnoreCase);

    private static bool IsStreamingRequest(HttpListenerRequest request, byte[] body)
    {
        if (request.RawUrl?.Contains("streamGenerateContent", StringComparison.OrdinalIgnoreCase) == true ||
            request.RawUrl?.Contains("alt=sse", StringComparison.OrdinalIgnoreCase) == true)
        {
            return true;
        }

        if (request.RawUrl?.Contains("stream=true", StringComparison.OrdinalIgnoreCase) == true)
        {
            return true;
        }

        if (body.Length == 0)
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                   document.RootElement.TryGetProperty("stream", out var streamProperty) &&
                   streamProperty.ValueKind == JsonValueKind.True;
        }
        catch
        {
            return false;
        }
    }

    private static string? TryReadRequestModel(byte[] requestBody)
    {
        if (requestBody.Length == 0)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(requestBody);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                   document.RootElement.TryGetProperty("model", out var model) &&
                   model.ValueKind == JsonValueKind.String
                ? model.GetString()
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static string? TryReadGeminiModelFromPath(string relativePath)
    {
        var path = NormalizeRelativePath(relativePath);
        if (path.StartsWith("v1beta/", StringComparison.OrdinalIgnoreCase))
        {
            path = path["v1beta/".Length..];
        }

        if (!path.StartsWith("models/", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var model = path["models/".Length..];
        var actionIndex = model.IndexOf(':');
        if (actionIndex >= 0)
        {
            model = model[..actionIndex];
        }

        return string.IsNullOrWhiteSpace(model) ? null : model;
    }

    private static string ExtractRelativePath(string pathAndQuery)
    {
        var path = pathAndQuery;
        var queryIndex = path.IndexOf('?');
        if (queryIndex >= 0)
        {
            path = path[..queryIndex];
        }

        return NormalizeRelativePath(path);
    }

    private static bool IsResponsesPath(string relativePath)
    {
        var normalized = NormalizeRelativePath(relativePath);
        return normalized.Equals("responses", StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals("responses/compact", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeRelativePath(string value)
    {
        var path = value.Trim('/').Trim();
        while (path.StartsWith("v1/v1/", StringComparison.OrdinalIgnoreCase))
        {
            path = path[3..];
        }

        if (path.StartsWith("backend-api/codex/", StringComparison.OrdinalIgnoreCase))
        {
            path = path["backend-api/codex/".Length..];
        }
        else if (path.Equals("backend-api/codex", StringComparison.OrdinalIgnoreCase))
        {
            path = string.Empty;
        }
        else if (path.StartsWith("codex/v1/", StringComparison.OrdinalIgnoreCase))
        {
            path = path["codex/v1/".Length..];
        }

        if (path.StartsWith("v1/", StringComparison.OrdinalIgnoreCase))
        {
            path = path[3..];
        }

        return path;
    }
}

internal sealed record TransparentProxyRequestClassification(
    TransparentProxyRequestKind Kind,
    string RelativePath,
    string? ModelName,
    bool StreamRequested)
{
    public bool IsModelsList => Kind == TransparentProxyRequestKind.ModelsList;
}

internal enum TransparentProxyRequestKind
{
    ModelsList,
    OpenAiChatCompletions,
    OpenAiResponses,
    AnthropicMessages,
    AnthropicCountTokens,
    Embeddings,
    GeminiNative,
    PassThrough
}
