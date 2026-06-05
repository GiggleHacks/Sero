using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls.Primitives;
using Newtonsoft.Json;
using SeroServer.Net;
using SeroServer.Protocol;

namespace SeroServer.UI;

public class ServiceEntryVM
{
    public System.Windows.Media.ImageSource? Icon { get; set; }
    public string Name        { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Status      { get; set; } = "";
    public string StartType   { get; set; } = "";
    public string Description { get; set; } = "";
}

// Cached services.exe icon — same for every service row
file static class SvcIconCache
{
    public static readonly System.Windows.Media.ImageSource? Icon =
        ShellIcon.GetFromPath(System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System), "services.exe"));
}

public partial class ServiceManagerWindow : Window
{
    private readonly TlsServer _server;
    private readonly string    _clientId;
    private readonly ObservableCollection<ServiceEntryVM> _services = [];

    public ServiceManagerWindow(TlsServer server, string clientId, string label)
    {
        InitializeComponent();
        _server   = server;
        _clientId = clientId;
        TxtTitle.Text = label;
        GridServices.ItemsSource = _services;
        _server.RegisterHandler(clientId, PacketType.SvcListResult, OnList);
        Closed += (_, _) => _server.UnregisterHandler(clientId, PacketType.SvcListResult);
        Refresh();
    }

    private void Refresh() => _ = _server.SendToClient(_clientId, new Packet { Type = PacketType.SvcGetList });

    private void OnList(Packet pkt)
    {
        var d = JsonConvert.DeserializeObject<SvcListResultData>(pkt.Data);
        if (d == null) return;
        Dispatcher.BeginInvoke(() =>
        {
            _services.Clear();
            foreach (var s in d.Services)
                _services.Add(new ServiceEntryVM { Icon = SvcIconCache.Icon, Name = s.Name, DisplayName = s.DisplayName, Status = s.Status, StartType = s.StartType, Description = s.Description });
            TxtCount.Text = $"({d.Services.Count})";
            TxtStatus.Text = $"Updated {DateTime.Now:HH:mm:ss} — {d.Services.Count} services";
        });
    }

    private void SendAction(PacketType type)
    {
        if (GridServices.SelectedItem is not ServiceEntryVM vm) return;
        _ = _server.SendToClient(_clientId, new Packet { Type = type, Data = JsonConvert.SerializeObject(new SvcActionData { ServiceName = vm.Name }) });
        TxtStatus.Text = $"{type} → {vm.Name}";
    }

    private void BtnRefresh_Click(object s, RoutedEventArgs e) => Refresh();
    private void BtnStart_Click  (object s, RoutedEventArgs e) => SendAction(PacketType.SvcStart);
    private void BtnStop_Click   (object s, RoutedEventArgs e) => SendAction(PacketType.SvcStop);
    private void BtnRestart_Click(object s, RoutedEventArgs e) => SendAction(PacketType.SvcRestart);
    private void BtnDisable_Click(object s, RoutedEventArgs e) => SendAction(PacketType.SvcDisable);
    private void BtnDelete_Click (object s, RoutedEventArgs e) => SendAction(PacketType.SvcDelete);

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
