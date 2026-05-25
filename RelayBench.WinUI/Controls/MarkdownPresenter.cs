using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;

using MdBlock = Markdig.Syntax.Block;
using MdInline = Markdig.Syntax.Inlines.Inline;
using MdTable = Markdig.Extensions.Tables.Table;
using MdTableCell = Markdig.Extensions.Tables.TableCell;
using MdTableRow = Markdig.Extensions.Tables.TableRow;
using MarkdigMarkdown = Markdig.Markdown;

namespace RelayBench.WinUI.Controls;

/// <summary>
/// Renders Markdown in chat and report surfaces with the same useful block support
/// that the old WPF viewer had: headings, lists, quotes, tables, links, and code.
/// </summary>
public sealed class MarkdownPresenter : ContentControl
{
    private const string TextForegroundBrushKey = "MarkdownTextForegroundBrush";
    private const string QuoteBorderBrushKey = "MarkdownQuoteBorderBrush";
    private const string QuoteBackgroundBrushKey = "MarkdownQuoteBackgroundBrush";
    private const string ListMarkerForegroundBrushKey = "MarkdownListMarkerForegroundBrush";
    private const string TableBorderBrushKey = "MarkdownTableBorderBrush";
    private const string TableHeaderBackgroundBrushKey = "MarkdownTableHeaderBackgroundBrush";
    private const string TableCellBackgroundBrushKey = "MarkdownTableCellBackgroundBrush";
    private const string DividerBrushKey = "MarkdownDividerBrush";
    private const string InlineCodeForegroundBrushKey = "MarkdownInlineCodeForegroundBrush";
    private const string ImageLinkForegroundBrushKey = "MarkdownImageLinkForegroundBrush";
    private const string HtmlInlineForegroundBrushKey = "MarkdownHtmlInlineForegroundBrush";

    private static readonly MarkdownPipeline s_pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .Build();

    private readonly StackPanel _rootPanel;

    public static readonly DependencyProperty MarkdownProperty =
        DependencyProperty.Register(
            nameof(Markdown),
            typeof(string),
            typeof(MarkdownPresenter),
            new PropertyMetadata(null, OnMarkdownChanged));

    public string? Markdown
    {
        get => (string?)GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    public MarkdownPresenter()
    {
        _rootPanel = new StackPanel { Spacing = 4 };
        Content = _rootPanel;
        ActualThemeChanged += (_, _) => RenderMarkdown();
    }

    private static void OnMarkdownChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is MarkdownPresenter presenter)
        {
            presenter.RenderMarkdown();
        }
    }

    private void RenderMarkdown()
    {
        _rootPanel.Children.Clear();

        var markdown = Markdown;
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return;
        }

        var document = MarkdigMarkdown.Parse(markdown, s_pipeline);
        foreach (var block in document)
        {
            foreach (var element in RenderBlock(block, depth: 0))
            {
                _rootPanel.Children.Add(element);
            }
        }
    }

    private IEnumerable<UIElement> RenderBlock(MdBlock block, int depth)
    {
        switch (block)
        {
            case HeadingBlock heading:
                yield return RenderHeading(heading);
                break;
            case ParagraphBlock paragraph:
                yield return RenderParagraph(paragraph, new Thickness(0, 2, 0, 2));
                break;
            case FencedCodeBlock fenced:
                yield return RenderCodeBlock(fenced.Lines.ToString(), fenced.Info);
                break;
            case CodeBlock code:
                yield return RenderCodeBlock(code.Lines.ToString(), "text");
                break;
            case QuoteBlock quote:
                yield return RenderQuote(quote, depth);
                break;
            case ListBlock list:
                yield return RenderList(list, depth);
                break;
            case MdTable table:
                yield return RenderTable(table);
                break;
            case ThematicBreakBlock:
                yield return RenderDivider();
                break;
            case HtmlBlock html:
                yield return RenderCodeBlock(html.Lines.ToString(), "html");
                break;
            case ContainerBlock container:
                foreach (var child in container)
                {
                    foreach (var element in RenderBlock(child, depth))
                    {
                        yield return element;
                    }
                }

                break;
            default:
                var fallback = block.ToString();
                if (!string.IsNullOrWhiteSpace(fallback))
                {
                    yield return RenderPlainText(fallback);
                }

                break;
        }
    }

    private RichTextBlock RenderHeading(HeadingBlock heading)
    {
        var block = CreateRichTextBlock(new Thickness(0, heading.Level <= 2 ? 10 : 7, 0, 4));
        var paragraph = new Paragraph
        {
            FontSize = heading.Level switch
            {
                1 => 18,
                2 => 16,
                3 => 14,
                _ => 13
            },
            FontWeight = heading.Level <= 3 ? FontWeights.Bold : FontWeights.SemiBold
        };
        RenderInlines(heading.Inline, paragraph.Inlines);
        block.Blocks.Add(paragraph);
        return block;
    }

    private RichTextBlock RenderParagraph(ParagraphBlock paragraphBlock, Thickness margin)
    {
        var block = CreateRichTextBlock(margin);
        var paragraph = new Paragraph();
        RenderInlines(paragraphBlock.Inline, paragraph.Inlines);
        block.Blocks.Add(paragraph);
        return block;
    }

    private UIElement RenderPlainText(string text)
    {
        var block = CreateRichTextBlock(new Thickness(0, 2, 0, 2));
        block.Blocks.Add(new Paragraph { Inlines = { new Run { Text = text } } });
        return block;
    }

    private static CodeBlockControl RenderCodeBlock(string code, string? language)
        => new()
        {
            Code = code.TrimEnd('\r', '\n'),
            Language = string.IsNullOrWhiteSpace(language) ? "text" : language.Trim()
        };

    private Border RenderQuote(QuoteBlock quote, int depth)
    {
        var panel = new StackPanel { Spacing = 2 };
        foreach (var child in quote)
        {
            foreach (var element in RenderBlock(child, depth + 1))
            {
                panel.Children.Add(element);
            }
        }

        return new Border
        {
            BorderBrush = ResolveBrush(QuoteBorderBrushKey),
            BorderThickness = new Thickness(3, 0, 0, 0),
            Background = ResolveBrush(QuoteBackgroundBrushKey),
            CornerRadius = new CornerRadius(6),
            Margin = new Thickness(depth * 10, 4, 0, 6),
            Padding = new Thickness(10, 6, 8, 6),
            Child = panel
        };
    }

    private StackPanel RenderList(ListBlock list, int depth)
    {
        var panel = new StackPanel
        {
            Spacing = 3,
            Margin = new Thickness(depth * 12, 3, 0, 6)
        };

        var index = 1;
        foreach (var itemBlock in list.OfType<ListItemBlock>())
        {
            var row = new Grid { ColumnSpacing = 7 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var marker = new TextBlock
            {
                Text = list.IsOrdered ? $"{index}." : "\u2022",
                FontSize = 13,
                Foreground = ResolveBrush(ListMarkerForegroundBrushKey),
                Margin = new Thickness(0, 2, 0, 0)
            };
            row.Children.Add(marker);

            var content = new StackPanel { Spacing = 2 };
            Grid.SetColumn(content, 1);
            foreach (var child in itemBlock)
            {
                foreach (var element in RenderBlock(child, depth + 1))
                {
                    content.Children.Add(element);
                }
            }

            if (content.Children.Count == 0)
            {
                content.Children.Add(RenderPlainText(string.Empty));
            }

            row.Children.Add(content);
            panel.Children.Add(row);
            index++;
        }

        return panel;
    }

    private UIElement RenderTable(MdTable table)
    {
        var rows = table.OfType<MdTableRow>().ToArray();
        var columnCount = Math.Max(1, rows.Select(row => row.OfType<MdTableCell>().Count()).DefaultIfEmpty(1).Max());

        var grid = new Grid
        {
            MinWidth = Math.Min(960, Math.Max(360, columnCount * 150))
        };
        for (var column = 0; column < columnCount; column++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }

        for (var rowIndex = 0; rowIndex < rows.Length; rowIndex++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var row = rows[rowIndex];
            var columnIndex = 0;
            foreach (var cell in row.OfType<MdTableCell>())
            {
                var cellBorder = RenderTableCell(cell, row.IsHeader);
                Grid.SetRow(cellBorder, rowIndex);
                Grid.SetColumn(cellBorder, columnIndex);
                grid.Children.Add(cellBorder);
                columnIndex++;
            }
        }

        var tableSurface = new Border
        {
            BorderBrush = ResolveBrush(TableBorderBrushKey),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Child = grid
        };

        var tableScroller = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollMode = ScrollMode.Auto,
            IsHorizontalRailEnabled = true,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollMode = ScrollMode.Disabled,
            ZoomMode = ZoomMode.Disabled,
            Margin = new Thickness(0, 5, 0, 9),
            Content = tableSurface
        };
        AutomationProperties.SetName(tableScroller, "Markdown table");
        AutomationProperties.SetLocalizedControlType(tableScroller, "markdown table");
        AutomationProperties.SetHelpText(tableScroller, "Horizontally scrollable Markdown table.");
        ToolTipService.SetToolTip(tableScroller, "Markdown table - scroll horizontally if columns overflow.");
        return tableScroller;
    }

    private Border RenderTableCell(MdTableCell cell, bool isHeader)
    {
        var stack = new StackPanel { Spacing = 2 };
        foreach (var child in cell)
        {
            if (child is ParagraphBlock paragraph)
            {
                var block = RenderParagraph(paragraph, new Thickness(0));
                if (block.Blocks.FirstOrDefault() is Paragraph firstParagraph && isHeader)
                {
                    firstParagraph.FontWeight = FontWeights.SemiBold;
                }

                stack.Children.Add(block);
            }
            else
            {
                foreach (var element in RenderBlock(child, depth: 0))
                {
                    stack.Children.Add(element);
                }
            }
        }

        if (stack.Children.Count == 0)
        {
            stack.Children.Add(RenderPlainText(string.Empty));
        }

        return new Border
        {
            Background = ResolveBrush(isHeader ? TableHeaderBackgroundBrushKey : TableCellBackgroundBrushKey),
            BorderBrush = ResolveBrush(TableBorderBrushKey),
            BorderThickness = new Thickness(0, 0, 1, 1),
            MinWidth = 132,
            Padding = new Thickness(8, 5, 8, 5),
            Child = stack
        };
    }

    private UIElement RenderDivider()
        => new Border
        {
            Height = 1,
            Background = ResolveBrush(DividerBrushKey),
            Margin = new Thickness(0, 8, 0, 12)
        };

    private RichTextBlock CreateRichTextBlock(Thickness margin)
        => new()
        {
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
            FontSize = 13,
            Margin = margin,
            Foreground = ResolveBrush(TextForegroundBrushKey)
        };

    private void RenderInlines(ContainerInline? container, InlineCollection target)
    {
        if (container is null)
        {
            return;
        }

        foreach (var inline in container)
        {
            RenderInline(inline, target);
        }
    }

    private void RenderInline(MdInline inline, InlineCollection target)
    {
        switch (inline)
        {
            case LiteralInline literal:
                target.Add(new Run { Text = literal.Content.ToString() });
                break;
            case CodeInline code:
                target.Add(new Run
                {
                    Text = code.Content,
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 12,
                    Foreground = ResolveBrush(InlineCodeForegroundBrushKey)
                });
                break;
            case EmphasisInline emphasis:
                Span span = new()
                {
                    FontWeight = emphasis.DelimiterCount >= 2 ? FontWeights.SemiBold : FontWeights.Normal,
                    FontStyle = emphasis.DelimiterCount >= 2 ? Windows.UI.Text.FontStyle.Normal : Windows.UI.Text.FontStyle.Italic
                };
                RenderInlines(emphasis, span.Inlines);
                target.Add(span);
                break;
            case LinkInline { IsImage: true } image:
                target.Add(new Run
                {
                    Text = string.IsNullOrWhiteSpace(image.Url) ? "[image]" : $"[image: {image.Url}]",
                    Foreground = ResolveBrush(ImageLinkForegroundBrushKey)
                });
                break;
            case LinkInline link:
                Hyperlink hyperlink = new();
                if (Uri.TryCreate(link.Url, UriKind.Absolute, out var uri))
                {
                    hyperlink.NavigateUri = uri;
                }

                RenderInlines(link, hyperlink.Inlines);
                if (hyperlink.Inlines.Count == 0)
                {
                    hyperlink.Inlines.Add(new Run { Text = link.Url ?? string.Empty });
                }

                target.Add(hyperlink);
                break;
            case LineBreakInline:
                target.Add(new LineBreak());
                break;
            case HtmlInline html:
                target.Add(new Run
                {
                    Text = html.Tag,
                    Foreground = ResolveBrush(HtmlInlineForegroundBrushKey)
                });
                break;
            case ContainerInline nested:
                RenderInlines(nested, target);
                break;
            default:
                var fallback = inline.ToString();
                if (!string.IsNullOrWhiteSpace(fallback))
                {
                    target.Add(new Run { Text = fallback });
                }

                break;
        }
    }

    private Brush ResolveBrush(string resourceKey)
    {
        if (Resources.TryGetValue(resourceKey, out var localValue) && localValue is Brush localBrush)
        {
            return localBrush;
        }

        return (Brush)Application.Current.Resources[resourceKey];
    }
}
