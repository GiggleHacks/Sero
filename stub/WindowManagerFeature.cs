using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace SeroStub;

internal static class WindowManagerFeature
{
    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextW(IntPtr hwnd, StringBuilder lpString, int nMaxCount);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(IntPtr hwnd, StringBuilder lpClassName, int nMaxCount);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint lpdwProcessId);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int nCmdShow);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool PostMessageW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, IntPtr lpdwProcessId);
    [DllImport("user32.dll")] private static extern nint SendMessage(IntPtr hwnd, uint msg, nint wParam, nint lParam);

    // icon extraction via process exe
    private static readonly ConcurrentDictionary<string, string> _iconCache = new(StringComparer.OrdinalIgnoreCase);

    private static string GetExeIcon(uint pid)
    {
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById((int)pid);
            var exe = p.MainModule?.FileName;
            if (string.IsNullOrEmpty(exe)) return "";
            return _iconCache.GetOrAdd(exe, path => StubIconHelper.ExtractExeIcon(path));
        }
        catch { return ""; }
    }

    private const int SW_HIDE     = 0;
    private const int SW_SHOW     = 5;
    private const int SW_RESTORE  = 9;
    private const int SW_MINIMIZE = 6;
    private const int SW_MAXIMIZE = 3;
    private const uint WM_CLOSE   = 0x0010;

    internal static string GetList()
    {
        var wins = new List<WindowEntryStub>();
        EnumWindows((hwnd, _) =>
        {
            try
            {
                var titleSb = new StringBuilder(256);
                GetWindowTextW(hwnd, titleSb, 256);
                var title = titleSb.ToString();
                if (string.IsNullOrWhiteSpace(title)) return true;

                var classSb = new StringBuilder(128);
                GetClassNameW(hwnd, classSb, 128);

                GetWindowThreadProcessId(hwnd, out uint pid);
                wins.Add(new WindowEntryStub
                {
                    Handle    = hwnd.ToInt64(),
                    Title     = title,
                    ClassName = classSb.ToString(),
                    Pid       = (int)pid,
                    Visible   = IsWindowVisible(hwnd),
                    IconB64   = GetExeIcon(pid)
                });
            }
            catch { }
            return true;
        }, IntPtr.Zero);

        return JsonSerializer.Serialize(new WinListResultStub { Windows = wins }, SeroJson.Default.WinListResultStub);
    }

    internal static void DoAction(long handle, string action)
    {
        try
        {
            var hwnd = new IntPtr(handle);
            switch (action)
            {
                case "show":     ShowWindow(hwnd, SW_SHOW);     break;
                case "hide":     ShowWindow(hwnd, SW_HIDE);     break;
                case "focus":    SetForegroundWindow(hwnd);     break;
                case "restore":  ShowWindow(hwnd, SW_RESTORE);  break;
                case "minimize": ShowWindow(hwnd, SW_MINIMIZE); break;
                case "maximize": ShowWindow(hwnd, SW_MAXIMIZE); break;
                case "close":    PostMessageW(hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero); break;
                case "kill":
                    GetWindowThreadProcessId(hwnd, out uint pid);
                    if (pid > 0) System.Diagnostics.Process.GetProcessById((int)pid).Kill();
                    break;
            }
        }
        catch { }
    }
}
