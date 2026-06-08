using System.IO;
using System.Windows.Media;
using System.Windows;
using System.Drawing;
using System.Windows.Forms;

namespace SeroServer.UI;

public static class NotificationService
{
    private static NotifyIcon? _trayIcon;
    private static MediaPlayer? _player;
    private static bool _enabled;

    private static readonly string ConnectSound    = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Sounds", "connect.mp3");
    private static readonly string DisconnectSound = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Sounds", "disconnect.mp3");

    public static void Initialize(bool enabled)
    {
        _enabled = enabled;

        var iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "sero.ico");
        var icon = File.Exists(iconPath)
            ? new Icon(iconPath)
            : SystemIcons.Application;

        _trayIcon = new NotifyIcon
        {
            Icon    = icon,
            Visible = true,
            Text    = "SeroC2 Server"
        };
    }

    public static void SetEnabled(bool enabled) => _enabled = enabled;

    public static void NotifyConnected(string clientId)
    {
        if (!_enabled) return;
        PlaySound(ConnectSound);
        ShowBalloon("Client Connected", clientId, ToolTipIcon.Info);
    }

    public static void NotifyDisconnected(string clientId)
    {
        if (!_enabled) return;
        PlaySound(DisconnectSound);
        ShowBalloon("Client Disconnected", clientId, ToolTipIcon.Warning);
    }

    public static void Shutdown()
    {
        _trayIcon?.Dispose();
        _trayIcon = null;
    }

    private static void PlaySound(string path)
    {
        if (!File.Exists(path)) return;

        System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
        {
            _player?.Stop();
            _player ??= new MediaPlayer();
            _player.Open(new Uri(path, UriKind.Absolute));
            _player.Play();
        });
    }

    private static void ShowBalloon(string title, string text, ToolTipIcon icon)
    {
        if (_trayIcon == null) return;
        _trayIcon.ShowBalloonTip(3000, title, text, icon);
    }
}
