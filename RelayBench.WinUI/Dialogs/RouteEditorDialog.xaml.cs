using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using RelayBench.Services;
using RelayBench.WinUI.Storage;
using RelayBench.WinUI.ViewModels;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;

namespace RelayBench.WinUI.Dialogs;

/// <summary>
/// A ContentDialog for adding or editing a proxy route definition.
/// Validates: name required and ≤64 chars, URL must be http/https, priority 0-100.
/// IsPrimaryButtonEnabled is set to false while any field is invalid.
/// </summary>
public sealed partial class RouteEditorDialog : ContentDialog
{
    private bool _nameValid;
    private bool _urlValid;
    private bool _priorityValid = true; // NumberBox defaults to 0 which is valid
    private bool _headersValid = true;
    private bool _payloadRulesValid = true;
    private bool _outboundProxyValid = true;

    /// <summary>
    /// Gets the resulting RouteDefinition after the dialog is closed with Primary button.
    /// </summary>
    public RouteDefinition? Result { get; private set; }

    /// <summary>
    /// Optional existing route to edit. When set, fields are pre-populated.
    /// </summary>
    public RouteDefinition? ExistingRoute { get; set; }

    public ObservableCollection<ModelRewriteRule> RouteModelMappings { get; } = new();

    public RouteEditorDialog()
    {
        this.InitializeComponent();
        this.Loaded += OnLoaded;
        this.PrimaryButtonClick += OnPrimaryButtonClick;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (ExistingRoute is not null)
        {
            NameBox.Text = ExistingRoute.Name;
            UrlBox.Text = ExistingRoute.UpstreamUrl;
            ApiKeyBox.Password = ExistingRoute.ApiKeyProtected ?? string.Empty;
            PriorityBox.Value = ExistingRoute.Priority;
            ModelFilterBox.Text = ExistingRoute.ModelFilter ?? string.Empty;
            LoadRouteModelMappingsFromFilter(ModelFilterBox.Text);
            PrefixBox.Text = ExistingRoute.Prefix ?? string.Empty;
            OutboundProxyBox.Text = ExistingRoute.OutboundProxy ?? string.Empty;
            RequestRetryBox.Value = ExistingRoute.RequestRetry ?? double.NaN;
            RetryIntervalBox.Value = ExistingRoute.MaxRetryIntervalSeconds ?? double.NaN;
            ModelCooldownBox.Value = ExistingRoute.ModelCooldownSeconds ?? double.NaN;
            ExcludedModelsBox.Text = ExistingRoute.ExcludedModelPatterns ?? string.Empty;
            SelectComboBoxTag(PreferredWireApiBox, ExistingRoute.PreferredWireApi);
            SelectComboBoxTag(AuthModeBox, ExistingRoute.AuthMode ?? TransparentProxyRouteAuthModes.ApiKey);
            OAuthProviderBox.Text = IsCodexOAuthSelected() ? "codex" : ExistingRoute.OAuthProvider ?? "codex";
            OAuthCredentialIdBox.Text = ExistingRoute.OAuthCredentialId ?? string.Empty;
            CodexBackendBaseUrlBox.Text = ExistingRoute.CodexBackendBaseUrl ?? string.Empty;
            CodexOAuthFastModeSwitch.IsOn = ExistingRoute.CodexOAuthFastMode;
            HeadersBox.Text = ExistingRoute.HeadersText ?? string.Empty;
            PayloadRulesBox.Text = ExistingRoute.PayloadRulesText ?? string.Empty;
        }
        else
        {
            RouteModelMappings.Clear();
            SelectComboBoxTag(PreferredWireApiBox, null);
            SelectComboBoxTag(AuthModeBox, TransparentProxyRouteAuthModes.ApiKey);
            OAuthProviderBox.Text = "codex";
            CodexOAuthFastModeSwitch.IsOn = false;
        }

        UpdateAuthModeVisibility();

        // Run initial validation
        ValidateName();
        ValidateUrl();
        ValidatePriority();
        ValidateOutboundProxy();
        ValidateHeaders();
        ValidatePayloadRules();
        UpdatePrimaryButton();
    }

    private void OnNameChanged(object sender, TextChangedEventArgs e)
    {
        ValidateName();
        UpdatePrimaryButton();
    }

    private void OnUrlChanged(object sender, TextChangedEventArgs e)
    {
        ValidateUrl();
        UpdatePrimaryButton();
    }

    private void OnApiKeyChanged(object sender, RoutedEventArgs e)
    {
        // API key is optional, no validation needed
    }

    private void OnPriorityChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        ValidatePriority();
        UpdatePrimaryButton();
    }

    private void OnAuthModeChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateAuthModeVisibility();
    }

    private void OnOutboundProxyChanged(object sender, TextChangedEventArgs e)
    {
        ValidateOutboundProxy();
        UpdatePrimaryButton();
    }

    private void OnHeadersChanged(object sender, TextChangedEventArgs e)
    {
        ValidateHeaders();
        UpdatePrimaryButton();
    }

    private void OnPayloadRulesChanged(object sender, TextChangedEventArgs e)
    {
        ValidatePayloadRules();
        UpdatePrimaryButton();
    }

    private void OnFormatHeadersClick(object sender, RoutedEventArgs e)
    {
        FormatJsonTextBox(HeadersBox);
        ValidateHeaders();
        UpdatePrimaryButton();
    }

    private void OnFormatPayloadRulesClick(object sender, RoutedEventArgs e)
    {
        FormatJsonTextBox(PayloadRulesBox);
        ValidatePayloadRules();
        UpdatePrimaryButton();
    }

    private void OnAddModelMappingClick(object sender, RoutedEventArgs e)
    {
        RouteModelMappings.Add(new ModelRewriteRule(string.Empty, string.Empty));
    }

    private void OnInsertThinkingBudgetModelClick(object sender, RoutedEventArgs e)
    {
        AppendModelFilterToken("gpt-5.5(8192)");
    }

    private void OnInsertThinkingHighModelClick(object sender, RoutedEventArgs e)
    {
        AppendModelFilterToken("gpt-5.5(high)");
    }

    private void OnInsertThinkingMaxModelClick(object sender, RoutedEventArgs e)
    {
        AppendModelFilterToken("claude-opus-4-6(max)");
    }

    private void OnInsertThinkingAutoModelClick(object sender, RoutedEventArgs e)
    {
        AppendModelFilterToken("gpt-5.5(auto)");
    }

    private void OnInsertThinkingNoneModelClick(object sender, RoutedEventArgs e)
    {
        AppendModelFilterToken("gpt-5.5(none)");
    }

    private void OnCopyThinkingSuffixGuideClick(object sender, RoutedEventArgs e)
    {
        DataPackage package = new();
        package.SetText("""
            Thinking 后缀 examples:
            gpt-5.5(8192) -> numeric reasoning budget
            gpt-5.5(high) -> high reasoning effort
            claude-opus-4-6(max) -> Claude adaptive max effort
            gpt-5.5(auto) -> automatic reasoning
            gpt-5.5(none) -> disable reasoning where upstream supports it
            """);
        Clipboard.SetContent(package);
    }

    private void OnRemoveModelMappingClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ModelRewriteRule rule })
        {
            RouteModelMappings.Remove(rule);
        }
    }

    private void AppendModelFilterToken(string token)
    {
        var existing = ModelFilterBox.Text?.Trim();
        ModelFilterBox.Text = string.IsNullOrWhiteSpace(existing)
            ? token
            : $"{existing}, {token}";
        ModelFilterBox.Focus(FocusState.Programmatic);
        ModelFilterBox.SelectionStart = ModelFilterBox.Text.Length;
    }

    private void OnInsertDefaultPayloadRuleClick(object sender, RoutedEventArgs e)
    {
        ApplyPayloadRulesSample("""
            {
              "default": [
                {
                  "protocol": "responses",
                  "models": [ "gpt-*" ],
                  "params": {
                    "temperature": 0.2
                  }
                }
              ]
            }
            """);
    }

    private void OnInsertOverridePayloadRuleClick(object sender, RoutedEventArgs e)
    {
        ApplyPayloadRulesSample("""
            {
              "override": [
                {
                  "protocol": "chat",
                  "models": [ "legacy-*" ],
                  "params": {
                    "max_tokens": 4096
                  }
                }
              ]
            }
            """);
    }

    private void OnInsertFilterPayloadRuleClick(object sender, RoutedEventArgs e)
    {
        ApplyPayloadRulesSample("""
            {
              "filter": [
                {
                  "protocol": "anthropic",
                  "paths": [ "metadata.debug", "extra_headers" ]
                }
              ]
            }
            """);
    }

    private void OnInsertSourceHeaderRawPayloadRuleClick(object sender, RoutedEventArgs e)
    {
        ApplyPayloadRulesSample("""
            {
              "default-raw": [
                {
                  "models": [
                    {
                      "name": "gpt-*",
                      "protocol": "responses",
                      "from-protocol": "openai",
                      "headers": {
                        "X-Client-Tier": "tenant-*-region-*"
                      },
                      "match": [{ "metadata.client": "codex" }],
                      "not-exist": [ "metadata.disable_payload" ]
                    }
                  ],
                  "params": {
                    "text.format": "{\"type\":\"json_object\"}"
                  }
                }
              ]
            }
            """);
    }

    private void ApplyPayloadRulesSample(string sample)
    {
        PayloadRulesBox.Text = sample;
        ValidatePayloadRules();
        PayloadRulesBox.Focus(FocusState.Programmatic);
        PayloadRulesBox.SelectionStart = PayloadRulesBox.Text.Length;
    }

    private void ValidateName()
    {
        var name = NameBox.Text?.Trim() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(name))
        {
            _nameValid = false;
            ShowError(NameBox, NameError, "\u8DEF\u7531\u540D\u79F0\u4E0D\u80FD\u4E3A\u7A7A"); // Route name cannot be empty
        }
        else if (name.Length > 64)
        {
            _nameValid = false;
            ShowError(NameBox, NameError, "\u8DEF\u7531\u540D\u79F0\u4E0D\u80FD\u8D85\u8FC7 64 \u4E2A\u5B57\u7B26"); // Route name cannot exceed 64 characters
        }
        else
        {
            _nameValid = true;
            ClearError(NameBox, NameError);
        }
    }

    private void ValidateUrl()
    {
        var url = UrlBox.Text?.Trim() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(url))
        {
            _urlValid = false;
            ShowError(UrlBox, UrlError, "URL \u4E0D\u80FD\u4E3A\u7A7A"); // URL cannot be empty
        }
        else if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                 (uri.Scheme != "http" && uri.Scheme != "https"))
        {
            _urlValid = false;
            ShowError(UrlBox, UrlError, "URL \u5FC5\u987B\u4EE5 http:// \u6216 https:// \u5F00\u5934"); // URL must start with http:// or https://
        }
        else
        {
            _urlValid = true;
            ClearError(UrlBox, UrlError);
        }
    }

    private void ValidatePriority()
    {
        var value = PriorityBox.Value;

        if (double.IsNaN(value))
        {
            _priorityValid = false;
            ShowError(null, PriorityError, "\u4F18\u5148\u7EA7\u5FC5\u987B\u662F 0\u20130100 \u7684\u6574\u6570"); // Priority must be an integer 0-100
        }
        else if (value < 0 || value > 100 || value != Math.Floor(value))
        {
            _priorityValid = false;
            ShowError(null, PriorityError, "\u4F18\u5148\u7EA7\u5FC5\u987B\u662F 0\u20130100 \u7684\u6574\u6570"); // Priority must be an integer 0-100
        }
        else
        {
            _priorityValid = true;
            ClearError(null, PriorityError);
        }
    }

    private void ValidateOutboundProxy()
    {
        var result = TransparentProxyRouteEditorValidation.ValidateOutboundProxy(OutboundProxyBox.Text);
        _outboundProxyValid = result.IsValid;
        OutboundProxyStatus.Text = result.Message;
        ApplyStatusForeground(OutboundProxyStatus, result.IsConfigured, result.IsValid);
        ApplyValidationBorder(OutboundProxyBox, result.IsValid);
    }

    private void ValidateHeaders()
    {
        var result = TransparentProxyRouteEditorValidation.ValidateHeadersJson(HeadersBox.Text);
        _headersValid = result.IsValid;
        HeadersStatus.Text = result.Message;
        ApplyStatusForeground(HeadersStatus, result.IsConfigured, result.IsValid);
        ApplyValidationBorder(HeadersBox, result.IsValid);
    }

    private void ValidatePayloadRules()
    {
        var preview = TransparentProxyPayloadRulePreview.FromText(PayloadRulesBox.Text);
        _payloadRulesValid = preview.IsValid;
        PayloadRuleSummary.Text = preview.Summary;
        ApplyStatusForeground(PayloadRuleSummary, preview.IsConfigured, preview.IsValid);
        ApplyValidationBorder(PayloadRulesBox, preview.IsValid);
    }

    private void UpdatePrimaryButton()
    {
        IsPrimaryButtonEnabled = _nameValid &&
                                 _urlValid &&
                                 _priorityValid &&
                                 _headersValid &&
                                 _payloadRulesValid &&
                                 _outboundProxyValid;
    }

    private void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        ValidateName();
        ValidateUrl();
        ValidatePriority();
        ValidateOutboundProxy();
        ValidateHeaders();
        ValidatePayloadRules();
        UpdatePrimaryButton();
        if (!IsPrimaryButtonEnabled)
        {
            args.Cancel = true;
            return;
        }

        // Build the result
        var id = ExistingRoute?.Id ?? Guid.NewGuid().ToString();
        var apiKey = string.IsNullOrEmpty(ApiKeyBox.Password) ? null : ApiKeyBox.Password;
        var modelFilterText = BuildModelFilterText();
        var modelFilter = string.IsNullOrWhiteSpace(modelFilterText) ? null : modelFilterText;
        var prefix = string.IsNullOrWhiteSpace(PrefixBox.Text) ? null : PrefixBox.Text.Trim();
        var outboundProxy = string.IsNullOrWhiteSpace(OutboundProxyBox.Text) ? null : OutboundProxyBox.Text.Trim();
        var excludedModels = string.IsNullOrWhiteSpace(ExcludedModelsBox.Text) ? null : ExcludedModelsBox.Text.Trim();
        var preferredWireApi = ReadComboBoxTag(PreferredWireApiBox);
        var authMode = ReadComboBoxTag(AuthModeBox) ?? TransparentProxyRouteAuthModes.ApiKey;
        var isCodexOAuth = string.Equals(authMode, TransparentProxyRouteAuthModes.CodexOAuth, StringComparison.OrdinalIgnoreCase);
        var oauthProvider = isCodexOAuth ? "codex" : null;
        var oauthCredentialId = isCodexOAuth
            ? (string.IsNullOrWhiteSpace(OAuthCredentialIdBox.Text) ? ExistingRoute?.OAuthCredentialId : OAuthCredentialIdBox.Text.Trim())
            : null;
        var codexBackendBaseUrl = isCodexOAuth && !string.IsNullOrWhiteSpace(CodexBackendBaseUrlBox.Text)
            ? CodexBackendBaseUrlBox.Text.Trim()
            : null;
        var codexOAuthFastMode = isCodexOAuth && CodexOAuthFastModeSwitch.IsOn;
        var headersText = string.IsNullOrWhiteSpace(HeadersBox.Text) ? null : HeadersBox.Text.Trim();
        var payloadRules = string.IsNullOrWhiteSpace(PayloadRulesBox.Text) ? null : PayloadRulesBox.Text.Trim();

        Result = new RouteDefinition(
            Id: id,
            Name: NameBox.Text.Trim(),
            UpstreamUrl: UrlBox.Text.Trim(),
            ApiKeyProtected: apiKey,
            Priority: (int)PriorityBox.Value,
            ModelFilter: modelFilter,
            Enabled: ExistingRoute?.Enabled ?? true,
            UpdatedAtUtc: DateTime.UtcNow,
            Prefix: prefix,
            OutboundProxy: outboundProxy,
            RequestRetry: ReadOptionalInt(RequestRetryBox),
            MaxRetryIntervalSeconds: ReadOptionalInt(RetryIntervalBox),
            ModelCooldownSeconds: ReadOptionalInt(ModelCooldownBox),
            ExcludedModelPatterns: excludedModels,
            PayloadRulesText: payloadRules,
            PreferredWireApi: preferredWireApi,
            HeadersText: headersText,
            AuthMode: authMode,
            OAuthProvider: oauthProvider,
            OAuthCredentialId: oauthCredentialId,
            CodexBackendBaseUrl: codexBackendBaseUrl,
            CodexOAuthFastMode: codexOAuthFastMode);
    }

    private void UpdateAuthModeVisibility()
    {
        var isCodexOAuth = IsCodexOAuthSelected();
        CodexOAuthOptionsGrid.Visibility = isCodexOAuth ? Visibility.Visible : Visibility.Collapsed;
        if (isCodexOAuth)
        {
            OAuthProviderBox.Text = "codex";
        }
        else
        {
            CodexOAuthFastModeSwitch.IsOn = false;
        }
    }

    private bool IsCodexOAuthSelected()
        => string.Equals(ReadComboBoxTag(AuthModeBox), TransparentProxyRouteAuthModes.CodexOAuth, StringComparison.OrdinalIgnoreCase);

    private static string? ReadComboBoxTag(ComboBox box)
    {
        if (box.SelectedItem is ComboBoxItem item)
        {
            var tag = item.Tag?.ToString()?.Trim();
            return string.IsNullOrWhiteSpace(tag) ? null : tag;
        }

        return null;
    }

    private static void SelectComboBoxTag(ComboBox box, string? tag)
    {
        var normalized = tag?.Trim() ?? string.Empty;
        foreach (var item in box.Items.OfType<ComboBoxItem>())
        {
            var candidate = item.Tag?.ToString() ?? string.Empty;
            if (string.Equals(candidate, normalized, StringComparison.OrdinalIgnoreCase))
            {
                box.SelectedItem = item;
                return;
            }
        }

        box.SelectedIndex = 0;
    }

    private static int? ReadOptionalInt(NumberBox box)
        => double.IsNaN(box.Value) ? null : (int)Math.Round(box.Value);

    private void ShowError(Control? field, TextBlock errorBlock, string message)
    {
        if (field is not null)
        {
            ApplyValidationBorder(field, isValid: false);
        }
        errorBlock.Text = message;
        errorBlock.Visibility = Visibility.Visible;
    }

    private static void ClearError(Control? field, TextBlock errorBlock)
    {
        if (field is not null)
        {
            field.ClearValue(Control.BorderBrushProperty);
        }
        errorBlock.Text = string.Empty;
        errorBlock.Visibility = Visibility.Collapsed;
    }

    private static void FormatJsonTextBox(TextBox textBox)
    {
        if (string.IsNullOrWhiteSpace(textBox.Text))
        {
            return;
        }

        try
        {
            textBox.Text = TransparentProxyRouteEditorValidation.FormatJson(textBox.Text);
        }
        catch
        {
            // Validation text already explains parse failures; formatting should be a soft action.
        }
    }

    private void LoadRouteModelMappingsFromFilter(string? value)
    {
        RouteModelMappings.Clear();
        foreach (var token in SplitModelTokens(value))
        {
            if (!HasModelMappingSeparator(token))
            {
                continue;
            }

            var (source, target) = ParseModelMappingToken(token);
            if (!string.IsNullOrWhiteSpace(source))
            {
                RouteModelMappings.Add(new ModelRewriteRule(source, target));
            }
        }
    }

    private string BuildModelFilterText()
    {
        var rawTokens = SplitModelTokens(ModelFilterBox.Text)
            .Where(static token => !HasModelMappingSeparator(token))
            .Select(static token => EscapeModelMappingToken(token))
            .Where(static token => !string.IsNullOrWhiteSpace(token));
        var mappingTokens = RouteModelMappings
            .Select(static rule =>
            {
                var source = EscapeModelMappingToken(rule.SourceModel);
                if (string.IsNullOrWhiteSpace(source))
                {
                    return string.Empty;
                }

                var target = EscapeModelMappingToken(rule.TargetModel);
                return string.IsNullOrWhiteSpace(target)
                    ? source
                    : $"{source}=>{target}";
            })
            .Where(static token => !string.IsNullOrWhiteSpace(token));

        return string.Join(", ", rawTokens.Concat(mappingTokens).Distinct(StringComparer.OrdinalIgnoreCase));
    }

    private static IReadOnlyList<string> SplitModelTokens(string? value)
        => (value ?? string.Empty)
            .Split([',', ';', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static token => !string.IsNullOrWhiteSpace(token))
            .ToArray();

    private static bool HasModelMappingSeparator(string value)
        => value.Contains("=>", StringComparison.Ordinal) ||
           value.Contains("->", StringComparison.Ordinal);

    private static (string Source, string Target) ParseModelMappingToken(string token)
    {
        var separator = token.IndexOf("=>", StringComparison.Ordinal);
        if (separator < 0)
        {
            separator = token.IndexOf("->", StringComparison.Ordinal);
        }

        if (separator < 0)
        {
            var normalized = EscapeModelMappingToken(token);
            return (normalized, normalized);
        }

        var source = EscapeModelMappingToken(token[..separator]);
        var target = EscapeModelMappingToken(token[(separator + 2)..]);
        return (source, string.IsNullOrWhiteSpace(target) ? source : target);
    }

    private static string EscapeModelMappingToken(string? value)
        => (value ?? string.Empty)
            .Replace("|", " ", StringComparison.Ordinal)
            .Replace(",", " ", StringComparison.Ordinal)
            .Replace(";", " ", StringComparison.Ordinal)
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Trim();

    private void ApplyValidationBorder(Control field, bool isValid)
    {
        if (isValid)
        {
            field.ClearValue(Control.BorderBrushProperty);
            return;
        }

        field.BorderBrush = ResolveThemeBrush("DialogDangerChipBorderBrush");
    }

    private void ApplyStatusForeground(TextBlock statusBlock, bool isConfigured, bool isValid)
    {
        if (!isConfigured)
        {
            statusBlock.ClearValue(TextBlock.ForegroundProperty);
            return;
        }

        statusBlock.Foreground = ResolveThemeBrush(
            isValid ? "DialogSuccessChipForegroundBrush" : "DialogDangerChipForegroundBrush");
    }

    private Brush ResolveThemeBrush(string resourceKey)
    {
        if (Resources.TryGetValue(resourceKey, out var localResource) &&
            localResource is Brush localBrush)
        {
            return localBrush;
        }

        if (Application.Current.Resources.TryGetValue(resourceKey, out var appResource) &&
            appResource is Brush appBrush)
        {
            return appBrush;
        }

        return (Brush)Application.Current.Resources["DialogAccentChipForegroundBrush"];
    }

    private void FlyoutButton_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (sender is not Button button ||
            e.Key is not (VirtualKey.Enter or VirtualKey.Space or VirtualKey.GamepadA or VirtualKey.Application))
        {
            return;
        }

        button.Flyout?.ShowAt(button);
        e.Handled = true;
    }
}
