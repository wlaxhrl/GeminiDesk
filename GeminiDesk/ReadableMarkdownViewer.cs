using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MdXaml;

namespace GeminiDesk;

public sealed class ReadableMarkdownViewer : MarkdownScrollViewer
{
    private bool _formatQueued;

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
            ApplyReadabilityFormatting();
        });
    }

    private void ApplyReadabilityFormatting()
    {
        if (Document is null)
        {
            return;
        }

        Document.PagePadding = new Thickness(0);
        Document.FontFamily = new System.Windows.Media.FontFamily(
            "Segoe UI Variable Text, Segoe UI Emoji, Malgun Gothic");
        Document.FontSize = 15.5;
        Document.LineHeight = 30;
        Document.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;

        FormatBlocks(Document.Blocks);
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
