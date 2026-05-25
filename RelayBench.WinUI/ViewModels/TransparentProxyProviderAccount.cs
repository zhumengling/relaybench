using Microsoft.UI.Xaml;

namespace RelayBench.WinUI.ViewModels;

public sealed class TransparentProxyProviderAccount
{
    public TransparentProxyProviderAccount(
        string name,
        string provider,
        string endpoint,
        string protocolSummary,
        string modelsText,
        string statusText,
        string bindingText = "",
        string detailText = "",
        string healthText = "",
        bool isOAuthCredential = false,
        string oAuthCredentialId = "")
    {
        Name = name;
        Provider = provider;
        Endpoint = endpoint;
        ProtocolSummary = protocolSummary;
        ModelsText = modelsText;
        StatusText = statusText;
        BindingText = bindingText;
        DetailText = detailText;
        HealthText = healthText;
        IsOAuthCredential = isOAuthCredential;
        OAuthCredentialId = oAuthCredentialId;
    }

    public string Name { get; set; }

    public string Provider { get; set; }

    public string Endpoint { get; set; }

    public string ProtocolSummary { get; set; }

    public string ModelsText { get; set; }

    public string StatusText { get; set; }

    public string BindingText { get; set; }

    public string DetailText { get; set; }

    public string HealthText { get; set; }

    public bool IsOAuthCredential { get; set; }

    public string OAuthCredentialId { get; set; }

    public Visibility OAuthActionsVisibility => IsOAuthCredential ? Visibility.Visible : Visibility.Collapsed;

    public string BadgeText => string.IsNullOrWhiteSpace(StatusText) ? "--" : StatusText.Trim();

    public string BindingDisplay => string.IsNullOrWhiteSpace(BindingText) ? "--" : BindingText.Trim();

    public string DetailDisplay => string.IsNullOrWhiteSpace(DetailText) ? Endpoint : DetailText.Trim();

    public string HealthDisplay => string.IsNullOrWhiteSpace(HealthText) ? ProtocolSummary : HealthText.Trim();

    public string TooltipText =>
        $"{Provider}\n" +
        $"{Name}\n" +
        $"{Endpoint}\n" +
        $"Protocol: {ProtocolSummary}\n" +
        $"Models: {ModelsText}\n" +
        $"Binding: {BindingDisplay}\n" +
        $"Status: {BadgeText}\n" +
        $"{DetailDisplay}";
}
