using RelayBench.Core.Services;
using RelayBench.Core.Models;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using RelayBench.App.Services;
using RelayBench.App.ViewModels;
using static RelayBench.Core.Tests.TestSupport;

namespace RelayBench.Core.Tests;

internal static class UiWorkflowTests
{
    public static IEnumerable<TestCase> Create()
    {
        yield return new TestCase("main window keeps model pickers above batch editor overlay", () =>
    {
        var zIndexes = ReadMainWindowOverlayZIndexes();

        AssertTrue(
            zIndexes["ProxyModelPickerOverlay"] > zIndexes["ProxyBatchEditorOverlay"],
            "Single-model picker must render above the batch editor overlay.");
        AssertTrue(
            zIndexes["ProxyMultiModelPickerOverlay"] > zIndexes["ProxyBatchEditorOverlay"],
            "Multi-model picker must render above the batch editor overlay.");
        AssertTrue(
            zIndexes["ConfirmationDialogOverlay"] > zIndexes["ProxyModelPickerOverlay"],
            "Confirmation dialogs must stay above model picker overlays.");
        });

        yield return new TestCase("main window overlays use the shared dashboard panel style", () =>
    {
        var styles = ReadMainWindowOverlayPanelStyles();

        AssertTrue(styles.Count >= 8, $"Expected main window overlay panel styles to be discovered, got {styles.Count}.");
        foreach (var (panelName, styleName) in styles)
        {
            AssertEqual(styleName, "DashboardOverlayPanelStyle");
            AssertTrue(panelName.EndsWith("OverlayPanel", StringComparison.Ordinal), $"{panelName} should be an overlay panel.");
        }
        });

        yield return new TestCase("batch template toggle switches all entry test flags", () =>
    {
        var viewModel = new MainWindowViewModel();
        viewModel.ProxyBatchTemplateDraftItems.Clear();
        viewModel.ProxyBatchTemplateDraftItems.Add(CreateBatchTemplateRow("入口 1", "https://one.example.com/v1", includeInBatchTest: true));
        viewModel.ProxyBatchTemplateDraftItems.Add(CreateBatchTemplateRow("入口 2", "https://two.example.com/v1", includeInBatchTest: false));

        AssertEqual(viewModel.ProxyBatchTemplateToggleAllTestText, "全部加入");
        viewModel.ToggleProxyBatchTemplateRowsTestInclusionCommand.Execute(null);
        AssertTrue(
            viewModel.ProxyBatchTemplateDraftItems.All(item => item.IncludeInBatchTest),
            "All template rows should be included after toggling to include.");

        AssertEqual(viewModel.ProxyBatchTemplateToggleAllTestText, "全部关闭");
        viewModel.ToggleProxyBatchTemplateRowsTestInclusionCommand.Execute(null);
        AssertTrue(
            viewModel.ProxyBatchTemplateDraftItems.All(item => !item.IncludeInBatchTest),
            "All template rows should be skipped after toggling to close.");
        });

        yield return new TestCase("chat settings panel can be closed by the shared close command", () =>
    {
        var viewModel = new MainWindowViewModel();

        AssertFalse(viewModel.IsChatSettingsPanelOpen, "Chat settings panel should start closed.");
        viewModel.ToggleChatSettingsPanelCommand.Execute(null);
        AssertTrue(viewModel.IsChatSettingsPanelOpen, "Toggle should open the chat settings panel.");
        viewModel.CloseChatSettingsPanelCommand.Execute(null);
        AssertFalse(viewModel.IsChatSettingsPanelOpen, "Close command should close the chat settings panel.");
        });

        yield return new TestCase("chat multi model selector caps duplicates and refreshes ordinals", () =>
    {
        var viewModel = new MainWindowViewModel();
        viewModel.ClearChatSelectedModelsCommand.Execute(null);

        foreach (var model in new[] { "model-a", "model-b", "model-c", "model-d" })
        {
            viewModel.ChatCandidateModel = model;
            viewModel.AddChatSelectedModelCommand.Execute(null);
        }

        viewModel.ChatCandidateModel = "model-d";
        viewModel.AddChatSelectedModelCommand.Execute(null);
        viewModel.ChatCandidateModel = "model-e";
        viewModel.AddChatSelectedModelCommand.Execute(null);

        AssertTrue(viewModel.ChatSelectedModels.Count == 4, $"Expected cap of 4 models, got {viewModel.ChatSelectedModels.Count}.");
        AssertContains(viewModel.ChatSelectedModelsSummary, "4");
        AssertContains(viewModel.ChatModeSummary, "4");

        var second = viewModel.ChatSelectedModels[1];
        viewModel.RemoveChatSelectedModelCommand.Execute(second);
        AssertTrue(viewModel.ChatSelectedModels.Count == 3, $"Expected 3 models after remove, got {viewModel.ChatSelectedModels.Count}.");
        AssertTrue(
            viewModel.ChatSelectedModels.Select(static item => item.Ordinal).SequenceEqual([1, 2, 3]),
            "Model ordinals should be compact after removing a middle item.");
        });

        yield return new TestCase("model chat page exposes upgraded chat workflow controls", () =>
    {
        var xamlPath = Path.Combine(FindRepositoryRoot(), "RelayBench.App", "Views", "Pages", "ModelChatPage.xaml");
        var themePath = Path.Combine(FindRepositoryRoot(), "RelayBench.App", "Resources", "WorkbenchTheme.xaml");
        var markdownViewerPath = Path.Combine(FindRepositoryRoot(), "RelayBench.App", "Controls", "MarkdownViewer.cs");
        var xaml = File.ReadAllText(xamlPath);
        var theme = File.ReadAllText(themePath);
        var markdownViewer = File.ReadAllText(markdownViewerPath);

        AssertFalse(xaml.Contains("ChatSessionSearchText", StringComparison.Ordinal), "Model chat page should not expose the removed session search box.");
        AssertFalse(xaml.Contains("VisibleChatSessions", StringComparison.Ordinal), "Model chat session list should bind directly to ChatSessions.");
        AssertFalse(xaml.Contains("Grid.Column=\"4\" Style=\"{StaticResource SectionPanelBorderStyle}\"", StringComparison.Ordinal), "Request status should live in the settings drawer instead of a permanent right column.");
        AssertFalse(xaml.Contains("Grid.Column=\"{Binding BubbleColumn}\"", StringComparison.Ordinal), "Chat bubbles should align inside the wide conversation surface instead of splitting the center into two fixed halves.");
        AssertContains(xaml, "ItemsSource=\"{Binding ChatSessions}\"");
        AssertContains(xaml, "ChatRecordToolButtonStyle");
        AssertContains(theme, "WorkbenchIconButtonStyle");
        AssertContains(xaml, "ChatMessageBubbleBorderStyle");
        AssertContains(xaml, "Width=\"{Binding BubbleWidth}\"");
        AssertContains(xaml, "Margin=\"{Binding BubbleOuterMargin}\"");
        AssertFalse(xaml.Contains("<Setter Property=\"MinWidth\" Value=\"220\" />", StringComparison.Ordinal), "Standard chat bubbles should shrink below the old fixed 220px floor.");
        AssertContains(xaml, "Margin=\"0,0,18,0\"");
        AssertContains(markdownViewer, "PreviewMouseWheel += ForwardMouseWheelToParentScrollViewer;");
        AssertContains(markdownViewer, "RaiseEvent(new MouseWheelEventArgs");
        AssertContains(xaml, "<ColumnDefinition Width=\"188\" />");
        AssertContains(xaml, "Content=\"&#xE713;\"");
        AssertContains(xaml, "Content=\"&#xE710;\"");
        AssertContains(xaml, "Content=\"&#xE74D;\"");
        AssertContains(xaml, "Content=\"&#xE8C8;\"");
        AssertContains(xaml, "Content=\"&#xE72C;\"");
        AssertContains(xaml, "Text=\"{Binding DisplayRole}\"");
        AssertContains(xaml, "RegenerateLastChatAnswerCommand");
        AssertContains(xaml, "CopyChatMessageCommand");
        AssertContains(xaml, "CopyChatModelAnswerCommand");
        AssertContains(xaml, "ExportChatSessionMarkdownCommand");
        AssertContains(xaml, "ExportChatSessionTextCommand");
        AssertContains(xaml, "ChatInputDropZone_Drop");
        AssertContains(xaml, "StopChatStreamingCommand");
        });

        yield return new TestCase("core workflow pages share icon buttons drawers and motion styles", () =>
        {
            var root = FindRepositoryRoot();
            var single = File.ReadAllText(Path.Combine(root, "RelayBench.App", "Views", "Pages", "SingleStationPage.xaml"));
            var batch = File.ReadAllText(Path.Combine(root, "RelayBench.App", "Views", "Pages", "BatchComparisonPage.xaml"));
            var application = File.ReadAllText(Path.Combine(root, "RelayBench.App", "Views", "Pages", "ApplicationCenterPage.xaml"));

            AssertContains(single, "SingleStationToolbarIconButtonStyle");
            AssertContains(single, "SingleStationMotionInsetPanelStyle");
            AssertContains(single, "Style=\"{StaticResource WorkbenchIconButtonPrimaryStyle}\"");
            AssertContains(single, "Command=\"{Binding RunSelectedSingleStationModeCommand}\"");
            AssertContains(single, "Content=\"&#xE768;\"");
            AssertContains(single, "Command=\"{Binding OpenProxyEndpointHistoryCommand}\"");
            AssertContains(single, "Content=\"&#xE81C;\"");

            AssertContains(batch, "BatchToolbarIconButtonStyle");
            AssertContains(batch, "BatchMotionInsetPanelStyle");
            AssertContains(batch, "Style=\"{StaticResource WorkbenchIconButtonPrimaryStyle}\"");
            AssertContains(batch, "Command=\"{Binding OpenProxyBatchEditorCommand}\"");
            AssertContains(batch, "Command=\"{Binding RunProxyBatchCommand}\"");
            AssertContains(batch, "Content=\"&#xE768;\"");

            AssertContains(application, "ApplicationCenterIconActionButtonStyle");
            AssertContains(application, "ApplicationCenterMotionInsetPanelStyle");
            AssertContains(application, "Style=\"{StaticResource WorkbenchIconButtonPrimaryStyle}\"");
            AssertContains(application, "Command=\"{Binding RunClientApiDiagnosticsCommand}\"");
            AssertContains(application, "Command=\"{Binding ApplyCurrentInterfaceToCodexAppsCommand}\"");
            AssertContains(application, "Content=\"&#xE768;\"");
        });

        yield return new TestCase("network review panes keep visible card separation", () =>
        {
            var root = FindRepositoryRoot();
            var xaml = File.ReadAllText(Path.Combine(root, "RelayBench.App", "Views", "Pages", "NetworkReviewPage.xaml"));

            AssertContains(xaml, "x:Key=\"ReviewPaneBorderStyle\"");
            AssertContains(xaml, "<Setter Property=\"Padding\" Value=\"12\" />");
            AssertContains(xaml, "<Setter Property=\"Background\" Value=\"#F8FBFF\" />");
            AssertContains(xaml, "<Setter Property=\"BorderBrush\" Value=\"#D8E3F2\" />");
            AssertContains(xaml, "<Setter Property=\"BorderThickness\" Value=\"1\" />");
            AssertContains(xaml, "<Border x:Name=\"TabContentBorder\"");
            AssertFalse(
                xaml.Contains("x:Key=\"ReviewPaneBorderStyle\"\r\n               TargetType=\"Border\">\r\n            <Setter Property=\"Padding\" Value=\"0\" />", StringComparison.Ordinal),
                "Network review pane cards must not be flattened into transparent containers.");
        });

        yield return new TestCase("icon buttons show immediate top chinese tooltips", () =>
        {
            var root = FindRepositoryRoot();
            var theme = File.ReadAllText(Path.Combine(root, "RelayBench.App", "Resources", "WorkbenchTheme.xaml"));

            AssertContains(theme, "ToolTipService.InitialShowDelay");
            AssertContains(theme, "ToolTipService.BetweenShowDelay");
            AssertContains(theme, "ToolTipService.Placement");
            AssertContains(theme, "Value=\"Top\"");
            AssertContains(theme, "ToolTipService.ShowDuration");
            AssertContains(theme, "TargetType=\"ToolTip\"");
            AssertContains(theme, "FontFamily\" Value=\"Microsoft YaHei UI, Segoe UI\"");
            AssertContains(theme, "ToolTipContentPresenter");
            AssertContains(theme, "Text=\"{Binding Content, RelativeSource={RelativeSource TemplatedParent}}\"");
            AssertContains(theme, "Foreground=\"{TemplateBinding Foreground}\"");
            AssertContains(theme, "x:Key=\"WorkbenchIconButtonStyle\"");
            AssertContains(theme, "IconButtonGlyphTextBlock");
            AssertFalse(
                theme.Contains("<Setter Property=\"FontFamily\" Value=\"Segoe MDL2 Assets\" />", StringComparison.Ordinal),
                "Icon buttons must render only the glyph content with Segoe MDL2 Assets so generated tooltips do not inherit the icon font.");
        });
    }
}
