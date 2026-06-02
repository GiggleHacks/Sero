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

    public int    Pid      { get; set; }
    public string Name     { get; set; } = "";
    public long   Memory   { get; set; }
    public float  CpuUsage { get; set; }
    public int    TcpConns { get; set; }
    public string Title    { get; set; } = "";
    public string ExePath  { get; set; } = "";
    public string NetDisplay => TcpConns > 0 ? $"{TcpConns} conn" : "—";

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
    private string   _filter = "";
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
            var vms = d.Processes.Select(p => new ProcEntryVM
            {
                Pid        = p.Pid,
                Name       = p.Name,
                Memory     = p.Memory,
                TotalRamMb = totalRam,
                CpuUsage   = p.CpuUsage,
                Title      = p.Title,
                ExePath    = p.ExePath,
                TcpConns   = p.TcpConns,
                IconImage  = GetIcon(p.ExePath)
            }).ToList();

            Dispatcher.BeginInvoke(() =>
            {
                _all.Clear();
                foreach (var v in vms) _all.Add(v);
                ApplyFilter();
                TxtCount.Text = $"({vms.Count})";
                TxtStatus.Text = $"Updated {DateTime.Now:HH:mm:ss} — {vms.Count} processes";
            });
        });
    }

    private void ApplyFilter()
    {
        var filtered = string.IsNullOrWhiteSpace(_filter)
            ? _all
            : new ObservableCollection<ProcEntryVM>(
                _all.Where(p => p.Name.Contains(_filter, StringComparison.OrdinalIgnoreCase)
                             || p.Title.Contains(_filter, StringComparison.OrdinalIgnoreCase)
                             || p.Pid.ToString().Contains(_filter)));
        _view = filtered;
        GridProcs.ItemsSource = _view;
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
    private static BitmapSource? _genericExeIcon;

    private static BitmapSource? GetIcon(string path)
    {
        var key = string.IsNullOrEmpty(path) ? "__generic__" : path;
        if (_iconCache.TryGetValue(key, out var cached)) return cached;

        BitmapSource? result = null;
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            try
            {
                // Try exact path first (works for C:\Windows\* paths that exist on server)
                if (!string.IsNullOrEmpty(path) && System.IO.File.Exists(path))
                {
                    using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
                    if (icon != null)
                    {
                        result = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                            icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                        result?.Freeze();
                        return;
                    }
                }

                // Fallback: SHGetFileInfo with USEFILEATTRIBUTES — no file needed on server.
                // For .exe files returns a generic application icon when file doesn't exist locally.
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
            catch { }
        });

        _iconCache[key] = result;
        return result;
    }

    private void BtnRefresh_Click(object s, RoutedEventArgs e) => RequestRefresh();

    private void BtnKill_Click(object s, RoutedEventArgs e)
    {
        if (GridProcs.SelectedItem is not ProcEntryVM vm) return;
        _ = _server.SendToClient(_clientId, new Packet
        {
            Type = PacketType.ProcKill,
            Data = JsonConvert.SerializeObject(new ProcKillData { Pid = vm.Pid })
        });
        TxtStatus.Text = $"Kill → PID {vm.Pid} ({vm.Name})";
    }

    private void BtnSuspend_Click(object s, RoutedEventArgs e)
    {
        if (GridProcs.SelectedItem is not ProcEntryVM vm) return;
        _ = _server.SendToClient(_clientId, new Packet
        {
            Type = PacketType.ProcSuspend,
            Data = JsonConvert.SerializeObject(new ProcSuspendData2 { Pid = vm.Pid })
        });
        TxtStatus.Text = $"Suspend → PID {vm.Pid} ({vm.Name})";
    }

    private void BtnResume_Click(object s, RoutedEventArgs e)
    {
        if (GridProcs.SelectedItem is not ProcEntryVM vm) return;
        _ = _server.SendToClient(_clientId, new Packet
        {
            Type = PacketType.ProcResume,
            Data = JsonConvert.SerializeObject(new ProcResumeData2 { Pid = vm.Pid })
        });
        TxtStatus.Text = $"Resume → PID {vm.Pid} ({vm.Name})";
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

    private void ResizeGrip_DragDelta(object s, DragDeltaEventArgs e)
    {
        Width  = Math.Max(MinWidth,  Width  + e.HorizontalChange);
        Height = Math.Max(MinHeight, Height + e.VerticalChange);
    }

    private void Close_Click(object s, RoutedEventArgs e) => Close();
}
