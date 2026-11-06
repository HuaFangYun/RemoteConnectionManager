using RemoteConnectionManager.App.ViewModels;

namespace RemoteConnectionManager.App.Services;

/// <summary>Where an RDP session's surface currently lives.</summary>
public enum SessionPlacement
{
    Tab,
    Window,
    FullScreen,
}

/// <summary>
/// Opens and closes the separate windows that detached and full screen sessions live in. The view
/// models only say what they want; the WPF windows stay in the App layer.
/// </summary>
public interface ISessionWindows
{
    /// <summary>Opens the session's window, or applies the session's current placement to the open one.</summary>
    void Show(RdpSessionTabViewModel session);

    /// <summary>Closes the session's window. The window tells the session when it has gone, which docks it back.</summary>
    void Close(RdpSessionTabViewModel session);

    void Activate(RdpSessionTabViewModel session);

    /// <summary>The display the session is on now: its window when detached, otherwise the main window.</summary>
    MonitorChoice? CurrentMonitor(RdpSessionTabViewModel session);
}
