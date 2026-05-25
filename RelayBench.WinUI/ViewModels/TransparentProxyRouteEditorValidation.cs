using System.Text.Json;
using RelayBench.Services;

namespace RelayBench.WinUI.ViewModels;

internal sealed record TransparentProxyTextValidationResult(
    bool IsConfigured,
    bool IsValid,
    string Message);

internal sealed class TransparentProxyPayloadRulePreview
{
    private TransparentProxyPayloadRulePreview(
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
                return "\u672A\u914D\u7F6E Payload \u89C4\u5219";
            }

            if (!IsValid)
            {
                return $"Payload \u89C4\u5219 JSON \u65E0\u6CD5\u89E3\u6790\uFF1A{ErrorMessage}";
            }

            return TotalRules <= 0
                ? "\u5DF2\u914D\u7F6E\u89C4\u5219\u5BB9\u5668\uFF0C\u6682\u65E0\u751F\u6548\u89C4\u5219"
                : $"Payload \u89C4\u5219 {TotalRules} \u6761\uFF1A\u9ED8\u8BA4 {DefaultRules} / \u8986\u76D6 {OverrideRules} / \u8FC7\u6EE4 {FilterRules}";
        }
    }

    public static TransparentProxyPayloadRulePreview FromText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new TransparentProxyPayloadRulePreview(false, true, 0, 0, 0, string.Empty);
        }

        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            if (root.ValueKind is not JsonValueKind.Object and not JsonValueKind.Array)
            {
                return new TransparentProxyPayloadRulePreview(true, false, 0, 0, 0, "\u6839\u8282\u70B9\u5FC5\u987B\u662F object \u6216 array");
            }

            var defaults = CountActionRules(root, "default") +
                           CountActionRules(root, "default-raw") +
                           CountActionRules(root, "default_raw");
            var overrides = CountActionRules(root, "override") +
                            CountActionRules(root, "override-raw") +
                            CountActionRules(root, "override_raw");
            var filters = CountActionRules(root, "filter") + CountPrivateFilterRules(root);
            return new TransparentProxyPayloadRulePreview(true, true, defaults, overrides, filters, string.Empty);
        }
        catch (JsonException ex)
        {
            return new TransparentProxyPayloadRulePreview(true, false, 0, 0, 0, ex.Message);
        }
    }

    private static int CountActionRules(JsonElement root, string action)
    {
        if (root.ValueKind == JsonValueKind.Object)
        {
            var count = 0;
            if (root.TryGetProperty(action, out var actionNode))
            {
                count += actionNode.ValueKind switch
                {
                    JsonValueKind.Array => actionNode.GetArrayLength(),
                    JsonValueKind.Object => 1,
                    _ => 0
                };
            }

            if (root.TryGetProperty("action", out var actionProperty) &&
                actionProperty.ValueKind == JsonValueKind.String &&
                string.Equals(actionProperty.GetString(), action, StringComparison.OrdinalIgnoreCase))
            {
                count++;
            }

            return count;
        }

        if (root.ValueKind != JsonValueKind.Array)
        {
            return 0;
        }

        var arrayCount = 0;
        foreach (var item in root.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (item.TryGetProperty("action", out var actionProperty) &&
                actionProperty.ValueKind == JsonValueKind.String &&
                string.Equals(actionProperty.GetString(), action, StringComparison.OrdinalIgnoreCase))
            {
                arrayCount++;
            }
            else if (string.Equals(action, "filter", StringComparison.OrdinalIgnoreCase) &&
                     item.TryGetProperty("filter", out _))
            {
                arrayCount++;
            }
            else if (string.Equals(action, "override", StringComparison.OrdinalIgnoreCase) &&
                     !item.TryGetProperty("action", out _) &&
                     !item.TryGetProperty("filter", out _))
            {
                arrayCount++;
            }
        }

        return arrayCount;
    }

    private static int CountPrivateFilterRules(JsonElement root)
        => CountActionRules(root, "filter_private") +
           CountActionRules(root, "filter-private") +
           CountActionRules(root, "private_filter") +
           CountActionRules(root, "private-fields");
}

internal static class TransparentProxyRouteEditorValidation
{
    public static TransparentProxyTextValidationResult ValidateHeadersJson(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new TransparentProxyTextValidationResult(false, true, "\u672A\u914D\u7F6E\u81EA\u5B9A\u4E49 Header");
        }

        try
        {
            using var document = JsonDocument.Parse(text);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return new TransparentProxyTextValidationResult(true, false, "Header JSON \u6839\u8282\u70B9\u5FC5\u987B\u662F object");
            }

            var count = document.RootElement.EnumerateObject().Count();
            return new TransparentProxyTextValidationResult(true, true, $"Headers {count} \u9879\uFF0C\u4F1A\u5728\u8DEF\u7531\u8BF7\u6C42\u4E0A\u6E38\u65F6\u5408\u5E76");
        }
        catch (JsonException ex)
        {
            return new TransparentProxyTextValidationResult(true, false, $"Header JSON \u65E0\u6CD5\u89E3\u6790\uFF1A{ex.Message}");
        }
    }

    public static TransparentProxyTextValidationResult ValidateOutboundProxy(string? text)
    {
        var setting = TransparentProxyOutboundProxy.Parse(text);
        if (setting.Mode is TransparentProxyOutboundProxyMode.Inherit)
        {
            return new TransparentProxyTextValidationResult(false, true, "\u672A\u914D\u7F6E\u51FA\u7AD9\u4EE3\u7406\uFF0C\u7EE7\u627F\u7CFB\u7EDF\u4EE3\u7406\u8BBE\u7F6E");
        }

        if (setting.Mode is TransparentProxyOutboundProxyMode.Direct)
        {
            return new TransparentProxyTextValidationResult(true, true, "\u5DF2\u5F3A\u5236\u76F4\u8FDE\uFF0C\u4E0D\u4F7F\u7528\u7CFB\u7EDF\u4EE3\u7406");
        }

        if (setting.Mode is TransparentProxyOutboundProxyMode.Invalid &&
            setting.Error is TransparentProxyOutboundProxyError.MissingSchemeOrHost)
        {
            return new TransparentProxyTextValidationResult(true, false, "\u51FA\u7AD9\u4EE3\u7406\u5FC5\u987B\u662F direct/none \u6216\u5B8C\u6574 URL");
        }

        if (setting.Mode is TransparentProxyOutboundProxyMode.Invalid)
        {
            return new TransparentProxyTextValidationResult(true, false, "\u4EC5\u652F\u6301 http / https / socks5 / socks5h \u51FA\u7AD9\u4EE3\u7406");
        }

        return new TransparentProxyTextValidationResult(true, true, $"\u51FA\u7AD9\u4EE3\u7406\u5DF2\u8BBE\u7F6E\uFF1A{setting.DisplayEndpoint}");
    }

    public static string FormatJson(string text)
    {
        using var document = JsonDocument.Parse(text);
        return JsonSerializer.Serialize(document.RootElement, new JsonSerializerOptions { WriteIndented = true });
    }
}
