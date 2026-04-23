using System.Net;
using RelayBench.Core.Support;

namespace RelayBench.Core.Services;

public sealed partial class UnlockCatalogDiagnosticsService
{
    private SemanticAssessment EvaluateSemantics(
        UnlockDefinition definition,
        HttpStatusCode statusCode,
        string? finalUrl,
        string? contentType,
        string bodySample)
    {
        return definition.Kind switch
        {
            UnlockProbeKind.WebApiTrace => EvaluateWebApiTrace(statusCode, bodySample),
            UnlockProbeKind.OpenAiApiModels => EvaluateOpenAiApi(statusCode, bodySample),
            UnlockProbeKind.AnthropicApi => EvaluateAnthropicApi(statusCode, bodySample),
            UnlockProbeKind.GeminiApiModels => EvaluateGenericProtectedApi("Gemini API", statusCode, bodySample),
            UnlockProbeKind.PerplexityApiModels => EvaluateGenericProtectedApi("Perplexity API", statusCode, bodySample),
            UnlockProbeKind.XAiApiModels => EvaluateGenericProtectedApi("xAI API", statusCode, bodySample),
            UnlockProbeKind.GroqApiModels => EvaluateGenericProtectedApi("Groq API", statusCode, bodySample),
            UnlockProbeKind.MistralApiModels => EvaluateGenericProtectedApi("Mistral API", statusCode, bodySample),
            UnlockProbeKind.CohereApiModels => EvaluateGenericProtectedApi("Cohere API", statusCode, bodySample),
            UnlockProbeKind.DeepSeekApiModels => EvaluateGenericProtectedApi("DeepSeek API", statusCode, bodySample),
            UnlockProbeKind.ChatGptWeb => EvaluateWebEntry("ChatGPT", statusCode, finalUrl, bodySample),
            UnlockProbeKind.OpenAiPlatform => EvaluateWebEntry("OpenAI Platform", statusCode, finalUrl, bodySample),
            UnlockProbeKind.ClaudeWeb => EvaluateWebEntry("Claude", statusCode, finalUrl, bodySample),
            UnlockProbeKind.GeminiWeb => EvaluateWebEntry("Gemini", statusCode, finalUrl, bodySample),
            UnlockProbeKind.GoogleAiStudio => EvaluateWebEntry("Google AI Studio", statusCode, finalUrl, bodySample),
            UnlockProbeKind.AntigravityWeb => EvaluateWebEntry("Google Antigravity", statusCode, finalUrl, bodySample),
            UnlockProbeKind.PerplexityWeb => EvaluateWebEntry("Perplexity", statusCode, finalUrl, bodySample),
            _ => new SemanticAssessment(
                SemanticCategories.ReviewRequired,
                "\u8bed\u4e49\u5f85\u590d\u6838",
                $"\u5df2\u5230\u8fbe {definition.Name}\uFF0C\u4f46\u5f53\u524d\u6ca1\u6709\u4e3a\u8be5\u76ee\u6807\u5b9a\u4e49\u66f4\u7ec6\u7684\u4e1a\u52a1\u8bed\u4e49\u89c4\u5219\u3002",
                BuildEvidence(finalUrl, contentType, bodySample))
        };
    }

    private SemanticAssessment EvaluateWebApiTrace(HttpStatusCode statusCode, string bodySample)
    {
        if (statusCode != HttpStatusCode.OK)
        {
            return new SemanticAssessment(
                SemanticCategories.ReviewRequired,
                "Trace \u63a5\u53e3\u5f02\u5e38",
                "Trace \u63a5\u53e3\u6ca1\u6709\u8fd4\u56de\u6807\u51c6 200 \u7ed3\u679c\uFF0C\u5730\u533a\u5224\u65ad\u9700\u8981\u590d\u6838\u3002",
                bodySample);
        }

        var values = TraceDocumentParser.Parse(bodySample);
        values.TryGetValue("loc", out var locationCode);
        values.TryGetValue("colo", out var colo);
        values.TryGetValue("ip", out var publicIp);

        if (string.IsNullOrWhiteSpace(locationCode))
        {
            return new SemanticAssessment(
                SemanticCategories.ReviewRequired,
                "Trace \u53ef\u8fbe\u4f46\u7f3a\u5c11 loc",
                "Trace \u5df2\u8fd4\u56de\u5185\u5bb9\uFF0C\u4f46\u7f3a\u5c11 loc \u5b57\u6bb5\uFF0C\u65e0\u6cd5\u76f4\u63a5\u5224\u65ad\u7f51\u9875 API \u5730\u533a\u53ef\u7528\u6027\u3002",
                $"ip={publicIp ?? "--"}\uFF1Bcolo={colo ?? "--"}");
        }

        var isSupported = _openAiSupportedRegionCatalog.IsSupported(locationCode);
        var locationName = _openAiSupportedRegionCatalog.TryGetRegionName(locationCode) ?? locationCode;
        return isSupported
            ? new SemanticAssessment(
                SemanticCategories.Ready,
                "\u7f51\u9875 API \u5730\u533a\u547d\u4e2d\u652f\u6301\u8303\u56f4",
                $"{locationName}\uFF08{locationCode}\uFF09\u5728\u5f53\u524d\u5185\u7f6e OpenAI \u652f\u6301\u5730\u533a\u76ee\u5f55\u4e2d\uFF0C\u53ef\u89c6\u4e3a\u7f51\u9875 API Trace \u8bed\u4e49\u901a\u8fc7\u3002",
                $"ip={publicIp ?? "--"}\uFF1Bloc={locationCode}\uFF1Bcolo={colo ?? "--"}")
            : new SemanticAssessment(
                SemanticCategories.RegionRestricted,
                "\u7f51\u9875 API Trace \u63d0\u793a\u5730\u533a\u4e0d\u5728\u652f\u6301\u8303\u56f4",
                $"{locationName}\uFF08{locationCode}\uFF09\u4e0d\u5728\u5f53\u524d\u5185\u7f6e OpenAI \u652f\u6301\u5730\u533a\u76ee\u5f55\u4e2d\uFF0C\u7591\u4f3c\u5b58\u5728\u5730\u533a\u9650\u5236\u3002",
                $"ip={publicIp ?? "--"}\uFF1Bloc={locationCode}\uFF1Bcolo={colo ?? "--"}");
    }

    private static SemanticAssessment EvaluateOpenAiApi(HttpStatusCode statusCode, string bodySample)
    {
        var jsonError = TryExtractJsonError(bodySample);

        if (statusCode == HttpStatusCode.OK)
        {
            return new SemanticAssessment(
                SemanticCategories.Ready,
                "OpenAI API \u53ef\u76f4\u63a5\u4f7f\u7528",
                "OpenAI API /models \u8fd4\u56de 200\uFF0C\u8bf4\u660e\u5f53\u524d\u7f51\u7edc\u81f3\u5c11\u5177\u5907\u57fa\u7840 API \u53ef\u7528\u6027\u3002",
                jsonError ?? "HTTP 200");
        }

        if (statusCode == HttpStatusCode.Unauthorized || BodyContainsAny(bodySample, "missing_api_key", "invalid_api_key", "api key", "authorization"))
        {
            return new SemanticAssessment(
                SemanticCategories.AuthRequired,
                "OpenAI API \u8fb9\u7f18\u53ef\u8fbe\uFF0C\u4f46\u9700\u8981\u6709\u6548 API Key",
                "\u8bf7\u6c42\u5df2\u7ecf\u6253\u5230 OpenAI API \u8fb9\u7f18\uFF0C\u53ea\u662f\u56e0\u4e3a\u7f3a\u5c11\u6216\u65e0\u6548\u5bc6\u94a5\u800c\u5931\u8d25\u3002",
                jsonError ?? bodySample);
        }

        if (statusCode == HttpStatusCode.Forbidden && ContainsRegionSignal(bodySample))
        {
            return new SemanticAssessment(
                SemanticCategories.RegionRestricted,
                "OpenAI API \u7591\u4f3c\u5730\u533a\u9650\u5236",
                "\u63a5\u53e3\u8fb9\u7f18\u53ef\u8fbe\uFF0C\u4f46\u8fd4\u56de\u5185\u5bb9\u6307\u5411\u56fd\u5bb6\u6216\u5730\u533a\u9650\u5236\uFF0C\u9700\u8981\u7ed3\u5408\u4ee3\u7406\u51fa\u53e3\u4f4d\u7f6e\u590d\u6838\u3002",
                jsonError ?? bodySample);
        }

        if ((int)statusCode == 429)
        {
            return new SemanticAssessment(
                SemanticCategories.ReviewRequired,
                "OpenAI API \u53ef\u8fbe\uFF0C\u4f46\u89e6\u53d1\u9650\u6d41",
                "API \u5df2\u7ecf\u53ef\u8fbe\uFF0C\u4f46\u5f53\u524d\u8bf7\u6c42\u88ab\u9650\u6d41\uFF0C\u4e1a\u52a1\u8bed\u4e49\u4e0d\u80fd\u7b80\u5355\u89c6\u4e3a\u4e0d\u53ef\u7528\u3002",
                jsonError ?? bodySample);
        }

        if (statusCode == HttpStatusCode.NotFound || statusCode == HttpStatusCode.MethodNotAllowed)
        {
            return new SemanticAssessment(
                SemanticCategories.ReviewRequired,
                "OpenAI API \u8fb9\u7f18\u53ef\u8fbe\uFF0C\u4f46\u8bf7\u6c42\u8def\u5f84\u6216\u65b9\u6cd5\u5f85\u590d\u6838",
                "\u51fa\u73b0 404 / 405 \u901a\u5e38\u4ee3\u8868\u8fb9\u7f18\u5df2\u5230\u8fbe\uFF0C\u4f46\u5f53\u524d\u8bf7\u6c42\u6761\u4ef6\u4e0d\u80fd\u76f4\u63a5\u8bf4\u660e\u4e1a\u52a1\u5b8c\u5168\u53ef\u7528\u3002",
                jsonError ?? bodySample);
        }

        return new SemanticAssessment(
            SemanticCategories.ReviewRequired,
            "OpenAI API \u7ed3\u679c\u5f85\u590d\u6838",
            "\u63a5\u53e3\u5df2\u8fd4\u56de\u54cd\u5e94\uFF0C\u4f46\u5f53\u524d\u54cd\u5e94\u4e0d\u80fd\u76f4\u63a5\u5224\u65ad\u4e3a\u4e1a\u52a1\u5c31\u7eea\u6216\u660e\u786e\u53d7\u9650\u3002",
            jsonError ?? bodySample);
    }

    private static SemanticAssessment EvaluateAnthropicApi(HttpStatusCode statusCode, string bodySample)
        => EvaluateGenericProtectedApi("Anthropic API", statusCode, bodySample);

    private static SemanticAssessment EvaluateGenericProtectedApi(string productName, HttpStatusCode statusCode, string bodySample)
    {
        var jsonError = TryExtractJsonError(bodySample);
        var evidence = jsonError ?? bodySample;

        if (statusCode == HttpStatusCode.OK)
        {
            return new SemanticAssessment(
                SemanticCategories.Ready,
                $"{productName} \u53ef\u76f4\u63a5\u4f7f\u7528",
                $"{productName} \u8fd4\u56de 200\uFF0C\u8bf4\u660e\u8be5\u5b98\u65b9 API \u5165\u53e3\u81f3\u5c11\u5177\u5907\u57fa\u7840\u4e1a\u52a1\u53ef\u7528\u6027\u3002",
                string.IsNullOrWhiteSpace(evidence) ? "HTTP 200" : evidence);
        }

        if (statusCode == HttpStatusCode.Forbidden && ContainsRegionSignal(bodySample))
        {
            return new SemanticAssessment(
                SemanticCategories.RegionRestricted,
                $"{productName} \u7591\u4f3c\u5730\u533a\u9650\u5236",
                "\u63a5\u53e3\u8fb9\u7f18\u53ef\u8fbe\uFF0C\u4f46\u8fd4\u56de\u5185\u5bb9\u6307\u5411\u56fd\u5bb6\u3001\u5730\u533a\u6216\u8bbf\u95ee\u7b56\u7565\u9650\u5236\u3002",
                evidence);
        }

        if (statusCode == HttpStatusCode.Unauthorized || ContainsAuthSignal(bodySample))
        {
            return new SemanticAssessment(
                SemanticCategories.AuthRequired,
                $"{productName} \u8fb9\u7f18\u53ef\u8fbe\uFF0C\u4f46\u9700\u8981\u9274\u6743",
                $"\u8bf7\u6c42\u5df2\u7ecf\u5230\u8fbe {productName}\uFF0C\u53ea\u662f\u56e0\u4e3a\u5f53\u524d\u7f3a\u5c11\u6709\u6548\u9274\u6743\u6761\u4ef6\u800c\u5931\u8d25\u3002",
                evidence);
        }

        if ((int)statusCode == 429)
        {
            return new SemanticAssessment(
                SemanticCategories.ReviewRequired,
                $"{productName} \u53ef\u8fbe\uFF0C\u4f46\u89e6\u53d1\u9650\u6d41",
                "\u63a5\u53e3\u5df2\u8fd4\u56de\u9650\u6d41\u54cd\u5e94\uFF0C\u8bf4\u660e\u7f51\u7edc\u4fa7\u5927\u6982\u7387\u53ef\u8fbe\uFF0C\u4f46\u5f53\u524d\u7ed3\u679c\u4ecd\u9700\u7ed3\u5408\u914d\u989d\u4e0e\u98ce\u63a7\u590d\u6838\u3002",
                evidence);
        }

        if (statusCode == HttpStatusCode.BadRequest ||
            statusCode == HttpStatusCode.NotFound ||
            statusCode == HttpStatusCode.MethodNotAllowed)
        {
            return new SemanticAssessment(
                SemanticCategories.ReviewRequired,
                $"{productName} \u8fb9\u7f18\u53ef\u8fbe\uFF0C\u4f46\u8bf7\u6c42\u65b9\u5f0f\u5f85\u590d\u6838",
                "\u63a5\u53e3\u5df2\u7ecf\u8fd4\u56de\u4e1a\u52a1\u4fa7\u54cd\u5e94\uFF0C\u4f46\u5f53\u524d\u63a2\u6d4b\u65b9\u6cd5\u3001\u8def\u5f84\u6216\u53c2\u6570\u8fd8\u4e0d\u80fd\u76f4\u63a5\u8bf4\u660e\u4e1a\u52a1\u5b8c\u5168\u53ef\u7528\u3002",
                evidence);
        }

        return new SemanticAssessment(
            SemanticCategories.ReviewRequired,
            $"{productName} \u7ed3\u679c\u5f85\u590d\u6838",
            "\u63a5\u53e3\u5df2\u8fd4\u56de\u5185\u5bb9\uFF0C\u4f46\u5f53\u524d\u72b6\u6001\u7801\u4e0e\u54cd\u5e94\u6b63\u6587\u4e0d\u8db3\u4ee5\u76f4\u63a5\u5224\u65ad\u4e3a\u4e1a\u52a1\u5c31\u7eea\u6216\u660e\u786e\u53d7\u9650\u3002",
            evidence);
    }

    private static SemanticAssessment EvaluateWebEntry(string productName, HttpStatusCode statusCode, string? finalUrl, string bodySample)
    {
        if ((int)statusCode is >= 200 and < 400)
        {
            var loweredFinalUrl = finalUrl?.ToLowerInvariant() ?? string.Empty;
            if (loweredFinalUrl.Contains("login", StringComparison.Ordinal) || loweredFinalUrl.Contains("auth", StringComparison.Ordinal) || loweredFinalUrl.Contains("signin", StringComparison.Ordinal))
            {
                return new SemanticAssessment(
                    SemanticCategories.AuthRequired,
                    $"{productName} \u7f51\u9875\u5165\u53e3\u53ef\u8fbe\uFF0C\u540e\u7eed\u9700\u8981\u767b\u5f55",
                    "\u7f51\u9875\u5165\u53e3\u5df2\u7ecf\u80fd\u6253\u5f00\uFF0C\u5f53\u524d\u66f4\u50cf\u662f\u8d26\u53f7 / \u767b\u5f55\u6001\u95ee\u9898\uFF0C\u800c\u4e0d\u662f\u7eaf\u7f51\u7edc\u4e0d\u53ef\u8fbe\u3002",
                    BuildEvidence(finalUrl, null, bodySample));
            }

            if (BodyContainsAny(bodySample, "captcha", "access denied", "verify you are human", "challenge"))
            {
                return new SemanticAssessment(
                    SemanticCategories.ReviewRequired,
                    $"{productName} \u7f51\u9875\u53ef\u8fbe\uFF0C\u4f46\u7591\u4f3c\u89e6\u53d1\u98ce\u63a7",
                    "\u7f51\u9875\u5df2\u7ecf\u8fd4\u56de\u4e3b\u4f53\u5185\u5bb9\uFF0C\u4f46\u51fa\u73b0\u98ce\u63a7\u6216\u9a8c\u8bc1\u4fe1\u53f7\uFF0C\u4e1a\u52a1\u80fd\u5426\u6b63\u5e38\u4f7f\u7528\u4ecd\u9700\u590d\u6838\u3002",
                    BuildEvidence(finalUrl, null, bodySample));
            }

            return new SemanticAssessment(
                SemanticCategories.Ready,
                $"{productName} \u7f51\u9875\u5165\u53e3\u53ef\u8fbe",
                "\u7f51\u9875\u5165\u53e3\u5df2\u7ecf\u5efa\u7acb\u6b63\u5e38 HTTP \u4f1a\u8bdd\uFF0C\u53ef\u89c6\u4e3a\u4e1a\u52a1\u5165\u53e3\u7ea7\u522b\u53ef\u8fbe\u3002",
                BuildEvidence(finalUrl, null, bodySample));
        }

        if (statusCode == HttpStatusCode.Forbidden && ContainsRegionSignal(bodySample))
        {
            return new SemanticAssessment(
                SemanticCategories.RegionRestricted,
                $"{productName} \u7f51\u9875\u7591\u4f3c\u5730\u533a\u9650\u5236",
                "\u7f51\u9875\u8fb9\u7f18\u53ef\u8fbe\uFF0C\u4f46\u8fd4\u56de\u5185\u5bb9\u5305\u542b\u56fd\u5bb6\u6216\u5730\u533a\u76f8\u5173\u9650\u5236\u4fe1\u53f7\u3002",
                BuildEvidence(finalUrl, null, bodySample));
        }

        if (statusCode == HttpStatusCode.Forbidden || statusCode == HttpStatusCode.Unauthorized)
        {
            return new SemanticAssessment(
                SemanticCategories.AuthRequired,
                $"{productName} \u7f51\u9875\u8fb9\u7f18\u53ef\u8fbe\uFF0C\u4f46\u9700\u8d26\u53f7\u6216\u989d\u5916\u9a8c\u8bc1",
                "\u7ad9\u70b9\u5df2\u7ecf\u8fd4\u56de\u4e1a\u52a1\u4fa7\u54cd\u5e94\uFF0C\u95ee\u9898\u66f4\u53ef\u80fd\u5728\u767b\u5f55\u3001\u98ce\u63a7\u6216\u6743\u9650\u5c42\uFF0C\u800c\u4e0d\u53ea\u662f\u7f51\u7edc\u5c42\u3002",
                BuildEvidence(finalUrl, null, bodySample));
        }

        return new SemanticAssessment(
            SemanticCategories.ReviewRequired,
            $"{productName} \u7f51\u9875\u7ed3\u679c\u5f85\u590d\u6838",
            "\u7ad9\u70b9\u5df2\u7ecf\u8fd4\u56de\u54cd\u5e94\uFF0C\u4f46\u5f53\u524d\u72b6\u6001\u8fd8\u4e0d\u8db3\u4ee5\u5224\u65ad\u4e1a\u52a1\u662f\u5426\u771f\u6b63\u53ef\u7528\u3002",
            BuildEvidence(finalUrl, null, bodySample));
    }

    private static bool ContainsAuthSignal(string bodySample)
        => BodyContainsAny(
            bodySample,
            "api key",
            "api_key",
            "apikey",
            "missing_api_key",
            "invalid_api_key",
            "authorization",
            "authentication",
            "unauthorized",
            "access token",
            "bearer",
            "x-api-key",
            "requires an api key",
            "permission denied",
            "permission");

    private static bool ContainsRegionSignal(string bodySample)
        => BodyContainsAny(
            bodySample,
            "unsupported_country_region_territory",
            "unsupported country",
            "country",
            "region",
            "geo",
            "location",
            "not available in your country");
}
