using System.Runtime.InteropServices;

namespace RemoteConnectionManager.App.Services;

/// <summary>
/// One display. Bounds are in the process's own coordinate space (what EnumDisplayMonitors returns
/// for a system DPI aware app), which is also what SetWindowPos takes, so the two always agree.
/// </summary>
public sealed record MonitorChoice(int Number, string DeviceName, bool IsPrimary, int Left, int Top, int Width, int Height)
{
    public string Label => $"Display {Number}{(IsPrimary ? " (main)" : "")}  {Width} x {Height}";

    public bool Contains(int x, int y) => x >= Left && x < Left + Width && y >= Top && y < Top + Height;
}

public static class Monitors
{
    public static IReadOnlyList<MonitorChoice> All()
    {
        var found = new List<(IntPtr Handle, MONITORINFOEX Info)>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (handle, _, _, _) =>
        {
            var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
            if (GetMonitorInfo(handle, ref info)) found.Add((handle, info));
            return true;
        }, IntPtr.Zero);

        // Number left to right, then top to bottom, which matches how people point at screens.
        return found
            .OrderBy(m => m.Info.rcMonitor.Left).ThenBy(m => m.Info.rcMonitor.Top)
            .Select((m, i) => new MonitorChoice(
                i + 1,
                m.Info.szDevice,
                (m.Info.dwFlags & MONITORINFOF_PRIMARY) != 0,
                m.Info.rcMonitor.Left,
                m.Info.rcMonitor.Top,
                m.Info.rcMonitor.Right - m.Info.rcMonitor.Left,
                m.Info.rcMonitor.Bottom - m.Info.rcMonitor.Top))
            .ToList();
    }

    /// <summary>The display a window is mostly on, so "full screen" defaults to where the user is looking.</summary>
    public static MonitorChoice? ForWindow(IntPtr hwnd)
    {
        var all = All();
        if (hwnd == IntPtr.Zero) return all.FirstOrDefault(m => m.IsPrimary) ?? all.FirstOrDefault();
        var handle = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
        if (!GetMonitorInfo(handle, ref info)) return all.FirstOrDefault();
        return all.FirstOrDefault(m => m.DeviceName == info.szDevice) ?? all.FirstOrDefault();
    }

    /// <summary>Finds the same display again after a change, by device name, else by number.</summary>
    public static MonitorChoice? Refresh(MonitorChoice monitor)
    {
        var all = All();
        return all.FirstOrDefault(m => m.DeviceName == monitor.DeviceName) ?? all.FirstOrDefault(m => m.Number == monitor.Number);
    }

    public static (int X, int Y) CursorPosition() => GetCursorPos(out var p) ? (p.X, p.Y) : (int.MinValue, int.MinValue);

    /// <summary>Moves and sizes a window in raw coordinates, bypassing WPF's DIP conversion.</summary>
    public static void Place(IntPtr hwnd, int left, int top, int width, int height, bool topmost = false)
    {
        SetWindowPos(hwnd, topmost ? HWND_TOPMOST : HWND_NOTOPMOST, left, top, width, height, SWP_NOACTIVATE | SWP_FRAMECHANGED);
    }

    private const int MONITORINFOF_PRIMARY = 1;
    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_FRAMECHANGED = 0x0020;
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private static readonly IntPtr HWND_NOTOPMOST = new(-2);

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, IntPtr lprcMonitor, IntPtr dwData);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public int dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);
}
