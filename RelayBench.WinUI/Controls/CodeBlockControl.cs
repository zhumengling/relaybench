using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;

namespace RelayBench.WinUI.Controls;

/// <summary>
/// A control that renders code with keyword-based syntax highlighting.
/// Supports Go, Python, JavaScript, TypeScript, C#, Java, Rust, and Shell.
/// </summary>
public sealed class CodeBlockControl : ContentControl
{
    private const string BackgroundBrushKey = "CodeBlockBackgroundBrush";
    private const string BorderBrushKey = "CodeBlockBorderBrush";
    private const string LabelForegroundBrushKey = "CodeBlockLabelForegroundBrush";
    private const string TextForegroundBrushKey = "CodeBlockTextForegroundBrush";
    private const string KeywordForegroundBrushKey = "CodeBlockKeywordForegroundBrush";
    private const string StringForegroundBrushKey = "CodeBlockStringForegroundBrush";
    private const string CommentForegroundBrushKey = "CodeBlockCommentForegroundBrush";
    private const string NumberForegroundBrushKey = "CodeBlockNumberForegroundBrush";

    private readonly Border _surface;
    private readonly RichTextBlock _codeBlock;
    private readonly TextBlock _languageLabel;

    public static readonly DependencyProperty CodeProperty =
        DependencyProperty.Register(
            nameof(Code),
            typeof(string),
            typeof(CodeBlockControl),
            new PropertyMetadata(null, OnCodeChanged));

    public static new readonly DependencyProperty LanguageProperty =
        DependencyProperty.Register(
            nameof(Language),
            typeof(string),
            typeof(CodeBlockControl),
            new PropertyMetadata("text", OnCodeChanged));

    public string? Code
    {
        get => (string?)GetValue(CodeProperty);
        set => SetValue(CodeProperty, value);
    }

    public new string Language
    {
        get => (string)GetValue(LanguageProperty);
        set => SetValue(LanguageProperty, value);
    }

    public CodeBlockControl()
    {
        _surface = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(0),
            Margin = new Thickness(0, 4, 0, 4)
        };

        var container = new StackPanel();

        // Header with language label
        var header = new Grid { Padding = new Thickness(12, 6, 12, 6) };
        _languageLabel = new TextBlock
        {
            FontSize = 11,
            MaxLines = 1,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        header.Children.Add(_languageLabel);
        container.Children.Add(header);

        // Code content
        _codeBlock = new RichTextBlock
        {
            Padding = new Thickness(14, 4, 14, 14),
            TextWrapping = TextWrapping.NoWrap,
            IsTextSelectionEnabled = true
        };
        AutomationProperties.SetName(_codeBlock, "Code block content");

        var codeScroller = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollMode = ScrollMode.Enabled,
            IsHorizontalRailEnabled = true,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollMode = ScrollMode.Disabled,
            ZoomMode = ZoomMode.Disabled,
            Content = _codeBlock
        };
        container.Children.Add(codeScroller);

        _surface.Child = container;
        Content = _surface;
        AutomationProperties.SetName(this, "Code block");
        AutomationProperties.SetLocalizedControlType(this, "code block");
        ActualThemeChanged += (_, _) =>
        {
            ApplyChrome();
            RenderCode();
        };
        ApplyChrome();
    }

    private static void OnCodeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is CodeBlockControl control)
        {
            control.RenderCode();
        }
    }

    private void RenderCode()
    {
        _codeBlock.Blocks.Clear();
        var language = string.IsNullOrWhiteSpace(Language) ? "text" : Language.Trim();
        _languageLabel.Text = language;
        AutomationProperties.SetHelpText(this, $"{language} code block with horizontal scrolling.");
        ToolTipService.SetToolTip(this, $"{language} code block - scroll horizontally if lines overflow.");

        var code = Code;
        if (string.IsNullOrEmpty(code)) return;

        var keywords = GetKeywords(language);
        var palette = ResolveCodePalette();
        var lines = code.Split('\n');

        foreach (var line in lines)
        {
            var paragraph = new Paragraph { FontFamily = new FontFamily("Consolas"), FontSize = 12 };
            HighlightLine(paragraph, line, keywords, language, palette);
            _codeBlock.Blocks.Add(paragraph);
        }
    }

    private void HighlightLine(Paragraph paragraph, string line, HashSet<string> keywords, string language, CodePalette palette)
    {
        var i = 0;
        while (i < line.Length)
        {
            // String literals
            if (line[i] is '"' or '\'')
            {
                var quote = line[i];
                var end = line.IndexOf(quote, i + 1);
                if (end < 0) end = line.Length - 1;
                var str = line[i..(end + 1)];
                paragraph.Inlines.Add(new Run
                {
                    Text = str,
                    Foreground = palette.String
                });
                i = end + 1;
                continue;
            }

            // Line comments
            if (i + 1 < line.Length && line[i] == '/' && line[i + 1] == '/')
            {
                paragraph.Inlines.Add(new Run
                {
                    Text = line[i..],
                    Foreground = palette.Comment
                });
                return;
            }

            // Hash comments (Python, Shell)
            if (line[i] == '#' && (language is "python" or "py" or "shell" or "bash" or "sh" or "zsh"))
            {
                paragraph.Inlines.Add(new Run
                {
                    Text = line[i..],
                    Foreground = palette.Comment
                });
                return;
            }

            // Numbers
            if (char.IsDigit(line[i]) && (i == 0 || !char.IsLetterOrDigit(line[i - 1])))
            {
                var end = i;
                while (end < line.Length && (char.IsDigit(line[end]) || line[end] == '.'))
                    end++;
                paragraph.Inlines.Add(new Run
                {
                    Text = line[i..end],
                    Foreground = palette.Number
                });
                i = end;
                continue;
            }

            // Identifiers and keywords
            if (char.IsLetter(line[i]) || line[i] == '_')
            {
                var end = i;
                while (end < line.Length && (char.IsLetterOrDigit(line[end]) || line[end] == '_'))
                    end++;
                var word = line[i..end];

                if (keywords.Contains(word))
                {
                    paragraph.Inlines.Add(new Run
                    {
                        Text = word,
                        Foreground = palette.Keyword,
                        FontWeight = FontWeights.SemiBold
                    });
                }
                else
                {
                    paragraph.Inlines.Add(new Run
                    {
                        Text = word,
                        Foreground = palette.Text
                    });
                }
                i = end;
                continue;
            }

            // Other characters
            paragraph.Inlines.Add(new Run
            {
                Text = line[i].ToString(),
                Foreground = palette.Text
            });
            i++;
        }
    }

    private void ApplyChrome()
    {
        _surface.Background = ResolveBrush(BackgroundBrushKey);
        _surface.BorderBrush = ResolveBrush(BorderBrushKey);
        _languageLabel.Foreground = ResolveBrush(LabelForegroundBrushKey);
    }

    private CodePalette ResolveCodePalette() => new(
        ResolveBrush(TextForegroundBrushKey),
        ResolveBrush(KeywordForegroundBrushKey),
        ResolveBrush(StringForegroundBrushKey),
        ResolveBrush(CommentForegroundBrushKey),
        ResolveBrush(NumberForegroundBrushKey));

    private Brush ResolveBrush(string resourceKey)
    {
        if (Resources.TryGetValue(resourceKey, out var localValue) && localValue is Brush localBrush)
        {
            return localBrush;
        }

        return (Brush)Application.Current.Resources[resourceKey];
    }

    private static HashSet<string> GetKeywords(string language) => language.ToLowerInvariant() switch
    {
        "go" or "golang" => new(StringComparer.Ordinal)
        {
            "package", "import", "func", "var", "const", "type", "struct", "interface",
            "map", "chan", "go", "defer", "return", "if", "else", "for", "range",
            "switch", "case", "default", "break", "continue", "select", "nil", "true", "false",
            "make", "new", "append", "len", "cap", "error", "string", "int", "bool", "byte"
        },
        "python" or "py" => new(StringComparer.Ordinal)
        {
            "def", "class", "import", "from", "return", "if", "elif", "else", "for",
            "while", "try", "except", "finally", "with", "as", "yield", "lambda",
            "pass", "break", "continue", "raise", "None", "True", "False", "self",
            "and", "or", "not", "in", "is", "async", "await"
        },
        "javascript" or "js" => new(StringComparer.Ordinal)
        {
            "function", "const", "let", "var", "return", "if", "else", "for", "while",
            "switch", "case", "break", "continue", "new", "this", "class", "extends",
            "import", "export", "default", "async", "await", "try", "catch", "finally",
            "throw", "typeof", "instanceof", "null", "undefined", "true", "false", "yield"
        },
        "typescript" or "ts" => new(StringComparer.Ordinal)
        {
            "function", "const", "let", "var", "return", "if", "else", "for", "while",
            "switch", "case", "break", "continue", "new", "this", "class", "extends",
            "import", "export", "default", "async", "await", "try", "catch", "finally",
            "throw", "typeof", "instanceof", "null", "undefined", "true", "false",
            "interface", "type", "enum", "implements", "abstract", "readonly", "private",
            "public", "protected", "static", "override", "as", "is", "keyof", "never", "void"
        },
        "csharp" or "c#" or "cs" => new(StringComparer.Ordinal)
        {
            "using", "namespace", "class", "struct", "interface", "enum", "record",
            "public", "private", "protected", "internal", "static", "readonly", "const",
            "void", "int", "string", "bool", "double", "float", "long", "var", "new",
            "return", "if", "else", "for", "foreach", "while", "switch", "case", "break",
            "continue", "try", "catch", "finally", "throw", "async", "await", "null",
            "true", "false", "this", "base", "override", "virtual", "abstract", "sealed",
            "partial", "where", "get", "set", "init", "required", "yield", "in", "out", "ref"
        },
        "java" => new(StringComparer.Ordinal)
        {
            "package", "import", "class", "interface", "enum", "extends", "implements",
            "public", "private", "protected", "static", "final", "abstract", "void",
            "int", "long", "double", "float", "boolean", "String", "new", "return",
            "if", "else", "for", "while", "switch", "case", "break", "continue",
            "try", "catch", "finally", "throw", "throws", "null", "true", "false",
            "this", "super", "synchronized", "volatile", "instanceof"
        },
        "rust" or "rs" => new(StringComparer.Ordinal)
        {
            "fn", "let", "mut", "const", "static", "struct", "enum", "impl", "trait",
            "pub", "use", "mod", "crate", "self", "super", "return", "if", "else",
            "for", "while", "loop", "match", "break", "continue", "async", "await",
            "move", "ref", "where", "type", "unsafe", "true", "false", "None", "Some",
            "Ok", "Err", "Box", "Vec", "String", "Option", "Result"
        },
        "shell" or "bash" or "sh" or "zsh" => new(StringComparer.Ordinal)
        {
            "if", "then", "else", "elif", "fi", "for", "while", "do", "done",
            "case", "esac", "function", "return", "exit", "echo", "export",
            "local", "readonly", "set", "unset", "shift", "source", "true", "false",
            "cd", "ls", "rm", "cp", "mv", "mkdir", "cat", "grep", "sed", "awk",
            "curl", "wget", "sudo", "apt", "npm", "pip", "git"
        },
        _ => new(StringComparer.Ordinal)
    };

    private sealed record CodePalette(
        Brush Text,
        Brush Keyword,
        Brush String,
        Brush Comment,
        Brush Number);
}
