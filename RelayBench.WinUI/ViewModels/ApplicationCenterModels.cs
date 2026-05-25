using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using RelayBench.Core.Models;
using RelayBench.Core.Services;
using RelayBench.Services;
using RelayBench.WinUI.Services;
using RelayBench.WinUI.Storage;

namespace RelayBench.WinUI.ViewModels;

public sealed partial class AppTargetItem : ObservableObject
{
    private readonly Action? _selectionChanged;

    [ObservableProperty] public partial bool IsSelected { get; set; }

    public AppTargetItem(
        string targetId,
        string name,
        string description,
        bool installed,
        ClientApplyProtocolKind protocolKind,
        bool isSelectable,
        bool isSelected,
        string protocol,
        string configFile,
        string currentConfig,
        string endpoint,
        string iconGlyph,
        string iconTone,
        string? disabledReason,
        CodexConfigTemplate? codexConfigTemplate,
        Action? selectionChanged = null)
    {
        _selectionChanged = selectionChanged;
        TargetId = targetId;
        Name = name;
        Description = description;
        Installed = installed;
        ProtocolKind = protocolKind;
        IsSelectable = isSelectable;
        IsSelected = isSelected;
        Protocol = protocol;
        ConfigFile = configFile;
        CurrentConfig = currentConfig;
        Endpoint = endpoint;
        IconGlyph = iconGlyph;
        IconTone = iconTone;
        DisabledReason = disabledReason;
        CodexConfigTemplate = codexConfigTemplate;
    }

    partial void OnIsSelectedChanged(bool value) => _selectionChanged?.Invoke();

    public string TargetId { get; }
    public string Name { get; }
    public string Description { get; }
    public bool Installed { get; }
    public ClientApplyProtocolKind ProtocolKind { get; }
    public bool IsSelectable { get; }
    public string Protocol { get; }
    public string ConfigFile { get; }
    public string CurrentConfig { get; }
    public string Endpoint { get; }
    public string IconGlyph { get; }
    public string IconTone { get; }
    public string? DisabledReason { get; }
    public CodexConfigTemplate? CodexConfigTemplate { get; }
    public Visibility IconAccentToneVisibility => ApplicationAccessToneVisibility.Accent(IconTone);
    public Visibility IconHealthyToneVisibility => ApplicationAccessToneVisibility.Healthy(IconTone);
    public Visibility IconWarningToneVisibility => ApplicationAccessToneVisibility.Warning(IconTone);
    public Visibility IconDangerToneVisibility => ApplicationAccessToneVisibility.Danger(IconTone);
    public string InstalledText => Installed ? "\u5df2\u68c0\u6d4b" : "\u672a\u68c0\u6d4b";
    public string SelectabilityText => IsSelectable ? "\u53ef\u5199\u5165" : DisabledReason ?? "\u5f85\u63a2\u6d4b";
    public string DialogDetailText => IsSelectable
        ? ConfigFile
        : string.IsNullOrWhiteSpace(DisabledReason) ? ConfigFile : DisabledReason;

    public static AppTargetItem FromClientTarget(
        ClientApplyTarget target,
        TransparentProxyDetectedApp? detected,
        string baseUrl,
        bool protocolKnown,
        bool keepSelected,
        CodexConfigTemplate? codexConfigTemplateOverride = null,
        Action? selectionChanged = null)
    {
        var protocol = target.Id switch
        {
            "vscode-codex" => "OpenAI / Anthropic environment variables",
            _ => target.Protocol switch
            {
                ClientApplyProtocolKind.Responses => protocolKnown ? "Responses" : "\u5f85\u63a2\u6d4b",
                ClientApplyProtocolKind.Anthropic => protocolKnown ? "Anthropic" : "\u5f85\u63a2\u6d4b",
                ClientApplyProtocolKind.OpenAiCompatible => protocolKnown ? "OpenAI Chat" : "\u5f85\u63a2\u6d4b",
                ClientApplyProtocolKind.Gemini => "Gemini env",
                _ => "\u5f85\u63a2\u6d4b"
            }
        };
        var description = target.Id switch
        {
            "codex" => "Command line / desktop AI assistant",
            "claude-cli" => "Anthropic command line tool",
            "antigravity" => "Google Gemini desktop client",
            "vscode-codex" => "Editor terminal environment",
            _ => "Client"
        };
        var icon = target.Id switch
        {
            "claude-cli" => "\uE8D4",
            "antigravity" => "\uE8A7",
            "vscode-codex" => "\uE8A7",
            _ => "\uE74C"
        };
        var iconTone = target.Id switch
        {
            "claude-cli" => ApplicationAccessTones.Warning,
            "antigravity" => ApplicationAccessTones.Healthy,
            "vscode-codex" => ApplicationAccessTones.Accent,
            _ => ApplicationAccessTones.Accent
        };
        var configPath = detected?.ConfigPath ?? target.ConfigSummary;
        var installed = detected?.IsDetected ?? target.IsInstalled;
        var selected = keepSelected ? target.IsSelectable : target.IsDefaultSelected;

        return new AppTargetItem(
            target.Id,
            target.DisplayName,
            description,
            installed,
            target.Protocol,
            target.IsSelectable,
            selected,
            protocol,
            configPath,
            target.ConfigSummary,
            string.IsNullOrWhiteSpace(baseUrl) ? "--" : baseUrl.Trim(),
            icon,
            iconTone,
            target.DisabledReason,
            codexConfigTemplateOverride ?? target.CodexConfigTemplate,
            selectionChanged);
    }

}

public sealed record ProtocolProbeItem(string Protocol, string Request, string Result, string Latency, string ResultTone)
{
    public Visibility ResultAccentToneVisibility => ApplicationAccessToneVisibility.Accent(ResultTone);
    public Visibility ResultHealthyToneVisibility => ApplicationAccessToneVisibility.Healthy(ResultTone);
    public Visibility ResultWarningToneVisibility => ApplicationAccessToneVisibility.Warning(ResultTone);
    public Visibility ResultDangerToneVisibility => ApplicationAccessToneVisibility.Danger(ResultTone);

}

public sealed record WriteTraceItem(string Step, string Title, string Detail, string State, string Tone)
{
    public Visibility AccentToneVisibility => ApplicationAccessToneVisibility.Accent(Tone);
    public Visibility HealthyToneVisibility => ApplicationAccessToneVisibility.Healthy(Tone);
    public Visibility WarningToneVisibility => ApplicationAccessToneVisibility.Warning(Tone);
    public Visibility DangerToneVisibility => ApplicationAccessToneVisibility.Danger(Tone);

}

public sealed record ConfigTemplateRow(string Parameter, string Value, string Description);

internal static class ApplicationAccessTones
{
    public const string Accent = "Accent";
    public const string Healthy = "Healthy";
    public const string Warning = "Warning";
    public const string Danger = "Danger";
}

internal static class ApplicationAccessToneVisibility
{
    public static Visibility Accent(string? tone) => VisibleWhen(Resolve(tone) == ApplicationAccessToneKind.Accent);
    public static Visibility Healthy(string? tone) => VisibleWhen(Resolve(tone) == ApplicationAccessToneKind.Healthy);
    public static Visibility Warning(string? tone) => VisibleWhen(Resolve(tone) == ApplicationAccessToneKind.Warning);
    public static Visibility Danger(string? tone) => VisibleWhen(Resolve(tone) == ApplicationAccessToneKind.Danger);

    private static Visibility VisibleWhen(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;

    private static ApplicationAccessToneKind Resolve(string? tone)
    {
        return Normalize(tone) switch
        {
            "HEALTHY" => ApplicationAccessToneKind.Healthy,
            "WARNING" => ApplicationAccessToneKind.Warning,
            "DANGER" => ApplicationAccessToneKind.Danger,
            "ACCENT" => ApplicationAccessToneKind.Accent,
            _ => ApplicationAccessToneKind.Accent
        };
    }

    private static string Normalize(string? tone)
    {
        if (string.IsNullOrWhiteSpace(tone))
        {
            return string.Empty;
        }

        return tone.Trim().ToUpperInvariant();
    }
}

internal enum ApplicationAccessToneKind
{
    Accent,
    Healthy,
    Warning,
    Danger
}
