namespace RelayBench.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private Task FetchDefaultProxyModelsWithGlobalProgressAsync()
        => FetchProxyModelsForTargetWithGlobalProgressAsync(
            ProxyModelPickerTarget.DefaultModel,
            "\u9ED8\u8BA4\u6A21\u578B\u5217\u8868");

    private Task FetchProxyBatchSharedModelsWithGlobalProgressAsync()
        => FetchProxyModelsForTargetWithGlobalProgressAsync(
            ProxyModelPickerTarget.BatchSharedModel,
            "\u7AD9\u70B9\u6A21\u578B\u5217\u8868");

    private Task FetchProxyBatchEntryModelsWithGlobalProgressAsync()
        => FetchProxyModelsForTargetWithGlobalProgressAsync(
            ProxyModelPickerTarget.BatchEntryModel,
            "\u5165\u53E3\u6A21\u578B\u5217\u8868");

    private Task FetchProxyCapabilityModelsWithGlobalProgressAsync(string? capabilityKey)
    {
        if (!TryParseCapabilityModelPickerTarget(capabilityKey, out var target))
        {
            StatusMessage = "\u672A\u8BC6\u522B\u8981\u56DE\u586B\u7684\u80FD\u529B\u6A21\u578B\u9879\u3002";
            return Task.CompletedTask;
        }

        return FetchProxyModelsForTargetWithGlobalProgressAsync(target, "\u80FD\u529B\u6A21\u578B\u5217\u8868");
    }

    private Task FetchProxyModelsForTargetWithGlobalProgressAsync(
        ProxyModelPickerTarget target,
        string progressTitle)
    {
        SetProxyModelPickerTarget(target);
        if (!TryBuildProxyModelCatalogSettings(target, out var settings, out var message))
        {
            StatusMessage = message;
            return Task.CompletedTask;
        }

        return ExecuteBusyActionAsync(
            "\u6B63\u5728\u62C9\u53D6\u63A5\u53E3\u6A21\u578B\u5217\u8868...",
            async () =>
            {
                UpdateGlobalTaskProgress("\u62C9\u53D6\u4E2D", 32d);
                await FetchProxyModelsCoreAsync(settings);
                UpdateGlobalTaskProgress("\u6574\u7406\u4E2D", 90d);
            },
            progressTitle,
            "\u62C9\u53D6\u4E2D",
            12d);
    }

    private Task RunSpeedTestWithGlobalProgressAsync()
        => ExecuteBusyActionAsync(
            "\u6B63\u5728\u8FD0\u884C Cloudflare \u98CE\u683C\u6D4B\u901F...",
            RunSpeedTestCoreAsync,
            "\u6D4B\u901F",
            "\u51C6\u5907\u4E2D",
            8d);

    private Task RunRouteWithGlobalProgressAsync()
        => ExecuteBusyActionAsync(
            "\u6B63\u5728\u8FD0\u884C\u5185\u7F6E MTR / tracert \u8DEF\u7531\u63A2\u6D4B...",
            RunRouteCoreAsync,
            "\u8DEF\u7531 / MTR",
            "\u51C6\u5907\u4E2D",
            6d);

    private Task RunRouteContinuousWithGlobalProgressAsync()
        => ExecuteBusyActionAsync(
            "\u6B63\u5728\u6309\u8BBE\u5B9A\u65F6\u957F\u6301\u7EED\u8FD0\u884C\u8DEF\u7531 / MTR...",
            RunRouteContinuousCoreAsync,
            "\u6301\u7EED\u8DEF\u7531 / MTR",
            "\u51C6\u5907\u4E2D",
            6d);

    private Task DetectPortScanEngineWithGlobalProgressAsync()
        => ExecuteBusyActionAsync(
            "\u6B63\u5728\u68C0\u6D4B\u672C\u5730\u7AEF\u53E3\u626B\u63CF\u5F15\u64CE...",
            DetectPortScanEngineCoreAsync,
            "\u7AEF\u53E3\u626B\u63CF\u5F15\u64CE",
            "\u68C0\u6D4B\u4E2D",
            20d);

    private Task RunPortScanWithGlobalProgressAsync()
        => ExecuteBusyActionAsync(
            "\u6B63\u5728\u8FD0\u884C\u672C\u5730\u7AEF\u53E3\u626B\u63CF...",
            RunPortScanCoreAsync,
            "\u7AEF\u53E3\u626B\u63CF",
            "\u51C6\u5907\u4E2D",
            8d);

    private Task RunPortScanBatchWithGlobalProgressAsync()
        => ExecuteBusyActionAsync(
            "\u6B63\u5728\u8FD0\u884C\u6279\u91CF\u7AEF\u53E3\u626B\u63CF...",
            RunPortScanBatchCoreAsync,
            "\u6279\u91CF\u7AEF\u53E3\u626B\u63CF",
            "\u51C6\u5907\u4E2D",
            6d);

    private Task RunSplitRoutingWithGlobalProgressAsync()
        => ExecuteBusyActionAsync(
            "\u6B63\u5728\u8FD0\u884C IP \u4E0E\u5206\u6D41\u8BCA\u65AD...",
            RunSplitRoutingCoreAsync,
            "IP / \u5206\u6D41",
            "\u51C6\u5907\u4E2D",
            8d);

    private Task RunIpRiskReviewWithGlobalProgressAsync()
        => ExecuteBusyActionAsync(
            "\u6B63\u5728\u8FD0\u884C当前出口 IP 风险复核...",
            RunIpRiskReviewCoreAsync,
            "IP \u98CE\u9669",
            "\u51C6\u5907\u4E2D",
            10d);
}
