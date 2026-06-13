using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls.Primitives;
using Newtonsoft.Json;
using SeroServer.Net;
using SeroServer.Protocol;

namespace SeroServer.UI;

public class DeviceEntryVM
{
    public string DeviceId     { get; set; } = "";
    public string Name         { get; set; } = "";
    public string Class        { get; set; } = "";
    public string Status       { get; set; } = "";
    public string Manufacturer { get; set; } = "";
}

public partial class DeviceManagerWindow : Window
{
    private readonly TlsServer _server;
    private readonly string    _clientId;
    private readonly ObservableCollection<DeviceEntryVM> _devices = [];

    public DeviceManagerWindow(TlsServer server, string clientId, string label)
    {
        InitializeComponent();
        _server   = server;
        _clientId = clientId;
        TxtTitle.Text = label;
        GridDevs.ItemsSource = _devices;
        _server.RegisterHandler(clientId, PacketType.DevListResult, OnList);
        Closed += (_, _) => _server.UnregisterHandler(clientId, PacketType.DevListResult);
        Refresh();
    }

    private void Refresh() => _ = _server.SendToClient(_clientId, new Packet { Type = PacketType.DevGetList });

    private void OnList(Packet pkt)
    {
        var d = JsonConvert.DeserializeObject<DevListResultData>(pkt.Data);
        if (d == null) return;
        Dispatcher.BeginInvoke(() =>
        {
            _devices.Clear();
            foreach (var dev in d.Devices)
                _devices.Add(new DeviceEntryVM { DeviceId = dev.DeviceId, Name = dev.Name, Class = dev.Class, Status = dev.Status, Manufacturer = dev.Manufacturer });
            TxtCount.Text = $"({d.Devices.Count})";
            TxtStatus.Text = $"Updated {DateTime.Now:HH:mm:ss} — {d.Devices.Count} devices";
        });
    }

    private void BtnUninstall_Click(object s, RoutedEventArgs e)
    {
        if (GridDevs.SelectedItem is not DeviceEntryVM vm) return;
        if (MessageBox.Show($"Uninstall device \"{vm.Name}\"?\nThis will disable the device until it is reconnected.", "Confirm", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        _ = _server.SendToClient(_clientId, new Packet { Type = PacketType.DevUninstall, Data = JsonConvert.SerializeObject(new DevUninstallData { DeviceId = vm.DeviceId }) });
        TxtStatus.Text = $"Uninstall sent → {vm.Name}";
    }

    private void BtnRefresh_Click(object s, RoutedEventArgs e) => Refresh();

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
    private void GridDevs_CopyName_Click(object s, RoutedEventArgs e)
    {
        if (GridDevs.SelectedItem is DeviceEntryVM vm)
            try { System.Windows.Clipboard.SetText(vm.Name); TxtStatus.Text = $"Copied: {vm.Name}"; } catch { }
    }

    private void GridDevs_CopyId_Click(object s, RoutedEventArgs e)
    {
        if (GridDevs.SelectedItem is DeviceEntryVM vm)
            try { System.Windows.Clipboard.SetText(vm.DeviceId); TxtStatus.Text = $"Copied device ID"; } catch { }
    }

    private void Close_Click(object s, RoutedEventArgs e) => Close();
}
