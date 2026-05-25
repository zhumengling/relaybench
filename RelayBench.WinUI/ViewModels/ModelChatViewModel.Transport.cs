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
    private List<ChatAttachment> BuildAttachments()
    {
        var result = new List<ChatAttachment>();
        foreach (var item in PendingAttachments)
        {
            try
            {
                result.Add(BuildCoreAttachment(item));
            }
            catch
            {
                // Skip files that can't be read
            }
        }
        return result;
    }

    private static ChatAttachment BuildCoreAttachment(ChatAttachmentItem item)
    {
        if (item.IsImage)
        {
            var mediaType = NormalizeMediaType(item.MediaType, item.FilePath, item.IsImage);
            var dataUrl = item.CachedContent;
            if (string.IsNullOrWhiteSpace(dataUrl) && File.Exists(item.FilePath))
            {
                var bytes = File.ReadAllBytes(item.FilePath);
                dataUrl = $"data:{mediaType};base64,{Convert.ToBase64String(bytes)}";
            }

            return new ChatAttachment(
                item.Id,
                ChatAttachmentKind.Image,
                item.FileName,
                mediaType,
                item.SizeBytes,
                dataUrl ?? string.Empty);
        }

        var text = item.CachedContent;
        if (string.IsNullOrWhiteSpace(text) && File.Exists(item.FilePath))
        {
            text = File.ReadAllText(item.FilePath, System.Text.Encoding.UTF8);
        }

        return new ChatAttachment(
            item.Id,
            ChatAttachmentKind.TextFile,
            item.FileName,
            NormalizeMediaType(item.MediaType, item.FilePath, item.IsImage),
            item.SizeBytes,
            text ?? string.Empty);
    }

    private static string NormalizeMediaType(string? mediaType, string filePath, bool isImage)
    {
        if (!string.IsNullOrWhiteSpace(mediaType))
        {
            return mediaType.Trim();
        }

        if (!string.IsNullOrWhiteSpace(filePath))
        {
            var ext = Path.GetExtension(filePath).ToLowerInvariant();
            return ext switch
            {
                ".png" => "image/png",
                ".jpg" or ".jpeg" => "image/jpeg",
                ".webp" => "image/webp",
                ".gif" => "image/gif",
                _ when isImage => "application/octet-stream",
                _ => "text/plain"
            };
        }

        return isImage ? "application/octet-stream" : "text/plain";
    }

    private static List<ChatMessage> BuildChatHistory(IEnumerable<ChatMessageItem> messages)
        => messages
            .Select(static message => new ChatMessage(
                Guid.NewGuid().ToString("N"),
                message.IsUser ? "user" : "assistant",
                message.Content,
                DateTimeOffset.UtcNow,
                message.Attachments,
                null,
                null))
            .ToList();

    private async Task<ChatRequestOptions> BuildChatRequestOptionsAsync(
        string? modelName,
        CancellationToken cancellationToken,
        bool updateEndpointSummary)
    {
        var options = BuildChatRequestOptions(modelName);
        if (string.IsNullOrWhiteSpace(options.BaseUrl) ||
            string.IsNullOrWhiteSpace(options.ApiKey) ||
            string.IsNullOrWhiteSpace(options.Model))
        {
            return options;
        }

        try
        {
            var settings = new ProxyEndpointSettings(
                options.BaseUrl,
                options.ApiKey,
                options.Model,
                options.IgnoreTlsErrors,
                options.TimeoutSeconds);
            var resolution = await _protocolProbeService.ResolveAsync(
                settings,
                new ProxyEndpointProtocolProbeOptions(
                    ForceProbe: false,
                    UseCache: true,
                    SaveResult: true),
                cancellationToken);

            if (updateEndpointSummary)
            {
                ApplyProtocolProbeResult(resolution.Result, resolution.FromCache);
            }

            return string.IsNullOrWhiteSpace(resolution.Result.PreferredWireApi)
                ? options
                : options with { PreferredWireApi = resolution.Result.PreferredWireApi };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            if (updateEndpointSummary)
            {
                EndpointName = string.IsNullOrWhiteSpace(options.Model) ? "--" : options.Model;
                EndpointAddress = options.BaseUrl;
                EndpointProtocol = "--";
                EndpointHealth = "协议复核失败，已回退自动尝试";
                RouteSummary = "自动协议尝试";
                EndpointTimeout = $"{options.TimeoutSeconds}s";
            }

            return options;
        }
    }

    private ChatRequestOptions BuildChatRequestOptions(string? modelName = null)
    {
        var reasoningEffort = SelectedReasoningEffort switch
        {
            "Low" => ChatReasoningEffort.Low,
            "Medium" => ChatReasoningEffort.Medium,
            "High" => ChatReasoningEffort.High,
            _ => ChatReasoningEffort.Auto
        };

        return new ChatRequestOptions(
            BaseUrl.Trim(),
            ApiKey.Trim(),
            string.IsNullOrWhiteSpace(modelName) ? Model.Trim() : modelName.Trim(),
            SystemPrompt,
            Temperature,
            MaxTokens,
            IgnoreTlsErrors: false,
            TimeoutSeconds: 60,
            ReasoningEffort: reasoningEffort,
            PreferResponsesApi: false)
        {
            JsonMode = this.JsonMode
        };
    }

    private void ApplyProtocolProbeResult(ProxyEndpointProtocolProbeResult result, bool fromCache)
    {
        _lastProtocolProbeResult = result;
        EndpointName = string.IsNullOrWhiteSpace(result.ProbeModel) ? Model.Trim() : result.ProbeModel.Trim();
        EndpointAddress = string.IsNullOrWhiteSpace(result.BaseUrl) ? BaseUrl.Trim() : result.BaseUrl.Trim();
        EndpointProtocol = FormatWireApiDisplay(result.PreferredWireApi);
        EndpointTimeout = "60s";
        EndpointHealth = fromCache
            ? "已复用协议缓存"
            : "已完成真实协议复核";
        RouteSummary = BuildRouteSummary(result, fromCache);
    }

    private static string BuildRouteSummary(ProxyEndpointProtocolProbeResult result, bool fromCache)
    {
        var source = fromCache ? "缓存" : "实测";
        var supported = new[]
        {
            result.ChatCompletionsSupported ? "Chat" : null,
            result.ResponsesSupported ? "Responses" : null,
            result.AnthropicMessagesSupported ? "Anthropic" : null
        }.Where(static value => !string.IsNullOrWhiteSpace(value));
        var supportedText = string.Join("/", supported);
        return string.IsNullOrWhiteSpace(supportedText)
            ? $"{source} · 未确认协议"
            : $"{source} · {FormatWireApiDisplay(result.PreferredWireApi)} · {supportedText}";
    }

    [RelayCommand]
    private void AddChatSelectedModel(string? modelName)
    {
        var candidate = string.IsNullOrWhiteSpace(modelName) ? Model : modelName;
        if (AddSelectedCompareModel(candidate))
        {
            MultiModelEnabled = true;
            StatusText = $"已加入对比模型：{candidate.Trim()}";
        }
    }

    [RelayCommand]
    private void RemoveChatSelectedModel(string? modelName)
    {
        var candidate = modelName?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return;
        }

        var existing = SelectedCompareModels.FirstOrDefault(item =>
            item.Equals(candidate, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            SelectedCompareModels.Remove(existing);
            StatusText = $"已移除对比模型：{existing}";
        }
    }

    [RelayCommand]
    private void ClearChatSelectedModels()
    {
        SelectedCompareModels.Clear();
        ModelCompareEntries.Clear();
        StatusText = "已清空对比模型";
    }

    private bool AddSelectedCompareModel(string? modelName)
    {
        var candidate = modelName?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(candidate) ||
            SelectedCompareModels.Contains(candidate, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        SelectedCompareModels.Add(candidate);
        return true;
    }

    // ─── Phase 12: Multi-Model Comparison ─────────────────────────────────

    private async Task SendMultiModelAsync(string userMessage, List<ChatAttachment> attachments)
    {
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        IsStreaming = true;
        StatusText = "正在发送到多个模型...";
        ModelCompareEntries.Clear();

        var history = BuildChatHistory(Messages);
        var cancellationToken = _cts.Token;

        // Create entries for each model
        var models = SelectedCompareModels.ToList();
        foreach (var model in models)
        {
            ModelCompareEntries.Add(new ModelCompareEntry { ModelName = model, IsLoading = true });
        }

        // Fan out to all models concurrently
        var tasks = models.Select((model, index) => SendToModelAsync(
            model, index, history, attachments, cancellationToken)).ToArray();

        try
        {
            await Task.WhenAll(tasks);
            StatusText = cancellationToken.IsCancellationRequested
                ? "已停止多模型对比"
                : $"多模型对比完成（{models.Count} 个模型）";
        }
        catch (OperationCanceledException)
        {
            StatusText = "已停止多模型对比";
        }
        catch (Exception ex)
        {
            StatusText = $"多模型对比失败：{ex.Message}";
        }
        finally
        {
            IsStreaming = false;
            _cts?.Dispose();
            _cts = null;
            MessageCount = Messages.Count;
            SaveCurrentSessionMessages();
            if (!cancellationToken.IsCancellationRequested && ModelCompareEntries.Count > 0)
            {
                await RecordMultiModelChatRunAsync(models, attachments.Count);
            }
        }
    }

    private async Task RecordMultiModelChatRunAsync(IReadOnlyList<string> requestedModels, int attachmentCount)
    {
        try
        {
            var results = ModelCompareEntries
                .Select(static entry =>
                {
                    var failed = entry.ResponseText.StartsWith("[错误:", StringComparison.Ordinal) ||
                                 entry.ResponseText.StartsWith("[已停止]", StringComparison.Ordinal);
                    return new
                    {
                        Model = entry.ModelName,
                        Succeeded = !failed,
                        ResponseTimeMs = Math.Max(0, entry.ResponseTimeMs),
                        OutputCharacters = failed ? 0 : Math.Max(0, entry.ResponseText.Length),
                        Error = failed ? entry.ResponseText : null
                    };
                })
                .ToArray();
            var successCount = results.Count(static item => item.Succeeded);
            var durationMs = results.Length == 0
                ? 0
                : (int)Math.Min(int.MaxValue, results.Max(static item => item.ResponseTimeMs));
            var payload = JsonSerializer.Serialize(new
            {
                Schema = "model-chat-multi-v1",
                Action = "compare",
                BaseUrl,
                Models = requestedModels,
                JsonMode,
                ResultCount = results.Length,
                SuccessCount = successCount,
                FailedCount = Math.Max(0, results.Length - successCount),
                Results = results,
                MessageCount,
                AttachmentCount = attachmentCount,
                IsTransparentProxyEndpoint,
                EndpointName,
                EndpointProtocol,
                RouteSummary
            });

            await RunHistoryRecorder.RecordAsync(
                "大模型对话",
                BaseUrl,
                $"多模型对比完成：{successCount}/{results.Length} 成功",
                results.Length == 0 ? 0 : successCount * 100.0 / results.Length,
                durationMs,
                payload);
        }
        catch
        {
            // History persistence should never interrupt the completed chat flow.
        }
    }

    private async Task SendToModelAsync(
        string model, int entryIndex,
        IReadOnlyList<ChatMessage> history,
        IReadOnlyList<ChatAttachment> attachments,
        CancellationToken cancellationToken)
    {
        var entry = ModelCompareEntries[entryIndex];
        var sw = Stopwatch.StartNew();

        var responseBuilder = new StringBuilder();

        try
        {
            var options = await BuildChatRequestOptionsAsync(model, cancellationToken, updateEndpointSummary: false);
            await foreach (var update in _chatService.SendStreamingAsync(
                options, history, attachments, cancellationToken))
            {
                switch (update.Kind)
                {
                    case ChatStreamUpdateKind.Delta:
                        responseBuilder.Append(update.Delta);
                        entry.ResponseText = responseBuilder.ToString();
                        break;
                    case ChatStreamUpdateKind.Completed:
                        sw.Stop();
                        entry.ResponseTimeMs = sw.ElapsedMilliseconds;
                        entry.IsLoading = false;
                        break;
                    case ChatStreamUpdateKind.Failed:
                        sw.Stop();
                        entry.ResponseText = $"[错误: {update.Error}]";
                        entry.ResponseTimeMs = sw.ElapsedMilliseconds;
                        entry.IsLoading = false;
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            entry.ResponseText = "[已停止]";
            entry.ResponseTimeMs = sw.ElapsedMilliseconds;
            entry.IsLoading = false;
        }
        catch (Exception ex)
        {
            sw.Stop();
            entry.ResponseText = $"[错误: {ex.Message}]";
            entry.ResponseTimeMs = sw.ElapsedMilliseconds;
            entry.IsLoading = false;
        }
    }

}
