using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using MdBlock = Markdig.Syntax.Block;
using MdInline = Markdig.Syntax.Inlines.Inline;
using MdTable = Markdig.Extensions.Tables.Table;
using MdTableCell = Markdig.Extensions.Tables.TableCell;
using MdTableRow = Markdig.Extensions.Tables.TableRow;

namespace RelayBench.App.Controls;

public sealed class MarkdownViewer : FlowDocumentScrollViewer
{
    public static readonly DependencyProperty MarkdownProperty =
        DependencyProperty.Register(
            nameof(Markdown),
            typeof(string),
            typeof(MarkdownViewer),
            new PropertyMetadata(string.Empty, OnMarkdownChanged));

    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .Build();

    private static readonly HashSet<string> CodeKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "abstract", "async", "await", "break", "case", "catch", "class", "const", "continue", "default",
        "do", "else", "enum", "false", "finally", "for", "foreach", "from", "function", "if", "import",
        "in", "interface", "internal", "let", "namespace", "new", "null", "private", "protected", "public",
        "readonly", "return", "sealed", "static", "string", "switch", "this", "throw", "true", "try",
        "using", "var", "void", "while"
    };

    private static readonly Regex CodeTokenRegex = new(
        @"(?<comment>//.*?$|#.*?$)|(?<string>""(?:\\.|[^""\\])*""|'(?:\\.|[^'\\])*')|(?<number>\b\d+(?:\.\d+)?\b)|(?<word>\b[A-Za-z_][A-Za-z0-9_]*\b)",
        RegexOptions.Compiled | RegexOptions.Multiline);

    public MarkdownViewer()
    {
        IsToolBarVisible = false;
        VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
        Background = Brushes.Transparent;
        Focusable = false;
        Document = BuildDocument(string.Empty);
    }

    public string Markdown
    {
        get => (string)GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    private static void OnMarkdownChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs e)
    {
        if (dependencyObject is MarkdownViewer viewer)
        {
            viewer.Document = BuildDocument(e.NewValue as string ?? string.Empty);
        }
    }

    private static FlowDocument BuildDocument(string markdown)
    {
        FlowDocument document = new()
        {
            PagePadding = new Thickness(0),
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 12.5,
            Foreground = Brush("#101828"),
            Background = Brushes.Transparent
        };

        if (string.IsNullOrWhiteSpace(markdown))
        {
            return document;
        }

        var parsed = Markdig.Markdown.Parse(markdown, Pipeline);
        foreach (var block in parsed)
        {
            AppendBlock(document.Blocks, block);
        }

        return document;
    }

    private static void AppendBlock(BlockCollection target, MdBlock block)
    {
        switch (block)
        {
            case HeadingBlock heading:
                target.Add(RenderHeading(heading));
                break;
            case ParagraphBlock paragraph:
                target.Add(RenderParagraph(paragraph));
                break;
            case FencedCodeBlock fencedCode:
                target.Add(RenderCodeBlock(fencedCode.Lines.ToString(), fencedCode.Info));
                break;
            case CodeBlock code:
                target.Add(RenderCodeBlock(code.Lines.ToString(), string.Empty));
                break;
            case QuoteBlock quote:
                target.Add(RenderQuote(quote));
                break;
            case ListBlock list:
                target.Add(RenderList(list));
                break;
            case ThematicBreakBlock:
                target.Add(RenderDivider());
                break;
            case MdTable table:
                target.Add(RenderTable(table));
                break;
            case HtmlBlock html:
                target.Add(RenderCodeBlock(html.Lines.ToString(), "html"));
                break;
            default:
                var fallback = block.ToString();
                if (!string.IsNullOrWhiteSpace(fallback))
                {
                    target.Add(new Paragraph(new Run(fallback)) { Margin = new Thickness(0, 0, 0, 8) });
                }
                break;
        }
    }

    private static Paragraph RenderHeading(HeadingBlock heading)
    {
        Paragraph paragraph = new()
        {
            Margin = new Thickness(0, heading.Level == 1 ? 2 : 8, 0, 8),
            FontWeight = FontWeights.SemiBold,
            FontSize = heading.Level switch
            {
                1 => 21,
                2 => 18,
                3 => 15.5,
                4 => 14,
                _ => 13
            },
            Foreground = Brush("#101828")
        };
        RenderInlines(heading.Inline, paragraph.Inlines);
        return paragraph;
    }

    private static Paragraph RenderParagraph(ParagraphBlock block)
    {
        Paragraph paragraph = new()
        {
            Margin = new Thickness(0, 0, 0, 8),
            LineHeight = 20,
            Foreground = Brush("#101828")
        };
        RenderInlines(block.Inline, paragraph.Inlines);
        return paragraph;
    }

    private static BlockUIContainer RenderCodeBlock(string code, string? language)
    {
        var normalizedLanguage = string.IsNullOrWhiteSpace(language) ? "text" : language.Trim();
        Border border = new()
        {
            Background = Brush("#0F172A"),
            BorderBrush = Brush("#1E293B"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            Margin = new Thickness(0, 4, 0, 10)
        };

        Grid grid = new();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        Border header = new()
        {
            Background = Brush("#111827"),
            CornerRadius = new CornerRadius(7, 7, 0, 0),
            Padding = new Thickness(9, 5, 9, 5)
        };
        Grid headerGrid = new();
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        TextBlock languageText = new()
        {
            Text = normalizedLanguage,
            FontSize = 10.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush("#CBD5E1"),
            VerticalAlignment = VerticalAlignment.Center
        };
        Button copyButton = new()
        {
            Content = "\u590d\u5236",
            Padding = new Thickness(8, 2, 8, 2),
            FontSize = 10.5,
            Cursor = Cursors.Hand
        };
        copyButton.Click += (_, _) => Clipboard.SetText(code ?? string.Empty);
        Grid.SetColumn(copyButton, 1);
        headerGrid.Children.Add(languageText);
        headerGrid.Children.Add(copyButton);
        header.Child = headerGrid;

        TextBlock codeText = new()
        {
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            Foreground = Brush("#E5E7EB"),
            Padding = new Thickness(10),
            TextWrapping = TextWrapping.NoWrap
        };
        RenderCodeInlines(codeText, code ?? string.Empty);

        ScrollViewer scroller = new()
        {
            MaxHeight = 280,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = codeText
        };
        scroller.SetResourceReference(FrameworkElement.StyleProperty, "WorkbenchScrollViewerStyle");

        Grid.SetRow(header, 0);
        Grid.SetRow(scroller, 1);
        grid.Children.Add(header);
        grid.Children.Add(scroller);
        border.Child = grid;
        return new BlockUIContainer(border) { Margin = new Thickness(0) };
    }

    private static BlockUIContainer RenderQuote(QuoteBlock quote)
    {
        FlowDocument nested = new()
        {
            PagePadding = new Thickness(0),
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 12.5,
            Foreground = Brush("#344054")
        };
        foreach (var child in quote)
        {
            AppendBlock(nested.Blocks, child);
        }

        FlowDocumentScrollViewer viewer = new()
        {
            Document = nested,
            IsToolBarVisible = false,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Background = Brushes.Transparent
        };
        Border border = new()
        {
            BorderBrush = Brush("#B2CCFF"),
            BorderThickness = new Thickness(3, 0, 0, 0),
            Background = Brush("#F5F8FF"),
            Padding = new Thickness(10, 8, 8, 2),
            Margin = new Thickness(0, 0, 0, 10),
            Child = viewer
        };
        return new BlockUIContainer(border);
    }

    private static System.Windows.Documents.List RenderList(ListBlock listBlock)
    {
        System.Windows.Documents.List list = new()
        {
            MarkerStyle = listBlock.IsOrdered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
            Margin = new Thickness(18, 0, 0, 8),
            Padding = new Thickness(0)
        };

        foreach (var itemBlock in listBlock.OfType<ListItemBlock>())
        {
            ListItem item = new() { Margin = new Thickness(0, 0, 0, 4) };
            foreach (var child in itemBlock)
            {
                AppendBlock(item.Blocks, child);
            }
            list.ListItems.Add(item);
        }

        return list;
    }

    private static BlockUIContainer RenderDivider()
        => new(new Border
        {
            Height = 1,
            Background = Brush("#E4E7EC"),
            Margin = new Thickness(0, 8, 0, 12)
        });

    private static System.Windows.Documents.Table RenderTable(MdTable source)
    {
        System.Windows.Documents.Table table = new()
        {
            CellSpacing = 0,
            Margin = new Thickness(0, 4, 0, 12)
        };

        var rows = source.OfType<MdTableRow>().ToArray();
        var columnCount = Math.Max(1, rows.Select(row => row.OfType<MdTableCell>().Count()).DefaultIfEmpty(1).Max());
        for (var i = 0; i < columnCount; i++)
        {
            table.Columns.Add(new TableColumn());
        }

        TableRowGroup group = new();
        table.RowGroups.Add(group);
        foreach (var sourceRow in rows)
        {
            System.Windows.Documents.TableRow row = new();
            group.Rows.Add(row);
            foreach (var sourceCell in sourceRow.OfType<MdTableCell>())
            {
                System.Windows.Documents.TableCell cell = new()
                {
                    BorderBrush = Brush("#D0D5DD"),
                    BorderThickness = new Thickness(1),
                    Padding = new Thickness(8, 5, 8, 5),
                    Background = sourceRow.IsHeader ? Brush("#F2F4F7") : Brushes.White
                };
                foreach (var child in sourceCell)
                {
                    AppendBlock(cell.Blocks, child);
                }

                if (cell.Blocks.Count == 0)
                {
                    cell.Blocks.Add(new Paragraph(new Run(string.Empty)));
                }

                row.Cells.Add(cell);
            }
        }

        return table;
    }

    private static void RenderInlines(ContainerInline? container, InlineCollection target)
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

    private static void RenderInline(MdInline inline, InlineCollection target)
    {
        switch (inline)
        {
            case LiteralInline literal:
                target.Add(new Run(literal.Content.ToString()));
                break;
            case CodeInline code:
                target.Add(new Run(code.Content)
                {
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 12,
                    Background = Brush("#F2F4F7"),
                    Foreground = Brush("#B42318")
                });
                break;
            case EmphasisInline emphasis:
                Span span = emphasis.DelimiterChar == '~'
                    ? new Span { TextDecorations = TextDecorations.Strikethrough }
                    : emphasis.DelimiterCount >= 2
                    ? new Bold()
                    : new Italic();
                RenderInlines(emphasis, span.Inlines);
                target.Add(span);
                break;
            case LinkInline { IsImage: true } image:
                target.Add(new Run($"[image: {image.Url}]")
                {
                    Foreground = Brush("#175CD3")
                });
                break;
            case LinkInline link:
                Hyperlink hyperlink = new()
                {
                    NavigateUri = TryBuildUri(link.Url)
                };
                hyperlink.RequestNavigate += (_, e) =>
                {
                    if (e.Uri is not null)
                    {
                        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
                    }
                };
                RenderInlines(link, hyperlink.Inlines);
                if (!hyperlink.Inlines.Any())
                {
                    hyperlink.Inlines.Add(new Run(link.Url ?? string.Empty));
                }
                target.Add(hyperlink);
                break;
            case LineBreakInline:
                target.Add(new LineBreak());
                break;
            case HtmlInline html:
                target.Add(new Run(html.Tag)
                {
                    Foreground = Brush("#667085")
                });
                break;
            case ContainerInline nested:
                RenderInlines(nested, target);
                break;
            default:
                var fallback = inline.ToString();
                if (!string.IsNullOrWhiteSpace(fallback))
                {
                    target.Add(new Run(fallback));
                }
                break;
        }
    }

    private static Uri? TryBuildUri(string? value)
        => Uri.TryCreate(value, UriKind.Absolute, out var uri) ? uri : null;

    private static void RenderCodeInlines(TextBlock target, string code)
    {
        var index = 0;
        foreach (Match match in CodeTokenRegex.Matches(code))
        {
            if (match.Index > index)
            {
                target.Inlines.Add(new Run(code[index..match.Index]));
            }

            var value = match.Value;
            var brush = ResolveCodeBrush(match);
            target.Inlines.Add(new Run(value) { Foreground = brush });
            index = match.Index + match.Length;
        }

        if (index < code.Length)
        {
            target.Inlines.Add(new Run(code[index..]));
        }
    }

    private static Brush ResolveCodeBrush(Match match)
    {
        if (match.Groups["comment"].Success)
        {
            return Brush("#94A3B8");
        }

        if (match.Groups["string"].Success)
        {
            return Brush("#86EFAC");
        }

        if (match.Groups["number"].Success)
        {
            return Brush("#FBBF24");
        }

        if (match.Groups["word"].Success && CodeKeywords.Contains(match.Value))
        {
            return Brush("#93C5FD");
        }

        return Brush("#E5E7EB");
    }

    private static SolidColorBrush Brush(string hex)
    {
        var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
        brush.Freeze();
        return brush;
    }
}
