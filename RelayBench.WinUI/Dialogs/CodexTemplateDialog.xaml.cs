using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Text.Json;
using RelayBench.Core.Models;
using RelayBench.Core.Services;

namespace RelayBench.WinUI.Dialogs;

public sealed partial class CodexTemplateDialog : ContentDialog
{
    private const string WireApiResponses = "responses";

    public CodexConfigTemplate? CodexTemplate { get; set; }

    public CodexConfigTemplate? DefaultTemplate { get; set; }

    public CodexConfigTemplate? Result { get; private set; }

    public CodexTemplateDialog()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        PrimaryButtonClick += OnPrimaryButtonClick;
        SecondaryButtonClick += OnSecondaryButtonClick;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
        => LoadTemplate(CodexTemplate ?? CreateEmptyTemplate());

    private void OnSecondaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        args.Cancel = true;
        Result = null;
        LoadTemplate(DefaultTemplate ?? CreateEmptyTemplate());
    }

    private void LoadTemplate(CodexConfigTemplate template)
    {
        ModelBox.Text = template.Model;
        ProviderNameBox.Text = template.ProviderName;
        BaseUrlBox.Text = template.BaseUrl;
        SetOptionalNumber(ContextWindowBox, template.ModelContextWindow);
        SetOptionalNumber(AutoCompactBox, template.ModelAutoCompactTokenLimit);
        SelectStringSetting(ReasoningEffortBox, template.AdditionalRawSettings, "model_reasoning_effort");
        SelectStringSetting(ReasoningSummaryBox, template.AdditionalRawSettings, "model_reasoning_summary");
        SelectStringSetting(VerbosityBox, template.AdditionalRawSettings, "model_verbosity");
        SelectRawSetting(WebSearchBox, template.AdditionalRawSettings, "tools.web_search");
        SelectStringSetting(ApprovalPolicyBox, template.AdditionalRawSettings, "approval_policy");
        SelectStringSetting(SandboxModeBox, template.AdditionalRawSettings, "sandbox_mode");
        SelectRawSetting(SandboxNetworkBox, template.AdditionalRawSettings, "sandbox_workspace_write.network_access");
        SelectStringSetting(PersonalityBox, template.AdditionalRawSettings, "personality");
        SelectRawSetting(FeatureHooksBox, template.AdditionalRawSettings, "features.hooks");
        SelectRawSetting(ShellSnapshotBox, template.AdditionalRawSettings, "features.shell_snapshot");
        SetOptionalNumber(RequestRetriesBox, template.RequestMaxRetries);
        SetOptionalNumber(StreamRetriesBox, template.StreamMaxRetries);
        SetOptionalNumber(StreamIdleTimeoutBox, template.StreamIdleTimeoutMs);
        ApiKeyBox.Password = template.ExperimentalBearerToken;
        HttpHeadersBox.Text = template.HttpHeaders;
        AdditionalSettingsBox.Text = FormatAdditionalSettings(RemoveCommonSettings(template.AdditionalRawSettings));
        ValidateRequiredFields();
    }

    private static CodexConfigTemplate CreateEmptyTemplate()
        => CodexFamilyConfigApplyService.CreateDefaultTemplate(
            baseUrl: "",
            apiKey: "",
            model: "",
            displayName: "RelayBench",
            modelContextWindow: null,
            preferredWireApi: "responses");

    private void OnRequiredTextChanged(object sender, TextChangedEventArgs e)
        => ValidateRequiredFields();

    private void OnRequiredPasswordChanged(object sender, RoutedEventArgs e)
        => ValidateRequiredFields();

    private void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (!ValidateRequiredFields())
        {
            args.Cancel = true;
            return;
        }

        Result = new CodexConfigTemplate(
            ModelBox.Text.Trim(),
            "relaybench",
            GetOptionalNumber(ContextWindowBox),
            GetOptionalNumber(AutoCompactBox),
            ProviderNameBox.Text.Trim(),
            BaseUrlBox.Text.Trim(),
            WireApiResponses,
            ApiKeyBox.Password.Trim(),
            string.IsNullOrWhiteSpace(HttpHeadersBox.Text)
                ? "{ \"Content-Type\" = \"application/json\" }"
                : HttpHeadersBox.Text.Trim(),
            GetOptionalNumber(RequestRetriesBox),
            GetOptionalNumber(StreamRetriesBox),
            GetOptionalNumber(StreamIdleTimeoutBox),
            BuildAdditionalSettings());
    }

    private bool ValidateRequiredFields()
    {
        var valid = true;
        var localOpenAiBaseUrlMode = CodexFamilyConfigApplyService.ShouldUseOpenAiBaseUrlMode(BaseUrlBox.Text);
        valid &= ValidateText(ModelBox, ModelError, "\u6A21\u578B\u4E0D\u80FD\u4E3A\u7A7A");
        valid &= ValidateText(ProviderNameBox, ProviderNameError, "\u63D0\u4F9B\u65B9\u540D\u79F0\u4E0D\u80FD\u4E3A\u7A7A");
        valid &= ValidateText(BaseUrlBox, BaseUrlError, "基础 URL \u4E0D\u80FD\u4E3A\u7A7A");
        if (localOpenAiBaseUrlMode && string.IsNullOrWhiteSpace(ApiKeyBox.Password))
        {
            ApiKeyError.Text = string.Empty;
            ApiKeyError.Visibility = Visibility.Collapsed;
        }
        else
        {
            valid &= ValidatePassword(ApiKeyBox, ApiKeyError, "API \u5BC6\u94A5\u4E0D\u80FD\u4E3A\u7A7A");
        }

        IsPrimaryButtonEnabled = valid;
        return valid;
    }

    private static bool ValidateText(TextBox box, TextBlock errorBlock, string message)
    {
        if (!string.IsNullOrWhiteSpace(box.Text))
        {
            errorBlock.Text = string.Empty;
            errorBlock.Visibility = Visibility.Collapsed;
            return true;
        }

        errorBlock.Text = message;
        errorBlock.Visibility = Visibility.Visible;
        return false;
    }

    private static bool ValidatePassword(PasswordBox box, TextBlock errorBlock, string message)
    {
        if (!string.IsNullOrWhiteSpace(box.Password))
        {
            errorBlock.Text = string.Empty;
            errorBlock.Visibility = Visibility.Collapsed;
            return true;
        }

        errorBlock.Text = message;
        errorBlock.Visibility = Visibility.Visible;
        return false;
    }

    private static void SetOptionalNumber(NumberBox box, int? value)
        => box.Value = value ?? double.NaN;

    private static int? GetOptionalNumber(NumberBox box)
    {
        if (double.IsNaN(box.Value))
        {
            return null;
        }

        return (int)Math.Round(box.Value, MidpointRounding.AwayFromZero);
    }

    private IReadOnlyDictionary<string, string>? BuildAdditionalSettings()
    {
        var settings = ParseAdditionalSettings(AdditionalSettingsBox.Text) is { } parsed
            ? new Dictionary<string, string>(parsed, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        MergeStringSetting(settings, "model_reasoning_effort", ReasoningEffortBox);
        MergeStringSetting(settings, "model_reasoning_summary", ReasoningSummaryBox);
        MergeStringSetting(settings, "model_verbosity", VerbosityBox);
        MergeRawSetting(settings, "tools.web_search", WebSearchBox);
        MergeStringSetting(settings, "approval_policy", ApprovalPolicyBox);
        MergeStringSetting(settings, "sandbox_mode", SandboxModeBox);
        MergeRawSetting(settings, "sandbox_workspace_write.network_access", SandboxNetworkBox);
        MergeStringSetting(settings, "personality", PersonalityBox);
        MergeRawSetting(settings, "features.hooks", FeatureHooksBox);
        MergeRawSetting(settings, "features.shell_snapshot", ShellSnapshotBox);

        return settings.Count == 0 ? null : settings;
    }

    private static IReadOnlyDictionary<string, string>? ParseAdditionalSettings(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        Dictionary<string, string> settings = new(StringComparer.OrdinalIgnoreCase);
        foreach (var rawLine in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var separator = line.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            var key = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();
            if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(value))
            {
                settings[key] = value;
            }
        }

        return settings.Count == 0 ? null : settings;
    }

    private static IReadOnlyDictionary<string, string>? RemoveCommonSettings(IReadOnlyDictionary<string, string>? settings)
    {
        if (settings is null || settings.Count == 0)
        {
            return null;
        }

        var filtered = settings
            .Where(static pair => !IsCommonSettingKey(pair.Key))
            .ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        return filtered.Count == 0 ? null : filtered;
    }

    private static bool IsCommonSettingKey(string key)
        => key is
            "model_reasoning_effort" or
            "model_reasoning_summary" or
            "model_verbosity" or
            "tools.web_search" or
            "approval_policy" or
            "sandbox_mode" or
            "sandbox_workspace_write.network_access" or
            "personality" or
            "features.hooks" or
            "features.shell_snapshot";

    private static void SelectStringSetting(
        ComboBox box,
        IReadOnlyDictionary<string, string>? settings,
        string key)
        => SelectComboTag(box, TryReadSettingValue(settings, key, stringValue: true));

    private static void SelectRawSetting(
        ComboBox box,
        IReadOnlyDictionary<string, string>? settings,
        string key)
        => SelectComboTag(box, TryReadSettingValue(settings, key, stringValue: false));

    private static string? TryReadSettingValue(
        IReadOnlyDictionary<string, string>? settings,
        string key,
        bool stringValue)
    {
        if (settings is null ||
            !settings.TryGetValue(key, out var rawValue) ||
            string.IsNullOrWhiteSpace(rawValue))
        {
            return null;
        }

        var value = rawValue.Trim();
        if (!stringValue)
        {
            return value.Trim('"', '\'');
        }

        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
        {
            try
            {
                return JsonSerializer.Deserialize<string>(value);
            }
            catch
            {
                return value.Trim('"');
            }
        }

        if (value.Length >= 2 && value[0] == '\'' && value[^1] == '\'')
        {
            return value[1..^1];
        }

        return value;
    }

    private static void SelectComboTag(ComboBox box, string? value)
    {
        box.SelectedIndex = 0;
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        for (var index = 0; index < box.Items.Count; index++)
        {
            if (box.Items[index] is ComboBoxItem item &&
                string.Equals(item.Tag?.ToString(), value.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                box.SelectedIndex = index;
                return;
            }
        }
    }

    private static void MergeStringSetting(Dictionary<string, string> settings, string key, ComboBox box)
    {
        var value = GetSelectedTag(box);
        if (string.IsNullOrWhiteSpace(value))
        {
            settings.Remove(key);
            return;
        }

        settings[key] = JsonSerializer.Serialize(value);
    }

    private static void MergeRawSetting(Dictionary<string, string> settings, string key, ComboBox box)
    {
        var value = GetSelectedTag(box);
        if (string.IsNullOrWhiteSpace(value))
        {
            settings.Remove(key);
            return;
        }

        settings[key] = value;
    }

    private static string? GetSelectedTag(ComboBox box)
        => box.SelectedItem is ComboBoxItem item
            ? item.Tag?.ToString()
            : null;

    private static string FormatAdditionalSettings(IReadOnlyDictionary<string, string>? settings)
    {
        if (settings is null || settings.Count == 0)
        {
            return string.Empty;
        }

        return string.Join(
            Environment.NewLine,
            settings
                .OrderBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(static pair => $"{pair.Key} = {pair.Value}"));
    }
}
