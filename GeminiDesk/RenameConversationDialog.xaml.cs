using System.Windows;

namespace GeminiDesk;

public partial class RenameConversationDialog : Window
{
    public string ConversationTitle => TitleBox.Text.Trim();

    public RenameConversationDialog(string currentTitle)
    {
        InitializeComponent();
        TitleBox.Text = currentTitle;
        TitleBox.SelectAll();
        TitleBox.Focus();
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(ConversationTitle))
        {
            MessageBox.Show(
                "대화 제목을 입력해 주세요.",
                "제목 필요",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            TitleBox.Focus();
            return;
        }

        DialogResult = true;
    }
}
