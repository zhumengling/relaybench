using RelayBench.Core.Models;

namespace RelayBench.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private Task FetchDefaultProxyModelsAsync()
        => FetchProxyModelsForTargetAsync(ProxyModelPickerTarget.DefaultModel);

    private Task FetchProxyBatchSharedModelsAsync()
        => FetchProxyModelsForTargetAsync(ProxyModelPickerTarget.BatchSharedModel);

    private Task FetchProxyBatchEntryModelsAsync()
        => FetchProxyModelsForTargetAsync(ProxyModelPickerTarget.BatchEntryModel);

    private Task FetchProxyCapabilityModelsAsync(string? capabilityKey)
    {
        if (!TryParseCapabilityModelPickerTarget(capabilityKey, out var target))
        {
            StatusMessage = "\u672A\u8BC6\u522B\u8981\u56DE\u586B\u7684\u80FD\u529B\u6A21\u578B\u9879\u3002";
            return Task.CompletedTask;
        }

        return FetchProxyModelsForTargetAsync(target);
    }

    private Task RunProxyWithValidationAsync()
        => EnsureDefaultProxyModelSelected(requireChatCapable: true)
            ? RunBasicProxyAsync()
            : Task.CompletedTask;

    private Task RunProxyDeepWithValidationAsync()
        => EnsureDefaultProxyModelSelected()
            ? RunDeepProxyAsync()
            : Task.CompletedTask;

    private Task RunProxySeriesWithValidationAsync()
        => EnsureDefaultProxyModelSelected()
            ? RunProxySeriesAsync()
            : Task.CompletedTask;

    private Task RunProxyConcurrencyWithValidationAsync()
        => EnsureDefaultProxyModelSelected()
            ? RunProxyConcurrencyPressureAsync()
            : Task.CompletedTask;

    private Task RunProxyBatchWithValidationAsync()
    {
        if (!TryEnsureProxyBatchModelsReady())
        {
            return Task.CompletedTask;
        }

        return RunProxyBatchAsync();
    }

    private Task FetchProxyModelsForTargetAsync(ProxyModelPickerTarget target)
    {
        SetProxyModelPickerTarget(target);
        if (!TryBuildProxyModelCatalogSettings(target, out var settings, out var message))
        {
            StatusMessage = message;
            return Task.CompletedTask;
        }

        return ExecuteBusyActionAsync(
            $"正在拉取{GetProxyModelPickerFetchDisplayName(target)}可用模型...",
            () => FetchProxyModelsCoreAsync(settings));
    }

    private async Task FetchProxyModelsCoreAsync(ProxyEndpointSettings settings)
    {
        var result = await _proxyDiagnosticsService.FetchModelsAsync(settings);
        ApplyProxyModelCatalogResult(result);
        await CacheProxyModelCatalogResultAsync(settings, result);
        IsProxyModelPickerOpen = true;
        IsProxyMultiModelPickerOpen = false;
        DashboardCards[3].Status = result.Success ? "模型已拉取" : "模型拉取失败";
        DashboardCards[3].Detail = result.Summary;
        StatusMessage = result.Summary;
        AppendHistory("接口", "拉取模型列表", ProxyModelCatalogSummary);
    }

    private bool EnsureDefaultProxyModelSelected(bool requireChatCapable = false)
    {
        if (!string.IsNullOrWhiteSpace(ProxyModel))
        {
            if (!requireChatCapable || !TryDescribeLikelyNonChatModel(ProxyModel, out var capabilityLabel))
            {
                return true;
            }

            StatusMessage =
                $"\u5F53\u524D\u6A21\u578B\u201C{ProxyModel.Trim()}\u201D\u7591\u4F3C\u5C5E\u4E8E {capabilityLabel}\u3002" +
                "\u5FEB\u901F\u6D4B\u8BD5\u53EA\u9002\u5408\u804A\u5929\u6A21\u578B\uFF0C" +
                "\u8BF7\u6539\u9009\u804A\u5929\u6A21\u578B\uFF0C\u6216\u76F4\u63A5\u4F7F\u7528\u6DF1\u5EA6\u6D4B\u8BD5\u91CC\u7684\u975E\u804A\u5929 API \u80FD\u529B\u77E9\u9635\u3002";
            return false;
        }

        SetProxyModelPickerTarget(ProxyModelPickerTarget.DefaultModel);
        if (ProxyCatalogModels.Count > 0)
        {
            IsProxyModelPickerOpen = true;
            StatusMessage = "请先选择模型后再运行接口诊断，已为你打开模型选择弹窗。";
        }
        else
        {
            StatusMessage = "请先点击“拉取并选择模型”，选中一个模型后再运行接口诊断。";
        }

        return false;
    }

    private bool TryEnsureProxyBatchModelsReady()
    {
        try
        {
            BuildProxyBatchPlan(requireRunnable: true);
            return true;
        }
        catch (InvalidOperationException ex)
        {
            StatusMessage = ex.Message;
            return false;
        }
    }

    private bool TryBuildProxyModelCatalogSettings(
        ProxyModelPickerTarget target,
        out ProxyEndpointSettings settings,
        out string message)
    {
        var context = ResolveProxyModelPickerContext(target);
        var sourceName = GetProxyModelPickerSourceDisplayName(target);

        if (string.IsNullOrWhiteSpace(context.BaseUrl))
        {
            settings = default!;
            message = $"请先填写{sourceName}的网址，再拉取模型列表。";
            return false;
        }

        if (string.IsNullOrWhiteSpace(context.ApiKey))
        {
            settings = default!;
            message = $"请先填写{sourceName}的 API Key，再拉取模型列表。";
            return false;
        }

        settings = new ProxyEndpointSettings(
            context.BaseUrl,
            context.ApiKey,
            context.Model,
            ProxyIgnoreTlsErrors,
            ParseBoundedInt(ProxyTimeoutSecondsText, fallback: 20, min: 5, max: 120));
        message = string.Empty;
        return true;
    }

    private ProxyModelPickerContext ResolveProxyModelPickerContext(ProxyModelPickerTarget target)
    {
        var siteGroupReference = ResolveExistingSiteGroupReference(
            NormalizeNullable(ProxyBatchFormSiteGroupName),
            SelectedProxyBatchEditorItem);
        var templateRow = _proxyBatchTemplateModelTargetRow;

        return target switch
        {
            ProxyModelPickerTarget.DefaultModel => new ProxyModelPickerContext(
                NormalizeNullable(ProxyBaseUrl) ?? string.Empty,
                NormalizeNullable(ProxyApiKey) ?? string.Empty,
                NormalizeNullable(ProxyModel) ?? string.Empty),
            ProxyModelPickerTarget.ApplicationCenterModel => new ProxyModelPickerContext(
                NormalizeNullable(ApplicationCenterBaseUrl) ?? string.Empty,
                NormalizeNullable(ApplicationCenterApiKey) ?? string.Empty,
                NormalizeNullable(ApplicationCenterModel) ?? string.Empty),
            ProxyModelPickerTarget.CapabilityEmbeddingsModel => new ProxyModelPickerContext(
                NormalizeNullable(ProxyBaseUrl) ?? string.Empty,
                NormalizeNullable(ProxyApiKey) ?? string.Empty,
                NormalizeNullable(ProxyEmbeddingsModel) ?? string.Empty),
            ProxyModelPickerTarget.CapabilityImagesModel => new ProxyModelPickerContext(
                NormalizeNullable(ProxyBaseUrl) ?? string.Empty,
                NormalizeNullable(ProxyApiKey) ?? string.Empty,
                NormalizeNullable(ProxyImagesModel) ?? string.Empty),
            ProxyModelPickerTarget.CapabilityAudioTranscriptionModel => new ProxyModelPickerContext(
                NormalizeNullable(ProxyBaseUrl) ?? string.Empty,
                NormalizeNullable(ProxyApiKey) ?? string.Empty,
                NormalizeNullable(ProxyAudioTranscriptionModel) ?? string.Empty),
            ProxyModelPickerTarget.CapabilityAudioSpeechModel => new ProxyModelPickerContext(
                NormalizeNullable(ProxyBaseUrl) ?? string.Empty,
                NormalizeNullable(ProxyApiKey) ?? string.Empty,
                NormalizeNullable(ProxyAudioSpeechModel) ?? string.Empty),
            ProxyModelPickerTarget.CapabilityModerationModel => new ProxyModelPickerContext(
                NormalizeNullable(ProxyBaseUrl) ?? string.Empty,
                NormalizeNullable(ProxyApiKey) ?? string.Empty,
                NormalizeNullable(ProxyModerationModel) ?? string.Empty),
            ProxyModelPickerTarget.BatchSharedModel => new ProxyModelPickerContext(
                FirstNonEmpty(
                    NormalizeNullable(ProxyBatchFormBaseUrl),
                    NormalizeNullable(siteGroupReference?.BaseUrl),
                    NormalizeNullable(ProxyBaseUrl)) ?? string.Empty,
                FirstNonEmpty(
                    NormalizeNullable(ProxyBatchFormSiteGroupApiKey),
                    NormalizeNullable(siteGroupReference?.SiteGroupApiKey),
                    NormalizeNullable(ProxyApiKey)) ?? string.Empty,
                NormalizeNullable(ProxyBatchFormSiteGroupModel) ?? string.Empty),
            ProxyModelPickerTarget.BatchEntryModel => new ProxyModelPickerContext(
                FirstNonEmpty(
                    NormalizeNullable(ProxyBatchFormBaseUrl),
                    NormalizeNullable(ProxyBaseUrl)) ?? string.Empty,
                FirstNonEmpty(
                    NormalizeNullable(ProxyBatchFormApiKey),
                    NormalizeNullable(ProxyBatchFormSiteGroupApiKey),
                    NormalizeNullable(siteGroupReference?.SiteGroupApiKey),
                    NormalizeNullable(ProxyApiKey)) ?? string.Empty,
                NormalizeNullable(ProxyBatchFormModel) ?? string.Empty),
            ProxyModelPickerTarget.BatchTemplateRowModel => new ProxyModelPickerContext(
                NormalizeNullable(templateRow?.BaseUrl) ?? string.Empty,
                ResolveProxyBatchTemplateRowApiKey(templateRow) ?? string.Empty,
                NormalizeNullable(templateRow?.EntryModel) ?? string.Empty),
            _ => new ProxyModelPickerContext(string.Empty, string.Empty, string.Empty)
        };
    }

    private void SetProxyModelPickerTarget(ProxyModelPickerTarget target)
    {
        if (_proxyModelPickerTarget == target)
        {
            SyncSelectedProxyCatalogModel(GetCurrentProxyModelPickerValue());
            return;
        }

        _proxyModelPickerTarget = target;
        OnPropertyChanged(nameof(ProxyModelPickerTargetSummary));
        OnPropertyChanged(nameof(ProxyModelPickerInstruction));
        SyncSelectedProxyCatalogModel(GetCurrentProxyModelPickerValue());
    }

    private string GetCurrentProxyModelPickerValue()
        => _proxyModelPickerTarget switch
        {
            ProxyModelPickerTarget.DefaultModel => ProxyModel,
            ProxyModelPickerTarget.ApplicationCenterModel => ApplicationCenterModel,
            ProxyModelPickerTarget.CapabilityEmbeddingsModel => ProxyEmbeddingsModel,
            ProxyModelPickerTarget.CapabilityImagesModel => ProxyImagesModel,
            ProxyModelPickerTarget.CapabilityAudioTranscriptionModel => ProxyAudioTranscriptionModel,
            ProxyModelPickerTarget.CapabilityAudioSpeechModel => ProxyAudioSpeechModel,
            ProxyModelPickerTarget.CapabilityModerationModel => ProxyModerationModel,
            ProxyModelPickerTarget.BatchSharedModel => ProxyBatchFormSiteGroupModel,
            ProxyModelPickerTarget.BatchEntryModel => ProxyBatchFormModel,
            ProxyModelPickerTarget.BatchTemplateRowModel => _proxyBatchTemplateModelTargetRow?.EntryModel ?? string.Empty,
            _ => string.Empty
        };

    private void ApplyProxyCatalogSelection(string model)
    {
        var normalizedModel = model.Trim();

        switch (_proxyModelPickerTarget)
        {
            case ProxyModelPickerTarget.CapabilityEmbeddingsModel:
                ProxyEmbeddingsModel = normalizedModel;
                StatusMessage = $"\u5DF2\u56DE\u586B Embeddings \u6A21\u578B\uFF1A{normalizedModel}";
                SaveState();
                break;
            case ProxyModelPickerTarget.CapabilityImagesModel:
                ProxyImagesModel = normalizedModel;
                StatusMessage = $"\u5DF2\u56DE\u586B Images \u6A21\u578B\uFF1A{normalizedModel}";
                SaveState();
                break;
            case ProxyModelPickerTarget.CapabilityAudioTranscriptionModel:
                ProxyAudioTranscriptionModel = normalizedModel;
                StatusMessage = $"\u5DF2\u56DE\u586B Audio Transcription \u6A21\u578B\uFF1A{normalizedModel}";
                SaveState();
                break;
            case ProxyModelPickerTarget.CapabilityAudioSpeechModel:
                ProxyAudioSpeechModel = normalizedModel;
                StatusMessage = $"\u5DF2\u56DE\u586B Audio Speech / TTS \u6A21\u578B\uFF1A{normalizedModel}";
                SaveState();
                break;
            case ProxyModelPickerTarget.CapabilityModerationModel:
                ProxyModerationModel = normalizedModel;
                StatusMessage = $"\u5DF2\u56DE\u586B Moderation \u6A21\u578B\uFF1A{normalizedModel}";
                SaveState();
                break;
            case ProxyModelPickerTarget.BatchSharedModel:
                ProxyBatchFormSiteGroupModel = normalizedModel;
                StatusMessage = $"已回填同站共用模型：{normalizedModel}";
                break;
            case ProxyModelPickerTarget.BatchEntryModel:
                ProxyBatchFormModel = normalizedModel;
                StatusMessage = $"已回填本条目模型：{normalizedModel}";
                break;
            case ProxyModelPickerTarget.BatchTemplateRowModel:
                if (_proxyBatchTemplateModelTargetRow is not null)
                {
                    _proxyBatchTemplateModelTargetRow.EntryModel = normalizedModel;
                    StatusMessage = $"已回填模板行模型：{normalizedModel}";
                }
                else
                {
                    StatusMessage = $"已选择模型：{normalizedModel}";
                }

                SaveState();
                break;
            case ProxyModelPickerTarget.ApplicationCenterModel:
                ApplicationCenterModel = normalizedModel;
                StatusMessage = $"已回填应用接入模型：{normalizedModel}";
                SaveState();
                break;
            default:
                ProxyModel = normalizedModel;
                StatusMessage = $"已选择默认模型：{normalizedModel}";
                SaveState();
                break;
        }
    }

    private string GetProxyModelPickerTargetDisplayName()
        => GetProxyModelPickerTargetDisplayName(_proxyModelPickerTarget);

    private static string GetProxyModelPickerTargetDisplayName(ProxyModelPickerTarget target)
        => target switch
        {
            ProxyModelPickerTarget.CapabilityEmbeddingsModel => "\u975E\u804A\u5929 API / Embeddings \u6A21\u578B",
            ProxyModelPickerTarget.CapabilityImagesModel => "\u975E\u804A\u5929 API / Images \u6A21\u578B",
            ProxyModelPickerTarget.CapabilityAudioTranscriptionModel => "\u975E\u804A\u5929 API / Audio Transcription \u6A21\u578B",
            ProxyModelPickerTarget.CapabilityAudioSpeechModel => "\u975E\u804A\u5929 API / Audio Speech \u6A21\u578B",
            ProxyModelPickerTarget.CapabilityModerationModel => "\u975E\u804A\u5929 API / Moderation \u6A21\u578B",
            ProxyModelPickerTarget.ApplicationCenterModel => "应用接入当前模型",
            ProxyModelPickerTarget.BatchSharedModel => "入口组的同站共用模型",
            ProxyModelPickerTarget.BatchEntryModel => "入口组的本条目模型",
            ProxyModelPickerTarget.BatchTemplateRowModel => "模板表当前行模型",
            _ => "主页默认模型"
        };

    private static string GetProxyModelPickerFetchDisplayName(ProxyModelPickerTarget target)
        => target switch
        {
            ProxyModelPickerTarget.CapabilityEmbeddingsModel => "\u4E0A\u65B9\u63A5\u53E3\u7684 Embeddings",
            ProxyModelPickerTarget.CapabilityImagesModel => "\u4E0A\u65B9\u63A5\u53E3\u7684 Images",
            ProxyModelPickerTarget.CapabilityAudioTranscriptionModel => "\u4E0A\u65B9\u63A5\u53E3\u7684 Audio Transcription",
            ProxyModelPickerTarget.CapabilityAudioSpeechModel => "\u4E0A\u65B9\u63A5\u53E3\u7684 Audio Speech / TTS",
            ProxyModelPickerTarget.CapabilityModerationModel => "\u4E0A\u65B9\u63A5\u53E3\u7684 Moderation",
            ProxyModelPickerTarget.ApplicationCenterModel => "应用接入接口",
            ProxyModelPickerTarget.BatchSharedModel => "\u5F53\u524D\u7AD9\u70B9",
            ProxyModelPickerTarget.BatchEntryModel => "\u5F53\u524D\u6761\u76EE",
            ProxyModelPickerTarget.BatchTemplateRowModel => "\u6A21\u677F\u884C",
            _ => "\u4E0A\u65B9\u63A5\u53E3"
        };

    private static string GetProxyModelPickerSourceDisplayName(ProxyModelPickerTarget target)
        => target switch
        {
            ProxyModelPickerTarget.BatchSharedModel => "\u5F53\u524D\u7AD9\u70B9",
            ProxyModelPickerTarget.BatchEntryModel => "\u5F53\u524D\u6761\u76EE",
            ProxyModelPickerTarget.BatchTemplateRowModel => "\u5F53\u524D\u6A21\u677F\u884C",
            ProxyModelPickerTarget.ApplicationCenterModel => "应用接入当前接口",
            _ => "\u4E0A\u65B9\u63A5\u53E3"
        };

    private static bool TryParseCapabilityModelPickerTarget(string? capabilityKey, out ProxyModelPickerTarget target)
    {
        target = capabilityKey?.Trim().ToLowerInvariant() switch
        {
            "embeddings" => ProxyModelPickerTarget.CapabilityEmbeddingsModel,
            "images" => ProxyModelPickerTarget.CapabilityImagesModel,
            "audio-transcription" => ProxyModelPickerTarget.CapabilityAudioTranscriptionModel,
            "audio-speech" => ProxyModelPickerTarget.CapabilityAudioSpeechModel,
            "moderation" => ProxyModelPickerTarget.CapabilityModerationModel,
            _ => default
        };

        return capabilityKey is not null &&
               (target == ProxyModelPickerTarget.CapabilityEmbeddingsModel ||
                target == ProxyModelPickerTarget.CapabilityImagesModel ||
                target == ProxyModelPickerTarget.CapabilityAudioTranscriptionModel ||
                target == ProxyModelPickerTarget.CapabilityAudioSpeechModel ||
                target == ProxyModelPickerTarget.CapabilityModerationModel);
    }

    private static bool TryDescribeLikelyNonChatModel(string? model, out string capabilityLabel)
    {
        capabilityLabel = string.Empty;
        var normalized = model?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return false;
        }

        if (normalized.Contains("embedding", StringComparison.Ordinal))
        {
            capabilityLabel = "Embeddings";
            return true;
        }

        if (normalized.Contains("rerank", StringComparison.Ordinal))
        {
            capabilityLabel = "Rerank";
            return true;
        }

        if (normalized.Contains("moderation", StringComparison.Ordinal))
        {
            capabilityLabel = "Moderation";
            return true;
        }

        if (normalized.Contains("whisper", StringComparison.Ordinal) ||
            normalized.Contains("transcribe", StringComparison.Ordinal) ||
            normalized.Contains("transcription", StringComparison.Ordinal))
        {
            capabilityLabel = "Audio Transcription";
            return true;
        }

        if (normalized.StartsWith("tts-", StringComparison.Ordinal) ||
            normalized.Contains("-tts", StringComparison.Ordinal) ||
            normalized.Contains("text-to-speech", StringComparison.Ordinal))
        {
            capabilityLabel = "Audio Speech / TTS";
            return true;
        }

        if (normalized.Contains("dall-e", StringComparison.Ordinal) ||
            normalized.Contains("gpt-image", StringComparison.Ordinal) ||
            normalized.Contains("stable-diffusion", StringComparison.Ordinal) ||
            normalized.Contains("sdxl", StringComparison.Ordinal) ||
            normalized.Contains("imagen", StringComparison.Ordinal) ||
            normalized.Contains("flux", StringComparison.Ordinal))
        {
            capabilityLabel = "Images";
            return true;
        }

        return false;
    }

    private sealed record ProxyModelPickerContext(
        string BaseUrl,
        string ApiKey,
        string Model);

    private enum ProxyModelPickerTarget
    {
        DefaultModel,
        ApplicationCenterModel,
        CapabilityEmbeddingsModel,
        CapabilityImagesModel,
        CapabilityAudioTranscriptionModel,
        CapabilityAudioSpeechModel,
        CapabilityModerationModel,
        BatchSharedModel,
        BatchEntryModel,
        BatchTemplateRowModel
    }
}
