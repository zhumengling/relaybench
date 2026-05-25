using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using RelayBench.Core.Models;
using RelayBench.Core.Services;
using RelayBench.WinUI.Services;
using RelayBench.WinUI.Storage;
using Windows.ApplicationModel.DataTransfer;



namespace RelayBench.WinUI.ViewModels;

public sealed partial class ModelChatViewModel : ObservableObject
{
    public void LoadPresets()
    {
        ChatPresets.Clear();
        var presets = _presetStore.LoadAll();
        foreach (var p in presets)
            ChatPresets.Add(p);
    }

    [RelayCommand]
    private void ApplyChatPreset()
        => ApplyPreset(SelectedPreset);

    [RelayCommand]
    private void ApplyPreset(ChatPreset? preset)
    {
        if (preset is null) return;
        SystemPrompt = preset.SystemPrompt;
        SelectedPreset = preset;
        StatusText = $"已应用预设：{preset.Name}";
    }

    [RelayCommand]
    private void SaveChatPreset()
        => SavePreset();

    [RelayCommand]
    private void SavePreset()
    {
        if (string.IsNullOrWhiteSpace(SystemPrompt)) return;

        var name = $"自定义 {DateTime.Now:HH:mm:ss}";
        var preset = new ChatPreset(name, SystemPrompt, false);
        _presetStore.Save(preset);
        ChatPresets.Add(preset);
        StatusText = $"已保存预设：{name}";
    }

    [RelayCommand]
    private void DeleteChatPreset()
        => DeletePreset(SelectedPreset);

    [RelayCommand]
    private void DeletePreset(ChatPreset? preset)
    {
        if (preset is null || preset.IsBuiltIn) return;

        _presetStore.Delete(preset.Name);
        ChatPresets.Remove(preset);
        if (SelectedPreset?.Name == preset.Name)
            SelectedPreset = null;
        StatusText = $"已删除预设：{preset.Name}";
    }

    [RelayCommand]
    private void ResetParameters()
    {
        Temperature = 0.7;
        MaxTokens = 4096;
        SelectedReasoningEffort = "Medium";
        SystemPrompt = "\u4F60\u662F\u4E00\u4E2A\u4E13\u4E1A\u3001\u4E25\u8C28\u3001\u4E50\u4E8E\u52A9\u4EBA\u7684 AI \u52A9\u624B\u3002\u8BF7\u7528\u7B80\u6D01\u3001\u6E05\u6670\u7684\u8BED\u8A00\u56DE\u7B54\u6211\u7684\u95EE\u9898\u3002";
        StatusText = "\u5BF9\u8BDD\u53C2\u6570\u5DF2\u91CD\u7F6E";
    }

    // ─── Phase 10: Attachment Commands ────────────────────────────────────

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".webp", ".gif"
    };

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".json", ".csv", ".log", ".cs", ".xaml", ".xml", ".yaml", ".yml", ".ps1"
    };

}
