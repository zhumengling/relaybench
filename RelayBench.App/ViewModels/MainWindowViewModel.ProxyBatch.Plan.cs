using System.Text;
using RelayBench.App.Infrastructure;
using RelayBench.Core.Models;

namespace RelayBench.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private ProxyBatchPlan BuildProxyBatchPlan(bool requireRunnable)
    {
        var defaultModel = NormalizeNullable(ProxyModel);
        var sourceEntries = ParseProxyBatchSourceEntries(ProxyBatchTargetsText, allowEmpty: true);

        if (sourceEntries.Count == 0)
        {
            if (!requireRunnable)
            {
                return new ProxyBatchPlan(Array.Empty<ProxyBatchSourceEntry>(), Array.Empty<ProxyBatchTargetEntry>(), false);
            }

            throw new InvalidOperationException("入口组至少需要一个网址。请先在右侧表格里填写并加入入口组。");
        }

        var runnableEntries = sourceEntries
            .Where(entry => entry.IncludeInBatchTest)
            .ToArray();
        if (runnableEntries.Length == 0)
        {
            if (!requireRunnable)
            {
                return new ProxyBatchPlan(sourceEntries, Array.Empty<ProxyBatchTargetEntry>(), false);
            }

            throw new InvalidOperationException("当前入口组里没有开启“加入测试”的网址，请先在右侧表格里打开至少一项。");
        }

        List<ProxyBatchTargetEntry> targets = [];
        foreach (var sourceEntry in runnableEntries)
        {
            var effectiveApiKey = FirstNonEmpty(sourceEntry.ApiKey, sourceEntry.SiteGroupApiKey);
            if (string.IsNullOrWhiteSpace(effectiveApiKey))
            {
                throw new InvalidOperationException($"条目“{sourceEntry.Name}”缺少可用密钥。请先在右侧表格里填写该行 key，或让空白 key 沿用上一行后再加入入口组。");
            }

            var effectiveModel = FirstNonEmpty(sourceEntry.Model, sourceEntry.SiteGroupModel, defaultModel);
            if (string.IsNullOrWhiteSpace(effectiveModel))
            {
                throw new InvalidOperationException(
                    $"条目“{BuildBatchProbeDisplayName(sourceEntry.Name, sourceEntry.SiteGroupName)}”缺少模型。请先在主页默认模型里选择，或在右侧表格最后一列为该行拉取 / 填写模型。");
            }

            var keySource = !string.IsNullOrWhiteSpace(sourceEntry.ApiKey)
                ? ProxyBatchKeySource.Entry
                : ProxyBatchKeySource.SiteGroup;
            var apiKeyAlias = keySource switch
            {
                ProxyBatchKeySource.Entry => "本行 key",
                ProxyBatchKeySource.SiteGroup => $"站点内继承（{sourceEntry.SiteGroupName}）",
                _ => "本行 key"
            };

            targets.Add(new ProxyBatchTargetEntry(
                BuildBatchProbeDisplayName(sourceEntry.Name, sourceEntry.SiteGroupName),
                sourceEntry.BaseUrl,
                effectiveApiKey!,
                apiKeyAlias,
                effectiveModel!,
                sourceEntry.Name,
                sourceEntry.SiteGroupName,
                keySource));
        }

        if (targets.Count == 0)
        {
            if (!requireRunnable)
            {
                return new ProxyBatchPlan(sourceEntries, Array.Empty<ProxyBatchTargetEntry>(), false);
            }

            throw new InvalidOperationException("入口组没有生成任何可测试条目。");
        }

        if (targets.Count > MaxProxyBatchProbeTargets)
        {
            throw new InvalidOperationException($"入口组一次最多支持 {MaxProxyBatchProbeTargets} 个测试项，请先缩减清单。");
        }

        return new ProxyBatchPlan(sourceEntries, targets, false);
    }

    private IReadOnlyList<ProxyBatchSourceEntry> ParseProxyBatchSourceEntries(string rawText, bool allowEmpty)
    {
        List<ProxyBatchSourceEntry> entries = [];
        ProxyBatchSiteGroupContext? currentSiteGroup = null;

        var lines = rawText
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var rawLine in lines)
        {
            if (rawLine.StartsWith('#') || rawLine.StartsWith("//", StringComparison.Ordinal))
            {
                continue;
            }

            if (TryParseSiteGroupHeader(rawLine, out var siteGroup))
            {
                currentSiteGroup = siteGroup;
                continue;
            }

            var isGroupChild = TryStripGroupEntryMarker(rawLine, out var normalizedLine);
            if (isGroupChild && currentSiteGroup is null)
            {
                throw new InvalidOperationException("发现了站点子入口，但前面还没有对应的站点头，请先检查保存前的数据格式。");
            }

            var parts = normalizedLine.Split('|')
                .Select(part => part.Trim())
                .ToArray();

            string name;
            string baseUrl;
            string? apiKey = null;
            string? model = null;

            if (parts.Length == 1)
            {
                baseUrl = parts[0];
                name = BuildBatchDefaultName(baseUrl, entries.Count + 1);
            }
            else
            {
                name = string.IsNullOrWhiteSpace(parts[0]) ? BuildBatchDefaultName(parts[1], entries.Count + 1) : parts[0];
                baseUrl = parts[1];
                apiKey = parts.Length >= 3 ? NormalizeNullable(parts[2]) : null;
                model = parts.Length >= 4 ? NormalizeNullable(parts[3]) : null;
            }

            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                throw new InvalidOperationException($"入口组第 {entries.Count + 1} 条没有填写地址。");
            }

            if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out _))
            {
                throw new InvalidOperationException($"入口组第 {entries.Count + 1} 条的地址不是有效的绝对 URI：{baseUrl}");
            }

            var includeInBatchTest = ParseProxyBatchIncludeInTest(parts);

            entries.Add(new ProxyBatchSourceEntry(
                name.Trim(),
                baseUrl.Trim(),
                apiKey,
                model,
                includeInBatchTest,
                isGroupChild ? currentSiteGroup?.Name : null,
                isGroupChild ? currentSiteGroup?.ApiKey : null,
                isGroupChild ? currentSiteGroup?.Model : null));
        }

        if (entries.Count == 0)
        {
            return allowEmpty ? Array.Empty<ProxyBatchSourceEntry>() : throw new InvalidOperationException("入口组里没有可用条目。");
        }

        if (entries.Count > MaxProxyBatchSourceEntries)
        {
            throw new InvalidOperationException($"入口组一次最多支持 {MaxProxyBatchSourceEntries} 个条目，请先缩减清单。");
        }

        return entries;
    }

    private static bool TryParseSiteGroupHeader(string line, out ProxyBatchSiteGroupContext? siteGroup)
    {
        var parts = line.Split('|')
            .Select(part => part.Trim())
            .ToArray();

        if (parts.Length >= 2 &&
            (string.Equals(parts[0], "站点", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(parts[0], "组", StringComparison.OrdinalIgnoreCase)))
        {
            var name = parts[1];
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new InvalidOperationException("站点头缺少名称，请检查入口组数据格式。");
            }

            siteGroup = new ProxyBatchSiteGroupContext(
                name.Trim(),
                parts.Length >= 3 ? NormalizeNullable(parts[2]) : null,
                parts.Length >= 4 ? NormalizeNullable(parts[3]) : null);
            return true;
        }

        siteGroup = null;
        return false;
    }

    private static bool TryStripGroupEntryMarker(string line, out string normalizedLine)
    {
        if (line.StartsWith("-"))
        {
            normalizedLine = line[1..].Trim();
            return true;
        }

        if (line.StartsWith(">"))
        {
            normalizedLine = line[1..].Trim();
            return true;
        }

        normalizedLine = line;
        return false;
    }

    private static string BuildBatchDefaultName(string baseUrl, int index)
    {
        if (Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri))
        {
            return string.IsNullOrWhiteSpace(uri.Host) ? $"入口 {index}" : uri.Host;
        }

        return $"入口 {index}";
    }

    private static string BuildBatchProbeDisplayName(string entryName, string? siteGroupName)
        => string.IsNullOrWhiteSpace(siteGroupName) ? entryName : $"{siteGroupName} / {entryName}";

    private static string BuildStandaloneEntryLine(ProxyBatchEditorItemViewModel item)
        => BuildDelimitedLine(
            item.EntryName,
            item.BaseUrl,
            item.EntryApiKey,
            item.EntryModel,
            item.IncludeInBatchTest ? null : "off");

    private static string BuildSiteGroupHeaderLine(ProxyBatchEditorItemViewModel item)
        => BuildDelimitedLine("站点", item.SiteGroupName, item.SiteGroupApiKey, item.SiteGroupModel);

    private static string BuildSiteGroupChildLine(ProxyBatchEditorItemViewModel item)
        => "- " + BuildDelimitedLine(
            item.EntryName,
            item.BaseUrl,
            item.EntryApiKey,
            item.EntryModel,
            item.IncludeInBatchTest ? null : "off");

    private static bool ParseProxyBatchIncludeInTest(IReadOnlyList<string> parts)
    {
        if (parts.Count < 5)
        {
            return true;
        }

        var raw = NormalizeNullable(parts[4]);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }

        return raw.ToLowerInvariant() switch
        {
            "1" or "true" or "on" or "yes" => true,
            "0" or "false" or "off" or "no" or "skip" or "disabled" or "disable" => false,
            _ => throw new InvalidOperationException($"入口组里的测试开关只支持 on/off、true/false、1/0 等写法，当前值为：{raw}")
        };
    }

    private static string BuildDelimitedLine(
        string? first,
        string? second,
        string? third,
        string? fourth,
        string? fifth = null)
    {
        var value1 = first?.Trim() ?? string.Empty;
        var value2 = second?.Trim() ?? string.Empty;
        var value3 = third?.Trim() ?? string.Empty;
        var value4 = fourth?.Trim() ?? string.Empty;
        var value5 = fifth?.Trim() ?? string.Empty;

        if (!string.IsNullOrWhiteSpace(value5))
        {
            return $"{value1} | {value2} | {value3} | {value4} | {value5}";
        }

        if (!string.IsNullOrWhiteSpace(value4))
        {
            return $"{value1} | {value2} | {value3} | {value4}";
        }

        if (!string.IsNullOrWhiteSpace(value3))
        {
            return $"{value1} | {value2} | {value3}";
        }

        return $"{value1} | {value2}";
    }

}
