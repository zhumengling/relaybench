using System.Text;
using NetTest.Core.Models;
using NetTest.Core.Services;

namespace NetTest.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private readonly UnlockCatalogDiagnosticsService _unlockCatalogDiagnosticsService = new();
    private string _unlockCatalogSummary = "运行可用性检测后，这里会显示扩展站点 / API 目录的可达性与业务语义结果。";
    private string _unlockCatalogDetail = "尚无扩展可用性目录结果。";

    public string UnlockCatalogSummary
    {
        get => _unlockCatalogSummary;
        private set => SetProperty(ref _unlockCatalogSummary, value);
    }

    public string UnlockCatalogDetail
    {
        get => _unlockCatalogDetail;
        private set => SetProperty(ref _unlockCatalogDetail, value);
    }

    private void ApplyUnlockCatalogResult(UnlockCatalogResult result)
    {
        _lastUnlockCatalogResult = result;

        var providerSummary = string.Join(
            "；",
            result.Checks
                .GroupBy(check => check.Provider)
                .Select(group =>
                {
                    var reachable = group.Count(check => check.Reachable);
                    var ready = group.Count(check => IsSemanticCategory(check.SemanticCategory, "Ready"));
                    var authRequired = group.Count(check => IsSemanticCategory(check.SemanticCategory, "AuthRequired"));
                    var regionRestricted = group.Count(check => IsSemanticCategory(check.SemanticCategory, "RegionRestricted"));
                    var reviewRequired = group.Count(check => IsSemanticCategory(check.SemanticCategory, "ReviewRequired"));
                    var unreachable = group.Count(check => IsSemanticCategory(check.SemanticCategory, "Unreachable"));

                    return $"{group.Key}：可达 {reachable}/{group.Count()}，业务就绪 {ready}，需鉴权 {authRequired}，地区受限 {regionRestricted}，待复核 {reviewRequired}，不可达 {unreachable}";
                }));

        UnlockCatalogSummary =
            $"检测时间：{result.CheckedAt:yyyy-MM-dd HH:mm:ss}\n" +
            $"目标数：{result.Checks.Count}\n" +
            $"网络可达：{result.ReachableCount}/{result.Checks.Count}\n" +
            $"业务就绪：{result.SemanticReadyCount}/{result.Checks.Count}\n" +
            $"需鉴权：{result.AuthenticationRequiredCount}\n" +
            $"疑似地区限制：{result.RegionRestrictedCount}\n" +
            $"待复核：{result.ReviewRequiredCount}\n" +
            $"提供商概览：{(string.IsNullOrWhiteSpace(providerSummary) ? "无" : providerSummary)}\n" +
            $"摘要：{result.Summary}\n" +
            $"错误：{result.Error ?? "无"}";

        StringBuilder builder = new();
        foreach (var check in result.Checks.OrderBy(check => check.Provider, StringComparer.OrdinalIgnoreCase).ThenBy(check => check.Name, StringComparer.OrdinalIgnoreCase))
        {
            builder.AppendLine($"{check.Name} [{check.Provider}]");
            builder.AppendLine($"地址：{check.Url}");
            builder.AppendLine($"方法：{check.Method}");
            builder.AppendLine($"网络可达：{(check.Reachable ? "是" : "否")}");
            builder.AppendLine($"状态码：{check.StatusCode?.ToString() ?? "--"}");
            builder.AppendLine($"延迟：{FormatMilliseconds(check.Latency)}");
            builder.AppendLine($"网络层结论：{check.Verdict}");
            builder.AppendLine($"业务语义分类：{TranslateSemanticCategory(check.SemanticCategory)}");
            builder.AppendLine($"业务语义结论：{check.SemanticVerdict}");
            builder.AppendLine($"网络摘要：{check.Summary}");
            builder.AppendLine($"业务说明：{check.SemanticSummary}");
            builder.AppendLine($"证据：{check.Evidence ?? "无"}");
            builder.AppendLine($"最终地址：{check.FinalUrl ?? "无"}");
            builder.AppendLine($"内容类型：{check.ResponseContentType ?? "无"}");
            builder.AppendLine($"错误：{check.Error ?? "无"}");
            builder.AppendLine();
        }

        UnlockCatalogDetail = builder.Length == 0
            ? "尚无扩展可用性目录结果。"
            : builder.ToString().TrimEnd();

        RefreshOfficialApiStatusRows(result);
                AppendModuleOutput("扩展可用性目录返回", UnlockCatalogSummary, UnlockCatalogDetail);
    }

    private static bool IsSemanticCategory(string? actual, string expected)
        => string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);

    private static string TranslateSemanticCategory(string? category)
        => category?.Trim() switch
        {
            "Ready" => "业务就绪",
            "AuthRequired" => "需要鉴权",
            "RegionRestricted" => "疑似地区限制",
            "ReviewRequired" => "待复核",
            "Unreachable" => "网络不可达",
            null or "" => "未分类",
            _ => category
        };
}
