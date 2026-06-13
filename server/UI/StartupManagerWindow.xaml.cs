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
        Loaded += async (_, _) => { await Task.Delay(Random.Shared.Next(0, 250)); await Refresh(); };
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
            Dispatcher.BeginInvoke(() =>
            {
                _entries.Clear();
                foreach (var e in data.Entries)
                {
                    // Skip COM-class activation tasks: no real path (varies by OS language —
                    // "COM handler" on EN, "Gestionnaire COM" on FR, etc.)
                    bool isComTask = !e.Path.Contains('\\') && !e.Path.Contains('/')
                                  && !e.Path.Contains(':') && !e.Path.StartsWith("%");
                    if (isComTask) continue;
                    _entries.Add(new StartupEntryVM(e.Name, e.Type, e.Location, e.Path, e.Verified, e.Publisher));
                }
                TxtCount.Text  = $"({_entries.Count})";
                TxtStatus.Text = $"Updated {DateTime.Now:HH:mm:ss} — {_entries.Count} startup item(s)";
            });
        }
        catch { }
    }

    private async void Refresh_Click(object s, RoutedEventArgs e) => await Refresh();

    private async void Delete_Click(object s, RoutedEventArgs e)
    {
        var selected = GridStartup.SelectedItems.Cast<StartupEntryVM>().ToList();
        if (selected.Count == 0) return;
        string msg = selected.Count == 1
            ? $"Delete startup entry '{selected[0].Name}'?"
            : $"Delete {selected.Count} startup entries?";
        if (MessageBox.Show(msg, "Confirm", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

        foreach (var row in selected)
        {
            var data = JsonConvert.SerializeObject(new StartupDeleteData { Name = row.Name, Type = row.Type, Location = row.Location });
            await _server.SendToClient(_clientId, new Packet { Type = PacketType.StartupDelete, Data = data });
            await Task.Delay(80);
        }
        ServerWindow.ReportGlobalActivity("Delete startup", selected.Count == 1 ? selected[0].Name : $"{selected.Count} items", "complete");
        ServerWindow.LogGlobal($"[STARTUP] Deleted startup entry {(selected.Count == 1 ? $"'{selected[0].Name}'" : $"{selected.Count} entries")} on client {_clientId}.");
        await Task.Delay(400);
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

    private void GridStartup_CopyName_Click(object s, RoutedEventArgs e)
    {
        if (GridStartup.SelectedItem is StartupEntryVM vm)
            try { System.Windows.Clipboard.SetText(vm.Name); TxtStatus.Text = $"Copied: {vm.Name}"; } catch { }
    }

    private void GridStartup_CopyPath_Click(object s, RoutedEventArgs e)
    {
        if (GridStartup.SelectedItem is StartupEntryVM vm)
            try { System.Windows.Clipboard.SetText(vm.Path); TxtStatus.Text = $"Copied: {vm.Path}"; } catch { }
    }

    private void Close_Click(object s, RoutedEventArgs e) => Close();
}

public record StartupEntryVM(string Name, string Type, string Location, string Path, bool Verified, string Publisher)
{
    public string PublisherDisplay => Verified
        ? (string.IsNullOrEmpty(Publisher) ? "(Verified)" : $"(Verified) {Publisher}")
        : "(Not Verified)";
}
