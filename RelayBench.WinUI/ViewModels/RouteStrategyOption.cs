using RelayBench.Services;

namespace RelayBench.WinUI.ViewModels;

public sealed class RouteStrategyOption
{
    public RouteStrategyOption(string key, string name, string description)
    {
        Key = key;
        Name = name;
        Description = description;
    }

    public string Key { get; set; }

    public string Name { get; set; }

    public string Description { get; set; }

    public static IReadOnlyList<RouteStrategyOption> Defaults { get; } =
    [
        new(TransparentProxyRouteStrategies.Smart, "智能调度", "综合优先级、健康状态和故障切换自动选择上游。"),
        new(TransparentProxyRouteStrategies.RoundRobin, "轮询", "在可用上游之间轮流分配请求。"),
        new(TransparentProxyRouteStrategies.Priority, "优先级", "优先使用高优先级路由，失败后再切换。"),
        new(TransparentProxyRouteStrategies.FillFirst, "填满优先", "尽量填满当前高优先级入口，再切换到备用入口。"),
        new(TransparentProxyRouteStrategies.LowestLatency, "最低延迟", "优先选择最近延迟最低的可用上游。"),
        new(TransparentProxyRouteStrategies.SessionAffinity, "会话粘滞", "同一会话尽量保持在同一条上游路由。")
    ];

    public static string GetDisplayName(string? key)
    {
        var normalized = TransparentProxyRouteStrategies.Normalize(key);
        return Defaults.FirstOrDefault(option =>
            string.Equals(option.Key, normalized, StringComparison.OrdinalIgnoreCase))?.Name ?? "智能调度";
    }

    public static string GetDescription(string? key)
    {
        var normalized = TransparentProxyRouteStrategies.Normalize(key);
        return Defaults.FirstOrDefault(option =>
            string.Equals(option.Key, normalized, StringComparison.OrdinalIgnoreCase))?.Description ??
               Defaults[0].Description;
    }
}
