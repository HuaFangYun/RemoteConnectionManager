using Microsoft.Terminal.Wpf;

namespace RemoteConnectionManager.App.Terminal;

/// <summary>
/// What <see cref="TerminalHost"/> needs from a session: a local process under ConPTY
/// (<see cref="ConPtyConnection"/>: shells and ssh.exe) or a network connection
/// (<see cref="TelnetConnection"/>). All events may be raised on a worker thread.
/// </summary>
public interface IRcmTerminalConnection : ITerminalConnection, IDisposable
{
    /// <summary>
    /// Raised once the session is up and before any output. Subscribers must be synchronous: the
    /// read loop starts as soon as this returns.
    /// </summary>
    event EventHandler? Ready;

    /// <summary>Raised on the read loop thread when the process exits or the connection closes.</summary>
    event EventHandler<TerminalExitedEventArgs>? Exited;

    /// <summary>Lifecycle and failure notes for the app log (never content).</summary>
    event Action<string>? Trace;

    /// <summary>Raised when the renderer resizes the session, with columns then rows.</summary>
    event Action<int, int>? Resized;

    bool IsStarted { get; }
    bool HasExited { get; }

    /// <summary>Throttles output flushing for tabs that are not on screen.</summary>
    bool IsForeground { get; set; }

    /// <summary>True while the program has turned on bracketed paste (DECSET 2004).</summary>
    bool BracketedPasteEnabled { get; }

    /// <summary>
    /// ConPTY understands Win32 input mode, which keeps modifier keys intact for PSReadLine.
    /// A remote device does not, so telnet keeps plain VT input.
    /// </summary>
    bool UsesWin32InputMode { get; }

    /// <summary>
    /// Starts the session at the given size, raises <see cref="Ready"/>, then runs the read loop on
    /// the caller's thread until the session ends. Call from a worker thread. Throws when the
    /// session cannot be started (the host shows the message).
    /// </summary>
    void Run(int columns, int rows);

    /// <summary>Sends input as if typed.</summary>
    void WriteToTerm(ReadOnlySpan<char> input);

    /// <summary>Pushes text to the renderer only, never to the session. Used for mode switches and notices.</summary>
    void RaiseRendererOutput(string s);

    void Resize(int columns, int rows);
}
