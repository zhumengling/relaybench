using System.Text;
using NetTest.Core.Models;

namespace NetTest.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private void ApplyProxyModelCatalogResult(ProxyModelCatalogResult result)
    {
        var recommendedModel = result.Models.FirstOrDefault();
        ProxyCatalogModels.Clear();
        foreach (var model in result.Models)
        {
            ProxyCatalogModels.Add(model);
        }

        ProxyModelCatalogFilterText = string.Empty;
        RefreshVisibleProxyCatalogModels();
        RefreshVisibleProxyMultiModelCatalogItems(trimToCatalog: result.Success);

        ProxyModelCatalogSummary =
            $"检测时间：{result.CheckedAt:yyyy-MM-dd HH:mm:ss}\n" +
            $"目标地址：{result.BaseUrl}\n" +
            $"状态：{(result.Success ? "拉取成功" : "拉取失败")}\n" +
            $"状态码：{result.StatusCode?.ToString() ?? "--"}\n" +
            $"模型数量：{result.ModelCount}\n" +
            $"弹窗可见模型：{VisibleProxyCatalogModels.Count}\n" +
            $"耗时：{FormatMilliseconds(result.Latency)}\n" +
            $"推荐模型：{recommendedModel ?? "未识别"}\n" +
            $"可追溯性：{result.TraceabilitySummary ?? "未识别"}\n" +
            $"CDN / 边缘：{result.CdnSummary ?? "无明显特征"}\n" +
            $"摘要：{result.Summary}";

        StringBuilder builder = new();
        builder.AppendLine("模型列表：");
        if (result.Models.Count == 0)
        {
            builder.AppendLine("未解析到模型。");
        }
        else
        {
            foreach (var model in result.Models)
            {
                builder.AppendLine(model);
            }
        }

        builder.AppendLine();
        builder.AppendLine($"解析地址：{(result.ResolvedAddresses is { Count: > 0 } ? string.Join(", ", result.ResolvedAddresses) : "未获取")}");
        builder.AppendLine($"CDN 提供商：{result.CdnProvider ?? "未识别"}");
        builder.AppendLine($"边缘签名：{result.EdgeSignature ?? "未识别"}");
        builder.AppendLine($"Request-ID：{result.RequestId ?? "--"}");
        builder.AppendLine($"Trace-ID：{result.TraceId ?? "--"}");
        builder.AppendLine($"可追溯性：{result.TraceabilitySummary ?? "未识别"}");

        if (result.ResponseHeaders is { Count: > 0 })
        {
            builder.AppendLine();
            builder.AppendLine("关键响应头：");
            foreach (var header in result.ResponseHeaders)
            {
                builder.AppendLine(header);
            }
        }

        builder.AppendLine();
        builder.AppendLine($"错误：{result.Error ?? "无"}");
        ProxyModelCatalogDetail = builder.ToString().TrimEnd();

        SyncSelectedProxyCatalogModel(GetCurrentProxyModelPickerValue());

        AppendModuleOutput("接口模型列表返回", ProxyModelCatalogSummary, ProxyModelCatalogDetail);
        SaveState();
    }
}
