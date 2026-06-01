using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using Newtonsoft.Json;
using SeroServer.Net;
using SeroServer.Protocol;

namespace SeroServer.UI;

public partial class StartupManagerWindow : Window
{
    private readonly TlsServer _server;
    private readonly string    _clientId;
    private readonly ObservableCollection<StartupEntryVM> _entries = [];

    public StartupManagerWindow(TlsServer server, string clientId, string clientLabel)
    {
        InitializeComponent();
        _server   = server;
        _clientId = clientId;
        TxtTitle.Text = clientLabel;
        GridStartup.ItemsSource = _entries;

        _server.RegisterHandler(clientId, PacketType.StartupListResult, OnList);
        Closed += (_, _) => _server.UnregisterHandler(clientId, PacketType.StartupListResult);
        Loaded += async (_, _) => await Refresh();
    }

    private async Task Refresh()
    {
        TxtStatus.Text = "Refreshing…";
        await _server.SendToClient(_clientId, new Packet { Type = PacketType.StartupGetList });
    }

    private void OnList(Packet pkt)
    {
        try
        {
            var data = JsonConvert.DeserializeObject<StartupListResultData>(pkt.Data);
            if (data == null) return;
            Dispatcher.Invoke(() =>
            {
                _entries.Clear();
                foreach (var e in data.Entries)
                    _entries.Add(new StartupEntryVM(e.Name, e.Type, e.Location, e.Path));
                TxtStatus.Text = $"{_entries.Count} startup item(s) — {DateTime.Now:HH:mm:ss}";
            });
        }
        catch { }
    }

    private async void Refresh_Click(object s, RoutedEventArgs e) => await Refresh();

    private async void Delete_Click(object s, RoutedEventArgs e)
    {
        if (GridStartup.SelectedItem is not StartupEntryVM row) return;
        if (MessageBox.Show($"Delete startup entry '{row.Name}'?", "Confirm",
            MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

        var data = JsonConvert.SerializeObject(new StartupDeleteData
        {
            Name = row.Name, Type = row.Type, Location = row.Location
        });
        await _server.SendToClient(_clientId, new Packet { Type = PacketType.StartupDelete, Data = data });
        await Task.Delay(500);
        await Refresh();
    }

    private bool _max;
    private void BtnMax_Click(object s, RoutedEventArgs e)
    {
        _max = !_max; WindowState = _max ? WindowState.Maximized : WindowState.Normal;
        BtnMaxStartup.Content = _max ? "❐" : "☐";
    }
    private void Window_MouseLeftButtonDown(object s, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed && WindowState != WindowState.Maximized) DragMove();
    }

    private void ResizeGrip_DragDelta(object s, System.Windows.Controls.Primitives.DragDeltaEventArgs e)
    {
        Width  = Math.Max(MinWidth,  Width  + e.HorizontalChange);
        Height = Math.Max(MinHeight, Height + e.VerticalChange);
    }

    private void Close_Click(object s, RoutedEventArgs e) => Close();
}

public record StartupEntryVM(string Name, string Type, string Location, string Path);
