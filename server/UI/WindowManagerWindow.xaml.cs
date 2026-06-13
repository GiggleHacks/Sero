using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls.Primitives;
using Newtonsoft.Json;
using SeroServer.Net;
using SeroServer.Protocol;

namespace SeroServer.UI;

public class WindowEntryVM
{
    public System.Windows.Media.ImageSource? Icon { get; set; }
    public long   Handle     { get; set; }
    public string Title      { get; set; } = "";
    public string ClassName  { get; set; } = "";
    public int    Pid        { get; set; }
    public bool   Visible    { get; set; }
    public string HandleHex  => $"0x{Handle:X8}";
    public string VisibleStr => Visible ? "Yes" : "No";
}

public partial class WindowManagerWindow : Window
{
    private readonly TlsServer _server;
    private readonly string    _clientId;
    private readonly ObservableCollection<WindowEntryVM> _windows = [];

    public WindowManagerWindow(TlsServer server, string clientId, string label)
    {
        InitializeComponent();
        _server   = server;
        _clientId = clientId;
        TxtTitle.Text = label;
        GridWins.ItemsSource = _windows;
        _server.RegisterHandler(clientId, PacketType.WinListResult, OnList);
        Closed += (_, _) => _server.UnregisterHandler(clientId, PacketType.WinListResult);
        Refresh();
    }

    private void Refresh() => _ = _server.SendToClient(_clientId, new Packet { Type = PacketType.WinGetList });

    private void OnList(Packet pkt)
    {
        var d = JsonConvert.DeserializeObject<WinListResultData>(pkt.Data);
        if (d == null) return;
        Dispatcher.BeginInvoke(() =>
        {
            _windows.Clear();
            foreach (var w in d.Windows)
                _windows.Add(new WindowEntryVM { Handle = w.Handle, Title = w.Title, ClassName = w.ClassName, Pid = w.Pid, Visible = w.Visible, Icon = DecodeIcon(w.IconB64) });
            TxtCount.Text = $"({d.Windows.Count})";
            TxtStatus.Text = $"Updated {DateTime.Now:HH:mm:ss} — {d.Windows.Count} windows";
        });
    }

    private void SendAction(string action)
    {
        var sel = GridWins.SelectedItems.Cast<WindowEntryVM>().ToList();
        if (sel.Count == 0) return;
        if (action is "close" or "kill")
        {
            string label = action == "close" ? "Close" : "Kill";
            string detail = action == "kill" ? "\nThe window process will be terminated." : "";
            string msg = sel.Count == 1
                ? $"{label} window '{sel[0].Title}'?{detail}"
                : $"{label} {sel.Count} windows?{detail}";
            if (MessageBox.Show(msg, "Confirm", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        }
        foreach (var vm in sel)
            _ = _server.SendToClient(_clientId, new Packet { Type = PacketType.WinAction, Data = JsonConvert.SerializeObject(new WinActionData { Handle = vm.Handle, Action = action }) });
        TxtStatus.Text = sel.Count == 1 ? $"{action} → {sel[0].Title}" : $"{action} → {sel.Count} windows";
    }

    private void BtnRefresh_Click(object s, RoutedEventArgs e) => Refresh();
    private void BtnShow_Click    (object s, RoutedEventArgs e) => SendAction("show");
    private void BtnHide_Click    (object s, RoutedEventArgs e) => SendAction("hide");
    private void BtnFocus_Click   (object s, RoutedEventArgs e) => SendAction("focus");
    private void BtnRestore_Click (object s, RoutedEventArgs e) => SendAction("restore");
    private void BtnMinimize_Click(object s, RoutedEventArgs e) => SendAction("minimize");
    private void BtnMaximize_Click2(object s, RoutedEventArgs e) => SendAction("maximize");
    private void BtnClose_Click2  (object s, RoutedEventArgs e) => SendAction("close");
    private void BtnKill_Click    (object s, RoutedEventArgs e) => SendAction("kill");
    private void BtnFreeze_Click  (object s, RoutedEventArgs e) => SendAction("freeze");
    private void BtnUnfreeze_Click(object s, RoutedEventArgs e) => SendAction("unfreeze");

    private void Window_MouseLeftButtonDown(object s, System.Windows.Input.MouseButtonEventArgs e)
    { if (e.LeftButton == System.Windows.Input.MouseButtonState.Pressed && WindowState != WindowState.Maximized) DragMove(); }
    private void ResizeGrip_DragDelta(object s, DragDeltaEventArgs e)
    { Width = Math.Max(MinWidth, Width + e.HorizontalChange); Height = Math.Max(MinHeight, Height + e.VerticalChange); }
    
    private bool _max;
    private void BtnMax_Click(object s, RoutedEventArgs e)
    {
        _max = !_max;
        WindowState = _max ? WindowState.Maximized : WindowState.Normal;
        RootBorder.CornerRadius = _max ? new System.Windows.CornerRadius(0) : new System.Windows.CornerRadius(8);
        if (FindName("BtnMax") is System.Windows.Controls.Button btn)
            btn.Content = _max ? "❐" : "☐";
    }
    private void GridWins_CopyTitle_Click(object s, RoutedEventArgs e)
    {
        if (GridWins.SelectedItem is WindowEntryVM vm)
            try { System.Windows.Clipboard.SetText(vm.Title); TxtStatus.Text = $"Copied: {vm.Title}"; } catch { }
    }

    private void GridWins_CopyHandle_Click(object s, RoutedEventArgs e)
    {
        if (GridWins.SelectedItem is WindowEntryVM vm)
            try { System.Windows.Clipboard.SetText(vm.HandleHex); TxtStatus.Text = $"Copied: {vm.HandleHex}"; } catch { }
    }

    private void Close_Click(object s, RoutedEventArgs e) => Close();

    private static readonly System.Windows.Media.ImageSource _fallbackIcon = MakeFallbackIcon();
    private static System.Windows.Media.ImageSource MakeFallbackIcon()
    {
        var dg = new System.Windows.Media.DrawingGroup();
        var frame = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x35, 0x48, 0x80));
        var title = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4A, 0x85, 0xF5));
        var body  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x18, 0x20, 0x40));
        using (var ctx = dg.Open())
        {
            ctx.DrawRoundedRectangle(frame, null, new System.Windows.Rect(0, 0, 16, 13), 1.5, 1.5);
            ctx.DrawRectangle(title, null, new System.Windows.Rect(1, 1, 14, 3.5));
            ctx.DrawRectangle(body,  null, new System.Windows.Rect(1, 4.5, 14, 7.5));
        }
        var img = new System.Windows.Media.DrawingImage(dg);
        img.Freeze();
        return img;
    }

    private static System.Windows.Media.ImageSource? DecodeIcon(string b64)
    {
        if (string.IsNullOrEmpty(b64)) return _fallbackIcon;
        try
        {
            var bytes = Convert.FromBase64String(b64);
            using var ms = new System.IO.MemoryStream(bytes);
            var bmp = new System.Windows.Media.Imaging.BitmapImage();
            bmp.BeginInit(); bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            bmp.StreamSource = ms; bmp.EndInit(); bmp.Freeze();
            return bmp;
        }
        catch { return _fallbackIcon; }
    }
}
