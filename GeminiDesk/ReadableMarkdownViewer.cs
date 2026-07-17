using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MdXaml;
using WpfMath.Controls;

namespace GeminiDesk;

public sealed class ReadableMarkdownViewer : MarkdownScrollViewer
{
    private static readonly Regex MathTokenPattern = new(
        @"BUNNYDESKMATH(?:BLOCK|INLINE)\d{6}END",
        RegexOptions.Compiled);

    public static readonly DependencyProperty SourceMarkdownProperty = DependencyProperty.Register(
        nameof(SourceMarkdown),
        typeof(string),
        typeof(ReadableMarkdownViewer),
        new FrameworkPropertyMetadata(string.Empty, OnSourceMarkdownChanged));

    private IReadOnlyDictionary<string, LatexExpression> _mathExpressions =
        new Dictionary<string, LatexExpression>();
    private bool _formatQueued;

    public string SourceMarkdown
    {
        get => (string)GetValue(SourceMarkdownProperty);
        set => SetValue(SourceMarkdownProperty, value);
    }

    public ReadableMarkdownViewer()
    {
        PreviewMouseWheel += ForwardMouseWheelToChat;
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
            {
                QueueReadabilityFormatting();
            }
        };
    }

    private void ForwardMouseWheelToChat(object sender, MouseWheelEventArgs e)
    {
        var parent = VisualTreeHelper.GetParent(this);

        while (parent is not null && parent is not ScrollViewer)
        {
            parent = VisualTreeHelper.GetParent(parent);
        }

        if (parent is not ScrollViewer chatScrollViewer)
        {
            return;
        }

        chatScrollViewer.ScrollToVerticalOffset(chatScrollViewer.VerticalOffset - e.Delta);
        e.Handled = true;
    }

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);

        if (e.Property.Name == "Markdown" && IsVisible)
        {
            QueueReadabilityFormatting();
        }
    }

    private void QueueReadabilityFormatting()
    {
        if (_formatQueued)
        {
            return;
        }

        _formatQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            _formatQueued = false;
            ApplyDocumentFormatting();
        });
    }

    private static void OnSourceMarkdownChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs e)
    {
        var viewer = (ReadableMarkdownViewer)dependencyObject;
        var prepared = LatexMarkdownProcessor.Prepare(e.NewValue as string);
        viewer._mathExpressions = prepared.Expressions;
        viewer.Markdown = prepared.Markdown;
    }

    private void ApplyDocumentFormatting()
    {
        if (Document is null)
        {
            return;
        }

        ReplaceMathExpressions(Document.Blocks);

        Document.PagePadding = new Thickness(0);
        Document.FontFamily = new System.Windows.Media.FontFamily(
            "Segoe UI Variable Text, Segoe UI Emoji, Malgun Gothic");
        Document.FontSize = 15.5;
        Document.LineHeight = 30;
        Document.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;

        FormatBlocks(Document.Blocks);
    }

    private void ReplaceMathExpressions(BlockCollection blocks)
    {
        foreach (var block in blocks.Cast<Block>().ToList())
        {
            switch (block)
            {
                case Paragraph paragraph when TryGetDisplayExpression(paragraph, out var expression):
                    blocks.InsertBefore(paragraph, CreateDisplayFormulaBlock(expression));
                    blocks.Remove(paragraph);
                    break;
                case Paragraph paragraph:
                    ReplaceInlineMath(paragraph.Inlines);
                    break;
                case Section section:
                    ReplaceMathExpressions(section.Blocks);
                    break;
                case List list:
                    foreach (var item in list.ListItems.Cast<ListItem>().ToList())
                    {
                        ReplaceMathExpressions(item.Blocks);
                    }

                    break;
                case Table table:
                    foreach (var rowGroup in table.RowGroups.Cast<TableRowGroup>().ToList())
                    {
                        foreach (var row in rowGroup.Rows.Cast<TableRow>().ToList())
                        {
                            foreach (var cell in row.Cells.Cast<TableCell>().ToList())
                            {
                                ReplaceMathExpressions(cell.Blocks);
                            }
                        }
                    }

                    break;
            }
        }
    }

    private bool TryGetDisplayExpression(Paragraph paragraph, out LatexExpression expression)
    {
        var text = new TextRange(paragraph.ContentStart, paragraph.ContentEnd).Text.Trim();
        if (_mathExpressions.TryGetValue(text, out var candidate) && candidate.IsDisplay)
        {
            expression = candidate;
            return true;
        }

        expression = null!;
        return false;
    }

    private void ReplaceInlineMath(InlineCollection inlines)
    {
        foreach (var inline in inlines.Cast<Inline>().ToList())
        {
            switch (inline)
            {
                case Run run:
                    ReplaceMathInRun(inlines, run);
                    break;
                case Span span:
                    ReplaceInlineMath(span.Inlines);
                    break;
            }
        }
    }

    private void ReplaceMathInRun(InlineCollection inlines, Run run)
    {
        var matches = MathTokenPattern.Matches(run.Text)
            .Cast<Match>()
            .Where(match => _mathExpressions.ContainsKey(match.Value))
            .ToList();

        if (matches.Count == 0)
        {
            return;
        }

        var offset = 0;
        foreach (var match in matches)
        {
            if (match.Index > offset)
            {
                inlines.InsertBefore(run, CreateRunLike(run, run.Text[offset..match.Index]));
            }

            var expression = _mathExpressions[match.Value];
            inlines.InsertBefore(run, new InlineUIContainer(CreateFormulaElement(expression, false))
            {
                BaselineAlignment = BaselineAlignment.Center
            });
            offset = match.Index + match.Length;
        }

        if (offset < run.Text.Length)
        {
            inlines.InsertBefore(run, CreateRunLike(run, run.Text[offset..]));
        }

        inlines.Remove(run);
    }

    private static Run CreateRunLike(Run source, string text)
    {
        return new Run(text)
        {
            Background = source.Background,
            BaselineAlignment = source.BaselineAlignment,
            FontFamily = source.FontFamily,
            FontSize = source.FontSize,
            FontStretch = source.FontStretch,
            FontStyle = source.FontStyle,
            FontWeight = source.FontWeight,
            Foreground = source.Foreground,
            TextDecorations = source.TextDecorations
        };
    }

    private static Block CreateDisplayFormulaBlock(LatexExpression expression)
    {
        var formula = CreateFormulaElement(expression, true);
        var formulaHost = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Center
        };
        formulaHost.Children.Add(formula);

        var scroller = new ScrollViewer
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Content = formulaHost,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            PanningMode = PanningMode.HorizontalOnly,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled
        };

        var card = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(251, 249, 255)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(232, 223, 246)),
            BorderThickness = new Thickness(1),
            Child = scroller,
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(16, 13, 16, 13)
        };

        return new BlockUIContainer(card)
        {
            Margin = new Thickness(0, 4, 0, 16)
        };
    }

    private static FrameworkElement CreateFormulaElement(LatexExpression expression, bool isDisplay)
    {
        var control = new FormulaControl
        {
            Focusable = false,
            Foreground = new SolidColorBrush(Color.FromRgb(43, 39, 57)),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = isDisplay ? new Thickness(2) : new Thickness(2, 0, 2, 0),
            Scale = isDisplay ? 21 : 17,
            SystemTextFontName = "Malgun Gothic",
            VerticalAlignment = VerticalAlignment.Center
        };
        control.Formula = expression.Formula;

        if (!control.HasError)
        {
            return control;
        }

        var fallback = new TextBlock
        {
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            FontSize = isDisplay ? 14.5 : 14,
            Foreground = new SolidColorBrush(Color.FromRgb(92, 83, 111)),
            Text = isDisplay ? expression.Formula : $"\\({expression.Formula}\\)",
            TextWrapping = TextWrapping.Wrap
        };

        var error = control.Errors.FirstOrDefault();
        if (error is not null)
        {
            fallback.ToolTip = $"이 수식은 아직 예쁘게 표시하지 못했어요.\n{error.Message}";
        }

        return fallback;
    }

    private static void FormatBlocks(BlockCollection blocks)
    {
        foreach (var block in blocks)
        {
            block.LineHeight = 30;
            block.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;

            switch (block)
            {
                case Paragraph paragraph:
                    paragraph.Margin = new Thickness(0, 3, 0, 15);
                    break;
                case Section section:
                    FormatBlocks(section.Blocks);
                    break;
                case List list:
                    list.Margin = new Thickness(0, 4, 0, 14);

                    foreach (var item in list.ListItems)
                    {
                        FormatBlocks(item.Blocks);
                    }

                    break;
                case Table table:
                    table.Margin = new Thickness(0, 6, 0, 16);

                    foreach (var rowGroup in table.RowGroups)
                    {
                        foreach (var row in rowGroup.Rows)
                        {
                            foreach (var cell in row.Cells)
                            {
                                FormatBlocks(cell.Blocks);
                            }
                        }
                    }

                    break;
            }
        }
    }
}
