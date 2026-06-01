using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace SeroStub;

internal static class ProcessManagerFeature
{
    [DllImport("ntdll.dll")] private static extern int NtSuspendProcess(IntPtr hProcess);
    [DllImport("ntdll.dll")] private static extern int NtResumeProcess(IntPtr hProcess);
    [DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);

    private const uint PROCESS_SUSPEND_RESUME = 0x0800;

    // ── TCP connection count per PID (via GetExtendedTcpTable) ───────────────
    [DllImport("iphlpapi.dll")] private static extern uint GetExtendedTcpTable(IntPtr pTcpTable, ref uint dwSize, bool sort, int ipVersion, int tableClass, uint reserved);

    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_TCPROW_OWNER_PID { public uint dwState, dwLocalAddr, dwLocalPort, dwRemoteAddr, dwRemotePort, dwOwningPid; }

    private static Dictionary<int, int> GetTcpCountsByPid()
    {
        var result = new Dictionary<int, int>();
        try
        {
            uint size = 0;
            GetExtendedTcpTable(IntPtr.Zero, ref size, false, 2, 5, 0); // AF_INET=2, TCP_TABLE_OWNER_PID_ALL=5
            if (size == 0) return result;
            var buf = Marshal.AllocHGlobal((int)size);
            try
            {
                if (GetExtendedTcpTable(buf, ref size, false, 2, 5, 0) != 0) return result;
                int count = Marshal.ReadInt32(buf);
                int rowSize = Marshal.SizeOf<MIB_TCPROW_OWNER_PID>();
                for (int i = 0; i < count; i++)
                {
                    var row = Marshal.PtrToStructure<MIB_TCPROW_OWNER_PID>(buf + 4 + i * rowSize);
                    int pid = (int)row.dwOwningPid;
                    result.TryGetValue(pid, out int c);
                    result[pid] = c + 1;
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        catch { }
        return result;
    }

    // Per-process CPU sampling: (lastTotalCpuTime, lastSampleTime)
    private static readonly Dictionary<int, (TimeSpan cpu, DateTime ts)> _cpuSamples = [];
    private static readonly int _cpuCount = Environment.ProcessorCount;

    internal static string GetProcessList()
    {
        var now = DateTime.UtcNow;
        var tcpCounts = GetTcpCountsByPid();
        var list = new List<ProcEntryStub>();
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                float cpuPct = 0f;
                try
                {
                    var totalCpu = p.TotalProcessorTime;
                    if (_cpuSamples.TryGetValue(p.Id, out var prev))
                    {
                        var deltaCpu = (totalCpu - prev.cpu).TotalMilliseconds;
                        var deltaMs  = (now - prev.ts).TotalMilliseconds;
                        if (deltaMs > 0)
                            cpuPct = (float)(deltaCpu / (deltaMs * _cpuCount) * 100.0);
                        cpuPct = Math.Max(0f, Math.Min(100f, cpuPct));
                    }
                    _cpuSamples[p.Id] = (totalCpu, now);
                }
                catch { }

                tcpCounts.TryGetValue(p.Id, out int tcpConns);
                list.Add(new ProcEntryStub
                {
                    Pid      = p.Id,
                    Name     = p.ProcessName,
                    Memory   = p.WorkingSet64 / 1024,
                    CpuUsage = cpuPct,
                    TcpConns = tcpConns,
                    Title    = p.MainWindowTitle,
                    ExePath  = GetExePath(p)
                });
            }
            catch { list.Add(new ProcEntryStub { Pid = p.Id, Name = p.ProcessName }); }
            finally { try { p.Dispose(); } catch { } }
        }

        // Remove stale CPU samples for dead processes
        var livePids = new HashSet<int>(list.Select(x => x.Pid));
        foreach (var k in _cpuSamples.Keys.Where(k => !livePids.Contains(k)).ToList())
            _cpuSamples.Remove(k);

        list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return JsonSerializer.Serialize(
            new ProcListResultStub { Processes = list },
            SeroJson.Default.ProcListResultStub);
    }

    internal static bool Kill(int pid)
    {
        try { Process.GetProcessById(pid).Kill(); return true; }
        catch { return false; }
    }

    internal static bool Suspend(int pid)
    {
        var h = OpenProcess(PROCESS_SUSPEND_RESUME, false, pid);
        if (h == IntPtr.Zero) return false;
        try { return NtSuspendProcess(h) >= 0; }
        finally { CloseHandle(h); }
    }

    internal static bool Resume(int pid)
    {
        var h = OpenProcess(PROCESS_SUSPEND_RESUME, false, pid);
        if (h == IntPtr.Zero) return false;
        try { return NtResumeProcess(h) >= 0; }
        finally { CloseHandle(h); }
    }

    private static string GetExePath(Process p)
    {
        try { return p.MainModule?.FileName ?? ""; }
        catch { return ""; }
    }
}

internal class ProcEntryStub
{
    public int    Pid      { get; set; }
    public string Name     { get; set; } = "";
    public long   Memory   { get; set; }
    public float  CpuUsage { get; set; }
    public int    TcpConns { get; set; }
    public string Title    { get; set; } = "";
    public string ExePath  { get; set; } = "";
}
internal class ProcListResultStub { public List<ProcEntryStub> Processes { get; set; } = []; }
internal class ProcKillDataStub   { public int Pid { get; set; } }
