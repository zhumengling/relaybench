using RelayBench.App.Services;
using RelayBench.Core.Services;

namespace RelayBench.App.ViewModels;

public sealed class TransparentProxyModelPoolMemberViewModel
{
    public TransparentProxyModelPoolMemberViewModel(TransparentProxyModelPoolMemberSnapshot snapshot)
    {
        RouteId = snapshot.RouteId;
        RouteName = snapshot.RouteName;
        BaseUrl = snapshot.BaseUrl;
        Prefix = snapshot.Prefix;
        Priority = snapshot.Priority;
        UpstreamModel = string.IsNullOrWhiteSpace(snapshot.UpstreamModel)
            ? "按请求透传"
            : snapshot.UpstreamModel;
        ClientModel = snapshot.ClientModel;
        Sent = snapshot.Sent;
        Success = snapshot.Success;
        Failed = snapshot.Failed;
        LastLatencyMs = snapshot.LastLatencyMs;
        ConsecutiveFailures = snapshot.ConsecutiveFailures;
        CircuitState = snapshot.CircuitState;
        CircuitOpenUntil = snapshot.CircuitOpenUntil;
        ModelCooldownUntil = snapshot.ModelCooldownUntil;
        ProtocolText = ResolveProtocolText(snapshot);
        HealthText = ResolveHealthText(snapshot);
        HealthBrush = ResolveHealthBrush(HealthText);
    }

    public string RouteId { get; }

    public string RouteName { get; }

    public string BaseUrl { get; }

    public string Prefix { get; }

    public int Priority { get; }

    public string UpstreamModel { get; }

    public string ClientModel { get; }

    public int Sent { get; }

    public int Success { get; }

    public int Failed { get; }

    public long LastLatencyMs { get; }

    public int ConsecutiveFailures { get; }

    public string CircuitState { get; }

    public DateTimeOffset CircuitOpenUntil { get; }

    public DateTimeOffset ModelCooldownUntil { get; }

    public string ProtocolText { get; }

    public string HealthText { get; }

    public string HealthBrush { get; }

    public string LatencyText
        => LastLatencyMs <= 0 ? "-" : $"{LastLatencyMs} ms";

    public string ToolTipLine
        => $"{RouteName}  |  {UpstreamModel}  |  {ProtocolText}  |  {HealthText}";

    private static string ResolveProtocolText(TransparentProxyModelPoolMemberSnapshot snapshot)
    {
        if (!string.IsNullOrWhiteSpace(snapshot.PreferredWireApi))
        {
            return snapshot.PreferredWireApi switch
            {
                ProxyWireApiProbeService.ResponsesWireApi => "Responses",
                ProxyWireApiProbeService.AnthropicMessagesWireApi => "Anthropic",
                ProxyWireApiProbeService.ChatCompletionsWireApi => "OpenAI",
                _ => snapshot.PreferredWireApi
            };
        }

        List<string> parts = [];
        if (snapshot.ResponsesSupported == true)
        {
            parts.Add("Responses");
        }

        if (snapshot.AnthropicMessagesSupported == true)
        {
            parts.Add("Anthropic");
        }

        if (snapshot.ChatCompletionsSupported == true)
        {
            parts.Add("OpenAI");
        }

        return parts.Count == 0 ? "待探测" : string.Join("/", parts);
    }

    private static string ResolveHealthText(TransparentProxyModelPoolMemberSnapshot snapshot)
    {
        if (snapshot.IsModelCooling)
        {
            return $"模型冷却到 {snapshot.ModelCooldownUntil.ToLocalTime():HH:mm:ss}";
        }

        if (string.Equals(snapshot.CircuitState, "Open", StringComparison.OrdinalIgnoreCase))
        {
            return $"熔断到 {snapshot.CircuitOpenUntil.ToLocalTime():HH:mm:ss}";
        }

        if (snapshot.ConsecutiveFailures > 0)
        {
            return $"观察 {snapshot.ConsecutiveFailures}";
        }

        return "可调度";
    }

    private static string ResolveHealthBrush(string healthText)
        => healthText.StartsWith("可调度", StringComparison.Ordinal)
            ? "#24A148"
            : healthText.StartsWith("观察", StringComparison.Ordinal)
                ? "#F1C21B"
                : "#DA1E28";
}
