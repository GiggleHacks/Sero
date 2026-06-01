using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Newtonsoft.Json;
using SeroServer.Net;
using SeroServer.Protocol;

namespace SeroServer.UI;

public partial class ProcessManagerWindow : Window
{
    private readonly TlsServer _server;
    private readonly string    _clientId;
    private readonly ObservableCollection<ProcEntryVM> _procs = [];
    private bool _maximized;

    public ProcessManagerWindow(TlsServer server, string clientId, string label)
    {
        InitializeComponent();
        _server   = server;
        _clientId = clientId;
        TxtTitle.Text = label;

        var view = CollectionViewSource.GetDefaultView(_procs);
        view.Filter = o => o is ProcEntryVM vm &&
            (string.IsNullOrEmpty(TxtSearch.Text) ||
             vm.Name.Contains(TxtSearch.Text, StringComparison.OrdinalIgnoreCase) ||
             vm.Title.Contains(TxtSearch.Text, StringComparison.OrdinalIgnoreCase));
        GridProcs.ItemsSource = view;

        _server.RegisterHandler(clientId, PacketType.ProcListResult, OnProcList);

        Closed += (_, _) => _server.UnregisterHandler(clientId, PacketType.ProcListResult);
        Loaded += async (_, _) => { await Task.Delay(Random.Shared.Next(0, 250)); await Refresh(); };
    }

    private async System.Threading.Tasks.Task Refresh()
    {
        TxtStatus.Text = "Loading…";
        await _server.SendToClient(_clientId, new Packet { Type = PacketType.ProcGetList });
    }

    private void OnProcList(Packet pkt)
    {
        var data = JsonConvert.DeserializeObject<ProcListResultData>(pkt.Data);
        if (data == null) return;
        // Build VMs on background thread (SHGetFileInfo with SHGFI_USEFILEATTRIBUTES is MTA-safe)
        _ = Task.Run(() =>
        {
            var vms = data.Processes.Select(p => new ProcEntryVM(p)).ToList();
            _ = Dispatcher.BeginInvoke(() =>
            {
                _procs.Clear();
                foreach (var vm in vms) _procs.Add(vm);
                TxtCount.Text = $"  {vms.Count} processes";
                TxtStatus.Text = $"{vms.Count} processes loaded";
            });
        });
    }

    private async void BtnRefresh_Click(object s, RoutedEventArgs e) => await Refresh();

    private async void BtnKill_Click(object s, RoutedEventArgs e)
    {
        if (GridProcs.SelectedItem is not ProcEntryVM vm) return;
        var r = MessageBox.Show($"Kill '{vm.Name}' (PID {vm.Pid})?", "Confirm",
            MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (r != MessageBoxResult.Yes) return;
        await _server.SendToClient(_clientId, new Packet
        {
            Type = PacketType.ProcKill,
            Data = Newtonsoft.Json.JsonConvert.SerializeObject(new ProcKillData { Pid = vm.Pid })
        });
        await System.Threading.Tasks.Task.Delay(500);
        await Refresh();
    }

    private void TxtSearch_TextChanged(object s, System.Windows.Controls.TextChangedEventArgs e)
        => CollectionViewSource.GetDefaultView(_procs)?.Refresh();

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

public class ProcEntryVM
{
    public int    Pid        { get; }
    public string Name       { get; }
    public long   Memory     { get; }
    public string MemDisplay { get; }
    public string Title      { get; }
    public string ExePath    { get; }
    public ImageSource? IconImage { get; }

    public ProcEntryVM(ProcEntry e)
    {
        Pid        = e.Pid;
        Name       = e.Name;
        Memory     = e.Memory;
        MemDisplay = e.Memory < 1024 ? $"{e.Memory} KB" : $"{e.Memory / 1024.0:F1} MB";
        Title      = e.Title;
        ExePath    = e.ExePath;
        // Use extension-based icon (exe paths are on the CLIENT, not this machine)
        var ext    = string.IsNullOrEmpty(e.ExePath) ? ".exe"
                     : (System.IO.Path.GetExtension(e.ExePath) is { } x && x.Length > 0 ? x : ".exe");
        IconImage  = ShellIconByPath.Get("file" + ext);
    }
}

internal static class ShellIconByPath
{
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public nint hIcon; public int iIcon; public uint dwAttributes;
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 80)]  public string szTypeName;
    }
    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern nint SHGetFileInfo(string path, uint attr, ref SHFILEINFO shfi, uint sz, uint flags);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool DestroyIcon(nint h);

    private const uint SHGFI_ICON = 0x100, SHGFI_SMALLICON = 0x001, SHGFI_USEFILEATTRIBUTES = 0x010;
    private const uint FILE_ATTRIBUTE_NORMAL = 0x080;

    private static readonly Dictionary<string, ImageSource?> _cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object _lock = new();

    public static ImageSource? Get(string exePath)
    {
        // Cache by filename only to avoid storing full paths
        string key = System.IO.Path.GetFileName(exePath).ToLowerInvariant();
        lock (_lock) { if (_cache.TryGetValue(key, out var c)) return c; }
        var result = Extract(exePath);
        lock (_lock) { _cache.TryAdd(key, result); }
        return result;
    }

    private static ImageSource? Extract(string path)
    {
        try
        {
            var shfi = new SHFILEINFO();
            uint flags = SHGFI_ICON | SHGFI_SMALLICON | SHGFI_USEFILEATTRIBUTES;
            if (SHGetFileInfo(path, FILE_ATTRIBUTE_NORMAL, ref shfi,
                (uint)System.Runtime.InteropServices.Marshal.SizeOf<SHFILEINFO>(), flags) == 0
                || shfi.hIcon == 0) return null;
            try
            {
                var src = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                    shfi.hIcon, System.Windows.Int32Rect.Empty,
                    System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
                src.Freeze(); return src;
            }
            finally { DestroyIcon(shfi.hIcon); }
        }
        catch { return null; }
    }
}
