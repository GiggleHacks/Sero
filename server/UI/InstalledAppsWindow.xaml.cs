using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Newtonsoft.Json;
using SeroServer.Net;
using SeroServer.Protocol;

namespace SeroServer.UI;

public class InstalledAppVM
{
    public System.Windows.Media.ImageSource? Icon { get; set; }
    public string Name            { get; set; } = "";
    public string Version         { get; set; } = "";
    public string Publisher       { get; set; } = "";
    public string InstallDate     { get; set; } = "";
    public string UninstallString { get; set; } = "";
}

public partial class InstalledAppsWindow : Window
{
    private readonly TlsServer _server;
    private readonly string    _clientId;
    private readonly ObservableCollection<InstalledAppVM> _all  = [];
    private          ObservableCollection<InstalledAppVM> _view = [];

    public InstalledAppsWindow(TlsServer server, string clientId, string label)
    {
        InitializeComponent();
        _server   = server;
        _clientId = clientId;
        TxtTitle.Text = label;
        GridApps.ItemsSource = _view;
        _server.RegisterHandler(clientId, PacketType.InstalledListResult, OnList);
        Closed += (_, _) => _server.UnregisterHandler(clientId, PacketType.InstalledListResult);
        Refresh();
    }

    private void Refresh() => _ = _server.SendToClient(_clientId, new Packet { Type = PacketType.InstalledGetList });

    private void OnList(Packet pkt)
    {
        var d = JsonConvert.DeserializeObject<InstalledListResultData>(pkt.Data);
        if (d == null) return;
        Dispatcher.BeginInvoke(() =>
        {
            _all.Clear();
            foreach (var a in d.Apps)
                _all.Add(new InstalledAppVM { Icon = DecodeIcon(a.IconB64), Name = a.Name, Version = a.Version, Publisher = a.Publisher, InstallDate = a.InstallDate, UninstallString = a.UninstallString });
            ApplyFilter(TxtSearch.Text);
            TxtCount.Text = $"({d.Apps.Count})";
            TxtStatus.Text = $"Updated {DateTime.Now:HH:mm:ss} — {d.Apps.Count} apps";
        });
    }

    private void ApplyFilter(string f)
    {
        _view = string.IsNullOrWhiteSpace(f)
            ? _all
            : new ObservableCollection<InstalledAppVM>(_all.Where(a => a.Name.Contains(f, StringComparison.OrdinalIgnoreCase) || a.Publisher.Contains(f, StringComparison.OrdinalIgnoreCase)));
        GridApps.ItemsSource = _view;
    }

    private void TxtSearch_Changed(object s, TextChangedEventArgs e) => ApplyFilter(TxtSearch.Text);

    private void BtnUninstall_Click(object s, RoutedEventArgs e)
    {
        if (GridApps.SelectedItem is not InstalledAppVM vm || string.IsNullOrEmpty(vm.UninstallString)) return;
        if (MessageBox.Show($"Uninstall \"{vm.Name}\"?", "Confirm", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        _ = _server.SendToClient(_clientId, new Packet { Type = PacketType.InstalledUninstall, Data = JsonConvert.SerializeObject(new InstalledUninstallData { UninstallString = vm.UninstallString }) });
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
    private void Close_Click(object s, RoutedEventArgs e) => Close();

    private static System.Windows.Media.ImageSource? DecodeIcon(string b64)
    {
        if (string.IsNullOrEmpty(b64)) return null;
        try
        {
            var bytes = Convert.FromBase64String(b64);
            using var ms = new System.IO.MemoryStream(bytes);
            var bmp = new System.Windows.Media.Imaging.BitmapImage();
            bmp.BeginInit(); bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            bmp.StreamSource = ms; bmp.EndInit(); bmp.Freeze();
            return bmp;
        }
        catch { return null; }
    }
}
