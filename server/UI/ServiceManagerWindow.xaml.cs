using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using Newtonsoft.Json;
using SeroServer.Net;
using SeroServer.Protocol;

namespace SeroServer.UI;

public class ServiceEntryVM : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify([CallerMemberName] string? p = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));

    public string Name        { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Description { get; set; } = "";
    public string StartType   { get; set; } = "";
    public string LogOnAs     { get; set; } = "";

    private string _status = "";
    public string Status
    {
        get => _status;
        set { _status = value; Notify(); Notify(nameof(StatusColor)); Notify(nameof(StatusDot)); Notify(nameof(StartTypeColor)); }
    }

    public Brush StatusColor => _status switch
    {
        "Running" => _green,
        "Stopped" => _dim,
        _         => _amber,
    };

    public string StatusDot => _status switch
    {
        "Running" => "●",
        "Stopped" => "○",
        _         => "◌",
    };

    public Brush StartTypeColor => StartType switch
    {
        "Auto"     => _blue,
        "Disabled" => _red,
        _          => _muted,
    };

    public static System.Windows.Media.ImageSource? SvcIcon { get; } = LoadSvcIcon();
    private static System.Windows.Media.ImageSource? LoadSvcIcon()
    {
        var p = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "services.msc");
        return ShellIcon.GetFromPath(p);
    }

    private static readonly Brush _green = Freeze(new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E)));
    private static readonly Brush _dim   = Freeze(new SolidColorBrush(Color.FromRgb(0x40, 0x48, 0x70)));
    private static readonly Brush _amber = Freeze(new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B)));
    private static readonly Brush _blue  = Freeze(new SolidColorBrush(Color.FromRgb(0x4A, 0x85, 0xF5)));
    private static readonly Brush _red   = Freeze(new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44)));
    private static readonly Brush _muted = Freeze(new SolidColorBrush(Color.FromRgb(0x80, 0x90, 0xB4)));
    private static SolidColorBrush Freeze(SolidColorBrush b) { b.Freeze(); return b; }
}

public partial class ServiceManagerWindow : Window
{
    private readonly TlsServer _server;
    private readonly string    _clientId;
    private readonly ObservableCollection<ServiceEntryVM> _services = [];
    private ICollectionView? _view;
    private readonly DispatcherTimer _autoRefresh;
    private int _countdown = 30;

    public ServiceManagerWindow(TlsServer server, string clientId, string label)
    {
        InitializeComponent();
        _server   = server;
        _clientId = clientId;
        TxtTitle.Text = label;

        _view = CollectionViewSource.GetDefaultView(_services);
        _view.Filter = Filter;
        GridServices.ItemsSource = _view;

        TxtSearch.TextChanged += (_, _) => _view?.Refresh();

        _autoRefresh = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _autoRefresh.Tick += AutoRefreshTick;

        _server.RegisterHandler(clientId, PacketType.SvcListResult, OnList);
        _server.RegisterHandler(clientId, PacketType.SvcAck,        OnAck);
        Closed += (_, _) =>
        {
            _autoRefresh.Stop();
            _server.UnregisterHandler(clientId, PacketType.SvcListResult);
            _server.UnregisterHandler(clientId, PacketType.SvcAck);
        };

        _autoRefresh.Start();

        Refresh();
    }

    private bool Filter(object obj)
    {
        if (string.IsNullOrWhiteSpace(TxtSearch.Text)) return true;
        if (obj is not ServiceEntryVM vm) return false;
        var q = TxtSearch.Text.Trim();
        return vm.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase)
            || vm.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
            || vm.Description.Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    private void AutoRefreshTick(object? sender, EventArgs e)
    {
        _countdown--;
        TxtCountdown.Text = $"Auto-refresh in {_countdown}s";
        if (_countdown <= 0)
        {
            _countdown = 30;
            Refresh();
        }
    }

    private void Refresh()
    {
        _countdown = 30;
        TxtStatus.Text = "Refreshing…";
        _ = _server.SendToClient(_clientId, new Packet { Type = PacketType.SvcGetList });
    }

    private void OnList(Packet pkt)
    {
        var d = JsonConvert.DeserializeObject<SvcListResultData>(pkt.Data);
        if (d == null) return;
        Dispatcher.BeginInvoke(() =>
        {
            _services.Clear();
            foreach (var s in d.Services)
                _services.Add(new ServiceEntryVM
                {
                    Name        = s.Name,
                    DisplayName = s.DisplayName.Length > 0 ? s.DisplayName : s.Name,
                    Status      = s.Status,
                    StartType   = s.StartType,
                    Description = s.Description,
                    LogOnAs     = s.LogOnAs,
                });
            TxtCount.Text  = $"({d.Services.Count})";
            TxtStatus.Text = $"Updated {DateTime.Now:HH:mm:ss} — {d.Services.Count} services";
        });
    }

    private void OnAck(Packet pkt)
    {
        var d = JsonConvert.DeserializeObject<SvcAckData>(pkt.Data);
        if (d == null) return;
        Dispatcher.BeginInvoke(() =>
        {
            TxtStatus.Text = d.Success ? "Action completed — refreshing…" : $"Error: {d.Error}";
            if (d.Success) Refresh();
        });
    }

    private void SendAction(PacketType type)
    {
        var sel = GridServices.SelectedItems.Cast<ServiceEntryVM>().ToList();
        if (sel.Count == 0) return;
        string label = type switch
        {
            PacketType.SvcStart   => "Start",
            PacketType.SvcStop    => "Stop",
            PacketType.SvcRestart => "Restart",
            PacketType.SvcDisable => "Disable",
            PacketType.SvcDelete  => "Delete",
            _ => type.ToString()
        };
        var destructive = type is PacketType.SvcStop or PacketType.SvcRestart or PacketType.SvcDisable or PacketType.SvcDelete;
        if (destructive)
        {
            string msg = sel.Count == 1
                ? $"{label} service '{sel[0].DisplayName}'?"
                : $"{label} {sel.Count} services?";
            if (MessageBox.Show(msg, "Confirm", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        }
        foreach (var vm in sel)
            _ = _server.SendToClient(_clientId, new Packet { Type = type, Data = JsonConvert.SerializeObject(new SvcActionData { ServiceName = vm.Name }) });
        TxtStatus.Text = sel.Count == 1 ? $"Sending {label} → {sel[0].DisplayName}…" : $"Sending {label} → {sel.Count} services…";
        ServerWindow.ReportGlobalActivity($"{label} service", sel.Count == 1 ? sel[0].DisplayName : $"{sel.Count} services", "complete");
        ServerWindow.LogGlobal($"[SVC] Sent {label} command for {(sel.Count == 1 ? $"service '{sel[0].DisplayName}'" : $"{sel.Count} services")} on client {_clientId}.");
    }

    private void BtnRefresh_Click(object s, RoutedEventArgs e) => Refresh();
    private void BtnStart_Click  (object s, RoutedEventArgs e) => SendAction(PacketType.SvcStart);
    private void BtnStop_Click   (object s, RoutedEventArgs e) => SendAction(PacketType.SvcStop);
    private void BtnRestart_Click(object s, RoutedEventArgs e) => SendAction(PacketType.SvcRestart);
    private void BtnDisable_Click(object s, RoutedEventArgs e) => SendAction(PacketType.SvcDisable);
    private void BtnDelete_Click (object s, RoutedEventArgs e) => SendAction(PacketType.SvcDelete);

    private void GridServices_CopyName_Click(object s, RoutedEventArgs e)
    {
        if (GridServices.SelectedItem is ServiceEntryVM vm)
            try { System.Windows.Clipboard.SetText(vm.Name); TxtStatus.Text = $"Copied: {vm.DisplayName}"; } catch { }
    }

    private void Window_MouseLeftButtonDown(object s, System.Windows.Input.MouseButtonEventArgs e)
    { if (e.LeftButton == System.Windows.Input.MouseButtonState.Pressed && WindowState != WindowState.Maximized) DragMove(); }

    private void ResizeGrip_DragDelta(object s, DragDeltaEventArgs e)
    { Width = Math.Max(MinWidth, Width + e.HorizontalChange); Height = Math.Max(MinHeight, Height + e.VerticalChange); }

    private bool _max;
    private void BtnMax_Click(object s, RoutedEventArgs e)
    {
        _max = !_max;
        WindowState = _max ? WindowState.Maximized : WindowState.Normal;
        RootBorder.CornerRadius = _max ? new System.Windows.CornerRadius(0) : new System.Windows.CornerRadius(8);
        if (FindName("BtnMax") is System.Windows.Controls.Button btn)
            btn.Content = _max ? "❐" : "☐";
    }
    private void Close_Click(object s, RoutedEventArgs e) => Close();
}
