using System.Text;
using NetTest.App.Infrastructure;
using NetTest.Core.Models;

namespace NetTest.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private string BuildProxyBatchTargetDigestSummary()
    {
        try
        {
            var entries = ParseProxyBatchSourceEntries(ProxyBatchTargetsText, allowEmpty: true);
            if (entries.Count == 0)
            {
                return "入口组：还没有录入站点。右侧先填一个站点，点“加入入口组”后会自动清空，继续下一个。";
            }

            var siteCount = entries
                .Select(ResolveProxyBatchSourceSiteName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();
            var urlsWithAvailableKey = entries.Count(entry =>
                !string.IsNullOrWhiteSpace(entry.ApiKey) ||
                !string.IsNullOrWhiteSpace(entry.SiteGroupApiKey));
            var urlsMissingKey = entries.Count - urlsWithAvailableKey;

            return urlsMissingKey > 0
                ? $"入口组：已录入 {siteCount} 个站点，共 {entries.Count} 个网址；其中 {urlsMissingKey} 个网址仍缺 key。"
                : $"入口组：已录入 {siteCount} 个站点，共 {entries.Count} 个网址；当前所有网址都已带入可用 key。";
        }
        catch (Exception ex)
        {
            return $"入口组：配置待修正。{ex.Message}";
        }
    }

    private string BuildProxyBatchTargetPreviewSummary()
    {
        try
        {
            var entries = ParseProxyBatchSourceEntries(ProxyBatchTargetsText, allowEmpty: true);
            if (entries.Count == 0)
            {
                return "左侧还没有已录入站点。\n\n建议操作：\n1. 右侧一次填写一个站点，站点内一行一个网址。\n2. 可以逐行输入，也可以直接从剪贴板粘贴。\n3. 点“加入入口组”后，右侧会自动清空，只保留第一行空白。";
            }

            StringBuilder builder = new();
            var siteGroups = entries
                .GroupBy(ResolveProxyBatchSourceSiteName, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            foreach (var siteGroup in siteGroups.Take(6))
            {
                var urls = siteGroup.ToArray();
                builder.AppendLine($"[{siteGroup.Key}] 共 {urls.Length} 个网址");
                foreach (var entry in urls.Take(2))
                {
                    builder.AppendLine($"  - {entry.Name} -> {entry.BaseUrl}");
                }

                if (urls.Length > 2)
                {
                    builder.AppendLine($"  - ... 另有 {urls.Length - 2} 个网址");
                }
            }

            if (siteGroups.Length > 6)
            {
                builder.AppendLine("...");
            }

            return builder.ToString().TrimEnd();
        }
        catch (Exception ex)
        {
            return $"预览失败：{ex.Message}";
        }
    }

    private string BuildProxyBatchExecutionPlanSummary()
    {
        try
        {
            var plan = BuildProxyBatchPlan(requireRunnable: false);
            if (plan.Targets.Count == 0)
            {
                return "组检测计划：右侧先录入一个站点，再开始快速对比。";
            }

            var siteCount = plan.Targets
                .Select(ResolveProxyBatchTargetSiteName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();
            var inlineKeyCount = plan.Targets.Count(entry => entry.KeySource == ProxyBatchKeySource.Entry);
            var siteGroupKeyCount = plan.Targets.Count(entry => entry.KeySource == ProxyBatchKeySource.SiteGroup);
            var longStreamSummary = ProxyBatchEnableLongStreamingTest
                ? $"另外会为每个网址追加 1 次长流稳定性简测（{GetProxyLongStreamSegmentCount()} 段）。"
                : "当前不附带长流稳定性简测。";

            return $"组检测计划：当前会运行 {plan.Targets.Count} 个网址测试，覆盖 {siteCount} 个站点；其中本行 key {inlineKeyCount} 项，站点内继承 key {siteGroupKeyCount} 项。{longStreamSummary}";
        }
        catch (Exception ex)
        {
            return $"组检测计划：配置待修正。{ex.Message}";
        }
    }

    private string BuildProxyBatchEditorListSummaryDisplay()
    {
        if (ProxyBatchSiteGroups.Count == 0)
        {
            return "左侧还没有已录入站点。右侧填完一个站点后点“加入入口组”，表格会自动清空，直接继续下一站。";
        }

        return $"已录入 {ProxyBatchSiteGroups.Count} 个站点，共 {ProxyBatchEditorItems.Count} 个网址。点左侧任一站点，可回填到右侧继续修改或删除。";
    }

    private string BuildProxyBatchGuideSummaryByMode()
        => "使用方式：\n" +
           "1. 右侧一次录入一个站点，站点内一行一个网址。\n" +
           "2. 可以逐行输入，也可以从剪贴板粘贴整表或零散数据；如果只粘贴了 URL 或 key，空白列会自动沿用上一行。\n" +
           "3. 点击“加入入口组”后，当前站点会保存到左侧，右侧自动清空为第一行空白。\n" +
           "4. 左侧点任一站点，可回填修改；不填站点名时，会按第一条有效名称或域名命名。";

    private string BuildProxyBatchEditorFormModeSummaryByMode()
        => "当前模式：站点批量录入。右侧表格里的所有行都会按同一个站点保存；模型列最后一格的 ⟳ 会按当前行 URL + key 拉取模型。";

    private static string ResolveProxyBatchSourceSiteName(ProxyBatchSourceEntry entry)
        => NormalizeNullable(entry.SiteGroupName)
           ?? NormalizeNullable(entry.Name)
           ?? TryGetHost(entry.BaseUrl)
           ?? "未命名站点";

    private static string ResolveProxyBatchTargetSiteName(ProxyBatchTargetEntry entry)
        => NormalizeNullable(entry.SiteGroupName)
           ?? NormalizeNullable(entry.SourceEntryName)
           ?? TryGetHost(entry.BaseUrl)
           ?? "未命名站点";

    private static string? NormalizeNullable(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
}
