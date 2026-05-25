using System.Text.RegularExpressions;

namespace RelayBench.Services;

internal static class TransparentProxyModelAliasResolver
{
    public static IReadOnlyList<string> ResolveUpstreamModelCandidates(string clientModel, TransparentProxyRoute route)
    {
        var model = StripRoutePrefix(clientModel.Trim(), route);
        if (string.IsNullOrWhiteSpace(model))
        {
            return Array.Empty<string>();
        }

        var requestSuffix = ParseSuffix(model);
        var candidates = BuildLookupCandidates(model, requestSuffix);
        var matches = route.ModelMappings
            .Where(mapping => MatchesMapping(mapping, candidates))
            .Select(mapping => PreserveRequestedSuffix(mapping.Name.Trim(), requestSuffix))
            .Where(static item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return matches.Length > 0 ? matches : [model];
    }

    public static bool CanRouteServeModel(TransparentProxyRoute route, string? requestedModel)
    {
        var model = requestedModel?.Trim();
        if (string.IsNullOrWhiteSpace(model))
        {
            return true;
        }

        model = StripRoutePrefix(model, route);
        if (IsExcluded(route, model))
        {
            return false;
        }

        return route.Models.Count == 0 || HasExplicitRouteModelMatch(route, requestedModel);
    }

    public static bool HasExplicitRouteModelMatch(TransparentProxyRoute route, string? requestedModel)
    {
        var model = requestedModel?.Trim();
        if (string.IsNullOrWhiteSpace(model))
        {
            return false;
        }

        model = StripRoutePrefix(model, route);
        if (IsExcluded(route, model))
        {
            return false;
        }

        var suffix = ParseSuffix(model);
        var candidates = BuildLookupCandidates(model, suffix);
        return route.ModelMappings.Any(mapping => MatchesMapping(mapping, candidates));
    }

    public static string StripRoutePrefix(string model, TransparentProxyRoute route)
    {
        var prefix = route.Prefix.Trim().Trim('/');
        return !string.IsNullOrWhiteSpace(prefix) &&
               model.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase)
            ? model[(prefix.Length + 1)..]
            : model;
    }

    private static bool MatchesMapping(TransparentProxyModelMapping mapping, IReadOnlyList<string> candidates)
        => candidates.Any(candidate =>
            string.Equals(mapping.Name, candidate, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(mapping.EffectiveAlias, candidate, StringComparison.OrdinalIgnoreCase));

    private static IReadOnlyList<string> BuildLookupCandidates(string model, ModelSuffix suffix)
    {
        if (!suffix.HasSuffix || string.Equals(suffix.ModelName, model, StringComparison.OrdinalIgnoreCase))
        {
            return [model];
        }

        return [suffix.ModelName, model];
    }

    private static bool IsExcluded(TransparentProxyRoute route, string model)
    {
        var suffix = ParseSuffix(model);
        return route.ExcludedModelPatterns.Any(pattern =>
            WildcardMatch(model, pattern) ||
            (suffix.HasSuffix && WildcardMatch(suffix.ModelName, pattern)));
    }

    private static string PreserveRequestedSuffix(string resolvedModel, ModelSuffix requestSuffix)
    {
        if (string.IsNullOrWhiteSpace(resolvedModel))
        {
            return string.Empty;
        }

        if (ParseSuffix(resolvedModel).HasSuffix)
        {
            return resolvedModel;
        }

        return requestSuffix.HasSuffix && !string.IsNullOrWhiteSpace(requestSuffix.RawSuffix)
            ? $"{resolvedModel}({requestSuffix.RawSuffix})"
            : resolvedModel;
    }

    private static ModelSuffix ParseSuffix(string value)
    {
        var text = value.Trim();
        if (!text.EndsWith(')'))
        {
            return new ModelSuffix(text, string.Empty, false);
        }

        var openIndex = text.LastIndexOf('(');
        if (openIndex <= 0 || openIndex >= text.Length - 2)
        {
            return new ModelSuffix(text, string.Empty, false);
        }

        var modelName = text[..openIndex].Trim();
        var suffix = text[(openIndex + 1)..^1].Trim();
        return string.IsNullOrWhiteSpace(modelName) || string.IsNullOrWhiteSpace(suffix)
            ? new ModelSuffix(text, string.Empty, false)
            : new ModelSuffix(modelName, suffix, true);
    }

    private static bool WildcardMatch(string value, string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return false;
        }

        var regex = "^" + string.Concat(pattern.Trim().Select(static character => character switch
        {
            '*' => ".*",
            '?' => ".",
            _ => Regex.Escape(character.ToString())
        })) + "$";
        return Regex.IsMatch(value, regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private sealed record ModelSuffix(string ModelName, string RawSuffix, bool HasSuffix);
}
