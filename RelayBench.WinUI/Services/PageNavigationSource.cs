using RelayBench.WinUI.Pages;

namespace RelayBench.WinUI.Services;

/// <summary>
/// Command palette source that provides page navigation items.
/// </summary>
public sealed class PageNavigationSource : ICommandPaletteSource
{
    private readonly Action<Type> _navigate;

    private static readonly (string Title, Type PageType)[] Pages =
    [
        ("\u6982\u89c8", typeof(DashboardPage)),
        ("\u5355\u7ad9\u6d4b\u8bd5", typeof(SingleStationPage)),
        ("\u6570\u636e\u5b89\u5168", typeof(DataSafetyPage)),
        ("\u6279\u91cf\u8bc4\u6d4b", typeof(BatchComparisonPage)),
        ("\u900f\u660e\u4ee3\u7406", typeof(TransparentProxyPage)),
        ("\u5927\u6a21\u578b\u5bf9\u8bdd", typeof(ModelChatPage)),
        ("\u5e94\u7528\u63a5\u5165", typeof(ApplicationCenterPage)),
        ("\u7f51\u7edc\u590d\u6838", typeof(NetworkReviewPage)),
        ("\u5386\u53f2\u62a5\u544a", typeof(HistoryReportsPage)),
        ("\u8bbe\u7f6e", typeof(SettingsPage)),
    ];

    /// <summary>
    /// Creates a new <see cref="PageNavigationSource"/>.
    /// </summary>
    /// <param name="navigate">Callback that navigates to the specified page type.</param>
    public PageNavigationSource(Action<Type> navigate)
    {
        _navigate = navigate;
    }

    public IEnumerable<CommandPaletteItem> Query(string text)
    {
        foreach (var (title, pageType) in Pages)
        {
            if (string.IsNullOrEmpty(text) ||
                title.Contains(text, StringComparison.OrdinalIgnoreCase))
            {
                var pt = pageType;
                yield return new CommandPaletteItem(title, "\u9875\u9762", () => _navigate(pt));
            }
        }
    }
}
