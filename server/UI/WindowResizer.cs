using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace SeroServer.UI;

/// <summary>
/// Enables native 8-direction resize for WindowStyle=None windows by intercepting WM_NCHITTEST.
/// Call WindowResizer.Enable(this) in any window's constructor or Loaded handler.
/// </summary>
internal static class WindowResizer
{
    private const int WM_NCHITTEST = 0x0084;
    private const int HTLEFT        = 10;
    private const int HTRIGHT       = 11;
    private const int HTTOP         = 12;
    private const int HTTOPLEFT     = 13;
    private const int HTTOPRIGHT    = 14;
    private const int HTBOTTOM      = 15;
    private const int HTBOTTOMLEFT  = 16;
    private const int HTBOTTOMRIGHT = 17;
    private const int HTCLIENT      = 1;
    private const int GripSize      = 8; // pixels from edge

    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT pt);
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }

    public static void Enable(Window window)
    {
        window.SourceInitialized += (_, _) =>
        {
            var hwndSource = HwndSource.FromHwnd(new WindowInteropHelper(window).Handle);
            hwndSource?.AddHook((nint hwnd, int msg, nint wp, nint lp, ref bool handled) =>
            {
                if (msg != WM_NCHITTEST) return 0;
                if (window.WindowState == WindowState.Maximized) return 0;

                GetCursorPos(out var pt);
                double left   = window.Left;
                double top    = window.Top;
                double right  = left + window.ActualWidth;
                double bottom = top  + window.ActualHeight;

                bool onLeft   = pt.X <= left   + GripSize;
                bool onRight  = pt.X >= right  - GripSize;
                bool onTop    = pt.Y <= top    + GripSize;
                bool onBottom = pt.Y >= bottom - GripSize;

                if (onTop    && onLeft)  { handled = true; return HTTOPLEFT;     }
                if (onTop    && onRight) { handled = true; return HTTOPRIGHT;    }
                if (onBottom && onLeft)  { handled = true; return HTBOTTOMLEFT;  }
                if (onBottom && onRight) { handled = true; return HTBOTTOMRIGHT; }
                if (onLeft)              { handled = true; return HTLEFT;        }
                if (onRight)             { handled = true; return HTRIGHT;       }
                if (onTop)               { handled = true; return HTTOP;         }
                if (onBottom)            { handled = true; return HTBOTTOM;      }

                return 0;
            });
        };
    }
}
