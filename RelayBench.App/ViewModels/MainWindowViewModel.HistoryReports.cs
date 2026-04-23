using System.Collections.ObjectModel;
using System.Text;
using RelayBench.App.Infrastructure;

namespace RelayBench.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private readonly record struct HistoryRunIdentity(
        DateTimeOffset Timestamp,
        string Category,
        string Title);

    private HistoryRunListItemViewModel? _selectedHistoryRunItem;

    public ObservableCollection<HistoryRunListItemViewModel> HistoryRunItems { get; } = [];

    public HistoryRunListItemViewModel? SelectedHistoryRunItem
    {
        get => _selectedHistoryRunItem;
        set
        {
            if (SetProperty(ref _selectedHistoryRunItem, value))
            {
                OnPropertyChanged(nameof(SelectedHistoryRunTitle));
                OnPropertyChanged(nameof(SelectedHistoryRunMeta));
                OnPropertyChanged(nameof(SelectedHistoryRunOriginalTitle));
                OnPropertyChanged(nameof(SelectedHistoryRunPreviewText));
                OnPropertyChanged(nameof(SelectedHistoryRunDetailText));
            }
        }
    }

    public bool HasHistoryRunItems => HistoryRunItems.Count > 0;

    public string SelectedHistoryRunTitle
        => SelectedHistoryRunItem?.DisplayTitle ?? "\u8FD8\u672A\u9009\u62E9\u8FD0\u884C\u5386\u53F2";

    public string SelectedHistoryRunMeta
        => SelectedHistoryRunItem is null
            ? "\u70B9\u51FB\u5DE6\u4FA7\u4EFB\u610F\u4E00\u6761\u8FD0\u884C\u5386\u53F2\uFF0C\u53F3\u4FA7\u4F1A\u663E\u793A\u8FD9\u6B21\u8FD0\u884C\u7684\u5B8C\u6574\u4FE1\u606F\u3002"
            : $"\u6267\u884C\u65F6\u95F4\uFF1A{SelectedHistoryRunItem.TimestampText}  |  \u8303\u56F4\uFF1A{SelectedHistoryRunItem.ScopeTag}  |  \u5206\u7C7B\uFF1A{SelectedHistoryRunItem.CategoryTag}";

    public string SelectedHistoryRunOriginalTitle
        => SelectedHistoryRunItem is null
            ? "\u5F53\u524D\u6CA1\u6709\u53EF\u663E\u793A\u7684\u539F\u59CB\u8BB0\u5F55\u540D\u3002"
            : $"\u539F\u59CB\u8BB0\u5F55\uFF1A{SelectedHistoryRunItem.OriginalTitle}";

    public string SelectedHistoryRunPreviewText
        => SelectedHistoryRunItem?.PreviewText ?? "\u8FD9\u91CC\u4F1A\u5148\u663E\u793A\u8FD9\u6B21\u8FD0\u884C\u7684\u7B80\u77ED\u9884\u89C8\u3002";

    public string SelectedHistoryRunDetailText
        => SelectedHistoryRunItem?.DetailText ?? "\u70B9\u51FB\u5DE6\u4FA7\u8FD0\u884C\u5386\u53F2\u540E\uFF0C\u8FD9\u91CC\u4F1A\u663E\u793A\u8BE6\u7EC6\u7ED3\u679C\u5168\u6587\u3002";

    private void RefreshHistoryRunItems()
    {
        HistoryRunIdentity? previousSelection = SelectedHistoryRunItem is null
            ? null
            : new HistoryRunIdentity(
                SelectedHistoryRunItem.Timestamp,
                SelectedHistoryRunItem.RawCategory,
                SelectedHistoryRunItem.OriginalTitle);

        var items = _historyEntries
            .Select((entry, index) => BuildHistoryRunListItem(entry, index + 1))
            .ToArray();

        HistoryRunItems.Clear();
        foreach (var item in items)
        {
            HistoryRunItems.Add(item);
        }

        SelectedHistoryRunItem = items.Length == 0
            ? null
            : TryFindHistoryRunItem(items, previousSelection) ?? items[0];

        OnPropertyChanged(nameof(HasHistoryRunItems));
    }

    private static HistoryRunListItemViewModel BuildHistoryRunListItem(RunHistoryEntry entry, int rank)
        => new(
            rank,
            entry.Timestamp,
            entry.Category,
            ResolveHistoryDisplayTitle(entry),
            ResolveHistoryScopeTag(entry),
            ResolveHistoryCategoryTag(entry.Category),
            entry.Timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
            BuildHistoryPreview(entry.Summary),
            string.IsNullOrWhiteSpace(entry.Title) ? "\u672A\u547D\u540D\u8FD0\u884C" : entry.Title,
            string.IsNullOrWhiteSpace(entry.Summary) ? "\uFF08\u65E0\u8BE6\u7EC6\u5185\u5BB9\uFF09" : entry.Summary.Trim());

    private static HistoryRunListItemViewModel? TryFindHistoryRunItem(
        IReadOnlyList<HistoryRunListItemViewModel> items,
        HistoryRunIdentity? selection)
    {
        if (selection is null)
        {
            return null;
        }

        var value = selection.Value;
        return items.FirstOrDefault(item =>
            item.Timestamp == value.Timestamp &&
            string.Equals(item.RawCategory, value.Category, StringComparison.Ordinal) &&
            string.Equals(item.OriginalTitle, value.Title, StringComparison.Ordinal));
    }

    private static string ResolveHistoryDisplayTitle(RunHistoryEntry entry)
        => entry.Title switch
        {
            "\u63A5\u53E3\u57FA\u7840\u5355\u6B21\u8BCA\u65AD" => "\u5355\u7AD9\u5FEB\u901F\u6D4B\u8BD5",
            "\u63A5\u53E3\u6DF1\u5EA6\u5355\u6B21\u8BCA\u65AD" => "\u5355\u7AD9\u6DF1\u5EA6\u6D4B\u8BD5",
            "\u6DF1\u5EA6\u5355\u6B21\u8BCA\u65AD\u91CD\u8BD5" => "\u5355\u7AD9\u6DF1\u5EA6\u6D4B\u8BD5\u91CD\u8BD5",
            "\u57FA\u7840\u5355\u6B21\u8BCA\u65AD\u91CD\u8BD5" => "\u5355\u7AD9\u5FEB\u901F\u6D4B\u8BD5\u91CD\u8BD5",
            "\u63A5\u53E3\u7A33\u5B9A\u6027\u5E8F\u5217" => "\u5355\u7AD9\u7A33\u5B9A\u6027\u6D4B\u8BD5",
            "\u7A33\u5B9A\u6027\u5DE1\u68C0\u8FFD\u52A0\u91CD\u8BD5" => "\u5355\u7AD9\u7A33\u5B9A\u6027\u6D4B\u8BD5\u91CD\u8BD5",
            "\u5E76\u53D1\u538B\u6D4B" => "\u5355\u7AD9\u5E76\u53D1\u538B\u6D4B",
            "\u63A5\u53E3\u5165\u53E3\u7EC4\u5BF9\u6BD4" => "\u6279\u91CF\u5FEB\u901F\u5BF9\u6BD4",
            "\u63A5\u53E3\u5165\u53E3\u7EC4\u8FFD\u52A0\u91CD\u8BD5" => "\u6279\u91CF\u5FEB\u901F\u5BF9\u6BD4\u91CD\u8BD5",
            "\u5165\u53E3\u7EC4\u5019\u9009\u6DF1\u5EA6\u6D4B\u8BD5" => "\u6279\u91CF\u6DF1\u5EA6\u5BF9\u6BD4",
            "\u62C9\u53D6\u6A21\u578B\u5217\u8868" => "\u6A21\u578B\u5217\u8868\u62C9\u53D6",
            "\u5B98\u65B9 API \u53EF\u7528\u6027\u68C0\u6D4B" => "\u7F51\u9875 API \u94FE\u8DEF\u68C0\u6D4B",
            "\u5BA2\u6237\u7AEF API \u8054\u901A\u9274\u5B9A" => "\u5BA2\u6237\u7AEF API \u8054\u901A\u9274\u5B9A",
            "\u57FA\u7840\u7F51\u7EDC\u68C0\u6D4B" => "\u57FA\u7840\u7F51\u7EDC\u68C0\u6D4B",
            "STUN \u68C0\u6D4B" => "NAT / STUN \u68C0\u6D4B",
            _ => string.IsNullOrWhiteSpace(entry.Title) ? "\u672A\u547D\u540D\u8FD0\u884C" : entry.Title
        };

    private static string ResolveHistoryScopeTag(RunHistoryEntry entry)
        => entry.Title switch
        {
            "\u63A5\u53E3\u57FA\u7840\u5355\u6B21\u8BCA\u65AD" or
            "\u63A5\u53E3\u6DF1\u5EA6\u5355\u6B21\u8BCA\u65AD" or
            "\u6DF1\u5EA6\u5355\u6B21\u8BCA\u65AD\u91CD\u8BD5" or
            "\u57FA\u7840\u5355\u6B21\u8BCA\u65AD\u91CD\u8BD5" or
            "\u63A5\u53E3\u7A33\u5B9A\u6027\u5E8F\u5217" or
            "\u7A33\u5B9A\u6027\u5DE1\u68C0\u8FFD\u52A0\u91CD\u8BD5" or
            "\u5E76\u53D1\u538B\u6D4B" => "\u5355\u7AD9",
            "\u63A5\u53E3\u5165\u53E3\u7EC4\u5BF9\u6BD4" or
            "\u63A5\u53E3\u5165\u53E3\u7EC4\u8FFD\u52A0\u91CD\u8BD5" or
            "\u5165\u53E3\u7EC4\u5019\u9009\u6DF1\u5EA6\u6D4B\u8BD5" => "\u6279\u91CF",
            "\u62C9\u53D6\u6A21\u578B\u5217\u8868" => "\u6A21\u578B",
            _ => entry.Category switch
            {
                "\u7F51\u7EDC" => "\u7F51\u7EDC",
                "\u5B98\u65B9API" => "\u7F51\u9875 API",
                "\u5BA2\u6237\u7AEFAPI" => "\u5BA2\u6237\u7AEF API",
                "STUN" => "NAT / STUN",
                "\u8DEF\u7531" => "\u8DEF\u7531",
                "\u7AEF\u53E3\u626B\u63CF" => "\u7AEF\u53E3\u626B\u63CF",
                "\u6D4B\u901F" => "\u6D4B\u901F",
                "\u5206\u6D41" => "\u5206\u6D41",
                "\u62A5\u544A" => "\u62A5\u544A",
                _ => ResolveHistoryCategoryTag(entry.Category)
            }
        };

    private static string ResolveHistoryCategoryTag(string category)
        => category switch
        {
            "\u63A5\u53E3" => "\u63A5\u53E3",
            "\u7F51\u7EDC" => "\u7F51\u7EDC",
            "\u5B98\u65B9API" => "\u5B98\u65B9 API",
            "\u5BA2\u6237\u7AEFAPI" => "\u5BA2\u6237\u7AEF API",
            "STUN" => "NAT / STUN",
            "\u8DEF\u7531" => "\u8DEF\u7531",
            "\u7AEF\u53E3\u626B\u63CF" => "\u7AEF\u53E3\u626B\u63CF",
            "\u6D4B\u901F" => "\u6D4B\u901F",
            "\u5206\u6D41" => "\u5206\u6D41",
            "\u62A5\u544A" => "\u62A5\u544A",
            _ => string.IsNullOrWhiteSpace(category) ? "\u672A\u5206\u7C7B" : category
        };

    private static string BuildHistoryPreview(string summary)
    {
        if (string.IsNullOrWhiteSpace(summary))
        {
            return "\uFF08\u65E0\u6458\u8981\uFF09";
        }

        var normalizedLines = summary
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(NormalizeInlineWhitespace)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Take(2)
            .ToArray();

        var preview = normalizedLines.Length == 0
            ? NormalizeInlineWhitespace(summary)
            : string.Join(" / ", normalizedLines);

        const int maxPreviewLength = 136;
        return preview.Length <= maxPreviewLength
            ? preview
            : preview[..(maxPreviewLength - 1)] + "\u2026";
    }

    private static string NormalizeInlineWhitespace(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        StringBuilder builder = new(text.Length);
        var inWhitespace = false;

        foreach (var character in text.Trim())
        {
            if (char.IsWhiteSpace(character))
            {
                if (inWhitespace)
                {
                    continue;
                }

                builder.Append(' ');
                inWhitespace = true;
                continue;
            }

            builder.Append(character);
            inWhitespace = false;
        }

        return builder.ToString();
    }
}
