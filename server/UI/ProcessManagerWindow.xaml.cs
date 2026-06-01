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

    public string MemDisplay => Memory > 1024 * 1024
        ? $"{Memory / 1024 / 1024:F1} GB"
        : Memory > 1024
            ? $"{Memory / 1024:N0} MB"
            : $"{Memory:N0} KB";

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
            var vms = d.Processes.Select(p => new ProcEntryVM
            {
                Pid      = p.Pid,
                Name     = p.Name,
                Memory   = p.Memory,
                CpuUsage = p.CpuUsage,
                Title    = p.Title,
                ExePath  = p.ExePath,
                TcpConns  = p.TcpConns,
                IconImage = GetIcon(p.ExePath)
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

    private static BitmapSource? GetIcon(string path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        try
        {
            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
            if (icon == null) return null;
            return System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                icon.Handle, Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
        }
        catch { return null; }
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

    private void AutoRefresh_Checked(object s, RoutedEventArgs e)   => _autoTimer.Start();
    private void AutoRefresh_Unchecked(object s, RoutedEventArgs e) => _autoTimer.Stop();

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
