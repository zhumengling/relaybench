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
    private void AddMetricTileIfPresent(string label, double? milliseconds, string delta, string tone)
    {
        if (!milliseconds.HasValue)
        {
            return;
        }

        MetricTiles.Add(new HistoryMetricTile(label, FormatMilliseconds(milliseconds), delta, tone));
    }

    private void AddNetworkMetricTile(string label, string? value, string delta, string tone)
    {
        if (string.IsNullOrWhiteSpace(value) || value == "--")
        {
            return;
        }

        MetricTiles.Add(new HistoryMetricTile(label, value, delta, tone));
    }

    private void AddNumberMetricTileIfPresent(string label, double? value, string delta, string tone, string suffix = "")
    {
        if (!value.HasValue)
        {
            return;
        }

        var text = FormatCompactNumber(value.Value);
        MetricTiles.Add(new HistoryMetricTile(label, string.IsNullOrWhiteSpace(suffix) ? text : $"{text}{suffix}", delta, tone));
    }

    private void AddPercentMetricTileIfPresent(string label, double? value, string delta, string tone)
    {
        if (!value.HasValue)
        {
            return;
        }

        MetricTiles.Add(new HistoryMetricTile(label, $"{value.Value:F1}%", delta, tone));
    }

    private static string? BuildFailedTargetSummary(JsonElement targets)
    {
        if (targets.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var failedTargets = targets.EnumerateArray()
            .Where(static item => !(TryGetBool(item, "Succeeded") ?? TryGetBool(item, "succeeded") ?? false))
            .Select(static item => TryGetString(item, "TargetName") ?? TryGetString(item, "targetName") ?? TryGetString(item, "TargetId") ?? TryGetString(item, "targetId") ?? "target")
            .Take(4)
            .ToArray();

        return failedTargets.Length == 0 ? null : string.Join(", ", failedTargets);
    }

    private static string? BuildStatusCodeSummary(JsonElement statusCodes)
    {
        if (statusCodes.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var items = statusCodes.EnumerateArray()
            .Select(static item =>
            {
                var name = TryGetString(item, "Name") ?? TryGetString(item, "name") ?? TryGetString(item, "Id") ?? TryGetString(item, "id") ?? "route";
                var code = TryGetDouble(item, "LastStatusCode") ?? TryGetDouble(item, "lastStatusCode");
                return code.HasValue ? $"{name}:{code.Value:F0}" : null;
            })
            .Where(static item => !string.IsNullOrWhiteSpace(item))
            .Take(4)
            .ToArray();

        return items.Length == 0 ? null : string.Join(", ", items);
    }

    private static (int OpenCircuits, int CoolingRoutes, int ModelCooldowns) ReadRouteHealthSummary(JsonElement routeHits)
    {
        if (routeHits.ValueKind != JsonValueKind.Array)
        {
            return (0, 0, 0);
        }

        var openCircuits = 0;
        var coolingRoutes = 0;
        var modelCooldowns = 0;
        foreach (var route in routeHits.EnumerateArray())
        {
            var state = TryGetString(route, "CircuitState") ?? TryGetString(route, "circuitState") ?? string.Empty;
            if (state.Contains("open", StringComparison.OrdinalIgnoreCase))
            {
                openCircuits++;
            }

            if ((TryGetBool(route, "Cooldown") ?? TryGetBool(route, "cooldown") ?? false) ||
                (TryGetDouble(route, "CooldownSeconds") ?? TryGetDouble(route, "cooldownSeconds") ?? 0) > 0)
            {
                coolingRoutes++;
            }

            if ((TryGetProperty(route, "ModelCooldowns", out var cooldowns) || TryGetProperty(route, "modelCooldowns", out cooldowns)) &&
                cooldowns.ValueKind == JsonValueKind.Array)
            {
                modelCooldowns += cooldowns.EnumerateArray().Count(static item =>
                    (TryGetDouble(item, "CooldownSeconds") ?? TryGetDouble(item, "cooldownSeconds") ?? 0) > 0 ||
                    (TryGetDouble(item, "FailureCount") ?? TryGetDouble(item, "failureCount") ?? 0) > 0);
            }
        }

        return (openCircuits, coolingRoutes, modelCooldowns);
    }

    private static (double HealthyMembers, double MemberCount, double OpenCircuitMembers, double? BestLatencyMs) ReadModelPoolHealthSummary(JsonElement modelPools)
    {
        if (modelPools.ValueKind != JsonValueKind.Array)
        {
            return (0, 0, 0, null);
        }

        double healthy = 0;
        double members = 0;
        double open = 0;
        double? bestLatency = null;
        foreach (var pool in modelPools.EnumerateArray())
        {
            healthy += TryGetDouble(pool, "HealthyMembers") ?? TryGetDouble(pool, "healthyMembers") ?? TryGetDouble(pool, "HealthyCount") ?? TryGetDouble(pool, "healthyCount") ?? 0;
            members += TryGetDouble(pool, "MemberCount") ?? TryGetDouble(pool, "memberCount") ?? TryGetDouble(pool, "TotalCount") ?? TryGetDouble(pool, "totalCount") ?? 0;
            open += TryGetDouble(pool, "OpenCircuitMembers") ?? TryGetDouble(pool, "openCircuitMembers") ?? 0;
            var latency = TryGetDouble(pool, "BestLatencyMs") ?? TryGetDouble(pool, "bestLatencyMs");
            if (latency is > 0 && (!bestLatency.HasValue || latency.Value < bestLatency.Value))
            {
                bestLatency = latency.Value;
            }
        }

        return (healthy, members, open, bestLatency);
    }

    private void AddScoreMetric(JsonElement scores, string propertyName, string label)
    {
        var score = TryGetDouble(scores, propertyName);
        if (!score.HasValue)
        {
            return;
        }

        MetricTiles.Add(new HistoryMetricTile(label, $"{score.Value:F1}", "score", HistoryTones.Accent));
    }

    private static bool HasSuccessfulScenario(IReadOnlyList<JsonElement> scenarios, string namePart)
        => scenarios.Any(scenario =>
        {
            var success = TryGetBool(scenario, "Success") ?? TryGetBool(scenario, "success") ?? false;
            if (!success)
            {
                return false;
            }

            var name = TryGetString(scenario, "scenario") ??
                       TryGetString(scenario, "DisplayName") ??
                       TryGetString(scenario, "displayName") ??
                       string.Empty;
            return name.Contains(namePart, StringComparison.OrdinalIgnoreCase);
        });

    private static JsonDocument? TryParsePayload(string? payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson) ||
            string.Equals(payloadJson.Trim(), "{}", StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            var document = JsonDocument.Parse(payloadJson);
            return document.RootElement.ValueKind == JsonValueKind.Object ? document : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ReadFirstStringFromArray(JsonElement root, string arrayName, params string[] propertyNames)
    {
        if (!TryGetProperty(root, arrayName, out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var item in array.EnumerateArray())
        {
            foreach (var propertyName in propertyNames)
            {
                var value = TryGetString(item, propertyName);
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }
        }

        return null;
    }

    private static string? ReadStringArraySummary(JsonElement root, string arrayName)
    {
        if (!TryGetProperty(root, arrayName, out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var values = array.EnumerateArray()
            .Where(static item => item.ValueKind == JsonValueKind.String)
            .Select(static item => item.GetString()?.Trim() ?? string.Empty)
            .Where(static item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return values.Length switch
        {
            0 => null,
            1 => values[0],
            _ => $"{values[0]} +{values.Length - 1}"
        };
    }

    private static double? ReadArrayLength(JsonElement root, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (TryGetProperty(root, propertyName, out var array) && array.ValueKind == JsonValueKind.Array)
            {
                return array.GetArrayLength();
            }
        }

        return null;
    }

    private static double? ReadOpenPortCount(JsonElement root)
    {
        if (!TryGetProperty(root, "Findings", out var findings) || findings.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var count = 0;
        foreach (var finding in findings.EnumerateArray())
        {
            var explicitOpen = TryGetBool(finding, "IsOpen") ?? TryGetBool(finding, "Open") ?? TryGetBool(finding, "isOpen");
            if (explicitOpen == true)
            {
                count++;
                continue;
            }

            if (explicitOpen == false)
            {
                continue;
            }

            var status = TryGetString(finding, "Status") ?? TryGetString(finding, "state") ?? string.Empty;
            if (status.Contains("open", StringComparison.OrdinalIgnoreCase) ||
                status.Contains("listen", StringComparison.OrdinalIgnoreCase) ||
                status.Contains("success", StringComparison.OrdinalIgnoreCase))
            {
                count++;
                continue;
            }

            if (finding.ValueKind == JsonValueKind.Object &&
                ((TryGetDouble(finding, "Port") ?? TryGetDouble(finding, "port")).HasValue ||
                 !string.IsNullOrWhiteSpace(TryGetString(finding, "Endpoint") ?? TryGetString(finding, "endpoint"))))
            {
                count++;
            }
        }

        return count;
    }

    private static string FormatWireApiSummary(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "--";
        }

        var normalized = value.Trim();
        if (normalized.Contains("responses", StringComparison.OrdinalIgnoreCase))
        {
            return "Responses";
        }

        if (normalized.Contains("anthropic", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("messages", StringComparison.OrdinalIgnoreCase))
        {
            return "Anthropic";
        }

        if (normalized.Contains("chat", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("completion", StringComparison.OrdinalIgnoreCase))
        {
            return "Chat";
        }

        return normalized;
    }

    private static string BuildProtocolSupportSummary(JsonElement root)
    {
        if (IsModelChatMultiPayload(root))
        {
            var successCount = TryGetDouble(root, "SuccessCount") ?? TryGetDouble(root, "successCount") ?? 0;
            var resultCount = TryGetDouble(root, "ResultCount") ?? TryGetDouble(root, "resultCount") ?? 0;
            return resultCount > 0
                ? $"\u591A\u6A21\u578B {successCount:F0}/{resultCount:F0}"
                : "\u591A\u6A21\u578B\u5BF9\u6BD4";
        }

        List<string> supported = [];
        AddProtocolSupportIfTrue(
            supported,
            "Responses",
            TryGetBool(root, "ResponsesSupported") ?? TryGetBool(root, "responsesSupported"));
        AddProtocolSupportIfTrue(
            supported,
            "Anthropic",
            TryGetBool(root, "AnthropicMessagesSupported") ?? TryGetBool(root, "anthropicMessagesSupported"));
        AddProtocolSupportIfTrue(
            supported,
            "Chat",
            TryGetBool(root, "ChatCompletionsSupported") ?? TryGetBool(root, "chatCompletionsSupported"));

        if (supported.Count == 0 &&
            TryGetProperty(root, "scenarios", out var scenarios) &&
            scenarios.ValueKind == JsonValueKind.Array)
        {
            foreach (var scenario in scenarios.EnumerateArray())
            {
                var success = TryGetBool(scenario, "Success") ?? TryGetBool(scenario, "success") ?? false;
                if (!success)
                {
                    continue;
                }

                var name = TryGetString(scenario, "scenario") ??
                           TryGetString(scenario, "DisplayName") ??
                           TryGetString(scenario, "displayName") ??
                           string.Empty;
                AddProtocolSupportByName(supported, name);
            }
        }

        return supported.Count == 0 ? "--" : string.Join(" / ", supported);
    }

    private static void AddProtocolSupportIfTrue(List<string> supported, string name, bool? value)
    {
        if (value == true && !supported.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            supported.Add(name);
        }
    }

    private static void AddProtocolSupportByName(List<string> supported, string name)
    {
        if (name.Contains("responses", StringComparison.OrdinalIgnoreCase))
        {
            AddProtocolSupportIfTrue(supported, "Responses", true);
        }
        else if (name.Contains("anthropic", StringComparison.OrdinalIgnoreCase) ||
                 name.Contains("messages", StringComparison.OrdinalIgnoreCase))
        {
            AddProtocolSupportIfTrue(supported, "Anthropic", true);
        }
        else if (name.Contains("chat", StringComparison.OrdinalIgnoreCase) ||
                 name.Contains("completion", StringComparison.OrdinalIgnoreCase))
        {
            AddProtocolSupportIfTrue(supported, "Chat", true);
        }
    }

    private static bool IsNetworkStatusPassing(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return value.Contains("open", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("listen", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("success", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("ok", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("pass", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("reachable", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("low", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("clean", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsNetworkStatusFailed(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return value.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("failure", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("error", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("timeout", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("refused", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("blocked", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("unreachable", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("high", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("malicious", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsNetworkRisky(string? value)
        => IsNetworkStatusFailed(value) ||
           (!string.IsNullOrWhiteSpace(value) &&
            (value.Contains("risk", StringComparison.OrdinalIgnoreCase) ||
             value.Contains("suspicious", StringComparison.OrdinalIgnoreCase) ||
             value.Contains("medium", StringComparison.OrdinalIgnoreCase)));

    private static string TranslateNetworkState(string value, bool success)
    {
        if (IsNetworkStatusPassing(value))
        {
            return HistoryStates.Passed;
        }

        if (IsNetworkStatusFailed(value))
        {
            return HistoryStates.Failed;
        }

        if (IsNetworkRisky(value))
        {
            return HistoryStates.Review;
        }

        return TranslateState(value, success);
    }

    private static IEnumerable<(string Path, string Value)> ExtractTextArtifactSummaries(JsonElement element, string path)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                var childPath = $"{path}-{property.Name}";
                if (property.Name.Equals("sections", StringComparison.OrdinalIgnoreCase) &&
                    property.Value.ValueKind == JsonValueKind.Array)
                {
                    foreach (var section in ExtractLegacySectionArtifacts(property.Value, childPath))
                    {
                        yield return section;
                    }
                }

                if (IsArtifactProperty(property.Name) &&
                    property.Value.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(property.Value.GetString()))
                {
                    yield return (childPath, property.Value.GetString()!);
                }

                foreach (var child in ExtractTextArtifactSummaries(property.Value, childPath))
                {
                    yield return child;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in element.EnumerateArray())
            {
                foreach (var child in ExtractTextArtifactSummaries(item, $"{path}-{index++}"))
                {
                    yield return child;
                }
            }
        }
    }

    private static IEnumerable<(string Path, string Value)> ExtractLegacySectionArtifacts(JsonElement sections, string path)
    {
        var index = 0;
        foreach (var section in sections.EnumerateArray())
        {
            if (section.ValueKind != JsonValueKind.Object)
            {
                index++;
                continue;
            }

            var title = TryGetString(section, "Title") ?? TryGetString(section, "title") ?? $"section-{index}";
            var content = TryGetString(section, "Content") ?? TryGetString(section, "content");
            if (!string.IsNullOrWhiteSpace(content))
            {
                yield return ($"{path}-{index}-{title}", content);
            }

            index++;
        }
    }

    private static IEnumerable<(string Path, string Description)> ExtractLegacyArtifactReferences(JsonElement root)
    {
        if (!TryGetProperty(root, "artifacts", out var artifacts) ||
            artifacts.ValueKind != JsonValueKind.Object)
        {
            yield break;
        }

        foreach (var item in ExtractLegacyArtifactReferenceGroup(artifacts, "text", "legacy-text"))
        {
            yield return item;
        }

        foreach (var item in ExtractLegacyArtifactReferenceGroup(artifacts, "images", "legacy-image"))
        {
            yield return item;
        }
    }

    private static IEnumerable<(string Path, string Description)> ExtractLegacyArtifactReferenceGroup(
        JsonElement artifacts,
        string propertyName,
        string prefix)
    {
        if (!TryGetProperty(artifacts, propertyName, out var values) ||
            values.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        var index = 0;
        foreach (var value in values.EnumerateArray())
        {
            if (value.ValueKind != JsonValueKind.Object)
            {
                index++;
                continue;
            }

            var relativePath = TryGetString(value, "RelativePath") ??
                               TryGetString(value, "relativePath") ??
                               TryGetString(value, "Path") ??
                               TryGetString(value, "path") ??
                               $"{prefix}-{index}";
            var description = TryGetString(value, "Description") ??
                              TryGetString(value, "description") ??
                              prefix;
            yield return ($"{prefix}-{relativePath}", description);
            index++;
        }
    }

    private static IEnumerable<(string Path, string SourcePath, long Size)> ExtractFileArtifactSummaries(JsonElement element, string path)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                var childPath = $"{path}-{property.Name}";
                if (IsFileArtifactProperty(property.Name) &&
                    property.Value.ValueKind == JsonValueKind.String &&
                    TryGetExistingArtifactFile(property.Value.GetString(), out var file))
                {
                    yield return (childPath, file.FullName, file.Length);
                }

                foreach (var child in ExtractFileArtifactSummaries(property.Value, childPath))
                {
                    yield return child;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in element.EnumerateArray())
            {
                foreach (var child in ExtractFileArtifactSummaries(item, $"{path}-{index++}"))
                {
                    yield return child;
                }
            }
        }
    }

    private static bool IsArtifactProperty(string name)
        => name.Equals("StandardOutput", StringComparison.OrdinalIgnoreCase) ||
           name.Equals("StandardError", StringComparison.OrdinalIgnoreCase) ||
           name.Equals("RawTrace", StringComparison.OrdinalIgnoreCase) ||
           name.Equals("RawTraceOutput", StringComparison.OrdinalIgnoreCase) ||
           name.Equals("TraceOutput", StringComparison.OrdinalIgnoreCase) ||
           name.Equals("CommandLine", StringComparison.OrdinalIgnoreCase) ||
           name.Equals("RawOutput", StringComparison.OrdinalIgnoreCase);

    private static bool IsFileArtifactProperty(string name)
        => name.Equals("ImagePath", StringComparison.OrdinalIgnoreCase) ||
           name.Equals("MapImagePath", StringComparison.OrdinalIgnoreCase) ||
           name.Equals("RouteMapImagePath", StringComparison.OrdinalIgnoreCase) ||
           name.Equals("ChartImagePath", StringComparison.OrdinalIgnoreCase) ||
           name.Equals("ScreenshotPath", StringComparison.OrdinalIgnoreCase) ||
           name.Equals("ArtifactPath", StringComparison.OrdinalIgnoreCase);

    private static bool TryGetExistingArtifactFile(string? path, out FileInfo file)
    {
        file = null!;
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            return false;
        }

        var extension = Path.GetExtension(path);
        if (!IsSupportedArtifactFileExtension(extension))
        {
            return false;
        }

        try
        {
            file = new FileInfo(path);
            return file.Exists;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsSupportedArtifactFileExtension(string extension)
        => extension.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
           extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
           extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) ||
           extension.Equals(".webp", StringComparison.OrdinalIgnoreCase) ||
           extension.Equals(".gif", StringComparison.OrdinalIgnoreCase) ||
           extension.Equals(".bmp", StringComparison.OrdinalIgnoreCase) ||
           extension.Equals(".svg", StringComparison.OrdinalIgnoreCase);

    private static string FormatByteSize(long byteCount)
    {
        if (byteCount < 1024)
        {
            return $"{byteCount} B";
        }

        return $"{byteCount / 1024.0:F1} KB";
    }

    private static string FormatArchiveSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        double value = Math.Max(0, bytes);
        var unitIndex = 0;
        while (value >= 1024 && unitIndex < units.Length - 1)
        {
            value /= 1024;
            unitIndex++;
        }

        return $"{value:F1} {units[unitIndex]}";
    }

    private static string CompactTileDelta(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "--";
        }

        var normalized = NormalizeInlineWhitespace(value);
        return normalized.Length <= 46 ? normalized : normalized[..45] + "...";
    }

    private static bool TryGetProperty(JsonElement element, string propertyName, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    private static string? TryGetString(JsonElement element, string propertyName)
    {
        if (!TryGetProperty(element, propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.ToString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null
        };
    }

    private static double? TryGetDouble(JsonElement element, string propertyName)
    {
        if (!TryGetProperty(element, propertyName, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number))
        {
            return number;
        }

        return value.ValueKind == JsonValueKind.String &&
               double.TryParse(value.GetString(), out var parsed)
            ? parsed
            : null;
    }

    private static bool? TryGetBool(JsonElement element, string propertyName)
    {
        if (!TryGetProperty(element, propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(value.GetString(), out var parsed) => parsed,
            _ => null
        };
    }

    private static string FormatMilliseconds(double? milliseconds)
    {
        if (!milliseconds.HasValue)
        {
            return "--";
        }

        return milliseconds.Value < 1000
            ? $"{Math.Round(milliseconds.Value, MidpointRounding.AwayFromZero):F0}ms"
            : $"{milliseconds.Value / 1000.0:F2}s";
    }

    private static string FormatMillisecondsZero(double? milliseconds)
    {
        if (!milliseconds.HasValue || milliseconds.Value <= 0)
        {
            return "0 ms";
        }

        return milliseconds.Value < 1000
            ? $"{Math.Round(milliseconds.Value, MidpointRounding.AwayFromZero):F0} ms"
            : $"{milliseconds.Value / 1000.0:F2}s";
    }

    private static string FormatCompactNumber(double value)
    {
        if (value <= 0)
        {
            return "0";
        }

        return value >= 1000
            ? value.ToString("N0")
            : Math.Round(value, MidpointRounding.AwayFromZero).ToString("F0");
    }

    private static string FormatPercentValue(double value)
    {
        var percentage = value is >= 0 and <= 1 ? value * 100 : value;
        return $"{percentage:F1}%";
    }

    private static double ClampPercent(double? value)
        => !value.HasValue || double.IsNaN(value.Value) || double.IsInfinity(value.Value)
            ? 0
            : Math.Clamp(value.Value, 0, 100);

    private static double NormalizeDirectRatio(double? value, double maxValue)
    {
        if (value is not > 0 || maxValue <= 0)
        {
            return 0;
        }

        return Math.Clamp(value.Value * 100 / maxValue, 8, 100);
    }

    private static double NormalizeInverseRatio(double? value, double maxValue)
    {
        if (value is not > 0 || maxValue <= 0)
        {
            return 0;
        }

        return Math.Clamp((maxValue - value.Value) * 100 / maxValue, 8, 100);
    }

    private static double? TryReadFirstPercent(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var span = text.AsSpan();
        for (var i = 0; i < span.Length; i++)
        {
            if (span[i] != '%')
            {
                continue;
            }

            var end = i;
            var start = end - 1;
            while (start >= 0 && (char.IsDigit(span[start]) || span[start] == '.'))
            {
                start--;
            }

            if (start == end - 1)
            {
                continue;
            }

            var number = span[(start + 1)..end].ToString();
            if (double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            {
                return parsed;
            }

            if (double.TryParse(number, out parsed))
            {
                return parsed;
            }
        }

        return null;
    }

    private static double? ReadMedian(IReadOnlyList<double> samples)
    {
        if (samples.Count == 0)
        {
            return null;
        }

        var ordered = samples.OrderBy(static item => item).ToArray();
        var middle = ordered.Length / 2;
        return ordered.Length % 2 == 1
            ? ordered[middle]
            : (ordered[middle - 1] + ordered[middle]) / 2.0;
    }

    private static double ReadAverage(IReadOnlyList<double> samples)
        => samples.Count == 0 ? 0 : samples.Average();

    private static bool HasMeaningfulPayload(string? payloadJson)
        => !string.IsNullOrWhiteSpace(payloadJson) &&
           !string.Equals(payloadJson.Trim(), "{}", StringComparison.Ordinal);

    private static bool Contains(string? value, string text)
        => !string.IsNullOrWhiteSpace(value) &&
           value.Contains(text, StringComparison.OrdinalIgnoreCase);

    private static string NormalizeInlineWhitespace(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(text.Length);
        var inWhitespace = false;
        foreach (var character in text.Trim())
        {
            if (char.IsWhiteSpace(character))
            {
                if (!inWhitespace)
                {
                    builder.Append(' ');
                    inWhitespace = true;
                }

                continue;
            }

            builder.Append(character);
            inWhitespace = false;
        }

        return builder.ToString();
    }

    private static string TranslateType(string value)
        => value switch
        {
            "Single Station" or "Quick" or "Deep" or "Stability" or "Concurrency" => "Single station",
            "Batch" or "Batch Comparison" => "Batch comparison",
            "Network" or "Network Review" => "Network review",
            "Data Safety" or "Security" => "Data safety",
            "Proxy" or "Transparent Proxy" => "Transparent proxy",
            "Model Chat" or "Chat" => "Model chat",
            _ => value
        };

    private static string TranslateState(string value, bool success)
    {
        if (value.Contains("Passed", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("Supported", StringComparison.OrdinalIgnoreCase))
        {
            return HistoryStates.Passed;
        }

        if (value.Contains("Failed", StringComparison.OrdinalIgnoreCase))
        {
            return HistoryStates.Failed;
        }

        if (value.Contains("Review", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("Partial", StringComparison.OrdinalIgnoreCase))
        {
            return HistoryStates.Review;
        }

        return string.IsNullOrWhiteSpace(value) ? success ? HistoryStates.Passed : HistoryStates.Review : value;
    }

    private static string SanitizePathSegment(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var filtered = new string(value.Where(ch => !invalid.Contains(ch)).ToArray());
        return string.IsNullOrWhiteSpace(filtered) ? "report" : filtered;
    }

    private sealed record ProxyConcurrencyChartStage(
        JsonElement Source,
        double? Concurrency,
        double TotalRequests,
        double SuccessCount,
        double RateLimitedCount,
        double ServerErrorCount,
        double TimeoutCount,
        double? P50ChatLatencyMs,
        double? P50TtftMs,
        double? AverageTokensPerSecond);

    private sealed record BatchRankingChartItem(
        JsonElement Source,
        int Rank,
        string Name,
        string BaseUrl,
        double? Score,
        double? ChatLatencyMs,
        double? TtftMs,
        double? TokensPerSecond,
        double? SuccessRate);

    private sealed record ProxyStabilityFailureChartItem(
        JsonElement Source,
        string Kind,
        double Count,
        double Rate);
}

public sealed record HistoryTypeFilter(string DisplayName, IReadOnlyList<string> Aliases);

internal sealed class HistoryAggregateAccumulator
{
    public double TotalRequests { get; set; }
    public double SuccessRequests { get; set; }
    public double FailedRequests { get; set; }
    public double TotalInputTokens { get; set; }
    public double TotalOutputTokens { get; set; }
    public double TimeoutCount { get; set; }
    public double RateLimitedRequests { get; set; }
    public double ServerErrorCount { get; set; }
    public List<double> P50Samples { get; } = [];
    public List<double> P95Samples { get; } = [];
    public List<double> P99Samples { get; } = [];
    public List<double> InputThroughputSamples { get; } = [];
    public List<double> OutputThroughputSamples { get; } = [];
}

public sealed record HistoryReportItem(
    string Id,
    string Time,
    string Type,
    string Detail,
    string Duration,
    double Score,
    bool Success,
    string Date)
{
    public string StatusText => Success ? HistoryDisplayText.Passed : HistoryDisplayText.Review;

    public Microsoft.UI.Xaml.Visibility SuccessVisibility => Success
        ? Microsoft.UI.Xaml.Visibility.Visible
        : Microsoft.UI.Xaml.Visibility.Collapsed;

    public Microsoft.UI.Xaml.Visibility ReviewVisibility => Success
        ? Microsoft.UI.Xaml.Visibility.Collapsed
        : Microsoft.UI.Xaml.Visibility.Visible;
}

public sealed record HistoryMetricTile(string Label, string Value, string Delta, string Tone)
{
    public Visibility AccentToneVisibility => HistoryToneVisibility.Accent(Tone);
    public Visibility HealthyToneVisibility => HistoryToneVisibility.Healthy(Tone);
    public Visibility WarningToneVisibility => HistoryToneVisibility.Warning(Tone);
    public Visibility DangerToneVisibility => HistoryToneVisibility.Danger(Tone);
}

public sealed record HistoryProtocolResult(string Name, string Latency, string Ttft, string ErrorRate, string State, string Tone)
{
    public string State { get; init; } = HistoryDisplayText.State(State);

    public string EvidenceText => string.IsNullOrWhiteSpace(ErrorRate) ? "--" : ErrorRate;

    public string EvidencePreview
    {
        get
        {
            var text = EvidenceText.ReplaceLineEndings(" ");
            return text.Length <= 42 ? text : text[..42] + "...";
        }
    }

    public Visibility AccentToneVisibility => HistoryToneVisibility.Accent(Tone);
    public Visibility HealthyToneVisibility => HistoryToneVisibility.Healthy(Tone);
    public Visibility WarningToneVisibility => HistoryToneVisibility.Warning(Tone);
    public Visibility DangerToneVisibility => HistoryToneVisibility.Danger(Tone);
}

public sealed record HistoryChartRow(
    string Title,
    string Subtitle,
    string State,
    string PrimaryLabel,
    string PrimaryValue,
    double PrimaryRatio,
    string SecondaryLabel,
    string SecondaryValue,
    double SecondaryRatio,
    string TertiaryLabel,
    string TertiaryValue,
    double TertiaryRatio,
    string Evidence,
    string Tone)
{
    public string State { get; init; } = HistoryDisplayText.State(State);

    public string EvidencePreview
    {
        get
        {
            var text = string.IsNullOrWhiteSpace(Evidence)
                ? "--"
                : Evidence.ReplaceLineEndings(" ");
            return text.Length <= 68 ? text : text[..68] + "...";
        }
    }

    public Visibility AccentToneVisibility => HistoryToneVisibility.Accent(Tone);
    public Visibility HealthyToneVisibility => HistoryToneVisibility.Healthy(Tone);
    public Visibility WarningToneVisibility => HistoryToneVisibility.Warning(Tone);
    public Visibility DangerToneVisibility => HistoryToneVisibility.Danger(Tone);
}

public sealed record HistoryAttachmentItem(string FileName, string Size);

public sealed record HistoryReportArchiveItem(
    string Name,
    string Kind,
    string Size,
    string LastWriteTimeText,
    string Path,
    DateTime LastWriteTimeUtc);

public sealed record HistoryCapabilityRow(string Capability, bool Responses, bool Anthropic, bool Chat)
{
    public string ResponsesText => Responses ? "OK" : "Part";
    public string AnthropicText => Anthropic ? "OK" : "Part";
    public string ChatText => Chat ? "OK" : "Part";
    public Visibility ResponsesPassVisibility => Responses ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ResponsesPartialVisibility => Responses ? Visibility.Collapsed : Visibility.Visible;
    public Visibility AnthropicPassVisibility => Anthropic ? Visibility.Visible : Visibility.Collapsed;
    public Visibility AnthropicPartialVisibility => Anthropic ? Visibility.Collapsed : Visibility.Visible;
    public Visibility ChatPassVisibility => Chat ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ChatPartialVisibility => Chat ? Visibility.Collapsed : Visibility.Visible;
}

internal static class HistoryTones
{
    public const string Accent = "Accent";
    public const string Healthy = "Healthy";
    public const string Warning = "Warning";
    public const string Danger = "Danger";
}

internal static class HistoryStates
{
    public const string Passed = "Passed";
    public const string Failed = "Failed";
    public const string Review = "Review";
}

internal static class HistoryDisplayText
{
    public const string Passed = "\u901A\u8FC7";
    public const string Failed = "\u5931\u8D25";
    public const string Review = "\u590D\u6838";

    public static string State(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Review;
        }

        if (value.Contains(HistoryStates.Passed, StringComparison.OrdinalIgnoreCase) ||
            value.Contains("Supported", StringComparison.OrdinalIgnoreCase) ||
            value.Contains(Passed, StringComparison.OrdinalIgnoreCase))
        {
            return Passed;
        }

        if (value.Contains(HistoryStates.Failed, StringComparison.OrdinalIgnoreCase) ||
            value.Contains(Failed, StringComparison.OrdinalIgnoreCase))
        {
            return Failed;
        }

        if (value.Contains(HistoryStates.Review, StringComparison.OrdinalIgnoreCase) ||
            value.Contains("Partial", StringComparison.OrdinalIgnoreCase) ||
            value.Contains(Review, StringComparison.OrdinalIgnoreCase))
        {
            return Review;
        }

        return value;
    }
}

internal static class HistoryToneVisibility
{
    public static Visibility Accent(string? color) => VisibleWhen(Resolve(color) == HistoryToneKind.Accent);
    public static Visibility Healthy(string? color) => VisibleWhen(Resolve(color) == HistoryToneKind.Healthy);
    public static Visibility Warning(string? color) => VisibleWhen(Resolve(color) == HistoryToneKind.Warning);
    public static Visibility Danger(string? color) => VisibleWhen(Resolve(color) == HistoryToneKind.Danger);

    private static Visibility VisibleWhen(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;

    private static HistoryToneKind Resolve(string? color)
    {
        return Normalize(color) switch
        {
            HistoryTones.Healthy => HistoryToneKind.Healthy,
            HistoryTones.Warning => HistoryToneKind.Warning,
            HistoryTones.Danger => HistoryToneKind.Danger,
            _ => HistoryToneKind.Accent
        };
    }

    private static string Normalize(string? tone)
    {
        if (string.IsNullOrWhiteSpace(tone))
        {
            return HistoryTones.Accent;
        }

        var trimmed = tone.Trim();
        if (string.Equals(trimmed, HistoryTones.Healthy, StringComparison.OrdinalIgnoreCase))
        {
            return HistoryTones.Healthy;
        }

        if (string.Equals(trimmed, HistoryTones.Warning, StringComparison.OrdinalIgnoreCase))
        {
            return HistoryTones.Warning;
        }

        if (string.Equals(trimmed, HistoryTones.Danger, StringComparison.OrdinalIgnoreCase))
        {
            return HistoryTones.Danger;
        }

        return HistoryTones.Accent;
    }
}

internal enum HistoryToneKind
{
    Accent,
    Healthy,
    Warning,
    Danger
}
