using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using Microsoft.Win32;
using RelayBench.App.Infrastructure;
using RelayBench.Core.Models;

namespace RelayBench.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    public string ChatInputText
    {
        get => _chatInputText;
        set
        {
            if (SetProperty(ref _chatInputText, value))
            {
                SendChatMessageCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string ChatSystemPrompt
    {
        get => _chatSystemPrompt;
        set
        {
            if (SetProperty(ref _chatSystemPrompt, value))
            {
                SaveChatSession();
            }
        }
    }

    public string ChatTemperatureText
    {
        get => _chatTemperatureText;
        set
        {
            if (SetProperty(ref _chatTemperatureText, value))
            {
                SaveChatSession();
            }
        }
    }

    public string ChatMaxTokensText
    {
        get => _chatMaxTokensText;
        set
        {
            if (SetProperty(ref _chatMaxTokensText, value))
            {
                SaveChatSession();
            }
        }
    }

    public string SelectedChatReasoningEffortKey
    {
        get => _selectedChatReasoningEffortKey;
        set
        {
            var normalized = NormalizeChatReasoningEffortKey(value);
            if (SetProperty(ref _selectedChatReasoningEffortKey, normalized))
            {
                RefreshChatReasoningEffortSummary();
                SaveChatSession();
            }
        }
    }

    public string ChatCandidateModel
    {
        get => _chatCandidateModel;
        set => SetProperty(ref _chatCandidateModel, value);
    }

    public ChatSessionListItemViewModel? SelectedChatSession
    {
        get => _selectedChatSession;
        set
        {
            if (SetProperty(ref _selectedChatSession, value) && value is not null && !_isLoadingChatSession)
            {
                SwitchChatSession(value.SessionId);
            }
        }
    }

    public ChatPromptPresetViewModel? SelectedChatPreset
    {
        get => _selectedChatPreset;
        set
        {
            if (SetProperty(ref _selectedChatPreset, value))
            {
                ApplyChatPresetCommand.RaiseCanExecuteChanged();
                DeleteChatPresetCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsChatSettingsPanelOpen
    {
        get => _isChatSettingsPanelOpen;
        private set => SetProperty(ref _isChatSettingsPanelOpen, value);
    }

    public bool IsChatStreaming
    {
        get => _isChatStreaming;
        private set
        {
            if (SetProperty(ref _isChatStreaming, value))
            {
                SendChatMessageCommand.RaiseCanExecuteChanged();
                StopChatStreamingCommand.RaiseCanExecuteChanged();
                AddChatImageAttachmentCommand.RaiseCanExecuteChanged();
                AddChatTextFileAttachmentCommand.RaiseCanExecuteChanged();
                AddChatAttachmentFilesCommand.RaiseCanExecuteChanged();
                AddChatSelectedModelCommand.RaiseCanExecuteChanged();
                ClearChatSelectedModelsCommand.RaiseCanExecuteChanged();
                RemoveChatSelectedModelCommand.RaiseCanExecuteChanged();
                RemoveChatAttachmentCommand.RaiseCanExecuteChanged();
                NewChatSessionCommand.RaiseCanExecuteChanged();
                DeleteChatSessionCommand.RaiseCanExecuteChanged();
                BeginRenameChatSessionCommand.RaiseCanExecuteChanged();
                CommitRenameChatSessionCommand.RaiseCanExecuteChanged();
                EditChatMessageCommand.RaiseCanExecuteChanged();
                CancelChatEditCommand.RaiseCanExecuteChanged();
                RegenerateLastChatAnswerCommand.RaiseCanExecuteChanged();
                ExportChatSessionMarkdownCommand.RaiseCanExecuteChanged();
                ExportChatSessionTextCommand.RaiseCanExecuteChanged();
                ApplyChatPresetCommand.RaiseCanExecuteChanged();
                SaveChatPresetCommand.RaiseCanExecuteChanged();
                DeleteChatPresetCommand.RaiseCanExecuteChanged();
                OnPropertyChanged(nameof(CanStopChatStreamingNow));
            }
        }
    }

    public bool CanStopChatStreamingNow => CanStopChatStreaming();

    public bool HasChatMessages => ChatMessages.Count > 0;

    public bool HasChatSessions => ChatSessions.Count > 0;

    public bool HasPendingChatAttachments => PendingChatAttachments.Count > 0;

    public bool HasChatSelectedModels => ChatSelectedModels.Count > 0;

    public bool IsEditingChatMessage => !string.IsNullOrWhiteSpace(_chatEditingMessageId);

    public string ChatSendButtonText => IsEditingChatMessage ? "\u91cd\u53d1" : "\u53d1\u9001";

    public string ChatEditStatusText
        => IsEditingChatMessage
            ? "\u6b63\u5728\u7f16\u8f91\u5386\u53f2\u6d88\u606f\uff0c\u53d1\u9001\u540e\u4f1a\u4ece\u8be5\u6d88\u606f\u5904\u91cd\u65b0\u751f\u6210\u56de\u7b54\u3002"
            : string.Empty;

    public string ChatSelectedModelsSummary
        => HasChatSelectedModels
            ? $"\u5df2\u9009 {ChatSelectedModels.Count} \u4e2a\u5bf9\u6bd4\u6a21\u578b\uff0c\u53d1\u9001\u540e\u6309\u5217\u5e76\u6392\u56de\u7b54\u3002"
            : "\u672a\u9009\u5bf9\u6bd4\u6a21\u578b\uff0c\u53d1\u9001\u65f6\u4f7f\u7528\u5f53\u524d\u6a21\u578b\u5355\u804a\u3002";

    public string ChatModeSummary
        => HasChatSelectedModels
            ? $"\u591a\u6a21\u578b {ChatSelectedModels.Count} \u5217"
            : string.IsNullOrWhiteSpace(ProxyModel)
                ? "\u5355\u6a21\u578b\uff08\u672a\u9009\u6a21\u578b\uff09"
                : $"\u5355\u6a21\u578b\uff1a{ProxyModel}";

    public string ChatStatusMessage
    {
        get => _chatStatusMessage;
        private set => SetProperty(ref _chatStatusMessage, value);
    }

    public string ChatMetricsSummary
    {
        get => _chatMetricsSummary;
        private set => SetProperty(ref _chatMetricsSummary, value);
    }

    public string ChatReasoningEffortSummary
    {
        get => _chatReasoningEffortSummary;
        private set => SetProperty(ref _chatReasoningEffortSummary, value);
    }

    private bool CanSendChatMessage()
        => !IsChatStreaming &&
           (!string.IsNullOrWhiteSpace(ChatInputText) || PendingChatAttachments.Count > 0);

    private bool CanStopChatStreaming()
        => IsChatStreaming && _currentChatCancellationSource is { IsCancellationRequested: false };

    private bool CanEditChatAttachments()
        => !IsChatStreaming;

    private bool CanRegenerateLastChatAnswer()
        => !IsChatStreaming && ChatMessages.Any(static message => message.IsUser);

    private bool CanExportChatSession()
        => !IsChatStreaming && ChatMessages.Count > 0;

    private async Task SendChatMessageAsync()
    {
        if (!CanSendChatMessage())
        {
            ChatStatusMessage = "\u8bf7\u5148\u8f93\u5165\u6d88\u606f\u6216\u6dfb\u52a0\u9644\u4ef6\u3002";
            return;
        }

        var attachments = PendingChatAttachments
            .Select(static attachment => attachment.Attachment)
            .ToArray();
        if (IsEditingChatMessage)
        {
            RemoveChatMessagesFromEditingPoint();
        }

        var targetModels = GetChatTargetModels();
        var userMessage = new ChatMessage(
            Guid.NewGuid().ToString("N"),
            "user",
            ChatInputText.Trim(),
            DateTimeOffset.Now,
            attachments,
            null,
            null);
        var userViewModel = new ChatMessageViewModel(userMessage);
        var assistantViewModel = CreateAssistantViewModel(targetModels);
        ChatMessages.Add(userViewModel);
        ChatMessages.Add(assistantViewModel);
        ChatInputText = string.Empty;
        ClearChatEditingState();
        PendingChatAttachments.Clear();
        NotifyChatCollectionsChanged();
        SaveChatSession();
        await StartChatGenerationAsync(assistantViewModel, attachments, targetModels);
    }

    private async Task StartChatGenerationAsync(
        ChatMessageViewModel assistantViewModel,
        IReadOnlyList<ChatAttachment> requestAttachments,
        IReadOnlyList<string> targetModels)
    {
        _currentChatCancellationSource?.Dispose();
        _currentChatCancellationSource = new CancellationTokenSource();
        IsChatStreaming = true;
        ChatStatusMessage = "\u6b63\u5728\u53d1\u9001\u5bf9\u8bdd\u8bf7\u6c42...";

        try
        {
            var history = ChatMessages
                .Where(message => !string.Equals(message.Id, assistantViewModel.Id, StringComparison.Ordinal))
                .Select(static message => message.ToCore())
                .ToArray();

            if (assistantViewModel.IsMultiModelAnswer)
            {
                ChatStatusMessage = $"\u6b63\u5728\u540c\u65f6\u8bf7\u6c42 {targetModels.Count} \u4e2a\u6a21\u578b...";
                var token = _currentChatCancellationSource.Token;
                var tasks = assistantViewModel.ModelAnswers
                    .Select(answer => StreamChatModelAnswerWithResolvedOptionsAsync(answer.ModelName, history, requestAttachments, answer, token))
                    .ToArray();
                await Task.WhenAll(tasks);
                ChatMetricsSummary = BuildMultiChatMetricsSummary(assistantViewModel.ModelAnswers);
                ChatStatusMessage = assistantViewModel.ModelAnswers.All(static answer => answer.HasError)
                    ? "\u591a\u6a21\u578b\u95ee\u7b54\u5168\u90e8\u5931\u8d25\u3002"
                    : "\u591a\u6a21\u578b\u95ee\u7b54\u5b8c\u6210\u3002";
                AppendHistory("\u5bf9\u8bdd", "\u591a\u6a21\u578b\u7edf\u4e00\u95ee\u7b54", ChatMetricsSummary);
            }
            else
            {
                await StreamSingleChatAnswerAsync(
                    await BuildChatRequestOptionsAsync(targetModels.FirstOrDefault(), _currentChatCancellationSource.Token),
                    history,
                    requestAttachments,
                    assistantViewModel,
                    _currentChatCancellationSource.Token);
            }
        }
        catch (OperationCanceledException)
        {
            ChatStatusMessage = "\u5df2\u505c\u6b62\u751f\u6210\u3002";
            if (assistantViewModel.IsMultiModelAnswer)
            {
                foreach (var answer in assistantViewModel.ModelAnswers.Where(static answer => answer.IsStreaming))
                {
                    answer.Cancel();
                }
            }
            else
            {
                assistantViewModel.SetError("\u7528\u6237\u5df2\u505c\u6b62\u751f\u6210\u3002");
            }
        }
        finally
        {
            IsChatStreaming = false;
            _currentChatCancellationSource?.Dispose();
            _currentChatCancellationSource = null;
            SaveChatSession();
        }
    }

    private static ChatMessageViewModel CreateAssistantViewModel(IReadOnlyList<string> targetModels)
    {
        if (targetModels.Count > 1)
        {
            return ChatMessageViewModel.CreateMultiModelAnswer(targetModels);
        }

        return new ChatMessageViewModel(new ChatMessage(
            Guid.NewGuid().ToString("N"),
            "assistant",
            string.Empty,
            DateTimeOffset.Now,
            Array.Empty<ChatAttachment>(),
            null,
            null));
    }

    private Task StopChatStreamingAsync()
    {
        _currentChatCancellationSource?.Cancel();
        ChatStatusMessage = "\u6b63\u5728\u505c\u6b62\u751f\u6210...";
        return Task.CompletedTask;
    }

    private Task ToggleChatSettingsPanelAsync()
    {
        IsChatSettingsPanelOpen = !IsChatSettingsPanelOpen;
        return Task.CompletedTask;
    }

    private Task CloseChatSettingsPanelAsync()
    {
        IsChatSettingsPanelOpen = false;
        return Task.CompletedTask;
    }

    private async Task StreamSingleChatAnswerAsync(
        ChatRequestOptions options,
        IReadOnlyList<ChatMessage> history,
        IReadOnlyList<ChatAttachment> attachments,
        ChatMessageViewModel assistantViewModel,
        CancellationToken cancellationToken)
    {
        await foreach (var update in _chatConversationService.SendStreamingAsync(
            options,
            history,
            attachments,
            cancellationToken))
        {
            switch (update.Kind)
            {
                case ChatStreamUpdateKind.Started:
                    ChatStatusMessage = "\u5df2\u8fde\u63a5\u4e0a\u6e38\uff0c\u7b49\u5f85\u6a21\u578b\u8f93\u51fa...";
                    break;
                case ChatStreamUpdateKind.Delta:
                    assistantViewModel.AppendDelta(update.Delta ?? string.Empty);
                    break;
                case ChatStreamUpdateKind.Completed:
                    assistantViewModel.SetMetrics(update.Metrics);
                    ApplyChatMetrics(update.Metrics);
                    ChatStatusMessage = "\u5bf9\u8bdd\u5b8c\u6210\u3002";
                    AppendHistory("\u5bf9\u8bdd", "\u5927\u6a21\u578b\u5bf9\u8bdd\u5b9e\u6d4b", ChatMetricsSummary);
                    break;
                case ChatStreamUpdateKind.Failed:
                    assistantViewModel.SetError(update.Error);
                    assistantViewModel.SetMetrics(update.Metrics);
                    ApplyChatMetrics(update.Metrics);
                    ChatStatusMessage = update.Error ?? "\u5bf9\u8bdd\u8bf7\u6c42\u5931\u8d25\u3002";
                    break;
            }
        }
    }

    private async Task StreamChatModelAnswerAsync(
        ChatRequestOptions options,
        IReadOnlyList<ChatMessage> history,
        IReadOnlyList<ChatAttachment> attachments,
        ChatModelAnswerViewModel answer,
        CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var update in _chatConversationService.SendStreamingAsync(
                options,
                history,
                attachments,
                cancellationToken))
            {
                switch (update.Kind)
                {
                    case ChatStreamUpdateKind.Started:
                        answer.MarkStarted();
                        ChatStatusMessage = $"\u5df2\u8fde\u63a5 {answer.ModelName}\uff0c\u7b49\u5f85\u8f93\u51fa...";
                        break;
                    case ChatStreamUpdateKind.Delta:
                        answer.AppendDelta(update.Delta ?? string.Empty);
                        break;
                    case ChatStreamUpdateKind.Completed:
                        answer.Complete(update.Metrics);
                        break;
                    case ChatStreamUpdateKind.Failed:
                        answer.Fail(update.Error, update.Metrics);
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            answer.Cancel();
        }
    }

    private async Task StreamChatModelAnswerWithResolvedOptionsAsync(
        string modelName,
        IReadOnlyList<ChatMessage> history,
        IReadOnlyList<ChatAttachment> attachments,
        ChatModelAnswerViewModel answer,
        CancellationToken cancellationToken)
    {
        var options = await BuildChatRequestOptionsAsync(modelName, cancellationToken);
        await StreamChatModelAnswerAsync(options, history, attachments, answer, cancellationToken);
    }

    private Task ClearChatSessionAsync()
    {
        ChatMessages.Clear();
        ChatInputText = string.Empty;
        ClearChatEditingState();
        PendingChatAttachments.Clear();
        ChatStatusMessage = "\u5df2\u6e05\u7a7a\u5bf9\u8bdd\u3002";
        ChatMetricsSummary = "\u5c1a\u672a\u53d1\u9001\u5bf9\u8bdd\u8bf7\u6c42\u3002";
        NotifyChatCollectionsChanged();
        SaveChatSession();
        return Task.CompletedTask;
    }

    private Task NewChatSessionAsync()
    {
        SaveChatSession();
        var session = CreateEmptyChatSession("\u65b0\u4f1a\u8bdd");
        _chatSessionsDocument.Sessions.Insert(0, session);
        var item = ToChatSessionListItem(session);
        ChatSessions.Insert(0, item);
        SelectChatSession(item);
        LoadChatSession(item.SessionId);
        ChatStatusMessage = "\u5df2\u521b\u5efa\u65b0\u4f1a\u8bdd\u3002";
        SaveChatDocument();
        return Task.CompletedTask;
    }

    private Task DeleteChatSessionAsync()
    {
        if (SelectedChatSession is null)
        {
            return Task.CompletedTask;
        }

        var sessionId = SelectedChatSession.SessionId;
        _chatSessionsDocument.Sessions.RemoveAll(session => string.Equals(session.SessionId, sessionId, StringComparison.Ordinal));
        ChatSessions.Remove(SelectedChatSession);
        if (_chatSessionsDocument.Sessions.Count == 0)
        {
            var replacement = CreateEmptyChatSession("\u65b0\u4f1a\u8bdd");
            _chatSessionsDocument.Sessions.Add(replacement);
            ChatSessions.Add(ToChatSessionListItem(replacement));
        }

        var nextItem = ChatSessions.First();
        SelectChatSession(nextItem);
        LoadChatSession(nextItem.SessionId);
        ChatStatusMessage = "\u5df2\u5220\u9664\u4f1a\u8bdd\u3002";
        SaveChatDocument();
        return Task.CompletedTask;
    }

    private Task BeginRenameChatSessionAsync(ChatSessionListItemViewModel? item)
    {
        if (item is null)
        {
            return Task.CompletedTask;
        }

        item.DraftTitle = item.Title;
        item.IsRenaming = true;
        return Task.CompletedTask;
    }

    private Task CommitRenameChatSessionAsync(ChatSessionListItemViewModel? item)
    {
        if (item is null)
        {
            return Task.CompletedTask;
        }

        var title = item.DraftTitle.Trim();
        if (string.IsNullOrWhiteSpace(title))
        {
            item.DraftTitle = item.Title;
            item.IsRenaming = false;
            return Task.CompletedTask;
        }

        var session = _chatSessionsDocument.Sessions.FirstOrDefault(session =>
            string.Equals(session.SessionId, item.SessionId, StringComparison.Ordinal));
        if (session is null)
        {
            return Task.CompletedTask;
        }

        session.ManualTitle = title;
        session.Title = TrimSessionTitle(title);
        session.UpdatedAt = DateTimeOffset.Now;
        item.Title = BuildChatSessionTitle(session);
        item.UpdatedAt = session.UpdatedAt;
        item.IsManualTitle = true;
        item.IsRenaming = false;
        SaveChatDocument();
        ChatStatusMessage = "\u4f1a\u8bdd\u5df2\u91cd\u547d\u540d\u3002";
        return Task.CompletedTask;
    }

    private static Task CancelRenameChatSessionAsync(ChatSessionListItemViewModel? item)
    {
        if (item is not null)
        {
            item.DraftTitle = item.Title;
            item.IsRenaming = false;
        }

        return Task.CompletedTask;
    }

    private async Task RegenerateLastChatAnswerAsync()
    {
        var userIndex = FindLastUserMessageIndex();
        if (userIndex < 0)
        {
            ChatStatusMessage = "\u6ca1\u6709\u53ef\u91cd\u65b0\u751f\u6210\u7684\u7528\u6237\u6d88\u606f\u3002";
            return;
        }

        var userMessage = ChatMessages[userIndex];
        var requestAttachments = userMessage.Attachments
            .Select(static attachment => attachment.Attachment)
            .ToArray();
        for (var index = ChatMessages.Count - 1; index > userIndex; index--)
        {
            ChatMessages.RemoveAt(index);
        }

        var targetModels = GetChatTargetModels();
        var assistantViewModel = CreateAssistantViewModel(targetModels);
        ChatMessages.Add(assistantViewModel);
        ClearChatEditingState();
        NotifyChatCollectionsChanged();
        SaveChatSession();
        ChatStatusMessage = "\u6b63\u5728\u91cd\u65b0\u751f\u6210\u6700\u540e\u4e00\u6b21\u56de\u7b54...";
        await StartChatGenerationAsync(assistantViewModel, requestAttachments, targetModels);
    }

    private Task ExportChatSessionMarkdownAsync()
    {
        var path = _modelChatExportService.ExportMarkdown(GetCurrentChatSessionTitle(), ChatMessages.Select(static message => message.ToCore()).ToArray());
        ChatStatusMessage = $"\u5df2\u5bfc\u51fa Markdown\uff1a{path}";
        return Task.CompletedTask;
    }

    private Task ExportChatSessionTextAsync()
    {
        var path = _modelChatExportService.ExportText(GetCurrentChatSessionTitle(), ChatMessages.Select(static message => message.ToCore()).ToArray());
        ChatStatusMessage = $"\u5df2\u5bfc\u51fa TXT\uff1a{path}";
        return Task.CompletedTask;
    }

    private Task EditChatMessageAsync(ChatMessageViewModel? message)
    {
        if (message?.IsUser != true)
        {
            return Task.CompletedTask;
        }

        _chatEditingMessageId = message.Id;
        ChatInputText = message.Content;
        PendingChatAttachments.Clear();
        foreach (var attachment in message.Attachments)
        {
            PendingChatAttachments.Add(new ChatAttachmentViewModel(attachment.Attachment));
        }

        ChatStatusMessage = "\u5df2\u8fdb\u5165\u7f16\u8f91\u91cd\u53d1\u6a21\u5f0f\u3002";
        NotifyChatCollectionsChanged();
        NotifyChatEditingChanged();
        return Task.CompletedTask;
    }

    private Task CancelChatEditAsync()
    {
        ClearChatEditingState();
        ChatInputText = string.Empty;
        PendingChatAttachments.Clear();
        NotifyChatCollectionsChanged();
        ChatStatusMessage = "\u5df2\u53d6\u6d88\u7f16\u8f91\u3002";
        return Task.CompletedTask;
    }

    private Task ApplyChatPresetAsync()
    {
        if (SelectedChatPreset is null)
        {
            return Task.CompletedTask;
        }

        ChatSystemPrompt = SelectedChatPreset.SystemPrompt;
        ChatTemperatureText = SelectedChatPreset.TemperatureText;
        ChatMaxTokensText = SelectedChatPreset.MaxTokensText;
        SelectedChatReasoningEffortKey = SelectedChatPreset.ReasoningEffortKey;
        SaveChatSession();
        ChatStatusMessage = $"\u5df2\u5e94\u7528\u9884\u8bbe\uff1a{SelectedChatPreset.Name}";
        return Task.CompletedTask;
    }

    private Task SaveChatPresetAsync()
    {
        var preset = new ChatPromptPresetViewModel(
            Guid.NewGuid().ToString("N"),
            $"\u81ea\u5b9a\u4e49 {DateTimeOffset.Now:MM-dd HH:mm}",
            ChatSystemPrompt,
            ChatTemperatureText,
            ChatMaxTokensText,
            SelectedChatReasoningEffortKey,
            false);
        ChatPresets.Add(preset);
        SelectedChatPreset = preset;
        SaveChatDocument();
        ChatStatusMessage = "\u5df2\u4fdd\u5b58\u5f53\u524d\u751f\u6210\u53c2\u6570\u4e3a\u9884\u8bbe\u3002";
        return Task.CompletedTask;
    }

    private Task DeleteChatPresetAsync()
    {
        if (SelectedChatPreset?.IsBuiltIn != false)
        {
            return Task.CompletedTask;
        }

        var index = ChatPresets.IndexOf(SelectedChatPreset);
        ChatPresets.Remove(SelectedChatPreset);
        SelectedChatPreset = ChatPresets.Count == 0
            ? null
            : ChatPresets[Math.Clamp(index, 0, ChatPresets.Count - 1)];
        SaveChatDocument();
        ChatStatusMessage = "\u5df2\u5220\u9664\u81ea\u5b9a\u4e49\u9884\u8bbe\u3002";
        return Task.CompletedTask;
    }

    private Task AddChatImageAttachmentAsync()
    {
        OpenFileDialog dialog = new()
        {
            Filter = "Images|*.png;*.jpg;*.jpeg;*.webp;*.gif",
            Multiselect = true
        };

        if (dialog.ShowDialog() == true)
        {
            foreach (var fileName in dialog.FileNames)
            {
                PendingChatAttachments.Add(new ChatAttachmentViewModel(
                    _chatAttachmentImportService.ImportImage(fileName)));
            }

            NotifyChatCollectionsChanged();
            ChatStatusMessage = "\u5df2\u6dfb\u52a0\u56fe\u7247\u9644\u4ef6\u3002";
        }

        return Task.CompletedTask;
    }

    private Task AddChatTextFileAttachmentAsync()
    {
        OpenFileDialog dialog = new()
        {
            Filter = "Text files|*.txt;*.md;*.json;*.csv;*.log;*.cs;*.xaml;*.xml;*.yaml;*.yml;*.ps1",
            Multiselect = true
        };

        if (dialog.ShowDialog() == true)
        {
            foreach (var fileName in dialog.FileNames)
            {
                PendingChatAttachments.Add(new ChatAttachmentViewModel(
                    _chatAttachmentImportService.ImportTextFile(fileName)));
            }

            NotifyChatCollectionsChanged();
            ChatStatusMessage = "\u5df2\u6dfb\u52a0\u6587\u672c\u6587\u4ef6\u9644\u4ef6\u3002";
        }

        return Task.CompletedTask;
    }

    private Task AddChatAttachmentFilesAsync(string[]? filePaths)
    {
        if (filePaths is null || filePaths.Length == 0)
        {
            return Task.CompletedTask;
        }

        var imported = 0;
        List<string> failures = [];
        foreach (var filePath in filePaths.Where(static path => !string.IsNullOrWhiteSpace(path)))
        {
            try
            {
                PendingChatAttachments.Add(new ChatAttachmentViewModel(
                    _chatAttachmentImportService.ImportFile(filePath)));
                imported++;
            }
            catch (Exception ex)
            {
                failures.Add($"{System.IO.Path.GetFileName(filePath)}：{ex.Message}");
            }
        }

        NotifyChatCollectionsChanged();
        ChatStatusMessage = failures.Count == 0
            ? $"\u5df2\u901a\u8fc7\u62d6\u62fd\u6dfb\u52a0 {imported} \u4e2a\u9644\u4ef6\u3002"
            : $"\u5df2\u6dfb\u52a0 {imported} \u4e2a\u9644\u4ef6\uff0c{failures.Count} \u4e2a\u6587\u4ef6\u672a\u5bfc\u5165\uff1a{string.Join("；", failures.Take(3))}";
        return Task.CompletedTask;
    }

    private Task AddChatSelectedModelAsync()
    {
        var model = string.IsNullOrWhiteSpace(ChatCandidateModel) ? ProxyModel : ChatCandidateModel;
        if (!TryAddChatSelectedModel(model, out var message))
        {
            ChatStatusMessage = message;
            return Task.CompletedTask;
        }

        ChatStatusMessage = message;
        SaveChatSession();
        return Task.CompletedTask;
    }

    private Task ClearChatSelectedModelsAsync()
    {
        ChatSelectedModels.Clear();
        RefreshChatSelectedModelOrdinals();
        NotifyChatSelectedModelsChanged();
        SaveChatSession();
        ChatStatusMessage = "\u5df2\u6e05\u7a7a\u5bf9\u6bd4\u6a21\u578b\uff0c\u4e0b\u6b21\u53d1\u9001\u5c06\u56de\u5230\u5f53\u524d\u6a21\u578b\u5355\u804a\u3002";
        return Task.CompletedTask;
    }

    private Task RemoveChatSelectedModelAsync(ChatModelSelectionViewModel? model)
    {
        if (model is null)
        {
            return Task.CompletedTask;
        }

        ChatSelectedModels.Remove(model);
        RefreshChatSelectedModelOrdinals();
        NotifyChatSelectedModelsChanged();
        SaveChatSession();
        ChatStatusMessage = $"\u5df2\u79fb\u9664\u5bf9\u6bd4\u6a21\u578b\uff1a{model.ModelName}";
        return Task.CompletedTask;
    }

    private Task RemoveChatAttachmentAsync(ChatAttachmentViewModel? attachment)
    {
        if (attachment is not null)
        {
            PendingChatAttachments.Remove(attachment);
            NotifyChatCollectionsChanged();
        }

        return Task.CompletedTask;
    }

    private Task CopyChatCodeBlockAsync(ChatContentBlockViewModel? block)
    {
        if (block?.IsCode == true)
        {
            Clipboard.SetText(block.Content);
            ChatStatusMessage = "\u4ee3\u7801\u5757\u5df2\u590d\u5236\u3002";
        }

        return Task.CompletedTask;
    }

    private Task CopyChatMessageAsync(ChatMessageViewModel? message)
    {
        if (message?.CanCopy == true)
        {
            Clipboard.SetText(message.CopyText);
            ChatStatusMessage = "\u6574\u6761\u6d88\u606f\u5df2\u590d\u5236\u3002";
        }

        return Task.CompletedTask;
    }

    private Task CopyChatModelAnswerAsync(ChatModelAnswerViewModel? answer)
    {
        if (answer?.CanCopy == true)
        {
            Clipboard.SetText(answer.CopyText);
            ChatStatusMessage = $"\u5df2\u590d\u5236 {answer.ModelName} \u7684\u56de\u7b54\u3002";
        }

        return Task.CompletedTask;
    }

    private ChatRequestOptions BuildChatRequestOptions(string? modelName = null)
    {
        var temperature = double.TryParse(ChatTemperatureText, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedTemperature)
            ? parsedTemperature
            : 0.7d;
        var maxTokens = int.TryParse(ChatMaxTokensText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedMaxTokens)
            ? parsedMaxTokens
            : 2048;
        var reasoningEffort = ParseChatReasoningEffort(SelectedChatReasoningEffortKey);
        return new ChatRequestOptions(
            ProxyBaseUrl,
            ProxyApiKey,
            string.IsNullOrWhiteSpace(modelName) ? ProxyModel : modelName.Trim(),
            ChatSystemPrompt,
            temperature,
            maxTokens,
            ProxyIgnoreTlsErrors,
            int.TryParse(ProxyTimeoutSecondsText, out var timeout) ? timeout : 60,
            reasoningEffort,
            reasoningEffort is not ChatReasoningEffort.Auto);
    }

    private async Task<ChatRequestOptions> BuildChatRequestOptionsAsync(
        string? modelName,
        CancellationToken cancellationToken)
    {
        var options = BuildChatRequestOptions(modelName);
        try
        {
            var preferredWireApi = await _proxyEndpointModelCacheService.TryResolvePreferredWireApiAsync(
                options.BaseUrl,
                options.ApiKey,
                options.Model,
                cancellationToken);
            return string.IsNullOrWhiteSpace(preferredWireApi)
                ? options
                : options with { PreferredWireApi = preferredWireApi };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppDiagnosticLog.Write("ModelChat.ResolvePreferredWireApi", ex);
            return options;
        }
    }

    private bool TryAddChatSelectedModel(string? model, out string message)
    {
        var normalized = model?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalized))
        {
            message = "\u8bf7\u5148\u9009\u62e9\u6216\u586b\u5199\u6a21\u578b\u3002";
            return false;
        }

        if (ChatSelectedModels.Any(item => string.Equals(item.ModelName, normalized, StringComparison.OrdinalIgnoreCase)))
        {
            message = "\u8be5\u6a21\u578b\u5df2\u5728\u5bf9\u6bd4\u5217\u8868\u4e2d\u3002";
            return false;
        }

        if (ChatSelectedModels.Count >= 4)
        {
            message = "\u4e00\u6b21\u6700\u591a\u5bf9\u6bd4 4 \u4e2a\u6a21\u578b\u3002";
            return false;
        }

        ChatSelectedModels.Add(new ChatModelSelectionViewModel(ChatSelectedModels.Count + 1, normalized));
        NotifyChatSelectedModelsChanged();
        message = $"\u5df2\u52a0\u5165\u5bf9\u6bd4\u6a21\u578b\uff1a{normalized}";
        return true;
    }

    private string[] GetChatTargetModels()
    {
        var selectedModels = ChatSelectedModels
            .Select(static item => item.ModelName)
            .Where(static item => !string.IsNullOrWhiteSpace(item))
            .Select(static item => item.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(4)
            .ToArray();

        if (selectedModels.Length > 0)
        {
            return selectedModels;
        }

        return string.IsNullOrWhiteSpace(ProxyModel)
            ? []
            : [ProxyModel.Trim()];
    }

    private void RefreshChatSelectedModelOrdinals()
    {
        for (var i = 0; i < ChatSelectedModels.Count; i++)
        {
            ChatSelectedModels[i].Ordinal = i + 1;
        }
    }

    private void NotifyChatSelectedModelsChanged()
    {
        OnPropertyChanged(nameof(HasChatSelectedModels));
        OnPropertyChanged(nameof(ChatSelectedModelsSummary));
        OnPropertyChanged(nameof(ChatModeSummary));
    }

    private static string BuildMultiChatMetricsSummary(IEnumerable<ChatModelAnswerViewModel> answers)
    {
        var parts = answers.Select(answer =>
        {
            if (answer.Metrics is null)
            {
                return answer.HasError ? $"{answer.ModelName}: \u5931\u8d25" : $"{answer.ModelName}: --";
            }

            return $"{answer.ModelName}: {answer.Metrics.Elapsed.TotalMilliseconds:F0} ms";
        });
        return string.Join(" | ", parts);
    }

    private void ApplyChatMetrics(ChatMessageMetrics? metrics)
    {
        if (metrics is null)
        {
            return;
        }

        var ttft = metrics.FirstTokenLatency is null
            ? "TTFT --"
            : $"TTFT {metrics.FirstTokenLatency.Value.TotalMilliseconds:F0} ms";
        var speed = metrics.CharactersPerSecond is null
            ? "-- chars/s"
            : $"{metrics.CharactersPerSecond.Value:F1} chars/s";
        ChatMetricsSummary = $"{metrics.WireApi} | {metrics.Elapsed.TotalMilliseconds:F0} ms | {ttft} | {speed}";
    }

    private void LoadChatSession()
    {
        _chatSessionsDocument = _chatSessionStore.LoadDocument();
        EnsureChatDocumentDefaults();
        ChatSessions.Clear();
        foreach (var session in _chatSessionsDocument.Sessions
                     .OrderByDescending(static session => session.UpdatedAt))
        {
            ChatSessions.Add(ToChatSessionListItem(session));
        }

        ChatPresets.Clear();
        foreach (var preset in _chatSessionsDocument.Presets)
        {
            ChatPresets.Add(ToChatPresetViewModel(preset));
        }

        SelectedChatPreset = ChatPresets.FirstOrDefault();
        var activeSessionId = string.IsNullOrWhiteSpace(_chatSessionsDocument.ActiveSessionId)
            ? _chatSessionsDocument.Sessions.First().SessionId
            : _chatSessionsDocument.ActiveSessionId;
        var activeItem = ChatSessions.FirstOrDefault(item => string.Equals(item.SessionId, activeSessionId, StringComparison.Ordinal)) ??
            ChatSessions.First();
        _activeChatSessionId = activeItem.SessionId;
        LoadChatSession(activeItem.SessionId);
        SelectChatSession(activeItem);
        OnPropertyChanged(nameof(HasChatSessions));
        DeleteChatSessionCommand.RaiseCanExecuteChanged();
    }

    private void LoadChatSession(string sessionId)
    {
        var snapshot = _chatSessionsDocument.Sessions.FirstOrDefault(session => string.Equals(session.SessionId, sessionId, StringComparison.Ordinal)) ??
            _chatSessionsDocument.Sessions.First();
        _isLoadingChatSession = true;
        _chatSystemPrompt = snapshot.SystemPrompt ?? string.Empty;
        _chatTemperatureText = string.IsNullOrWhiteSpace(snapshot.TemperatureText) ? "0.7" : snapshot.TemperatureText;
        _chatMaxTokensText = string.IsNullOrWhiteSpace(snapshot.MaxTokensText) ? "2048" : snapshot.MaxTokensText;
        _selectedChatReasoningEffortKey = NormalizeChatReasoningEffortKey(snapshot.ReasoningEffortKey);
        _chatCandidateModel = string.Empty;
        ClearChatEditingState();
        PendingChatAttachments.Clear();
        ChatMessages.Clear();
        foreach (var message in snapshot.Messages.TakeLast(80))
        {
            ChatMessages.Add(new ChatMessageViewModel(message));
        }

        ChatSelectedModels.Clear();
        foreach (var model in snapshot.SelectedModels.Take(4))
        {
            TryAddChatSelectedModel(model, out _);
        }

        RefreshChatReasoningEffortSummary();
        NotifyChatCollectionsChanged();
        NotifyChatSelectedModelsChanged();
        OnPropertyChanged(nameof(ChatSystemPrompt));
        OnPropertyChanged(nameof(ChatTemperatureText));
        OnPropertyChanged(nameof(ChatMaxTokensText));
        OnPropertyChanged(nameof(SelectedChatReasoningEffortKey));
        OnPropertyChanged(nameof(ChatCandidateModel));
        _isLoadingChatSession = false;
    }

    private void SaveChatSession()
    {
        if (_isLoadingChatSession)
        {
            return;
        }

        var session = GetActiveChatSession();
        session.SystemPrompt = ChatSystemPrompt;
        session.TemperatureText = ChatTemperatureText;
        session.MaxTokensText = ChatMaxTokensText;
        session.ReasoningEffortKey = SelectedChatReasoningEffortKey;
        session.SelectedModels = ChatSelectedModels.Select(static model => model.ModelName).ToList();
        session.Messages = ChatMessages.Select(static message => message.ToCore()).TakeLast(80).ToList();
        session.UpdatedAt = DateTimeOffset.Now;
        session.Title = BuildChatSessionTitle(session);
        _chatSessionsDocument.ActiveSessionId = session.SessionId;
        UpsertChatSessionListItem(session);
        SaveChatDocument();
    }

    private void SaveChatDocument()
    {
        _chatSessionsDocument.Presets = ChatPresets.Select(ToChatPresetSnapshot).ToList();
        _chatSessionStore.SaveDocument(_chatSessionsDocument);
    }

    private void EnsureChatDocumentDefaults()
    {
        if (_chatSessionsDocument.Sessions.Count == 0)
        {
            _chatSessionsDocument.Sessions.Add(CreateEmptyChatSession("\u65b0\u4f1a\u8bdd"));
        }

        foreach (var session in _chatSessionsDocument.Sessions)
        {
            if (string.IsNullOrWhiteSpace(session.SessionId))
            {
                session.SessionId = Guid.NewGuid().ToString("N");
            }

            if (session.UpdatedAt == default)
            {
                session.UpdatedAt = DateTimeOffset.Now;
            }

            session.Title = BuildChatSessionTitle(session);
        }

        EnsureBuiltInChatPresets();
        if (string.IsNullOrWhiteSpace(_chatSessionsDocument.ActiveSessionId) ||
            _chatSessionsDocument.Sessions.All(session => !string.Equals(session.SessionId, _chatSessionsDocument.ActiveSessionId, StringComparison.Ordinal)))
        {
            _chatSessionsDocument.ActiveSessionId = _chatSessionsDocument.Sessions[0].SessionId;
        }
    }

    private void EnsureBuiltInChatPresets()
    {
        var builtIns = new[]
        {
            new ChatPresetSnapshot
            {
                PresetId = "builtin-general",
                Name = "\u901a\u7528\u5bf9\u8bdd",
                SystemPrompt = string.Empty,
                TemperatureText = "0.7",
                MaxTokensText = "2048",
                ReasoningEffortKey = "auto",
                IsBuiltIn = true
            },
            new ChatPresetSnapshot
            {
                PresetId = "builtin-code",
                Name = "\u4ee3\u7801\u52a9\u624b",
                SystemPrompt = "\u4f60\u662f\u4e25\u8c28\u7684\u4ee3\u7801\u52a9\u624b\u3002\u4f18\u5148\u6307\u51fa\u98ce\u9669\u3001\u7ed9\u51fa\u53ef\u6267\u884c\u4fee\u6539\uff0c\u5e76\u4fdd\u6301\u56de\u7b54\u7b80\u6d01\u3002",
                TemperatureText = "0.3",
                MaxTokensText = "4096",
                ReasoningEffortKey = "medium",
                IsBuiltIn = true
            },
            new ChatPresetSnapshot
            {
                PresetId = "builtin-review",
                Name = "\u4ee3\u7801\u5ba1\u67e5",
                SystemPrompt = "\u4f60\u662f\u4ee3\u7801\u5ba1\u67e5\u5458\u3002\u6309\u4e25\u91cd\u7a0b\u5ea6\u5217\u51fa\u95ee\u9898\uff0c\u5f15\u7528\u6587\u4ef6\u6216\u4ee3\u7801\u4f4d\u7f6e\uff0c\u5e76\u8bf4\u660e\u53ef\u9a8c\u8bc1\u7684\u4fee\u590d\u5efa\u8bae\u3002",
                TemperatureText = "0.2",
                MaxTokensText = "4096",
                ReasoningEffortKey = "high",
                IsBuiltIn = true
            },
            new ChatPresetSnapshot
            {
                PresetId = "builtin-summary",
                Name = "\u603b\u7ed3\u6574\u7406",
                SystemPrompt = "\u8bf7\u628a\u8f93\u5165\u5185\u5bb9\u6574\u7406\u4e3a\u7ed3\u6784\u6e05\u6670\u3001\u91cd\u70b9\u660e\u786e\u7684\u4e2d\u6587\u6458\u8981\uff0c\u4fdd\u7559\u5173\u952e\u6570\u5b57\u3001\u7ed3\u8bba\u548c\u540e\u7eed\u52a8\u4f5c\u3002",
                TemperatureText = "0.5",
                MaxTokensText = "2048",
                ReasoningEffortKey = "low",
                IsBuiltIn = true
            }
        };

        foreach (var preset in builtIns)
        {
            var existingIndex = _chatSessionsDocument.Presets.FindIndex(item => string.Equals(item.PresetId, preset.PresetId, StringComparison.Ordinal));
            if (existingIndex >= 0)
            {
                _chatSessionsDocument.Presets[existingIndex] = preset;
            }
            else
            {
                _chatSessionsDocument.Presets.Insert(0, preset);
            }
        }
    }

    private ChatSessionSnapshot GetActiveChatSession()
    {
        var activeId = string.IsNullOrWhiteSpace(_activeChatSessionId)
            ? _chatSessionsDocument.ActiveSessionId
            : _activeChatSessionId;
        var session = _chatSessionsDocument.Sessions.FirstOrDefault(item => string.Equals(item.SessionId, activeId, StringComparison.Ordinal));
        if (session is not null)
        {
            return session;
        }

        session = CreateEmptyChatSession("\u65b0\u4f1a\u8bdd");
        _chatSessionsDocument.Sessions.Insert(0, session);
        _chatSessionsDocument.ActiveSessionId = session.SessionId;
        return session;
    }

    private static ChatSessionSnapshot CreateEmptyChatSession(string title)
        => new()
        {
            SessionId = Guid.NewGuid().ToString("N"),
            Title = title,
            UpdatedAt = DateTimeOffset.Now
        };

    private static ChatSessionListItemViewModel ToChatSessionListItem(ChatSessionSnapshot session)
        => new(
            session.SessionId,
            BuildChatSessionTitle(session),
            session.UpdatedAt,
            session.Messages.Count,
            !string.IsNullOrWhiteSpace(session.ManualTitle));

    private static ChatPromptPresetViewModel ToChatPresetViewModel(ChatPresetSnapshot preset)
        => new(
            preset.PresetId,
            preset.Name,
            preset.SystemPrompt,
            string.IsNullOrWhiteSpace(preset.TemperatureText) ? "0.7" : preset.TemperatureText,
            string.IsNullOrWhiteSpace(preset.MaxTokensText) ? "2048" : preset.MaxTokensText,
            NormalizeChatReasoningEffortKey(preset.ReasoningEffortKey),
            preset.IsBuiltIn);

    private static ChatPresetSnapshot ToChatPresetSnapshot(ChatPromptPresetViewModel preset)
        => new()
        {
            PresetId = preset.PresetId,
            Name = preset.Name,
            SystemPrompt = preset.SystemPrompt,
            TemperatureText = preset.TemperatureText,
            MaxTokensText = preset.MaxTokensText,
            ReasoningEffortKey = NormalizeChatReasoningEffortKey(preset.ReasoningEffortKey),
            IsBuiltIn = preset.IsBuiltIn
        };

    private static string BuildChatSessionTitle(ChatSessionSnapshot session)
    {
        if (!string.IsNullOrWhiteSpace(session.ManualTitle))
        {
            return TrimSessionTitle(session.ManualTitle);
        }

        var firstUserMessage = session.Messages.FirstOrDefault(static message => string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(firstUserMessage?.Content))
        {
            return TrimSessionTitle(firstUserMessage.Content);
        }

        return string.IsNullOrWhiteSpace(session.Title) ? "\u65b0\u4f1a\u8bdd" : TrimSessionTitle(session.Title);
    }

    private static string TrimSessionTitle(string value)
    {
        var normalized = string.Join(' ', value.Trim().Split(default(string[]), StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length <= 24 ? normalized : normalized[..24] + "...";
    }

    private void UpsertChatSessionListItem(ChatSessionSnapshot session)
    {
        var item = ChatSessions.FirstOrDefault(entry => string.Equals(entry.SessionId, session.SessionId, StringComparison.Ordinal));
        if (item is null)
        {
            item = ToChatSessionListItem(session);
            ChatSessions.Insert(0, item);
        }
        else
        {
            item.Title = BuildChatSessionTitle(session);
            item.UpdatedAt = session.UpdatedAt;
            item.MessageCount = session.Messages.Count;
            item.IsManualTitle = !string.IsNullOrWhiteSpace(session.ManualTitle);
        }

        OnPropertyChanged(nameof(HasChatSessions));
        DeleteChatSessionCommand.RaiseCanExecuteChanged();
    }

    private void SelectChatSession(ChatSessionListItemViewModel item)
    {
        _isLoadingChatSession = true;
        SelectedChatSession = item;
        _isLoadingChatSession = false;
        _activeChatSessionId = item.SessionId;
        _chatSessionsDocument.ActiveSessionId = item.SessionId;
    }

    private void SwitchChatSession(string sessionId)
    {
        if (string.Equals(_activeChatSessionId, sessionId, StringComparison.Ordinal))
        {
            return;
        }

        SaveChatSession();
        _activeChatSessionId = sessionId;
        _chatSessionsDocument.ActiveSessionId = sessionId;
        LoadChatSession(sessionId);
        var item = ChatSessions.FirstOrDefault(entry => string.Equals(entry.SessionId, sessionId, StringComparison.Ordinal));
        if (item is not null)
        {
            SelectChatSession(item);
        }

        SaveChatDocument();
    }

    private void RefreshChatReasoningEffortSummary()
    {
        ChatReasoningEffortSummary = ParseChatReasoningEffort(SelectedChatReasoningEffortKey) is ChatReasoningEffort.Auto
            ? "\u81ea\u52a8\u6a21\u5f0f\u4e0b\u9ed8\u8ba4\u4e0d\u53d1\u9001 reasoning \u53c2\u6570\u3002"
            : "\u5c06\u4f18\u5148\u4f7f\u7528 Responses API \u53d1\u9001 reasoning.effort\uff1b\u5982\u63a5\u53e3\u4e0d\u652f\u6301\uff0c\u4f1a\u8fd4\u56de\u660e\u786e\u9519\u8bef\u5f52\u56e0\u3002";
    }

    private string BuildModelChatReportSection()
    {
        var lastAssistant = ChatMessages.LastOrDefault(static message => message.IsAssistant);
        var lastUser = ChatMessages.LastOrDefault(static message => message.IsUser);
        var lastAssistantCore = lastAssistant?.ToCore();
        var lastAssistantContent = lastAssistantCore is null || string.IsNullOrWhiteSpace(lastAssistantCore.Content)
            ? "\u6682\u65e0\u52a9\u624b\u56de\u7b54\u3002"
            : TrimReportPreview(lastAssistantCore.Content);
        var lastUserContent = lastUser is null || string.IsNullOrWhiteSpace(lastUser.Content)
            ? "\u6682\u65e0\u7528\u6237\u8f93\u5165\u3002"
            : TrimReportPreview(lastUser.Content);

        return
            $"\u63a5\u53e3\u5730\u5740\uff1a{ProxyBaseUrl}\n" +
            $"\u6a21\u578b\uff1a{ProxyModel}\n" +
            $"\u6307\u6807\uff1a{ChatMetricsSummary}\n" +
            $"\u72b6\u6001\uff1a{ChatStatusMessage}\n\n" +
            $"\u6700\u8fd1\u7528\u6237\u8f93\u5165\uff1a\n{lastUserContent}\n\n" +
            $"\u6700\u8fd1\u52a9\u624b\u56de\u7b54\uff1a\n{lastAssistantContent}";
    }

    private static string TrimReportPreview(string value)
    {
        var normalized = value.Trim();
        return normalized.Length <= 2000 ? normalized : normalized[..2000] + "\n...";
    }

    private void NotifyChatCollectionsChanged()
    {
        OnPropertyChanged(nameof(HasChatMessages));
        OnPropertyChanged(nameof(HasPendingChatAttachments));
        SendChatMessageCommand.RaiseCanExecuteChanged();
        RegenerateLastChatAnswerCommand.RaiseCanExecuteChanged();
        ExportChatSessionMarkdownCommand.RaiseCanExecuteChanged();
        ExportChatSessionTextCommand.RaiseCanExecuteChanged();
    }

    private int FindLastUserMessageIndex()
    {
        for (var index = ChatMessages.Count - 1; index >= 0; index--)
        {
            if (ChatMessages[index].IsUser)
            {
                return index;
            }
        }

        return -1;
    }

    private string GetCurrentChatSessionTitle()
        => SelectedChatSession?.Title ?? BuildChatSessionTitle(GetActiveChatSession());

    private void RemoveChatMessagesFromEditingPoint()
    {
        if (string.IsNullOrWhiteSpace(_chatEditingMessageId))
        {
            return;
        }

        var index = ChatMessages
            .Select((message, i) => new { message, i })
            .FirstOrDefault(item => string.Equals(item.message.Id, _chatEditingMessageId, StringComparison.Ordinal))
            ?.i;
        if (index is null)
        {
            ClearChatEditingState();
            return;
        }

        for (var i = ChatMessages.Count - 1; i >= index.Value; i--)
        {
            ChatMessages.RemoveAt(i);
        }
    }

    private void ClearChatEditingState()
    {
        if (string.IsNullOrWhiteSpace(_chatEditingMessageId))
        {
            return;
        }

        _chatEditingMessageId = null;
        NotifyChatEditingChanged();
    }

    private void NotifyChatEditingChanged()
    {
        OnPropertyChanged(nameof(IsEditingChatMessage));
        OnPropertyChanged(nameof(ChatSendButtonText));
        OnPropertyChanged(nameof(ChatEditStatusText));
        CancelChatEditCommand.RaiseCanExecuteChanged();
    }

    private static string NormalizeChatReasoningEffortKey(string? value)
        => value switch
        {
            "low" => "low",
            "medium" => "medium",
            "high" => "high",
            _ => "auto"
        };

    private static ChatReasoningEffort ParseChatReasoningEffort(string? value)
        => NormalizeChatReasoningEffortKey(value) switch
        {
            "low" => ChatReasoningEffort.Low,
            "medium" => ChatReasoningEffort.Medium,
            "high" => ChatReasoningEffort.High,
            _ => ChatReasoningEffort.Auto
        };
}
