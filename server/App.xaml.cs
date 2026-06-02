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

        // ContextMenu creates an internal Popup with AllowsTransparency=True by default.
        // This creates a WS_EX_LAYERED window that causes artifacts on Hyper-V.
        // Disable it via reflection on the internal Popup when each ContextMenu opens.
        EventManager.RegisterClassHandler(
            typeof(System.Windows.Controls.ContextMenu),
            System.Windows.Controls.ContextMenu.OpenedEvent,
            new System.Windows.RoutedEventHandler(DisableContextMenuPopupTransparency));
    }

    private static void DisableContextMenuPopupTransparency(object sender, System.Windows.RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.ContextMenu cm) return;
        try
        {
            // The internal Popup field is named differently across WPF versions — try both
            var type = typeof(System.Windows.Controls.ContextMenu);
            var fi = type.GetField("_dropDownPopup",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                ?? type.BaseType?.GetField("_dropDownPopup",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (fi?.GetValue(cm) is System.Windows.Controls.Primitives.Popup popup)
                popup.AllowsTransparency = false;
        }
        catch { }
    }
}
