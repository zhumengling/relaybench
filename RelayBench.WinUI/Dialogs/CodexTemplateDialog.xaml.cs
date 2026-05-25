using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RelayBench.Core.Models;
using RelayBench.Core.Services;

namespace RelayBench.WinUI.Dialogs;

public sealed partial class CodexTemplateDialog : ContentDialog
{
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
        SelectWireApi(template.WireApi);
        SetOptionalNumber(ContextWindowBox, template.ModelContextWindow);
        SetOptionalNumber(AutoCompactBox, template.ModelAutoCompactTokenLimit);
        SetOptionalNumber(RequestRetriesBox, template.RequestMaxRetries);
        SetOptionalNumber(StreamRetriesBox, template.StreamMaxRetries);
        SetOptionalNumber(StreamIdleTimeoutBox, template.StreamIdleTimeoutMs);
        ApiKeyBox.Password = template.ExperimentalBearerToken;
        HttpHeadersBox.Text = template.HttpHeaders;
        AdditionalSettingsBox.Text = FormatAdditionalSettings(template.AdditionalRawSettings);
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
            GetSelectedWireApi(),
            ApiKeyBox.Password.Trim(),
            string.IsNullOrWhiteSpace(HttpHeadersBox.Text)
                ? "{ \"Content-Type\" = \"application/json\" }"
                : HttpHeadersBox.Text.Trim(),
            GetOptionalNumber(RequestRetriesBox),
            GetOptionalNumber(StreamRetriesBox),
            GetOptionalNumber(StreamIdleTimeoutBox),
            ParseAdditionalSettings(AdditionalSettingsBox.Text));
    }

    private bool ValidateRequiredFields()
    {
        var valid = true;
        valid &= ValidateText(ModelBox, ModelError, "\u6A21\u578B\u4E0D\u80FD\u4E3A\u7A7A");
        valid &= ValidateText(ProviderNameBox, ProviderNameError, "\u63D0\u4F9B\u65B9\u540D\u79F0\u4E0D\u80FD\u4E3A\u7A7A");
        valid &= ValidateText(BaseUrlBox, BaseUrlError, "基础 URL \u4E0D\u80FD\u4E3A\u7A7A");
        valid &= ValidatePassword(ApiKeyBox, ApiKeyError, "API \u5BC6\u94A5\u4E0D\u80FD\u4E3A\u7A7A");
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

    private void SelectWireApi(string? wireApi)
    {
        var normalized = ProxyWireApiProbeService.NormalizeWireApi(wireApi) ?? "responses";
        WireApiBox.SelectedIndex = normalized switch
        {
            "anthropic" => 1,
            "chat" => 2,
            _ => 0
        };
    }

    private string GetSelectedWireApi()
        => WireApiBox.SelectedIndex switch
        {
            1 => "anthropic",
            2 => "chat",
            _ => "responses"
        };

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
