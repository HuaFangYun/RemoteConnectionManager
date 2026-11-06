using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace RemoteConnectionManager.App.Views;

/// <summary>Dark caption for top level windows, matching Chrome.Panel.</summary>
internal static class DarkTitleBar
{
    public static void Apply(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        var useDark = 1;
        if (DwmSetWindowAttribute(hwnd, 20, ref useDark, sizeof(int)) != 0)
            DwmSetWindowAttribute(hwnd, 19, ref useDark, sizeof(int));
        var caption = 0x00262525; // COLORREF, BGR: Chrome.Panel
        DwmSetWindowAttribute(hwnd, 35, ref caption, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
}
