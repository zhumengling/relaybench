namespace RelayBench.App.ViewModels;

public sealed class HistoryRunListItemViewModel
{
    public HistoryRunListItemViewModel(
        int rank,
        DateTimeOffset timestamp,
        string rawCategory,
        string displayTitle,
        string scopeTag,
        string categoryTag,
        string timestampText,
        string previewText,
        string originalTitle,
        string detailText)
    {
        Rank = rank;
        Timestamp = timestamp;
        RawCategory = rawCategory;
        DisplayTitle = displayTitle;
        ScopeTag = scopeTag;
        CategoryTag = categoryTag;
        TimestampText = timestampText;
        PreviewText = previewText;
        OriginalTitle = originalTitle;
        DetailText = detailText;
    }

    public int Rank { get; }

    public DateTimeOffset Timestamp { get; }

    public string RawCategory { get; }

    public string DisplayTitle { get; }

    public string ScopeTag { get; }

    public string CategoryTag { get; }

    public string TimestampText { get; }

    public string PreviewText { get; }

    public string OriginalTitle { get; }

    public string DetailText { get; }
}
