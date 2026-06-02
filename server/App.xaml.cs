using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace SeroServer;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // WPF hardware acceleration (DirectX) produces random color artifacts on Hyper-V
        // and any basic/virtual display adapter (BlurEffect, DropShadowEffect, animated
        // gradients all trigger DirectX re-renders that fail silently on Basic Display Adapter).
        // Software rendering avoids all GPU compositing — CPU cost is negligible for a UI-only app.
        RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;

        // All WPF Popup-based controls (ContextMenu, ComboBox dropdown, ToolTip, sub-menus)
        // create a Popup with AllowsTransparency=True by default, producing WS_EX_LAYERED windows
        // that cause rendering artifacts on Hyper-V / Basic Display Adapter.
        // Intercept at Initialized (before HWND creation) to force AllowsTransparency=False.
        EventManager.RegisterClassHandler(
            typeof(System.Windows.Controls.Primitives.Popup),
            System.Windows.FrameworkElement.LoadedEvent,
            new RoutedEventHandler(DisablePopupTransparency));

        // ToolTip also uses an internal Popup — disable via Opened (template may not be applied at Init)
        EventManager.RegisterClassHandler(
            typeof(System.Windows.Controls.ToolTip),
            System.Windows.Controls.ToolTip.OpenedEvent,
            new RoutedEventHandler(DisableToolTipPopupTransparency));
    }

    private static void DisablePopupTransparency(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Primitives.Popup p && p.AllowsTransparency)
        {
            try { p.AllowsTransparency = false; } catch { }
        }
    }

    private static void DisableToolTipPopupTransparency(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.ToolTip tt) return;
        try
        {
            // Walk up to the ToolTip's internal Popup via ParentPopup field
            var type = typeof(System.Windows.Controls.ToolTip);
            var fi = type.GetField("_parentPopup",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (fi?.GetValue(tt) is System.Windows.Controls.Primitives.Popup popup && popup.AllowsTransparency)
                popup.AllowsTransparency = false;
        }
        catch { }
    }
}
