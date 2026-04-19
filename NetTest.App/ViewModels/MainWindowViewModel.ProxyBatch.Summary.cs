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
                return string.IsNullOrWhiteSpace(ProxyBaseUrl)
                    ? "入口组：还没有新增条目。留空时，会直接使用主页里的默认网址和默认 key。"
                    : $"入口组：还没有新增条目；如果直接跑组检测，会回退到主页默认网址 {ProxyBaseUrl.Trim()}。";
            }

            var siteGroupCount = entries
                .Select(entry => entry.SiteGroupName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();
            var standaloneCount = entries.Count(entry => string.IsNullOrWhiteSpace(entry.SiteGroupName));
            return $"入口组：已录入 {entries.Count} 条，其中独立入口 {standaloneCount} 条，同站组 {siteGroupCount} 个。";
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
                return "左侧还没有条目。\n\n建议操作：\n1. 右侧先填入口名称和地址。\n2. 多地址单 key 时，再补“同站组名称”和“共用 key”。\n3. 点“加入入口组”后，左侧会立即出现已填写条目。";
            }

            StringBuilder builder = new();
            foreach (var entry in entries.Take(6))
            {
                builder.AppendLine(string.IsNullOrWhiteSpace(entry.SiteGroupName)
                    ? $"{entry.Name} -> {entry.BaseUrl}"
                    : $"[{entry.SiteGroupName}] {entry.Name} -> {entry.BaseUrl}");
            }

            if (entries.Count > 6)
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
                return "组检测计划：请先在右侧小表单里新增至少 1 条记录，或确保主页已有默认网址和默认 key。";
            }

            var siteGroupCount = plan.SourceEntries
                .Select(entry => entry.SiteGroupName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();
            var defaultKeyCount = plan.Targets.Count(entry => entry.KeySource == ProxyBatchKeySource.Default);
            var siteGroupKeyCount = plan.Targets.Count(entry => entry.KeySource == ProxyBatchKeySource.SiteGroup);
            var inlineKeyCount = plan.Targets.Count(entry => entry.KeySource == ProxyBatchKeySource.Entry);
            var longStreamSummary = ProxyBatchEnableLongStreamingTest
                ? $"同时为每个入口追加 1 次长流稳定简测（{GetProxyLongStreamSegmentCount()} 段）。"
                : "当前不附带长流稳定简测。";
            return $"组检测计划：当前会运行 {plan.Targets.Count} 次基础测试；其中同站组 {siteGroupCount} 个，本条目 key {inlineKeyCount} 项，同站共用 key {siteGroupKeyCount} 项，主页默认 key 回退 {defaultKeyCount} 项。{longStreamSummary}";
        }
        catch (Exception ex)
        {
            return $"组检测计划：配置待修正。{ex.Message}";
        }
    }

    private static string BuildProxyBatchGuideSummary()
        => "常见用法一：多地址单 key\n" +
           "1. 先填“同站组名称”和“共用 key”\n" +
           "2. 再连续填写多个入口名称和地址\n" +
           "3. 每填完一条就点“新增到列表”\n\n" +
           "常见用法二：多地址多 key\n" +
           "1. 同站组留空\n" +
           "2. 每条单独填写入口名称、地址和 key\n" +
           "3. 左侧列表会逐条显示已填写结果";

    private string BuildProxyBatchEditorFormModeSummary()
    {
        var siteGroupName = NormalizeNullable(ProxyBatchFormSiteGroupName);
        if (string.IsNullOrWhiteSpace(siteGroupName))
        {
            return "当前模式：独立入口。适合一条地址配一个 key。";
        }

        var hasSharedKey = !string.IsNullOrWhiteSpace(NormalizeNullable(ProxyBatchFormSiteGroupApiKey));
        var hasEntryKey = !string.IsNullOrWhiteSpace(NormalizeNullable(ProxyBatchFormApiKey));
        if (hasSharedKey && !hasEntryKey)
        {
            return $"当前模式：多地址单 key。这个同站组会优先使用共用 key：{siteGroupName}";
        }

        if (hasEntryKey)
        {
            return $"当前模式：同站组 + 当前条目单独 key。适合某个入口单独覆盖。";
        }

        return $"当前模式：同站组继承。若此组已存在，会自动沿用同组之前填过的共用 key。";
    }

    private string BuildProxyBatchEditorListSummaryDisplay()
    {
        if (ProxyBatchEditorItems.Count == 0)
        {
            return "左侧还没有已录入入口。先在右侧选一种填写方式，填完后点“加入入口组”。";
        }

        var siteGroupCount = ProxyBatchEditorItems
            .Select(item => item.SiteGroupName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        var standaloneCount = ProxyBatchEditorItems.Count(item => string.IsNullOrWhiteSpace(item.SiteGroupName));
        return $"已录入 {ProxyBatchEditorItems.Count} 条，其中同站归类 {siteGroupCount} 组，独立入口 {standaloneCount} 条。点左侧任一项可回填到右侧继续修改。";
    }

    private string BuildProxyBatchGuideSummaryByMode()
        => _proxyBatchEditorMode switch
        {
            ProxyBatchEditorMode.SharedKeyGroup =>
                "先填一次同站组名称、共用 Key 和共用模型，再连续补入口名称与入口地址。组名留空时，会自动用域名归类。",
            ProxyBatchEditorMode.MultiKey =>
                "每条入口都带自己的 Key。可以额外填一个“站点归类”，这样左侧列表会更方便按站点观察。",
            _ =>
                "一条入口对应一组地址、Key 和模型。适合先加一个入口，或者临时补一条单独检测记录。"
        };

    private string BuildProxyBatchEditorFormModeSummaryByMode()
        => _proxyBatchEditorMode switch
        {
            ProxyBatchEditorMode.SharedKeyGroup => "当前模式：同站多入口共用 Key。适合同一个站点挂多个入口地址，一次填好共用 Key 后连续追加。",
            ProxyBatchEditorMode.MultiKey => "当前模式：多地址多个 Key。适合每个入口都使用不同 Key，或同站不同入口各自独立认证。",
            _ => "当前模式：单地址单 Key。适合只加一条入口，或者先做单独试跑。"
        };

    private static string? NormalizeNullable(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

}
