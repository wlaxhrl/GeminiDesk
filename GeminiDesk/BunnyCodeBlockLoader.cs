using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace GeminiDesk;

internal static class BunnyCodeBlockView
{
    private static readonly Brush CodeBackground = new SolidColorBrush(Color.FromRgb(247, 246, 250));
    private static readonly Brush CodeBorder = new SolidColorBrush(Color.FromRgb(229, 225, 235));
    private static readonly Brush CodeForeground = new SolidColorBrush(Color.FromRgb(48, 44, 58));
    private static readonly Brush HeaderBackground = new SolidColorBrush(Color.FromRgb(241, 239, 245));
    private static readonly Brush MutedForeground = new SolidColorBrush(Color.FromRgb(121, 114, 133));
    private static readonly Brush ButtonHoverBackground = new SolidColorBrush(Color.FromRgb(255, 255, 255));

    public static FrameworkElement Create(string code, string? language = null)
    {
        var codeBox = new TextBox
        {
            AcceptsReturn = true,
            AcceptsTab = true,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            FontFamily = new FontFamily("Cascadia Mono, Consolas, Malgun Gothic"),
            FontSize = 13.5,
            Foreground = CodeForeground,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            IsInactiveSelectionHighlightEnabled = true,
            IsReadOnly = true,
            IsReadOnlyCaretVisible = true,
            Padding = new Thickness(12, 10, 12, 12),
            SelectionBrush = new SolidColorBrush(Color.FromRgb(210, 191, 246)),
            SelectionOpacity = 0.72,
            SpellCheck = { IsEnabled = false },
            Text = code,
            TextWrapping = TextWrapping.NoWrap,
            VerticalAlignment = VerticalAlignment.Top,
            VerticalContentAlignment = VerticalAlignment.Top,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        TextBlock.SetLineHeight(codeBox, 22);
        TextBlock.SetLineStackingStrategy(codeBox, LineStackingStrategy.BlockLineHeight);

        // TextBox caret movement raises RequestBringIntoView. When the code editor is
        // inside the chat's outer ScrollViewer, allowing that routed event to escape
        // can make the entire conversation jump while a selection drag is starting.
        codeBox.AddHandler(
            FrameworkElement.RequestBringIntoViewEvent,
            new RequestBringIntoViewEventHandler((_, e) => e.Handled = true));

        var copyButton = CreateCopyButton(code);
        var languageLabel = new TextBlock
        {
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI, Malgun Gothic"),
            FontSize = 10.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = MutedForeground,
            Text = string.IsNullOrWhiteSpace(language) ? "CODE" : language.Trim().ToUpperInvariant(),
            VerticalAlignment = VerticalAlignment.Center
        };

        var header = new Grid
        {
            Background = HeaderBackground,
            Height = 36
        };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        languageLabel.Margin = new Thickness(12, 0, 8, 0);
        header.Children.Add(languageLabel);
        Grid.SetColumn(copyButton, 1);
        header.Children.Add(copyButton);

        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.Children.Add(header);
        Grid.SetRow(codeBox, 1);
        layout.Children.Add(codeBox);

        var card = new Border
        {
            Background = CodeBackground,
            BorderBrush = CodeBorder,
            BorderThickness = new Thickness(1),
            Child = layout,
            CornerRadius = new CornerRadius(10),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            SnapsToDevicePixels = true
        };

        card.AddHandler(
            FrameworkElement.RequestBringIntoViewEvent,
            new RequestBringIntoViewEventHandler((_, e) => e.Handled = true));

        return card;
    }

    private static Button CreateCopyButton(string code)
    {
        var button = new Button
        {
            Background = Brushes.Transparent,
            BorderBrush = new SolidColorBrush(Color.FromRgb(218, 213, 226)),
            BorderThickness = new Thickness(1),
            Content = "복사",
            Cursor = Cursors.Hand,
            Focusable = false,
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI, Malgun Gothic"),
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = MutedForeground,
            Height = 26,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 5, 7, 5),
            MinWidth = 51,
            Padding = new Thickness(10, 2, 10, 2),
            ToolTip = "코드 전체 복사",
            VerticalContentAlignment = VerticalAlignment.Center
        };
        TextBlock.SetLineHeight(button, 16);
        TextBlock.SetLineStackingStrategy(button, LineStackingStrategy.BlockLineHeight);

        button.Resources[SystemColors.ControlBrushKey] = ButtonHoverBackground;
        button.Click += async (_, _) =>
        {
            var originalContent = button.Content;

            try
            {
                Clipboard.SetText(code);
                button.Content = "복사됨 ✓";
                button.Foreground = new SolidColorBrush(Color.FromRgb(111, 77, 158));
                button.MinWidth = 72;
            }
            catch (Exception exception)
            {
                button.Content = "복사 실패";
                button.ToolTip = $"클립보드에 복사하지 못했어요.\n{exception.Message}";
                button.MinWidth = 70;
            }

            await Task.Delay(1400);

            if (!button.Dispatcher.HasShutdownStarted)
            {
                await button.Dispatcher.InvokeAsync(() =>
                {
                    button.Content = originalContent;
                    button.Foreground = MutedForeground;
                    button.MinWidth = 51;
                    button.ToolTip = "코드 전체 복사";
                }, DispatcherPriority.Background);
            }
        };

        return button;
    }
}
