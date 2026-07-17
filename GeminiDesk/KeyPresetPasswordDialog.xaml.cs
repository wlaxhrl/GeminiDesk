using System.Windows;

namespace GeminiDesk;

public partial class KeyPresetPasswordDialog : Window
{
    private readonly bool _requiresConfirmation;

    public string PresetPassword => PasswordBox.Password;

    public KeyPresetPasswordDialog(bool requiresConfirmation)
    {
        InitializeComponent();
        _requiresConfirmation = requiresConfirmation;

        if (requiresConfirmation)
        {
            Title = "키 프리셋 저장";
            HeadingText.Text = "프리셋을 잠글 비밀번호";
            DescriptionText.Text = "다른 PC에서 불러올 때 같은 비밀번호가 필요해요. 잊어버리면 복구할 수 없어요.";
        }
        else
        {
            Title = "키 프리셋 불러오기";
            Height = 285;
            HeadingText.Text = "프리셋 비밀번호";
            DescriptionText.Text = "이 파일을 저장할 때 사용한 비밀번호를 입력해 주세요.";
            ConfirmPasswordPanel.Visibility = Visibility.Collapsed;
        }

        Loaded += (_, _) => PasswordBox.Focus();
    }

    private void ConfirmButton_Click(object sender, RoutedEventArgs e)
    {
        ErrorText.Visibility = Visibility.Collapsed;

        if (string.IsNullOrEmpty(PresetPassword))
        {
            ShowError("비밀번호를 입력해 주세요.", PasswordBox);
            return;
        }

        if (_requiresConfirmation && PresetPassword.Length < 8)
        {
            ShowError("안전을 위해 비밀번호를 8자 이상으로 만들어 주세요.", PasswordBox);
            return;
        }

        if (_requiresConfirmation && PresetPassword != ConfirmPasswordBox.Password)
        {
            ShowError("두 비밀번호가 서로 달라요.", ConfirmPasswordBox);
            return;
        }

        DialogResult = true;
    }

    private void ShowError(string message, System.Windows.Controls.PasswordBox input)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
        input.Focus();
        input.SelectAll();
    }
}
