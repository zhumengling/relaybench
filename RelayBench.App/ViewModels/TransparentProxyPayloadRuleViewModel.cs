using System.Text.Json;

namespace RelayBench.App.ViewModels;

public sealed class TransparentProxyPayloadRuleViewModel
{
    private TransparentProxyPayloadRuleViewModel(
        bool isConfigured,
        bool isValid,
        int defaultRules,
        int overrideRules,
        int filterRules,
        string errorMessage)
    {
        IsConfigured = isConfigured;
        IsValid = isValid;
        DefaultRules = defaultRules;
        OverrideRules = overrideRules;
        FilterRules = filterRules;
        ErrorMessage = errorMessage;
    }

    public bool IsConfigured { get; }

    public bool IsValid { get; }

    public int DefaultRules { get; }

    public int OverrideRules { get; }

    public int FilterRules { get; }

    public int TotalRules => DefaultRules + OverrideRules + FilterRules;

    public string ErrorMessage { get; }

    public string Summary
    {
        get
        {
            if (!IsConfigured)
            {
                return "未配置请求体规则";
            }

            if (!IsValid)
            {
                return $"规则 JSON 无法解析：{ErrorMessage}";
            }

            return TotalRules <= 0
                ? "已配置规则容器，暂无生效规则"
                : $"规则 {TotalRules} 条：默认 {DefaultRules} / 覆盖 {OverrideRules} / 过滤 {FilterRules}";
        }
    }

    public string StateBrush
        => !IsConfigured
            ? "#64748B"
            : IsValid
                ? "#0F62FE"
                : "#DA1E28";

    public static TransparentProxyPayloadRuleViewModel FromText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new TransparentProxyPayloadRuleViewModel(false, true, 0, 0, 0, string.Empty);
        }

        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            var defaults = CountActionRules(root, "default");
            var overrides = CountActionRules(root, "override");
            var filters = CountActionRules(root, "filter");
            return new TransparentProxyPayloadRuleViewModel(true, true, defaults, overrides, filters, string.Empty);
        }
        catch (JsonException ex)
        {
            return new TransparentProxyPayloadRuleViewModel(true, false, 0, 0, 0, ex.Message);
        }
    }

    private static int CountActionRules(JsonElement root, string action)
    {
        if (root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty(action, out var actionArray) &&
            actionArray.ValueKind == JsonValueKind.Array)
        {
            return actionArray.GetArrayLength();
        }

        if (root.ValueKind != JsonValueKind.Array)
        {
            return 0;
        }

        var count = 0;
        foreach (var item in root.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Object &&
                item.TryGetProperty("action", out var actionProperty) &&
                actionProperty.ValueKind == JsonValueKind.String &&
                string.Equals(actionProperty.GetString(), action, StringComparison.OrdinalIgnoreCase))
            {
                count++;
            }
        }

        return count;
    }
}
