using System.Net;
using System.Text.Json;

namespace RelayBench.App.Services;

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
            TryReadRequestModel(body),
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

        if (relativePath.EndsWith("responses", StringComparison.OrdinalIgnoreCase))
        {
            return TransparentProxyRequestKind.OpenAiResponses;
        }

        if (relativePath.EndsWith("messages", StringComparison.OrdinalIgnoreCase))
        {
            return TransparentProxyRequestKind.AnthropicMessages;
        }

        if (relativePath.EndsWith("chat/completions", StringComparison.OrdinalIgnoreCase))
        {
            return TransparentProxyRequestKind.OpenAiChatCompletions;
        }

        if (relativePath.EndsWith("embeddings", StringComparison.OrdinalIgnoreCase))
        {
            return TransparentProxyRequestKind.Embeddings;
        }

        return TransparentProxyRequestKind.PassThrough;
    }

    private static bool IsStreamingRequest(HttpListenerRequest request, byte[] body)
    {
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

    private static string ExtractRelativePath(string pathAndQuery)
    {
        var path = pathAndQuery;
        var queryIndex = path.IndexOf('?');
        if (queryIndex >= 0)
        {
            path = path[..queryIndex];
        }

        return path.Trim('/').Trim();
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
    Embeddings,
    PassThrough
}
