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
    [RelayCommand]
    private void EditChatMessage(ChatMessageItem? message)
        => EditMessage(message);

    [RelayCommand]
    private void EditMessage(ChatMessageItem? message)
    {
        if (message is null || !message.IsUser) return;

        var index = -1;
        for (int i = 0; i < Messages.Count; i++)
        {
            if (ReferenceEquals(Messages[i], message))
            {
                index = i;
                break;
            }
        }

        if (index < 0) return;

        CancelChatEdit(restoreMessages: false);
        var removedMessages = Messages.Skip(index).ToArray();
        _chatEditSnapshot = new ChatEditSnapshot(index, removedMessages);

        // Put the message content back in the input for editing
        InputText = message.Content;
        ClearPendingAttachments();
        foreach (var attachment in message.AttachmentItems)
        {
            AddPendingAttachment(attachment);
        }

        while (Messages.Count > index)
        {
            Messages.RemoveAt(Messages.Count - 1);
        }

        MessageCount = Messages.Count;
        StatusText = "正在编辑消息，修改后可重新发送";
        NotifyChatEditingChanged();
    }

    [RelayCommand]
    private void CancelChatEdit()
        => CancelChatEdit(restoreMessages: true);

    private void CancelChatEdit(bool restoreMessages)
    {
        if (_chatEditSnapshot is null)
        {
            return;
        }

        if (restoreMessages)
        {
            var insertIndex = Math.Clamp(_chatEditSnapshot.StartIndex, 0, Messages.Count);
            foreach (var message in _chatEditSnapshot.RemovedMessages)
            {
                Messages.Insert(insertIndex++, message);
            }

            MessageCount = Messages.Count;
            StatusText = "已Cancel编辑";
        }

        _chatEditSnapshot = null;
        InputText = "";
        ClearPendingAttachments();
        NotifyChatEditingChanged();
    }

    private void ClearChatEditStateAfterCommit()
    {
        if (_chatEditSnapshot is null)
        {
            return;
        }

        _chatEditSnapshot = null;
        NotifyChatEditingChanged();
    }

    private void NotifyChatEditingChanged()
    {
        OnPropertyChanged(nameof(IsEditingChatMessage));
        OnPropertyChanged(nameof(ChatSendButtonText));
        OnPropertyChanged(nameof(ChatEditStatusText));
    }

    [RelayCommand]
    private void CopyChatMessage(ChatMessageItem? message)
    {
        if (message is null)
        {
            return;
        }

        CopyToClipboard(message.MarkdownContent, "已复制消息");
    }

    [RelayCommand]
    private void CopyChatCodeBlock(ChatMessageItem? message)
    {
        if (message is null || string.IsNullOrWhiteSpace(message.CodeBlock))
        {
            return;
        }

        CopyToClipboard(message.CodeBlock, "已复制代码块");
    }

    [RelayCommand]
    private void CopyChatModelAnswer(ModelCompareEntry? answer)
    {
        if (answer is null || string.IsNullOrWhiteSpace(answer.ResponseText))
        {
            return;
        }

        CopyToClipboard(answer.ResponseText, $"已复制 {answer.ModelName} 的回答");
    }

    private void CopyToClipboard(string? text, string successStatus)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        try
        {
            var package = new DataPackage();
            package.SetText(text);
            Clipboard.SetContent(package);
            StatusText = successStatus;
        }
        catch (Exception ex)
        {
            StatusText = $"复制失败：{ex.Message}";
        }
    }

    [RelayCommand]
    private async Task RegenerateLastChatAnswerAsync()
        => await RegenerateAsync();

    [RelayCommand]
    private async Task RegenerateAsync()
    {
        if (Messages.Count < 2) return;

        // Find the last assistant message and remove it
        var lastAssistant = Messages.LastOrDefault(m => !m.IsUser);
        if (lastAssistant is null) return;

        Messages.Remove(lastAssistant);
        MessageCount = Messages.Count;

        // Re-send: the history now ends with the last user message
        // Trigger a new send with empty input (we use the existing history)
        await ResendLastUserMessageAsync();
    }

    private async Task ResendLastUserMessageAsync()
    {
        if (string.IsNullOrWhiteSpace(BaseUrl) || string.IsNullOrWhiteSpace(ApiKey) || string.IsNullOrWhiteSpace(Model))
        {
            StatusText = "请先填写接口地址、API 密钥和模型";
            return;
        }

        _cts = new CancellationTokenSource();
        IsStreaming = true;
        StatusText = "正在重新生成...";

        var history = BuildChatHistory(Messages);
        var options = await BuildChatRequestOptionsAsync(Model.Trim(), _cts.Token, updateEndpointSummary: true);

        var responseBuilder = new StringBuilder();
        var assistantItem = new ChatMessageItem(false, "", null);
        Messages.Add(assistantItem);

        var streamStopwatch = Stopwatch.StartNew();
        var streamOutputTokens = 0;
        ChatMessageMetrics? completedMetrics = null;

        try
        {
            await foreach (var update in _chatService.SendStreamingAsync(
                options,
                history,
                history.LastOrDefault(static item => item.Role == "user")?.Attachments ?? Array.Empty<ChatAttachment>(),
                _cts.Token))
            {
                switch (update.Kind)
                {
                    case ChatStreamUpdateKind.Delta:
                        responseBuilder.Append(update.Delta);
                        assistantItem.Content = responseBuilder.ToString();
                        break;
                    case ChatStreamUpdateKind.Completed:
                        streamStopwatch.Stop();
                        if (update.Metrics is { } m)
                        {
                            LastMetrics = $"TTFT {m.FirstTokenLatency?.TotalMilliseconds:F0}ms | {m.OutputCharacterCount} 字符 | {m.WireApi}";
                            assistantItem.Metrics = LastMetrics;
                            completedMetrics = m;
                            streamOutputTokens = m.OutputTokenCount > 0
                                ? m.OutputTokenCount
                                : m.OutputCharacterCount / 4;
                            ApplyCompletedTokenMetrics(m);
                            var elapsedSec = streamStopwatch.Elapsed.TotalSeconds;
                            TokensPerSecond = elapsedSec > 0 ? $"{streamOutputTokens / elapsedSec:F1}" : "--";
                        }
                        StatusText = "完成";
                        break;
                    case ChatStreamUpdateKind.Failed:
                        streamStopwatch.Stop();
                        StatusText = $"失败: {update.Error}";
                        assistantItem.Content = $"[错误: {update.Error}]";
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            StatusText = "已Cancel";
        }
        catch (Exception ex)
        {
            StatusText = $"错误: {ex.Message}";
        }
        finally
        {
            IsStreaming = false;
            _cts = null;
            MessageCount = Messages.Count;
            SaveCurrentSessionMessages();
            if (completedMetrics is not null && StatusText == "完成")
            {
                await RecordCompletedChatRunAsync(
                    completedMetrics,
                    streamOutputTokens,
                    history.LastOrDefault(static item => item.Role == "user")?.Attachments.Count ?? 0,
                    "regenerate");
            }
        }
    }

    // ─── Chat Messaging ───────────────────────────────────────────────────

    [RelayCommand]
    private async Task SendChatMessageAsync()
        => await SendMessageAsync();

    [RelayCommand]
    private async Task SendMessageAsync()
    {
        var currentAttachmentItems = PendingAttachments.ToList();
        var currentAttachments = BuildAttachments();
        if (string.IsNullOrWhiteSpace(InputText) && currentAttachments.Count == 0) return;
        if (string.IsNullOrWhiteSpace(BaseUrl) || string.IsNullOrWhiteSpace(ApiKey) || string.IsNullOrWhiteSpace(Model))
        {
            StatusText = "请先填写接口地址、API 密钥和模型";
            return;
        }

        var userMessage = InputText.Trim();
        if (string.IsNullOrWhiteSpace(userMessage) && currentAttachments.Count > 0)
        {
            userMessage = "请根据附件回答。";
        }

        InputText = "";
        ClearChatEditStateAfterCommit();
        Messages.Add(new ChatMessageItem(
            true,
            userMessage,
            null,
            attachments: currentAttachments,
            attachmentItems: currentAttachmentItems));
        MessageCount = Messages.Count;

        _ = GlobalEndpointProtocolProbeCoordinator.Instance.RecordEndpointAsync(BaseUrl, ApiKey, Model, AvailableModels);
        GlobalEndpointProtocolProbeCoordinator.Instance.EnqueueEndpointProbe(
            BaseUrl,
            ApiKey,
            Model,
            AvailableModels);

        // Capture and clear pending attachments after they are attached to the user message.
        ClearPendingAttachments();

        // Multi-model comparison mode
        if (MultiModelEnabled && SelectedCompareModels.Count > 0)
        {
            await SendMultiModelAsync(userMessage, currentAttachments);
            return;
        }

        _cts = new CancellationTokenSource();
        IsStreaming = true;
        StatusText = "正在生成...";

        var responseBuilder = new StringBuilder();
        var assistantItem = new ChatMessageItem(false, "", null);
        Messages.Add(assistantItem);
        var history = BuildChatHistory(Messages.Take(Messages.Count - 1));
        var options = await BuildChatRequestOptionsAsync(Model.Trim(), _cts.Token, updateEndpointSummary: true);

        var streamStopwatch = Stopwatch.StartNew();
        int streamOutputTokens = 0;
        ChatMessageMetrics? completedMetrics = null;

        try
        {
            await foreach (var update in _chatService.SendStreamingAsync(
                options, history, currentAttachments, _cts.Token))
            {
                switch (update.Kind)
                {
                    case ChatStreamUpdateKind.Delta:
                        responseBuilder.Append(update.Delta);
                        assistantItem.Content = responseBuilder.ToString();
                        break;
                    case ChatStreamUpdateKind.Completed:
                        streamStopwatch.Stop();
                        if (update.Metrics is { } m)
                        {
                            LastMetrics = $"TTFT {m.FirstTokenLatency?.TotalMilliseconds:F0}ms | {m.OutputCharacterCount} 字符 | {m.WireApi}";
                            assistantItem.Metrics = LastMetrics;
                            completedMetrics = m;
                            streamOutputTokens = m.OutputTokenCount > 0
                                ? m.OutputTokenCount
                                : m.OutputCharacterCount / 4;
                            ApplyCompletedTokenMetrics(m);

                            var elapsedSec = streamStopwatch.Elapsed.TotalSeconds;
                            TokensPerSecond = elapsedSec > 0
                                ? $"{streamOutputTokens / elapsedSec:F1}"
                                : "--";
                        }
                        StatusText = "完成";
                        break;
                    case ChatStreamUpdateKind.Failed:
                        streamStopwatch.Stop();
                        StatusText = $"失败: {update.Error}";
                        assistantItem.Content = $"[错误: {update.Error}]";
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            StatusText = "已Cancel";
        }
        catch (Exception ex)
        {
            StatusText = $"错误: {ex.Message}";
        }
        finally
        {
            IsStreaming = false;
            _cts = null;
            MessageCount = Messages.Count;

            // Auto-save messages to current session
            SaveCurrentSessionMessages();

            // Auto-title: if session title is still default and we have a user message, use it
            if (CurrentSession is not null &&
                (CurrentSession.Title == "新会话" || CurrentSession.Title == "New Session") &&
                Messages.Count > 0)
            {
                var firstUserMsg = Messages.FirstOrDefault(m => m.IsUser);
                if (firstUserMsg is not null)
                {
                    var autoTitle = firstUserMsg.Content.Length > 30
                        ? firstUserMsg.Content[..30] + "..."
                        : firstUserMsg.Content;
                    CurrentSession.Title = autoTitle;
                    _sessionStore.RenameSession(CurrentSession.Id, autoTitle);
                }
            }

            if (completedMetrics is not null && StatusText == "完成")
            {
                await RecordCompletedChatRunAsync(
                    completedMetrics,
                    streamOutputTokens,
                    currentAttachments.Count);
            }
        }
    }

    private void ApplyCompletedTokenMetrics(ChatMessageMetrics metrics)
    {
        OutputTokens += Math.Max(0, metrics.OutputTokenCount);
        InputTokens += Math.Max(0, metrics.InputTokenCount);
        CachedTokens += Math.Max(0, metrics.CachedTokenCount);
        CacheHitRate = FormatCacheHitRate(InputTokens, CachedTokens);
    }

    private static string FormatCacheHitRate(int inputTokens, int cachedTokens)
        => inputTokens > 0 && cachedTokens > 0
            ? $"{(double)cachedTokens / Math.Max(1, inputTokens):P1}"
            : "0.0%";

    private async Task RecordCompletedChatRunAsync(
        ChatMessageMetrics metrics,
        int outputTokens,
        int attachmentCount,
        string action = "send")
    {
        try
        {
            var inputTokens = Math.Max(0, metrics.InputTokenCount);
            var cachedTokens = Math.Max(0, metrics.CachedTokenCount);
            var wireApi = string.IsNullOrWhiteSpace(metrics.WireApi)
                ? _lastProtocolProbeResult?.PreferredWireApi ?? string.Empty
                : metrics.WireApi;
            var payload = JsonSerializer.Serialize(new
            {
                Schema = "model-chat-v1",
                Action = action,
                BaseUrl,
                Model,
                WireApi = wireApi,
                PreferredWireApi = wireApi,
                JsonMode,
                ResponsesSupported = _lastProtocolProbeResult?.ResponsesSupported ?? false,
                ChatCompletionsSupported = _lastProtocolProbeResult?.ChatCompletionsSupported ?? false,
                AnthropicMessagesSupported = _lastProtocolProbeResult?.AnthropicMessagesSupported ?? false,
                ProtocolCheckedAt = _lastProtocolProbeResult?.CheckedAt,
                ProtocolSummary = RouteSummary,
                TotalInputTokens = inputTokens,
                TotalOutputTokens = outputTokens,
                PromptCacheTokens = cachedTokens,
                CacheHitRate = inputTokens > 0 ? (double)cachedTokens / Math.Max(1, inputTokens) * 100 : 0,
                OutputTokenCountEstimated = metrics.OutputTokenCountEstimated,
                OutputCharacters = metrics.OutputCharacterCount,
                FirstTokenLatencyMs = metrics.FirstTokenLatency?.TotalMilliseconds,
                P50LatencyMs = metrics.Elapsed.TotalMilliseconds,
                OutputTokensPerSecond = metrics.TokensPerSecond,
                MessageCount,
                AttachmentCount = attachmentCount,
                IsTransparentProxyEndpoint,
                EndpointName,
                EndpointProtocol,
                RouteSummary
            });

            var durationMs = (int)Math.Min(int.MaxValue, Math.Round(metrics.Elapsed.TotalMilliseconds));
            var summary = $"大模型对话完成：{Model} · 输出 {FormatCompactNumber(outputTokens)} tokens";
            await RunHistoryRecorder.RecordAsync(
                "大模型对话",
                BaseUrl,
                summary,
                score: 100,
                durationMs: durationMs,
                payloadJson: payload);
        }
        catch
        {
            // History persistence should never interrupt the completed chat flow.
        }
    }

    /// <summary>
    /// Builds ChatAttachment list from the current pending attachments collection.
    /// Images are base64-encoded; text files are read as UTF-8.
    /// </summary>
}
