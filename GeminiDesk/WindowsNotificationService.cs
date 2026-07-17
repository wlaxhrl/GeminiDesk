using System.Drawing;
using System.Windows.Forms;

namespace GeminiDesk;

public sealed class WindowsNotificationService : IDisposable
{
    private readonly Action _activateApp;
    private readonly NotifyIcon? _notifyIcon;
    private readonly Icon? _ownedIcon;

    public WindowsNotificationService(bool isSetupInstalled, Action activateApp)
    {
        _activateApp = activateApp;

        if (!isSetupInstalled)
        {
            return;
        }

        try
        {
            _ownedIcon = !string.IsNullOrWhiteSpace(System.Environment.ProcessPath)
                ? Icon.ExtractAssociatedIcon(System.Environment.ProcessPath)
                : null;

            _notifyIcon = new NotifyIcon
            {
                Icon = _ownedIcon ?? SystemIcons.Application,
                Text = "Bunny Desk",
                Visible = false
            };
            _notifyIcon.BalloonTipClicked += NotificationClicked;
            _notifyIcon.DoubleClick += NotificationClicked;
            IsSupported = true;
        }
        catch
        {
            _notifyIcon?.Dispose();
            _ownedIcon?.Dispose();
        }
    }

    public bool IsSupported { get; }

    public void SetEnabled(bool enabled)
    {
        if (_notifyIcon is not null)
        {
            _notifyIcon.Visible = IsSupported && enabled;
        }
    }

    public void ShowReplyCompleted()
    {
        if (!IsSupported || _notifyIcon is null)
        {
            return;
        }

        try
        {
            _notifyIcon.Visible = true;
            _notifyIcon.BalloonTipTitle = "Bunny Desk";
            _notifyIcon.BalloonTipText = "AI 답변이 완성됐어요! 눌러서 확인해 보세요.";
            _notifyIcon.BalloonTipIcon = ToolTipIcon.Info;
            _notifyIcon.ShowBalloonTip(5000);
        }
        catch
        {
            // Windows 알림이 꺼져 있어도 답변 흐름에는 영향을 주지 않습니다.
        }
    }

    private void NotificationClicked(object? sender, EventArgs e)
    {
        _activateApp();
    }

    public void Dispose()
    {
        if (_notifyIcon is not null)
        {
            _notifyIcon.BalloonTipClicked -= NotificationClicked;
            _notifyIcon.DoubleClick -= NotificationClicked;
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
        }

        _ownedIcon?.Dispose();
    }
}
