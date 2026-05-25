using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using RelayBench.WinUI.Services;
using RelayBench.WinUI.Storage;
using Windows.ApplicationModel.DataTransfer;

namespace RelayBench.WinUI.ViewModels;

public sealed partial class HistoryReportsViewModel
{
    private bool PopulateApplicationAccessRowsFromPayload(JsonElement root)
    {
        var added = false;
        if (TryGetProperty(root, "Targets", out var targets) && targets.ValueKind == JsonValueKind.Array)
        {
            foreach (var target in targets.EnumerateArray().Take(32))
            {
                var succeeded = TryGetBool(target, "Succeeded") ?? TryGetBool(target, "succeeded") ?? false;
                ProtocolResults.Add(new HistoryProtocolResult(
                    TryGetString(target, "TargetName") ?? TryGetString(target, "targetName") ?? "Application target",
                    FormatCompactNumber(TryGetDouble(target, "ChangedFileCount") ?? TryGetDouble(target, "changedFileCount") ?? 0),
                    FormatCompactNumber(TryGetDouble(target, "BackupFileCount") ?? TryGetDouble(target, "backupFileCount") ?? 0),
                    BuildApplicationAccessEvidence(target),
                    succeeded ? HistoryStates.Passed : HistoryStates.Failed,
                    succeeded ? HistoryTones.Healthy : HistoryTones.Danger));
                added = true;
            }

            return added;
        }

        var operationSucceeded = TryGetBool(root, "Succeeded") ?? TryGetBool(root, "succeeded") ?? false;
        ProtocolResults.Add(new HistoryProtocolResult(
            TryGetString(root, "TargetName") ?? TryGetString(root, "targetName") ?? "Application Access",
            FormatCompactNumber(TryGetDouble(root, "ChangedFileCount") ?? TryGetDouble(root, "changedFileCount") ?? 0),
            FormatCompactNumber(TryGetDouble(root, "BackupFileCount") ?? TryGetDouble(root, "backupFileCount") ?? 0),
            BuildApplicationAccessEvidence(root),
            operationSucceeded ? HistoryStates.Passed : HistoryStates.Failed,
            operationSucceeded ? HistoryTones.Healthy : HistoryTones.Danger));
        return true;
    }

    private static int CountRouteModelCooldowns(JsonElement route)
    {
        if ((!TryGetProperty(route, "ModelCooldowns", out var cooldowns) &&
             !TryGetProperty(route, "modelCooldowns", out cooldowns)) ||
            cooldowns.ValueKind != JsonValueKind.Array)
        {
            return 0;
        }

        return cooldowns.EnumerateArray().Count(static item =>
            (TryGetDouble(item, "CooldownSeconds") ?? TryGetDouble(item, "cooldownSeconds") ?? 0) > 0 ||
            (TryGetDouble(item, "FailureCount") ?? TryGetDouble(item, "failureCount") ?? 0) > 0);
    }

    private static bool IsTransparentProxyRouteHealthy(
        double failed,
        string circuitState,
        double cooldownSeconds,
        int modelCooldowns)
        => failed <= 0 &&
           cooldownSeconds <= 0 &&
           modelCooldowns == 0 &&
           !circuitState.Contains("Open", StringComparison.OrdinalIgnoreCase);

    private static string ResolveTransparentProxyRouteState(
        double failed,
        string circuitState,
        double cooldownSeconds,
        int modelCooldowns)
    {
        if (circuitState.Contains("Open", StringComparison.OrdinalIgnoreCase))
        {
            return HistoryStates.Failed;
        }

        return failed > 0 || cooldownSeconds > 0 || modelCooldowns > 0
            ? HistoryStates.Review
            : HistoryStates.Passed;
    }

    private static string BuildTransparentProxyRouteEvidence(
        double? statusCode,
        string circuitState,
        double cooldownSeconds,
        int modelCooldowns)
    {
        List<string> parts = [];
        if (statusCode.HasValue)
        {
            parts.Add(statusCode.Value.ToString("F0", CultureInfo.InvariantCulture));
        }

        if (!string.IsNullOrWhiteSpace(circuitState))
        {
            parts.Add(circuitState);
        }

        if (cooldownSeconds > 0)
        {
            parts.Add($"{Math.Ceiling(cooldownSeconds):F0}s cooldown");
        }

        if (modelCooldowns > 0)
        {
            parts.Add($"{modelCooldowns} model cooldown");
        }

        return parts.Count == 0 ? "--" : string.Join(" | ", parts);
    }

    private static string ResolveTransparentProxyPoolState(
        double memberCount,
        double healthyMembers,
        double openCircuitMembers)
    {
        if (memberCount > 0 && healthyMembers <= 0)
        {
            return HistoryStates.Failed;
        }

        return openCircuitMembers > 0 || healthyMembers < memberCount
            ? HistoryStates.Review
            : HistoryStates.Passed;
    }

    private static string BuildTransparentProxyPoolEvidence(JsonElement pool, double openCircuitMembers)
    {
        List<string> parts = [];
        if (openCircuitMembers > 0)
        {
            parts.Add($"{openCircuitMembers:F0} open circuit");
        }

        var protocol = TryGetString(pool, "ProtocolSummary") ?? TryGetString(pool, "protocolSummary");
        if (!string.IsNullOrWhiteSpace(protocol))
        {
            parts.Add(protocol);
        }

        var sent = TryGetDouble(pool, "Sent") ?? TryGetDouble(pool, "sent");
        var success = TryGetDouble(pool, "Success") ?? TryGetDouble(pool, "success");
        var failed = TryGetDouble(pool, "Failed") ?? TryGetDouble(pool, "failed");
        if (sent.HasValue || success.HasValue || failed.HasValue)
        {
            parts.Add($"{success ?? 0:F0}/{sent ?? 0:F0} ok / {failed ?? 0:F0} failed");
        }

        return parts.Count == 0 ? "--" : string.Join(" | ", parts);
    }

    private static double? CountConfiguredRoutes(JsonElement routes, bool enabled)
    {
        if (routes.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        return routes.EnumerateArray().Count(route =>
            route.ValueKind == JsonValueKind.Object &&
            (TryGetBool(route, "Enabled") ?? TryGetBool(route, "enabled") ?? true) == enabled);
    }

    private static double? CountConfiguredRoutesWithRetry(JsonElement routes)
    {
        if (routes.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        return routes.EnumerateArray().Count(route =>
            route.ValueKind == JsonValueKind.Object &&
            (TryGetDouble(route, "RequestRetry") ?? TryGetDouble(route, "requestRetry") ?? 0) > 0);
    }

    private static double? CountConfiguredRoutesWithCooldown(JsonElement routes)
    {
        if (routes.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        return routes.EnumerateArray().Count(route =>
            route.ValueKind == JsonValueKind.Object &&
            (TryGetDouble(route, "ModelCooldownSeconds") ?? TryGetDouble(route, "modelCooldownSeconds") ?? 0) > 0);
    }

    private static string FormatConfiguredRouteWireApi(JsonElement route)
    {
        var preferred = TryGetString(route, "PreferredWireApi") ?? TryGetString(route, "preferredWireApi");
        if (!string.IsNullOrWhiteSpace(preferred))
        {
            return FormatWireApiSummary(preferred);
        }

        var authMode = TryGetString(route, "AuthMode") ?? TryGetString(route, "authMode");
        return string.IsNullOrWhiteSpace(authMode) ? "--" : authMode;
    }

    private static string BuildConfiguredRouteEvidence(JsonElement route, double? retry, double? retryInterval, double? cooldown)
    {
        List<string> parts = [];
        var enabled = TryGetBool(route, "Enabled") ?? TryGetBool(route, "enabled") ?? true;
        parts.Add(enabled ? "enabled" : "disabled");

        AddEvidencePart(parts, TryGetString(route, "UpstreamUrl") ?? TryGetString(route, "upstreamUrl"));
        AddEvidencePart(parts, TryGetString(route, "ModelFilter") ?? TryGetString(route, "modelFilter"));
        AddEvidencePart(parts, TryGetString(route, "Prefix") ?? TryGetString(route, "prefix"));

        if (retry.HasValue)
        {
            parts.Add($"retry {retry.Value:F0}");
        }

        if (retryInterval.HasValue)
        {
            parts.Add($"interval {retryInterval.Value:F0}s");
        }

        if (cooldown.HasValue)
        {
            parts.Add($"cooldown {cooldown.Value:F0}s");
        }

        if (!string.IsNullOrWhiteSpace(TryGetString(route, "OutboundProxy") ?? TryGetString(route, "outboundProxy")))
        {
            parts.Add("outbound proxy");
        }

        if ((TryGetBool(route, "HasHeaders") ?? TryGetBool(route, "hasHeaders")) == true)
        {
            parts.Add("headers");
        }

        if ((TryGetBool(route, "HasPayloadRules") ?? TryGetBool(route, "hasPayloadRules")) == true)
        {
            parts.Add("payload rules");
        }

        var excluded = ReadArrayLength(route, "ExcludedModelPatterns", "excludedModelPatterns");
        if (excluded is > 0)
        {
            parts.Add($"excluded {excluded.Value:F0}");
        }

        AddEvidencePart(parts, TryGetString(route, "AuthMode") is { } authMode ? $"auth {authMode}" : null);
        AddEvidencePart(parts, TryGetString(route, "OAuthProvider") is { } oauthProvider ? $"oauth {oauthProvider}" : null);
        if (!string.IsNullOrWhiteSpace(TryGetString(route, "OAuthCredentialId") ?? TryGetString(route, "oauthCredentialId")))
        {
            parts.Add("oauth credential");
        }

        return parts.Count == 0 ? "--" : string.Join(" | ", parts);
    }

    private bool PopulateNetworkReviewRowsFromPayload(JsonElement root)
    {
        var schema = TryGetString(root, "Schema") ?? TryGetString(root, "schema");
        if (schema?.StartsWith("network-review", StringComparison.OrdinalIgnoreCase) != true)
        {
            return false;
        }

        var detail = TryGetProperty(root, "Result", out var result) && result.ValueKind == JsonValueKind.Object
            ? result
            : root;

        var added = 0;
        AddNetworkFindingRows(detail, ref added);
        AddNetworkRouteHopRows(detail, ref added);
        AddNetworkSourceRows(detail, ref added);
        AddNetworkBatchResultRows(detail, ref added);
        return added > 0;
    }

    private void AddNetworkFindingRows(JsonElement root, ref int added)
    {
        if (!TryGetProperty(root, "Findings", out var findings) || findings.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var finding in findings.EnumerateArray())
        {
            if (added >= 32 || finding.ValueKind != JsonValueKind.Object)
            {
                break;
            }

            var host = TryGetString(finding, "Endpoint") ??
                       TryGetString(finding, "Host") ??
                       TryGetString(finding, "Target") ??
                       TryGetString(finding, "Address") ??
                       TryGetString(finding, "Ip") ??
                       "port check";
            var port = TryGetDouble(finding, "Port") ?? TryGetDouble(finding, "port");
            var protocol = TryGetString(finding, "Protocol") ??
                           TryGetString(finding, "Service") ??
                           (port.HasValue ? $"port {port.Value:F0}" : "--");
            var status = TryGetString(finding, "Status") ??
                         TryGetString(finding, "State") ??
                         (TryGetBool(finding, "IsOpen") ?? TryGetBool(finding, "Open")) switch
                         {
                             true => "open",
                             false => "closed",
                             _ => string.Empty
                         };
            var evidence = TryGetString(finding, "Error") ??
                           TryGetString(finding, "ErrorMessage") ??
                           TryGetString(finding, "Banner") ??
                           TryGetString(finding, "Summary") ??
                           TryGetString(finding, "ResolvedAddress") ??
                           status;

            AddNetworkProtocolRow(
                port.HasValue && !host.Contains(':', StringComparison.Ordinal) ? $"{host}:{port.Value:F0}" : host,
                FormatMilliseconds(TryGetDouble(finding, "LatencyMs") ??
                                   TryGetDouble(finding, "DurationMs") ??
                                   TryGetDouble(finding, "ElapsedMs")),
                protocol,
                string.IsNullOrWhiteSpace(evidence) ? "--" : evidence,
                status,
                IsNetworkStatusPassing(status),
                ref added);
        }
    }

    private void AddNetworkRouteHopRows(JsonElement root, ref int added)
    {
        var hops = TryGetProperty(root, "Hops", out var hopsValue) ? hopsValue :
                   TryGetProperty(root, "RouteHops", out var routeHops) ? routeHops :
                   default;
        if (hops.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var hop in hops.EnumerateArray())
        {
            if (added >= 32)
            {
                break;
            }

            if (hop.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var hopIndex = TryGetDouble(hop, "Hop") ?? TryGetDouble(hop, "Index") ?? TryGetDouble(hop, "Ttl");
            var address = TryGetString(hop, "Address") ??
                          TryGetString(hop, "Ip") ??
                          TryGetString(hop, "Host") ??
                          TryGetString(hop, "Name");
            var name = hopIndex.HasValue
                ? string.IsNullOrWhiteSpace(address) ? $"Hop {hopIndex.Value:F0}" : $"Hop {hopIndex.Value:F0} {address}"
                : address ?? "Route hop";
            var loss = TryGetDouble(hop, "PacketLossPercent") ??
                       TryGetDouble(hop, "LossPercent") ??
                       TryGetDouble(hop, "Loss");
            var status = TryGetString(hop, "Status") ??
                         TryGetString(hop, "State") ??
                         TryGetString(hop, "Error") ??
                         "reachable";
            var detail = TryGetString(hop, "Asn") ??
                         TryGetString(hop, "Location") ??
                         TryGetString(hop, "Provider") ??
                         TryGetString(hop, "Error") ??
                         "--";

            AddNetworkProtocolRow(
                name,
                FormatMilliseconds(TryGetDouble(hop, "LatencyMs") ??
                                   TryGetDouble(hop, "AverageLatencyMs") ??
                                   TryGetDouble(hop, "RttMs") ??
                                   TryGetDouble(hop, "Ms")),
                loss.HasValue ? $"{loss.Value:F1}%" : "--",
                detail,
                status,
                !IsNetworkStatusFailed(status),
                ref added);
        }
    }

    private void AddNetworkSourceRows(JsonElement root, ref int added)
    {
        var sources = TryGetProperty(root, "Sources", out var sourcesValue) ? sourcesValue :
                      TryGetProperty(root, "RiskSources", out var riskSources) ? riskSources :
                      TryGetProperty(root, "IpRiskSources", out var ipRiskSources) ? ipRiskSources :
                      default;
        if (sources.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var source in sources.EnumerateArray())
        {
            if (added >= 32)
            {
                break;
            }

            if (source.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var name = TryGetString(source, "DisplayName") ??
                       TryGetString(source, "Name") ??
                       TryGetString(source, "Source") ??
                       TryGetString(source, "Provider") ??
                       "Risk source";
            var verdict = TryGetString(source, "Verdict") ??
                          TryGetString(source, "Risk") ??
                          TryGetString(source, "RiskLevel") ??
                          TryGetString(source, "Status") ??
                          "--";
            var detail = TryGetString(source, "Category") ??
                         TryGetString(source, "Summary") ??
                         TryGetString(source, "Error") ??
                         "--";

            AddNetworkProtocolRow(
                name,
                FormatMilliseconds(TryGetDouble(source, "LatencyMs") ?? TryGetDouble(source, "DurationMs")),
                verdict,
                detail,
                verdict,
                !IsNetworkRisky(verdict),
                ref added);
        }
    }

    private void AddNetworkBatchResultRows(JsonElement root, ref int added)
    {
        if (!TryGetProperty(root, "Results", out var results) || results.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var result in results.EnumerateArray())
        {
            if (added >= 32)
            {
                break;
            }

            if (result.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var name = TryGetString(result, "Target") ??
                       TryGetString(result, "Endpoint") ??
                       TryGetString(result, "Host") ??
                       TryGetString(result, "Name") ??
                       "Network target";
            var success = TryGetBool(result, "Succeeded") ??
                          TryGetBool(result, "Success") ??
                          !IsNetworkStatusFailed(TryGetString(result, "Status") ?? string.Empty);
            var status = TryGetString(result, "Status") ??
                         TryGetString(result, "Summary") ??
                         (success ? "success" : "failed");
            var openCount = ReadArrayLength(result, "OpenPorts", "OpenEndpoints", "Findings") ??
                            TryGetDouble(result, "OpenPortCount") ??
                            TryGetDouble(result, "OpenCount");
            var resolved = TryGetString(result, "ResolvedAddress") ??
                           TryGetString(result, "Address") ??
                           (openCount.HasValue ? $"{openCount.Value:F0} open" : "--");
            var error = TryGetString(result, "Error") ??
                        TryGetString(result, "ErrorMessage") ??
                        TryGetString(result, "StandardError") ??
                        status;

            AddNetworkProtocolRow(
                name,
                FormatMilliseconds(TryGetDouble(result, "LatencyMs") ??
                                   TryGetDouble(result, "DurationMs") ??
                                   TryGetDouble(result, "ElapsedMs")),
                resolved,
                string.IsNullOrWhiteSpace(error) ? "--" : error,
                status,
                success,
                ref added);
        }
    }

    private void AddNetworkProtocolRow(
        string name,
        string latency,
        string ttft,
        string errorRate,
        string stateSource,
        bool success,
        ref int added)
    {
        ProtocolResults.Add(new HistoryProtocolResult(
            string.IsNullOrWhiteSpace(name) ? "Network item" : name,
            latency,
            string.IsNullOrWhiteSpace(ttft) ? "--" : ttft,
            string.IsNullOrWhiteSpace(errorRate) ? "--" : errorRate,
            TranslateNetworkState(stateSource, success),
            success ? HistoryTones.Healthy : HistoryTones.Warning));
        added++;
    }

    private bool PopulateCapabilityRowsFromPayload(JsonElement root)
    {
        if ((!TryGetProperty(root, "scenarios", out var scenarios) || scenarios.ValueKind != JsonValueKind.Array) &&
            !TryGetProxySingleScenarioResults(root, out scenarios))
        {
            return false;
        }

        var scenarioItems = scenarios.EnumerateArray().ToArray();
        if (scenarioItems.Length == 0)
        {
            return false;
        }

        var responses = HasSuccessfulScenario(scenarioItems, "Responses");
        var anthropic = HasSuccessfulScenario(scenarioItems, "Anthropic");
        var chat = HasSuccessfulScenario(scenarioItems, "Chat");
        CapabilityRows.Add(new HistoryCapabilityRow("\u534F\u8BAE\u53EF\u7528\u6027", responses, anthropic, chat));

        var streaming = HasSuccessfulScenario(scenarioItems, "Stream");
        CapabilityRows.Add(new HistoryCapabilityRow("Streaming", streaming || responses, streaming || anthropic, streaming || chat));

        var tools = HasSuccessfulScenario(scenarioItems, "Function") || HasSuccessfulScenario(scenarioItems, "Tool");
        var structured = HasSuccessfulScenario(scenarioItems, "Structured") || HasSuccessfulScenario(scenarioItems, "JSON");
        var media = HasSuccessfulScenario(scenarioItems, "Image") ||
                    HasSuccessfulScenario(scenarioItems, "Audio") ||
                    HasSuccessfulScenario(scenarioItems, "MultiModal");
        CapabilityRows.Add(new HistoryCapabilityRow("Tool calling", tools, tools, tools));
        CapabilityRows.Add(new HistoryCapabilityRow("Structured output", structured, structured, structured));
        CapabilityRows.Add(new HistoryCapabilityRow("Multimodal/file", media, media, media));
        return true;
    }

}
