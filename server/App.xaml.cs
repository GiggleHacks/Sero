using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace SeroServer;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Hyper-V / basic display adapter: DropShadowEffect + AllowsTransparency require
        // DirectX 9 PS 2.0 which the Microsoft Basic Display Adapter doesn't support.
        // Force software rendering to prevent pink/artifact patches on VM desktops.
        // On physical machines with a real GPU this path is never taken.
        if (IsBasicDisplayAdapter())
            RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
    }

    private static bool IsBasicDisplayAdapter()
    {
        try
        {
            // Check for Microsoft Basic Display Adapter via WMI — zero-overhead since
            // it only runs once at startup and only queries a single registry key path.
            using var key = Microsoft.Win32.Registry.LocalMachine
                .OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Video", false);
            if (key == null) return false;
            foreach (var sub in key.GetSubKeyNames())
            {
                using var v = key.OpenSubKey(sub + @"\0000");
                var desc = v?.GetValue("Device Description")?.ToString() ?? "";
                if (desc.Contains("Basic Display", StringComparison.OrdinalIgnoreCase) ||
                    desc.Contains("Microsoft Hyper-V", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        catch { }
        return false;
    }
}
