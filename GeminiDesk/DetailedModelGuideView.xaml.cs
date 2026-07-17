using System.Windows;
using System.Windows.Controls;

namespace GeminiDesk;

public partial class DetailedModelGuideView : UserControl
{
    public DetailedModelGuideView()
    {
        InitializeComponent();
    }

    public event RoutedEventHandler? BackRequested;

    public void ScrollToTop()
    {
        GuideScrollViewer.ScrollToTop();
    }

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        BackRequested?.Invoke(this, e);
    }
}
