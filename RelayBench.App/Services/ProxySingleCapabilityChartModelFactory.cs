namespace RelayBench.App.Services;

public static class ProxySingleCapabilityChartModelFactory
{
    public static IReadOnlyList<ProxySingleCapabilityChartItem> NormalizeItems(
        IReadOnlyList<ProxySingleCapabilityChartItem> items,
        IReadOnlyList<string> baseLabels)
    {
        if (items.Count == 0)
        {
            return items;
        }

        return items
            .Select(item => new
            {
                Item = item,
                Rank = ResolveRank(item, baseLabels)
            })
            .OrderBy(item => item.Rank)
            .ThenBy(item => item.Item.Order)
            .Select((item, index) => item.Item with { Order = index + 1 })
            .ToArray();
    }

    public static int ResolveRank(
        ProxySingleCapabilityChartItem item,
        IReadOnlyList<string> baseLabels)
    {
        for (var index = 0; index < baseLabels.Count; index++)
        {
            if (string.Equals(baseLabels[index], item.Name, StringComparison.Ordinal))
            {
                return index + 1;
            }
        }

        return item.Name switch
        {
            "独立吞吐" => 100,
            "长流稳定" => 110,
            "流式完整性" => 120,
            "Embeddings" => 200,
            "Images" => 210,
            "Audio Transcription" => 220,
            "Audio Speech / TTS" => 230,
            "Moderation" => 240,
            "System Prompt" => 300,
            "Function Calling" => 310,
            "错误透传" => 320,
            "官方对照完整性" => 330,
            "多模态" => 340,
            "缓存命中" => 350,
            "指令遵循" => 360,
            "数据抽取" => 370,
            "结构化边界" => 380,
            "ToolCall 深测" => 390,
            "推理一致性" => 400,
            "代码块纪律" => 410,
            "缓存隔离" => 420,
            _ when string.Equals(item.SectionName, "多模型测速", StringComparison.Ordinal) => 900 + item.Order,
            _ => 1000 + item.Order
        };
    }
}
