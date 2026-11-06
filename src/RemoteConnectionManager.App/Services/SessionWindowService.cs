using System.Windows;
using System.Windows.Interop;
using RemoteConnectionManager.App.ViewModels;
using RemoteConnectionManager.App.Views;

namespace RemoteConnectionManager.App.Services;

/// <summary>One <see cref="DetachedSessionWindow"/> per detached session, created on demand.</summary>
public sealed class SessionWindowService : ISessionWindows
{
    private readonly Dictionary<RdpSessionTabViewModel, DetachedSessionWindow> _windows = new();

    public void Show(RdpSessionTabViewModel session)
    {
        if (!_windows.TryGetValue(session, out var window))
        {
            window = new DetachedSessionWindow(session);
            window.Closed += (_, _) => _windows.Remove(session);
            _windows[session] = window;

            // Start near the main window so it opens on the display the user is using.
            if (Application.Current?.MainWindow is { IsLoaded: true } main && main.WindowState != WindowState.Minimized)
            {
                window.WindowStartupLocation = WindowStartupLocation.Manual;
                window.Left = main.Left + 60;
                window.Top = main.Top + 60;
                window.Width = Math.Max(640, main.ActualWidth - 120);
                window.Height = Math.Max(480, main.ActualHeight - 120);
            }
            window.Show();
        }
        window.ApplyPlacement();
        window.Activate();
    }

    public void Close(RdpSessionTabViewModel session)
    {
        if (_windows.TryGetValue(session, out var window)) window.Close();
    }

    public void Activate(RdpSessionTabViewModel session)
    {
        if (!_windows.TryGetValue(session, out var window)) return;
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate();
    }

    public MonitorChoice? CurrentMonitor(RdpSessionTabViewModel session)
    {
        Window? window = _windows.TryGetValue(session, out var detached) ? detached : Application.Current?.MainWindow;
        var hwnd = window is null ? IntPtr.Zero : new WindowInteropHelper(window).Handle;
        return Monitors.ForWindow(hwnd);
    }
}
