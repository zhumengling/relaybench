using System.Diagnostics;
using System.Net;
using System.Text;
using NetTest.Core.Models;
using NetTest.Core.Support;

namespace NetTest.Core.Services;

public sealed partial class UnlockCatalogDiagnosticsService
{
    private static readonly HttpClient HttpClient = CreateHttpClient();
    private static readonly IReadOnlyList<UnlockDefinition> Definitions =
    [
        new("ChatGPT Web", "OpenAI", "https://chatgpt.com/", HttpMethod.Get, UnlockProbeKind.ChatGptWeb),
        new("ChatGPT Trace", "OpenAI", "https://chatgpt.com/cdn-cgi/trace", HttpMethod.Get, UnlockProbeKind.ChatGptTrace),
        new("OpenAI API /models", "OpenAI", "https://api.openai.com/v1/models", HttpMethod.Get, UnlockProbeKind.OpenAiApiModels),
        new("OpenAI Platform", "OpenAI", "https://platform.openai.com/", HttpMethod.Get, UnlockProbeKind.OpenAiPlatform),
        new("Claude Web", "Anthropic", "https://claude.ai/", HttpMethod.Get, UnlockProbeKind.ClaudeWeb),
        new("Anthropic API /messages", "Anthropic", "https://api.anthropic.com/v1/messages", HttpMethod.Post, UnlockProbeKind.AnthropicApi),
        new("Gemini Web", "Google", "https://gemini.google.com/", HttpMethod.Get, UnlockProbeKind.GeminiWeb),
        new("Google AI Studio", "Google", "https://aistudio.google.com/", HttpMethod.Get, UnlockProbeKind.GoogleAiStudio),
        new("Gemini API /models", "Google", "https://generativelanguage.googleapis.com/v1beta/models", HttpMethod.Get, UnlockProbeKind.GeminiApiModels),
        new("Google Antigravity", "Google", "https://antigravity.google/", HttpMethod.Get, UnlockProbeKind.AntigravityWeb),
        new("Perplexity Web", "Perplexity", "https://www.perplexity.ai/", HttpMethod.Get, UnlockProbeKind.PerplexityWeb),
        new("Perplexity API /models", "Perplexity", "https://api.perplexity.ai/v1/models", HttpMethod.Get, UnlockProbeKind.PerplexityApiModels),
        new("xAI API /models", "xAI", "https://api.x.ai/v1/models", HttpMethod.Get, UnlockProbeKind.XAiApiModels),
        new("Groq API /models", "Groq", "https://api.groq.com/openai/v1/models", HttpMethod.Get, UnlockProbeKind.GroqApiModels),
        new("Mistral API /models", "Mistral", "https://api.mistral.ai/v1/models", HttpMethod.Get, UnlockProbeKind.MistralApiModels),
        new("Cohere API /models", "Cohere", "https://api.cohere.com/v1/models", HttpMethod.Get, UnlockProbeKind.CohereApiModels),
        new("DeepSeek API /models", "DeepSeek", "https://api.deepseek.com/models", HttpMethod.Get, UnlockProbeKind.DeepSeekApiModels)
    ];

    private readonly OpenAiSupportedRegionCatalog _openAiSupportedRegionCatalog = new();

    public async Task<UnlockCatalogResult> RunAsync(
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        List<UnlockEndpointCheck> checks = new(Definitions.Count);
        foreach (var definition in Definitions.Select((value, index) => new { value, index }))
        {
            progress?.Report($"\u6b63\u5728\u68c0\u67e5\u5b98\u65b9\u5165\u53e3 / API {definition.index + 1}/{Definitions.Count}\uFF1A{definition.value.Name}");
            checks.Add(await ProbeAsync(definition.value, cancellationToken));
        }

        var reachableCount = checks.Count(check => check.Reachable);
        var semanticReadyCount = checks.Count(check => string.Equals(check.SemanticCategory, SemanticCategories.Ready, StringComparison.OrdinalIgnoreCase));
        var authenticationRequiredCount = checks.Count(check => string.Equals(check.SemanticCategory, SemanticCategories.AuthRequired, StringComparison.OrdinalIgnoreCase));
        var regionRestrictedCount = checks.Count(check => string.Equals(check.SemanticCategory, SemanticCategories.RegionRestricted, StringComparison.OrdinalIgnoreCase));
        var reviewRequiredCount = checks.Count(check => string.Equals(check.SemanticCategory, SemanticCategories.ReviewRequired, StringComparison.OrdinalIgnoreCase));

        var providerSummary = string.Join(
            "\uFF1B",
            checks.GroupBy(check => check.Provider)
                .Select(group =>
                    $"{group.Key} \u53ef\u8fbe {group.Count(check => check.Reachable)}/{group.Count()}\uFF0C\u4e1a\u52a1\u5c31\u7eea {group.Count(check => check.SemanticCategory == SemanticCategories.Ready)}/{group.Count()}"));

        var summary =
            $"\u5171\u5b8c\u6210 {checks.Count} \u4e2a\u5b98\u65b9\u5165\u53e3 / API \u63a2\u6d4b\uFF1B" +
            $"\u7f51\u7edc\u53ef\u8fbe {reachableCount}/{checks.Count}\uFF1B" +
            $"\u4e1a\u52a1\u5c31\u7eea {semanticReadyCount}/{checks.Count}\uFF1B" +
            $"\u9700\u9274\u6743 {authenticationRequiredCount}\uFF1B" +
            $"\u7591\u4f3c\u5730\u533a\u9650\u5236 {regionRestrictedCount}\uFF1B" +
            $"\u5f85\u590d\u6838 {reviewRequiredCount}\u3002" +
            (string.IsNullOrWhiteSpace(providerSummary) ? string.Empty : $" \u5382\u5546\u6982\u89c8\uFF1A{providerSummary}");

        var error = reachableCount == 0 ? "\u5b98\u65b9\u5165\u53e3 / API \u76ee\u5f55\u4e2d\u7684\u6240\u6709\u63a2\u6d4b\u76ee\u6807\u90fd\u4e0d\u53ef\u8fbe\u3002" : null;

        return new UnlockCatalogResult(
            DateTimeOffset.Now,
            checks,
            reachableCount,
            semanticReadyCount,
            authenticationRequiredCount,
            regionRestrictedCount,
            reviewRequiredCount,
            summary,
            error);
    }

    private async Task<UnlockEndpointCheck> ProbeAsync(UnlockDefinition definition, CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(definition.Method, definition.Url);
        ConfigureRequest(definition, request);
        var stopwatch = Stopwatch.StartNew();

        try
        {
            using var response = await HttpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);
            stopwatch.Stop();

            var statusCode = (int)response.StatusCode;
            var verdict = BuildVerdict(response.StatusCode);
            var reachable = IsReachableStatus(response.StatusCode);
            var finalUrl = response.RequestMessage?.RequestUri?.ToString();
            var contentType = response.Content.Headers.ContentType?.MediaType;
            var bodySample = await ReadBodySampleAsync(response, cancellationToken);
            var semantic = EvaluateSemantics(definition, response.StatusCode, finalUrl, contentType, bodySample);
            var summary = $"{definition.Name}\uFF1AHTTP {statusCode}\uFF0C\u8017\u65f6 {stopwatch.Elapsed.TotalMilliseconds:F0} ms\uFF0C\u7ed3\u8bba\uFF1A{verdict}\u3002";

            return new UnlockEndpointCheck(
                definition.Name,
                definition.Provider,
                definition.Url,
                definition.Method.Method,
                reachable,
                statusCode,
                stopwatch.Elapsed,
                verdict,
                semantic.Category,
                semantic.Verdict,
                summary,
                semantic.Summary,
                semantic.Evidence,
                finalUrl,
                contentType,
                null);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return new UnlockEndpointCheck(
                definition.Name,
                definition.Provider,
                definition.Url,
                definition.Method.Method,
                false,
                null,
                stopwatch.Elapsed,
                "\u4e0d\u53ef\u8fbe",
                SemanticCategories.Unreachable,
                "\u7f51\u7edc\u4e0d\u53ef\u8fbe",
                $"{definition.Name}\uFF1A\u8fde\u63a5\u5931\u8d25\u3002",
                "\u6ca1\u6709\u5efa\u7acb\u5230\u76ee\u6807\u7ad9\u70b9\u7684\u6709\u6548 HTTP \u8fde\u63a5\uFF0C\u65e0\u6cd5\u7ee7\u7eed\u5224\u65ad\u4e1a\u52a1\u8bed\u4e49\u3002",
                ex.Message,
                null,
                null,
                ex.Message);
        }
    }

    private static void ConfigureRequest(UnlockDefinition definition, HttpRequestMessage request)
    {
        if (definition.Kind != UnlockProbeKind.AnthropicApi)
        {
            return;
        }

        request.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
        request.Content = new StringContent(
            """
            {
              "model": "claude-3-5-sonnet-latest",
              "max_tokens": 16,
              "messages": [
                {
                  "role": "user",
                  "content": "ping"
                }
              ]
            }
            """,
            Encoding.UTF8,
            "application/json");
    }
}
