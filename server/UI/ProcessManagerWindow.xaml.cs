using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Newtonsoft.Json;
using SeroServer.Net;
using SeroServer.Protocol;

namespace SeroServer.UI;

public class ProcEntryVM : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    private void N([CallerMemberName] string? p = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));

    public int    Pid       { get; set; }
    public int    ParentPid { get; set; }
    public string Name      { get; set; } = "";
    public long   Memory    { get; set; }
    public float  CpuUsage  { get; set; }
    public int           TcpConns  { get; set; }
    public List<string>? RemoteIps { get; set; }
    public string        Title     { get; set; } = "";
    public string        ExePath   { get; set; } = "";
    public bool          IsClient  { get; set; }
    public float         NetKbps   { get; set; }

    public string NetDisplay
    {
        get
        {
            var parts = new System.Collections.Generic.List<string>();
            if (NetKbps >= 1f)
                parts.Add(NetKbps >= 1024f ? $"{NetKbps/1024f:F1} MB/s" : $"{NetKbps:F0} KB/s");
            if (RemoteIps is { Count: > 0 })
            {
                parts.AddRange(RemoteIps.Take(2));
                if (RemoteIps.Count > 2) parts.Add($"+{RemoteIps.Count - 2}");
            }
            else if (TcpConns > 0 && parts.Count == 0)
                parts.Add($"{TcpConns} conn");
            return parts.Count > 0 ? string.Join("  ", parts) : "—";
        }
    }

    // Tree view support
    private int _depth;
    public int Depth { get => _depth; set { _depth = value; N(); N(nameof(TreeIndent)); N(nameof(TreePrefix)); } }
    public Thickness TreeIndent  => new(_depth * 16, 0, 0, 0);
    public string    TreePrefix  => _depth == 0 ? "" : "└─ ";

    public long   TotalRamMb { get; set; }
    public string MemDisplay
    {
        get
        {
            var mb = Memory > 1024 ? $"{Memory / 1024:N0} MB" : $"{Memory:N0} KB";
            if (TotalRamMb > 0)
            {
                float pct = Memory / 1024f / TotalRamMb * 100f;
                return $"{mb}  {pct:F1}%";
            }
            return mb;
        }
    }

    public string CpuDisplay => CpuUsage > 0.05f ? $"{CpuUsage:F1}%" : "—";

    private static readonly Color _cold  = Color.FromRgb(0x0C, 0x0D, 0x18);
    private static readonly Color _warm1 = Color.FromRgb(0x10, 0x25, 0x4A);
    private static readonly Color _warm2 = Color.FromRgb(0x1A, 0x3A, 0x28);
    private static readonly Color _hot1  = Color.FromRgb(0x40, 0x28, 0x10);
    private static readonly Color _hot2  = Color.FromRgb(0x60, 0x14, 0x14);

    public Brush CpuHeatBrush => HeatBrush(CpuUsage);
    public Brush MemHeatBrush => HeatBrush(Memory > 0 ? Math.Min(100f, Memory / 10240f * 100f) : 0f);
    public Brush CpuTextBrush => CpuUsage > 60 ? Brushes.White : new SolidColorBrush(Color.FromRgb(0xAA, 0xB8, 0xD8));
    public Brush MemTextBrush => Memory > 512 * 1024 ? Brushes.White : new SolidColorBrush(Color.FromRgb(0xAA, 0xB8, 0xD8));

    private static Brush HeatBrush(float pct)
    {
        pct = Math.Max(0f, Math.Min(100f, pct));
        Color c;
        if (pct < 5f)        c = _cold;
        else if (pct < 25f)  c = Lerp(_cold, _warm1, (pct - 5f) / 20f);
        else if (pct < 50f)  c = Lerp(_warm1, _warm2, (pct - 25f) / 25f);
        else if (pct < 75f)  c = Lerp(_warm2, _hot1, (pct - 50f) / 25f);
        else                  c = Lerp(_hot1, _hot2, (pct - 75f) / 25f);
        return new SolidColorBrush(c);
    }

    private static Color Lerp(Color a, Color b, float t) =>
        Color.FromRgb(
            (byte)(a.R + (b.R - a.R) * t),
            (byte)(a.G + (b.G - a.G) * t),
            (byte)(a.B + (b.B - a.B) * t));

    private BitmapSource? _icon;
    public BitmapSource? IconImage
    {
        get => _icon;
        set { _icon = value; N(); }
    }
}

public partial class ProcessManagerWindow : Window
{
    private readonly TlsServer _server;
    private readonly string    _clientId;
    private readonly ObservableCollection<ProcEntryVM> _all  = [];
    private          ObservableCollection<ProcEntryVM> _view = [];
    private bool     _maximized;
    private string   _filter   = "";
    private bool     _treeMode = false;
    private readonly DispatcherTimer _autoTimer;

    public ProcessManagerWindow(TlsServer server, string clientId, string label)
    {
        InitializeComponent();
        _server   = server;
        _clientId = clientId;
        TxtTitle.Text = label;
        GridProcs.ItemsSource = _view;

        _autoTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _autoTimer.Tick += (_, _) => RequestRefresh();
        _autoTimer.Start();

        _server.RegisterHandler(clientId, PacketType.ProcListResult, OnProcList);

        Closed += (_, _) =>
        {
            _autoTimer.Stop();
            _server.UnregisterHandler(clientId, PacketType.ProcListResult);
        };

        RequestRefresh();
    }

    private void RequestRefresh()
    {
        _ = _server.SendToClient(_clientId, new Packet { Type = PacketType.ProcGetList });
    }

    private void OnProcList(Packet pkt)
    {
        var d = JsonConvert.DeserializeObject<ProcListResultData>(pkt.Data);
        if (d == null) return;

        _ = Task.Run(() =>
        {
            var totalRam = d.TotalRamMb;
            var stubPid  = d.StubPid;
            // Phase 1: build VM list without icons so the grid appears instantly.
            var vms = d.Processes.Select(p => new ProcEntryVM
            {
                Pid        = p.Pid,
                ParentPid  = p.ParentPid,
                Name       = p.Name,
                Memory     = p.Memory,
                TotalRamMb = totalRam,
                CpuUsage   = p.CpuUsage,
                Title      = p.Title,
                ExePath    = p.ExePath,
                TcpConns   = p.TcpConns,
                RemoteIps  = p.RemoteIps,
                IsClient   = stubPid > 0 && p.Pid == stubPid,
                NetKbps    = p.NetKbps,
            }).ToList();

            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () =>
            {
                var selectedPid = (GridProcs.SelectedItem as ProcEntryVM)?.Pid;
                _all.Clear();
                foreach (var v in vms) _all.Add(v);
                ApplyFilter();
                if (selectedPid.HasValue)
                    GridProcs.SelectedItem = _view.FirstOrDefault(p => p.Pid == selectedPid.Value);
                TxtCount.Text = $"({vms.Count})";
                TxtStatus.Text = $"Updated {DateTime.Now:HH:mm:ss} — {vms.Count} processes";
            });

            // Phase 2: load icons in background, push each one to its VM as it arrives.
            // Cached icons (subsequent refreshes) return instantly from _iconCache.
            foreach (var vm in vms)
            {
                var icon = GetIcon(vm.ExePath);
                if (icon != null)
                {
                    var capture = vm;
                    Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () => capture.IconImage = icon);
                }
            }
        });
    }

    private void ApplyFilter()
    {
        IEnumerable<ProcEntryVM> source = _all;
        if (!string.IsNullOrWhiteSpace(_filter))
            source = _all.Where(p => p.Name.Contains(_filter, StringComparison.OrdinalIgnoreCase)
                                  || p.Title.Contains(_filter, StringComparison.OrdinalIgnoreCase)
                                  || p.Pid.ToString().Contains(_filter));

        var list = _treeMode ? BuildTree(source.ToList()) : source.ToList();

        // Replacing ItemsSource resets sort state; save and restore so user-chosen
        // sort column and direction survive the 2-second auto-refresh cycle.
        var savedSorts  = GridProcs.Items.SortDescriptions
            .Select(sd => new SortDescription(sd.PropertyName, sd.Direction)).ToList();
        var savedArrows = GridProcs.Columns.Select(c => c.SortDirection).ToList();

        _view = new ObservableCollection<ProcEntryVM>(list);
        GridProcs.ItemsSource = _view;

        foreach (var sd in savedSorts)
            GridProcs.Items.SortDescriptions.Add(sd);
        for (int i = 0; i < GridProcs.Columns.Count && i < savedArrows.Count; i++)
            GridProcs.Columns[i].SortDirection = savedArrows[i];
    }

    // Build DFS-ordered tree with depth levels for visual indentation.
    // Processes whose PPID doesn't exist in the list become roots.
    private static List<ProcEntryVM> BuildTree(List<ProcEntryVM> flat)
    {
        var byPid    = flat.ToDictionary(p => p.Pid);
        var children = new Dictionary<int, List<ProcEntryVM>>();

        // Reset depths and group by parent
        foreach (var p in flat)
        {
            p.Depth = 0;
            if (p.ParentPid > 0 && byPid.ContainsKey(p.ParentPid))
            {
                if (!children.TryGetValue(p.ParentPid, out var list)) children[p.ParentPid] = list = [];
                list.Add(p);
            }
        }

        // Collect roots: processes with no parent in the list
        var childSet = children.Values.SelectMany(x => x).Select(x => x.Pid).ToHashSet();
        var roots    = flat.Where(p => !childSet.Contains(p.Pid)).OrderBy(p => p.Name).ToList();

        var result = new List<ProcEntryVM>(flat.Count);
        void Dfs(ProcEntryVM node, int depth)
        {
            node.Depth = depth;
            result.Add(node);
            if (!children.TryGetValue(node.Pid, out var kids)) return;
            foreach (var kid in kids.OrderBy(k => k.Name))
                Dfs(kid, depth + 1);
        }
        foreach (var root in roots) Dfs(root, 0);

        // Append any orphaned processes (DFS-visited set != flat set)
        var visited = result.Select(p => p.Pid).ToHashSet();
        foreach (var p in flat.Where(p => !visited.Contains(p.Pid)))
        { p.Depth = 0; result.Add(p); }

        return result;
    }

    private void BtnTree_Click(object s, RoutedEventArgs e)
    {
        _treeMode = !_treeMode;
        if (s is System.Windows.Controls.Button btn)
            btn.Content = _treeMode ? "⊞ Tree ✓" : "⊞ Tree";
        ApplyFilter();
    }

    private void TxtSearch_TextChanged(object s, TextChangedEventArgs e)
    {
        _filter = TxtSearch.Text.Trim();
        ApplyFilter();
    }

    // Typing any printable character while grid is focused → redirect to search box
    private void GridProcs_PreviewKeyDown(object s, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Escape)
        {
            TxtSearch.Clear();
            e.Handled = true;
            return;
        }
        if (e.Key == System.Windows.Input.Key.Back)
        {
            if (TxtSearch.Text.Length > 0)
                TxtSearch.Text = TxtSearch.Text[..^1];
            e.Handled = true;
            return;
        }
        var c = System.Windows.Input.KeyInterop.VirtualKeyFromKey(e.Key);
        var ch = (char)c;
        if (char.IsLetterOrDigit(ch) || ch == '.' || ch == '_' || ch == '-')
        {
            var str = e.KeyboardDevice.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Shift)
                ? ch.ToString().ToUpper() : ch.ToString().ToLower();
            TxtSearch.Text += str;
            TxtSearch.CaretIndex = TxtSearch.Text.Length;
            e.Handled = true;
        }
    }

    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern nint SHGetFileInfo(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbSFI, uint uFlags);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool DestroyIcon(nint hIcon);
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private struct SHFILEINFO { public nint hIcon; public int iIcon; public uint dwAttributes; [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName; [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName; }
    private const uint SHGFI_ICON           = 0x100;
    private const uint SHGFI_SMALLICON      = 0x001;
    private const uint SHGFI_USEFILEATTRIBS = 0x010;
    private const uint FILE_ATTRIBUTE_NORMAL = 0x80;

    // Cache icons by path to avoid repeated SHGetFileInfo calls
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, BitmapSource?> _iconCache = new();

    private static BitmapSource? GetIcon(string path)
    {
        var key = string.IsNullOrEmpty(path) ? "__generic__" : path;
        if (_iconCache.TryGetValue(key, out var cached)) return cached;

        // SHGetFileInfo (USEFILEATTRIBUTES) + CreateBitmapSourceFromHIcon + Freeze() are
        // safe on background threads — no Dispatcher.Invoke needed, which was causing
        // 150+ synchronous UI-thread round-trips and making the window slow to populate.
        BitmapSource? result = null;
        try
        {
            if (!string.IsNullOrEmpty(path) && System.IO.File.Exists(path))
            {
                using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
                if (icon != null)
                {
                    result = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                        icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    result?.Freeze();
                }
            }
            if (result == null)
            {
                var sfi = new SHFILEINFO();
                var fakePath = string.IsNullOrEmpty(path) ? "unknown.exe"
                    : (System.IO.Path.GetExtension(path).Length > 0 ? System.IO.Path.GetFileName(path) : path + ".exe");
                if (SHGetFileInfo(fakePath, FILE_ATTRIBUTE_NORMAL, ref sfi,
                    (uint)System.Runtime.InteropServices.Marshal.SizeOf<SHFILEINFO>(),
                    SHGFI_ICON | SHGFI_SMALLICON | SHGFI_USEFILEATTRIBS) != 0 && sfi.hIcon != 0)
                {
                    result = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                        sfi.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    result?.Freeze();
                    DestroyIcon(sfi.hIcon);
                }
            }
        }
        catch { }

        _iconCache[key] = result;
        return result;
    }

    private void BtnRefresh_Click(object s, RoutedEventArgs e) => RequestRefresh();

    private void BtnKill_Click(object s, RoutedEventArgs e)
    {
        var sel = GridProcs.SelectedItems.Cast<ProcEntryVM>().ToList();
        if (sel.Count == 0) return;
        string msg = sel.Count == 1
            ? $"Kill process '{sel[0].Name}' (PID {sel[0].Pid})?\nThe process will be terminated immediately."
            : $"Kill {sel.Count} processes?";
        if (MessageBox.Show(msg, "Confirm", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        foreach (var vm in sel)
            _ = _server.SendToClient(_clientId, new Packet { Type = PacketType.ProcKill, Data = JsonConvert.SerializeObject(new ProcKillData { Pid = vm.Pid }) });
        TxtStatus.Text = sel.Count == 1 ? $"Kill → PID {sel[0].Pid} ({sel[0].Name})" : $"Kill → {sel.Count} processes";
        ServerWindow.ReportGlobalActivity("Kill process", sel.Count == 1 ? sel[0].Name : $"{sel.Count} processes", "complete");
        ServerWindow.LogGlobal($"[PROC] Terminated process {(sel.Count == 1 ? $"'{sel[0].Name}' (PID {sel[0].Pid})" : $"{sel.Count} processes")} on client {_clientId}.");
    }

    private void BtnSuspend_Click(object s, RoutedEventArgs e)
    {
        var sel = GridProcs.SelectedItems.Cast<ProcEntryVM>().ToList();
        if (sel.Count == 0) return;
        foreach (var vm in sel)
            _ = _server.SendToClient(_clientId, new Packet { Type = PacketType.ProcSuspend, Data = JsonConvert.SerializeObject(new ProcSuspendData2 { Pid = vm.Pid }) });
        TxtStatus.Text = sel.Count == 1 ? $"Suspend → PID {sel[0].Pid} ({sel[0].Name})" : $"Suspend → {sel.Count} processes";
        ServerWindow.ReportGlobalActivity("Suspend process", sel.Count == 1 ? sel[0].Name : $"{sel.Count} processes", "complete");
        ServerWindow.LogGlobal($"[PROC] Suspended process {(sel.Count == 1 ? $"'{sel[0].Name}' (PID {sel[0].Pid})" : $"{sel.Count} processes")} on client {_clientId}.");
    }

    private void BtnResume_Click(object s, RoutedEventArgs e)
    {
        var sel = GridProcs.SelectedItems.Cast<ProcEntryVM>().ToList();
        if (sel.Count == 0) return;
        foreach (var vm in sel)
            _ = _server.SendToClient(_clientId, new Packet { Type = PacketType.ProcResume, Data = JsonConvert.SerializeObject(new ProcResumeData2 { Pid = vm.Pid }) });
        TxtStatus.Text = sel.Count == 1 ? $"Resume → PID {sel[0].Pid} ({sel[0].Name})" : $"Resume → {sel.Count} processes";
        ServerWindow.ReportGlobalActivity("Resume process", sel.Count == 1 ? sel[0].Name : $"{sel.Count} processes", "complete");
        ServerWindow.LogGlobal($"[PROC] Resumed process {(sel.Count == 1 ? $"'{sel[0].Name}' (PID {sel[0].Pid})" : $"{sel.Count} processes")} on client {_clientId}.");
    }


    private void BtnMaximize_Click(object s, RoutedEventArgs e)
    {
        _maximized = !_maximized;
        WindowState = _maximized ? WindowState.Maximized : WindowState.Normal;
        RootBorder.CornerRadius = _maximized ? new CornerRadius(0) : new CornerRadius(8);
        BtnMaximize.Content = _maximized ? "❐" : "☐";
    }

    private void Window_MouseLeftButtonDown(object s, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.LeftButton == System.Windows.Input.MouseButtonState.Pressed && WindowState != WindowState.Maximized)
            DragMove();
    }

    private void GridProcs_CopyPid_Click(object s, RoutedEventArgs e)
    {
        if (GridProcs.SelectedItem is ProcEntryVM vm)
            try { System.Windows.Clipboard.SetText(vm.Pid.ToString()); TxtStatus.Text = $"Copied PID: {vm.Pid}"; } catch { }
    }

    private void GridProcs_CopyName_Click(object s, RoutedEventArgs e)
    {
        if (GridProcs.SelectedItem is ProcEntryVM vm)
            try { System.Windows.Clipboard.SetText(vm.Name); TxtStatus.Text = $"Copied: {vm.Name}"; } catch { }
    }

    private void GridProcs_CopyPath_Click(object s, RoutedEventArgs e)
    {
        if (GridProcs.SelectedItem is ProcEntryVM vm && !string.IsNullOrEmpty(vm.ExePath))
            try { System.Windows.Clipboard.SetText(vm.ExePath); TxtStatus.Text = $"Copied path: {vm.ExePath}"; } catch { }
    }

    private void ResizeGrip_DragDelta(object s, DragDeltaEventArgs e)
    {
        Width  = Math.Max(MinWidth,  Width  + e.HorizontalChange);
        Height = Math.Max(MinHeight, Height + e.VerticalChange);
    }

    private void Close_Click(object s, RoutedEventArgs e) => Close();
}
