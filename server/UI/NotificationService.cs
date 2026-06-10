using System.IO;
using System.Windows.Media;
using System.Drawing;
using System.Windows.Forms;

namespace SeroServer.UI;

public static class NotificationService
{
    private static NotifyIcon? _trayIcon;
    private static MediaPlayer? _player;
    private static bool _enabled;
    private static long _lastConnectSoundTicks;
    private static System.Drawing.Bitmap? _seroBmp;   // kept alive so GetHicon() handle stays valid
    private static System.Drawing.Icon?   _seroIcon;  // notification icon from serofondtransparent.png

    private static string S(string file) =>
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Sounds", file);

    private static readonly string SndIntro = S("Intro.wav");

    // startup_shutdown
    private static readonly string SndStartup    = S("01_startup_chime.mp3");
    private static readonly string SndShutdown   = S("04_shutdown_gentle_descent.mp3");

    // network
    private static readonly string SndConnected    = S("34_device_connected.mp3");
    private static readonly string SndDisconnected = S("35_device_disconnected.mp3");

    // system
    private static readonly string SndSuccess = S("11_success_bright.mp3");
    private static readonly string SndError   = S("13_error_buzz.mp3");

    // retro
    private static readonly string SndCoinCollect = S("41_8bit_coin_collect.mp3");
    private static readonly string SndMailChime   = S("45_mail_chime.mp3");
    private static readonly string SndPowerUp     = S("40_8bit_power_up.mp3");

    // actions
    private static readonly string SndDownload   = S("26_download_complete.mp3");
    private static readonly string SndUpload     = S("27_upload_complete.mp3");
    private static readonly string SndFileDel    = S("23_file_deletion.mp3");
    private static readonly string SndScanDone   = S("20_scan_complete.mp3");

    public static void Initialize(bool enabled)
    {
        _enabled = enabled;

        // sero.ico is a WPF embedded resource — load it via pack URI, not filesystem path
        Icon icon;
        try
        {
            var sri = System.Windows.Application.GetResourceStream(
                new Uri("pack://application:,,,/sero.ico"));
            icon = sri != null ? new Icon(sri.Stream) : SystemIcons.Application;
        }
        catch { icon = SystemIcons.Application; }

        _trayIcon = new NotifyIcon { Icon = icon, Visible = true, Text = "SeroC2 Server" };

        // Build notification icon from serofondtransparent.png (kept alive as static bitmap)
        try
        {
            var sri2 = System.Windows.Application.GetResourceStream(
                new Uri("pack://application:,,,/serofondtransparent.png"));
            if (sri2 != null)
            {
                using var raw = new System.Drawing.Bitmap(sri2.Stream);
                _seroBmp  = new System.Drawing.Bitmap(raw, 256, 256);
                _seroIcon = System.Drawing.Icon.FromHandle(_seroBmp.GetHicon());
            }
        }
        catch { }

        PlayIntro();
    }

    public static void SetEnabled(bool enabled) => _enabled = enabled;

    // ── Always plays (server lifecycle) ──────────────────────────────────────
    public static void PlayIntro()    => PlaySound(SndIntro);
    public static void PlayStartup()  => PlaySound(SndStartup);
    public static void PlayShutdown() => PlaySound(SndShutdown);

    // ── Gated by notification checkbox ───────────────────────────────────────
    public static void NotifyConnected(string clientId, bool isNewHwid = false)
    {
        if (!_enabled) return;
        ShowBalloon(isNewHwid ? "New Client!" : "Client Connected", clientId, ToolTipIcon.Info);
        // Debounce: only play a sound if at least 400ms passed since the last connect sound.
        // Prevents a blast of simultaneous sounds when many clients reconnect at startup.
        long now  = DateTime.UtcNow.Ticks;
        long last = Interlocked.Read(ref _lastConnectSoundTicks);
        if (now - last > TimeSpan.TicksPerMillisecond * 400)
        {
            Interlocked.Exchange(ref _lastConnectSoundTicks, now);
            PlaySound(isNewHwid ? SndPowerUp : SndConnected);
        }
    }

    public static void NotifyDisconnected(string clientId)
    {
        if (!_enabled) return;
        PlaySound(SndDisconnected);
        ShowBalloon("Client Disconnected", clientId, ToolTipIcon.Warning);
    }

    public static void NotifyBuildSuccess() { if (_enabled) PlaySound(SndSuccess); }
    public static void NotifyBuildError()   { if (_enabled) PlaySound(SndError); }

    public static void NotifyClipperTriggered() { if (_enabled) PlaySound(SndCoinCollect); }
    public static void NotifyKeylogReceived()   { if (_enabled) PlaySound(SndMailChime); }
    public static void NotifyAutoTaskDone()     { if (_enabled) PlaySound(SndScanDone); }

    public static void NotifyDownloadComplete() { if (_enabled) PlaySound(SndDownload); }
    public static void NotifyUploadComplete()   { if (_enabled) PlaySound(SndUpload); }
    public static void NotifyFileDeleted()      { if (_enabled) PlaySound(SndFileDel); }

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

    private static void ShowBalloon(string title, string text, ToolTipIcon _)
    {
        if (_trayIcon == null) return;
        // Temporarily set tray icon to serofondtransparent so Windows uses it in the notification
        var orig = _trayIcon.Icon;
        if (_seroIcon != null) _trayIcon.Icon = _seroIcon;
        _trayIcon.ShowBalloonTip(3000, title, text, ToolTipIcon.None);
        if (_seroIcon != null) _trayIcon.Icon = orig;
    }
}
