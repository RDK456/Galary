using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace Galary.Services;

public static class Glass
{
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int v, int size);

    /// <summary>Dark titlebar + acrylic backdrop (Windows 11 22H2+). Older builds silently keep the plain tint.</summary>
    public static void Apply(Window w, bool dark = true)
    {
        var h = new WindowInteropHelper(w).Handle;
        int d = dark ? 1 : 0, acrylic = 3, round = 2;
        DwmSetWindowAttribute(h, 20, ref d, 4);     // DWMWA_USE_IMMERSIVE_DARK_MODE
        DwmSetWindowAttribute(h, 33, ref round, 4);    // DWMWA_WINDOW_CORNER_PREFERENCE = round
        DwmSetWindowAttribute(h, 38, ref acrylic, 4);  // DWMWA_SYSTEMBACKDROP_TYPE = transient (acrylic)
        if (HwndSource.FromHwnd(h)?.CompositionTarget is { } ct) ct.BackgroundColor = Colors.Transparent;
    }
}
