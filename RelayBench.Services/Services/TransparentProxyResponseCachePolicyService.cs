using System.Security.Cryptography;
using System.Text;

namespace RelayBench.Services;

internal sealed class TransparentProxyResponseCachePolicyService
{
    private static readonly IReadOnlyDictionary<string, string> BypassReasonLabels =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["disabled"] = "缓存未启用",
            ["stream"] = "流式请求",
            ["method"] = "请求方法不支持",
            ["large-body"] = "请求体过大",
            ["tool-file-image"] = "包含工具、文件或图片",
            ["unsupported"] = "无法生成稳定缓存键"
        };

    public TransparentProxyResponseCacheDecision CreateDecision(
        TransparentProxyServerConfig config,
        TransparentProxyRoute route,
        bool streamRequested,
        string method,
        string pathAndQuery,
        byte[] requestBody,
        string? requestedModel)
    {
        var scopeId = ResolveScopeId(route, requestedModel);
        if (!config.EnableCache)
        {
            return BuildBypass("disabled", scopeId);
        }

        if (streamRequested)
        {
            return BuildBypass("stream", scopeId);
        }

        if (!IsResponseCacheSupportedPath(pathAndQuery))
        {
            return BuildBypass("unsupported", scopeId);
        }

        var supported = TransparentProxyResponseCacheService.TryBuildResponseCacheKey(
            method,
            pathAndQuery,
            requestBody,
            scopeId,
            requestedModel,
            out var cacheKey,
            out var rejectReason);
        if (!supported)
        {
            var reason = string.IsNullOrWhiteSpace(rejectReason) ? "unsupported" : rejectReason;
            return BuildBypass(reason, scopeId);
        }

        return new TransparentProxyResponseCacheDecision(
            CanUseCache: true,
            CacheKey: cacheKey,
            ScopeId: scopeId,
            BypassReason: string.Empty,
            BypassReasonLabel: string.Empty,
            KeyPreview: BuildKeyPreview(cacheKey));
    }

    public object BuildPolicyPayload()
        => new
        {
            bypassReasons = BypassReasonLabels
                .Select(static item => new
                {
                    code = item.Key,
                    label = item.Value
                })
                .OrderBy(static item => item.code, StringComparer.OrdinalIgnoreCase)
                .ToArray()
        };

    private static TransparentProxyResponseCacheDecision BuildBypass(string reason, string scopeId)
        => new(
            CanUseCache: false,
            CacheKey: string.Empty,
            ScopeId: scopeId,
            BypassReason: reason,
            BypassReasonLabel: ResolveBypassReasonLabel(reason),
            KeyPreview: string.Empty);

    private static string ResolveScopeId(TransparentProxyRoute route, string? requestedModel)
    {
        var model = requestedModel?.Trim();
        if (string.IsNullOrWhiteSpace(model) ||
            !string.IsNullOrWhiteSpace(route.PayloadRulesText) ||
            !TransparentProxySchedulerService.HasExplicitRouteModelMatch(route, model))
        {
            return route.CacheScopeId;
        }

        return $"model-pool:{model.ToLowerInvariant()}";
    }

    private static string ResolveBypassReasonLabel(string reason)
        => BypassReasonLabels.TryGetValue(reason, out var label)
            ? label
            : reason;

    private static bool IsResponseCacheSupportedPath(string pathAndQuery)
    {
        var path = NormalizePath(pathAndQuery);
        if (path.Equals("chat/completions", StringComparison.OrdinalIgnoreCase) ||
            path.Equals("completions", StringComparison.OrdinalIgnoreCase) ||
            path.Equals("responses", StringComparison.OrdinalIgnoreCase) ||
            path.Equals("responses/compact", StringComparison.OrdinalIgnoreCase) ||
            path.Equals("messages", StringComparison.OrdinalIgnoreCase) ||
            path.Equals("messages/count_tokens", StringComparison.OrdinalIgnoreCase) ||
            path.Equals("embeddings", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return path.StartsWith("models/", StringComparison.OrdinalIgnoreCase) &&
               (path.EndsWith(":generateContent", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith(":streamGenerateContent", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith(":countTokens", StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizePath(string pathAndQuery)
    {
        var path = pathAndQuery.Trim().TrimStart('/');
        var queryIndex = path.IndexOf('?');
        if (queryIndex >= 0)
        {
            path = path[..queryIndex];
        }

        while (path.StartsWith("v1/v1/", StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith("v1beta/v1beta/", StringComparison.OrdinalIgnoreCase))
        {
            path = path.StartsWith("v1beta/", StringComparison.OrdinalIgnoreCase)
                ? path["v1beta/".Length..]
                : path[3..];
        }

        if (path.StartsWith("backend-api/codex/", StringComparison.OrdinalIgnoreCase))
        {
            path = path["backend-api/codex/".Length..];
        }
        else if (path.StartsWith("codex/v1/", StringComparison.OrdinalIgnoreCase))
        {
            path = path["codex/v1/".Length..];
        }

        if (path.StartsWith("v1beta/", StringComparison.OrdinalIgnoreCase))
        {
            path = path["v1beta/".Length..];
        }
        else if (path.StartsWith("v1/", StringComparison.OrdinalIgnoreCase))
        {
            path = path[3..];
        }

        return path.Trim('/');
    }

    private static string BuildKeyPreview(string cacheKey)
    {
        if (string.IsNullOrWhiteSpace(cacheKey))
        {
            return string.Empty;
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(cacheKey));
        return Convert.ToHexString(hash[..6]).ToLowerInvariant();
    }
}

internal sealed record TransparentProxyResponseCacheDecision(
    bool CanUseCache,
    string CacheKey,
    string ScopeId,
    string BypassReason,
    string BypassReasonLabel,
    string KeyPreview);
