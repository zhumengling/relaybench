using System.Net;
using System.Text.Json;

namespace NetTest.Core.Services;

public sealed partial class UnlockCatalogDiagnosticsService
{
    private static bool IsReachableStatus(HttpStatusCode statusCode)
        => (int)statusCode is >= 200 and < 400 or 401 or 403 or 404 or 405 or 429;

    private static string BuildVerdict(HttpStatusCode statusCode)
        => statusCode switch
        {
            HttpStatusCode.OK => "\u53ef\u8fbe",
            HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or HttpStatusCode.RedirectMethod or HttpStatusCode.TemporaryRedirect or HttpStatusCode.RedirectKeepVerb => "\u53ef\u8fbe\uff08\u53d1\u751f\u8df3\u8f6c\uff09",
            HttpStatusCode.Unauthorized => "\u53ef\u8fbe\uff08\u9700\u8981\u9274\u6743\uff09",
            HttpStatusCode.Forbidden => "\u53ef\u8fbe\uff08\u5b58\u5728\u6743\u9650 / \u98ce\u63a7 / \u5730\u533a\u9650\u5236\u53ef\u80fd\uff09",
            HttpStatusCode.NotFound => "\u5df2\u5230\u8fbe\u670d\u52a1\u7aef\uff08\u8d44\u6e90\u4e0d\u5b58\u5728\uff09",
            HttpStatusCode.MethodNotAllowed => "\u5df2\u5230\u8fbe\u670d\u52a1\u7aef\uff08\u65b9\u6cd5\u4e0d\u88ab\u5141\u8bb8\uff09",
            _ when (int)statusCode == 429 => "\u53ef\u8fbe\uff08\u89e6\u53d1\u9650\u6d41\uff09",
            _ when (int)statusCode is >= 200 and < 400 => "\u53ef\u8fbe",
            _ => "\u5f85\u590d\u6838"
        };

    private static async Task<string> ReadBodySampleAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var mediaType = response.Content.Headers.ContentType?.MediaType;
            if (!CanReadBodyAsText(mediaType))
            {
                return string.Empty;
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            body = body.Replace("\r\n", "\n", StringComparison.Ordinal);
            return body.Length <= 1500 ? body : body[..1500];
        }
        catch
        {
            return string.Empty;
        }
    }

    private static bool CanReadBodyAsText(string? mediaType)
    {
        if (string.IsNullOrWhiteSpace(mediaType))
        {
            return true;
        }

        return mediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
               || mediaType.Contains("json", StringComparison.OrdinalIgnoreCase)
               || mediaType.Contains("xml", StringComparison.OrdinalIgnoreCase)
               || mediaType.Contains("javascript", StringComparison.OrdinalIgnoreCase);
    }

    private static string? TryExtractJsonError(string bodySample)
    {
        if (string.IsNullOrWhiteSpace(bodySample))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(bodySample);

            if (document.RootElement.TryGetProperty("error", out var errorElement))
            {
                if (errorElement.ValueKind == JsonValueKind.String)
                {
                    return errorElement.GetString();
                }

                if (errorElement.ValueKind == JsonValueKind.Object)
                {
                    if (errorElement.TryGetProperty("message", out var messageElement))
                    {
                        return messageElement.GetString();
                    }

                    if (errorElement.TryGetProperty("type", out var typeElement))
                    {
                        return typeElement.GetString();
                    }
                }
            }

            if (document.RootElement.TryGetProperty("message", out var rootMessageElement))
            {
                return rootMessageElement.GetString();
            }
        }
        catch
        {
        }

        return null;
    }

    private static string BuildEvidence(string? finalUrl, string? contentType, string bodySample)
    {
        List<string> parts = [];

        if (!string.IsNullOrWhiteSpace(finalUrl))
        {
            parts.Add($"finalUrl={finalUrl}");
        }

        if (!string.IsNullOrWhiteSpace(contentType))
        {
            parts.Add($"contentType={contentType}");
        }

        if (!string.IsNullOrWhiteSpace(bodySample))
        {
            var singleLine = bodySample.Replace('\r', ' ').Replace('\n', ' ').Trim();
            if (singleLine.Length > 220)
            {
                singleLine = singleLine[..220] + "...";
            }

            parts.Add($"body={singleLine}");
        }

        return parts.Count == 0 ? "\u65e0\u989d\u5916\u8bc1\u636e" : string.Join("\uFF1B", parts);
    }

    private static bool BodyContains(string bodySample, string text)
        => bodySample.Contains(text, StringComparison.OrdinalIgnoreCase);

    private static bool BodyContainsAny(string bodySample, params string[] texts)
        => texts.Any(text => BodyContains(bodySample, text));

    private static HttpClient CreateHttpClient()
    {
        HttpClientHandler handler = new()
        {
            AutomaticDecompression = DecompressionMethods.All
        };

        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(8)
        };

        client.DefaultRequestHeaders.UserAgent.ParseAdd("NetTestSuite/0.8 (Windows desktop diagnostics)");
        client.DefaultRequestHeaders.Accept.ParseAdd("*/*");
        return client;
    }

    private sealed record UnlockDefinition(
        string Name,
        string Provider,
        string Url,
        HttpMethod Method,
        UnlockProbeKind Kind);

    private sealed record SemanticAssessment(
        string Category,
        string Verdict,
        string Summary,
        string? Evidence);

    private enum UnlockProbeKind
    {
        ChatGptWeb,
        ChatGptTrace,
        OpenAiApiModels,
        OpenAiPlatform,
        ClaudeWeb,
        AnthropicApi,
        GeminiWeb,
        GoogleAiStudio,
        GeminiApiModels,
        AntigravityWeb,
        PerplexityWeb,
        PerplexityApiModels,
        XAiApiModels,
        GroqApiModels,
        MistralApiModels,
        CohereApiModels,
        DeepSeekApiModels
    }

    private static class SemanticCategories
    {
        public const string Ready = "Ready";
        public const string AuthRequired = "AuthRequired";
        public const string RegionRestricted = "RegionRestricted";
        public const string ReviewRequired = "ReviewRequired";
        public const string Unreachable = "Unreachable";
    }
}
