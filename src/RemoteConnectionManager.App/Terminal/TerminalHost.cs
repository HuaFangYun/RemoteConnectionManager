using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Terminal.Wpf;
using RemoteConnectionManager.Core.Services;

namespace RemoteConnectionManager.App.Terminal;

/// <summary>
/// WPF control that hosts the Windows Terminal renderer (<see cref="TerminalControl"/>) and a
/// <see cref="ConPtyConnection"/> running one local shell. Adapted from Drover's DroverTerminal.
///
/// Lifecycle:
///   1. ctor: build the renderer, hook Loaded and keyboard handling.
///   2. renderer Loaded: apply the theme (needs a PresentationSource), start the PTY on a worker thread.
///   3. PTY Ready (worker thread): Dispatcher.Invoke to attach the connection to the renderer and
///      switch on Win32 input mode, so the renderer is listening before output is pumped.
///   4. read loop runs on the worker thread until the shell exits, then Exited fires on the UI thread.
///
/// The renderer is an HwndHost, so nothing WPF can be drawn over it (airspace). Notices go in the
/// chrome around it, never on top.
/// </summary>
public sealed class TerminalHost : UserControl, IDisposable
{
    public TerminalControl Terminal { get; }
    public IRcmTerminalConnection? Connection { get; private set; }

    /// <summary>What to run under ConPTY. Set before the control is loaded; Restart reuses it.</summary>
    public TerminalLaunchSpec? Launch { get; set; }

    /// <summary>Makes the session instead of <see cref="Launch"/>, for example a telnet connection. Called again on Restart.</summary>
    public Func<IRcmTerminalConnection>? ConnectionFactory { get; set; }

    /// <summary>Name used in the "[... exited]" notice when there is no <see cref="Launch"/>.</summary>
    public string SessionName { get; set; } = "session";

    public TerminalTheme Theme { get; set; } = RcmTerminalTheme.Default;
    public string TerminalFontFamily { get; set; } = "Cascadia Mono";
    public short TerminalFontSize { get; set; } = 12;

    /// <summary>All three fire on the UI thread.</summary>
    public event EventHandler? Started;
    public event EventHandler<TerminalExitedEventArgs>? Exited;
    public event EventHandler<Exception>? StartFailed;

    /// <summary>Diagnostic pipe traffic trace from the connection, forwarded to the app log.</summary>
    public event EventHandler<string>? Trace;

    /// <summary>
    /// Raised on the UI thread for each new session (first start and every restart), before it
    /// starts, so a session log can subscribe to its output from the first byte.
    /// </summary>
    public event EventHandler<TerminalConnectionCreatedEventArgs>? ConnectionCreated;

    private bool _themeApplied;
    private bool _disposed;

    public TerminalHost()
    {
        Terminal = new TerminalControl { AutoResize = true, Focusable = true };
        var grid = new Grid();
        grid.Children.Add(Terminal);
        Content = grid;
        Focusable = true;
        GotFocus += (_, _) => Terminal.Focus();
        Terminal.Loaded += OnTerminalLoaded;
        PreviewKeyDown += OnPreviewKeyDown;
    }

    public bool IsRunning => Connection is { IsStarted: true, HasExited: false };

    public void FocusTerminal() => Terminal.Focus();

    /// <summary>Throttles output flushing for tabs that are not on screen.</summary>
    public void SetForeground(bool foreground)
    {
        if (Connection is { } c) c.IsForeground = foreground;
    }

    private void OnTerminalLoaded(object sender, RoutedEventArgs e)
    {
        // Loaded fires again on reparent; keep the existing session alive.
        if (!_themeApplied)
        {
            Terminal.SetTheme(Theme, TerminalFontFamily, TerminalFontSize);
            _themeApplied = true;
        }
        if (Connection is null) Start();
    }

    /// <summary>Starts the shell if nothing is running. Safe to call before Loaded; it will start then.</summary>
    public void Start()
    {
        if (_disposed || Connection is not null || !Terminal.IsLoaded) return;
        var pty = ConnectionFactory?.Invoke() ?? (Launch is { } launch ? new ConPtyConnection(launch) : null);
        if (pty is null) return;

        Connection = pty;
        pty.Ready += OnPtyReady;
        pty.Exited += OnPtyExited;
        pty.Trace += s => Trace?.Invoke(this, s);

        var cols = Terminal.Columns;
        var rows = Terminal.Rows;
        ConnectionCreated?.Invoke(this, new TerminalConnectionCreatedEventArgs(pty, cols, rows));
        Task.Run(() =>
        {
            try
            {
                pty.Run(cols, rows);
            }
            catch (Exception ex)
            {
                Dispatcher.BeginInvoke(() =>
                {
                    if (ReferenceEquals(Connection, pty)) Connection = null;
                    pty.Dispose();
                    StartFailed?.Invoke(this, ex);
                });
            }
        });
    }

    /// <summary>Kills the current shell if any, clears the screen and starts a fresh one.</summary>
    public void Restart()
    {
        var old = Connection;
        Connection = null;
        if (old is not null)
        {
            old.Ready -= OnPtyReady;
            old.Exited -= OnPtyExited;
            old.RaiseRendererOutput("\x1b[2J\x1b[3J\x1b[H");
            old.Dispose();
        }
        Start();
    }

    private void OnPtyReady(object? sender, EventArgs e)
    {
        // Synchronous hop to the UI thread so the renderer is subscribed before the read loop pumps.
        Dispatcher.Invoke(() =>
        {
            if (sender is not IRcmTerminalConnection pty || !ReferenceEquals(Connection, pty)) return;
            Terminal.Connection = pty;
            Trace?.Invoke(this, $"renderer attached, {Terminal.Columns}x{Terminal.Rows}, loaded={Terminal.IsLoaded} visible={Terminal.IsVisible}");
            // Win32 input mode: the renderer sends full key records, which ConPTY turns back into
            // INPUT_RECORDs, so modifier combinations reach PSReadLine faithfully. A remote device
            // (telnet) would see those records as garbage, so it keeps plain VT keys.
            if (pty.UsesWin32InputMode) pty.RaiseRendererOutput("\x1b[?9001h");
            pty.Resize(Terminal.Columns, Terminal.Rows);
            Started?.Invoke(this, EventArgs.Empty);
            // The tab that just opened is the one on screen; put the caret in it without a click.
            if (IsVisible) Terminal.Focus();
        });
    }

    private void OnPtyExited(object? sender, TerminalExitedEventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (sender is not IRcmTerminalConnection pty || !ReferenceEquals(Connection, pty)) return;
            var name = Launch?.Executable ?? SessionName;
            var code = e.ExitCode is { } c ? $" with code {c}" : string.Empty;
            var notice = e.Message is { } m ? $"{name}: {m}" : $"{name} exited{code}";
            pty.RaiseRendererOutput($"\r\n\x1b[90m[{notice}]\x1b[0m\r\n");
            Exited?.Invoke(this, e);
        });
    }

    // ---------- keyboard ----------

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Handled || Connection is not { } pty) return;
        if (HandleClipboardKey(e)) return;

        // Tab, Shift+Tab and the arrows are claimed by WPF focus navigation before they reach the
        // renderer HWND, so PSReadLine completion and history would move WPF focus instead. Write
        // the VT sequence straight to the PTY. Ctrl/Alt combinations are left to the renderer.
        var mods = Keyboard.Modifiers;
        if (mods != ModifierKeys.None && mods != ModifierKeys.Shift) return;
        var shift = mods == ModifierKeys.Shift;

        string? seq = e.Key switch
        {
            Key.Tab => shift ? "\x1b[Z" : "\t",
            Key.Up => shift ? "\x1b[1;2A" : "\x1b[A",
            Key.Down => shift ? "\x1b[1;2B" : "\x1b[B",
            Key.Right => shift ? "\x1b[1;2C" : "\x1b[C",
            Key.Left => shift ? "\x1b[1;2D" : "\x1b[D",
            _ => null,
        };
        if (seq is null) return;

        try { pty.WriteToTerm(seq.AsSpan()); }
        catch { return; }
        e.Handled = true;
    }

    /// <summary>
    /// Ctrl+C / Ctrl+Insert copy when there is a selection, otherwise fall through so Ctrl+C still
    /// interrupts. Ctrl+Shift+C always copies. Ctrl+V, Ctrl+Shift+V and Shift+Insert paste.
    /// </summary>
    private bool HandleClipboardKey(KeyEventArgs e)
    {
        var mods = Keyboard.Modifiers;
        var ctrl = (mods & ModifierKeys.Control) != 0;
        var shift = (mods & ModifierKeys.Shift) != 0;
        if ((mods & ModifierKeys.Alt) != 0) return false;

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (ctrl && (key == Key.C || key == Key.Insert))
        {
            if (CopySelection()) { e.Handled = true; return true; }
            if (shift && key == Key.C) { e.Handled = true; return true; }
            return false;
        }

        var isPaste = (ctrl && key == Key.V) || (shift && !ctrl && key == Key.Insert);
        if (!isPaste) return false;
        if (PasteFromClipboard()) e.Handled = true;
        return e.Handled;
    }

    // ---------- clipboard ----------

    /// <summary>Copies the renderer's selection. Returns false when nothing is selected.</summary>
    public bool CopySelection()
    {
        string? selected;
        try { selected = Terminal.GetSelectedText(); }
        catch { return false; }
        if (string.IsNullOrEmpty(selected)) return false;

        try { Clipboard.SetText(selected); }
        catch { return false; }

        // Clear the selection like Windows Terminal does, so the next Ctrl+C interrupts.
        SelectionClearer.Clear(Terminal);
        return true;
    }

    public bool PasteFromClipboard()
    {
        if (Connection is null) return false;
        string? text = null;
        string[]? files = null;
        try
        {
            if (Clipboard.ContainsText()) text = Clipboard.GetText();
            else if (Clipboard.ContainsFileDropList()) files = Clipboard.GetFileDropList().Cast<string>().ToArray();
        }
        catch { return false; }

        if (!string.IsNullOrEmpty(text))
        {
            Paste(text);
            return true;
        }
        if (files is { Length: > 0 })
        {
            Paste(string.Join(" ", files.Select(f => f.Contains(' ') ? $"\"{f}\"" : f)));
            return true;
        }
        return false;
    }

    /// <summary>Types text into the session exactly as given (no paste markers). "\r" is Enter.</summary>
    public void SendText(string text)
    {
        if (Connection is not { } pty || string.IsNullOrEmpty(text)) return;
        try { pty.WriteToTerm(text.AsSpan()); } catch { }
    }

    /// <summary>Sends text as typed input. Newlines become Enter; wrapped as a bracketed paste when the shell supports it.</summary>
    public void Paste(string text)
    {
        if (Connection is not { } pty || string.IsNullOrEmpty(text)) return;
        var normalised = text.Replace("\r\n", "\r").Replace("\n", "\r");
        var payload = pty.BracketedPasteEnabled ? "\x1b[200~" + normalised + "\x1b[201~" : normalised;
        try { pty.WriteToTerm(payload.AsSpan()); } catch { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        var c = Connection;
        Connection = null;
        if (c is not null)
        {
            c.Ready -= OnPtyReady;
            c.Exited -= OnPtyExited;
            c.Dispose();
        }
    }

    /// <summary>
    /// TerminalControl has no public ClearSelection; the native export takes the renderer handle
    /// that TerminalContainer keeps privately. Best effort: if the internals move, the selection
    /// simply stays highlighted after a copy.
    /// </summary>
    private static class SelectionClearer
    {
        private static readonly FieldInfo? ContainerField =
            typeof(TerminalControl).GetField("termContainer", BindingFlags.NonPublic | BindingFlags.Instance);
        private static FieldInfo? _terminalHandleField;

        [DllImport("Microsoft.Terminal.Control.dll", CallingConvention = CallingConvention.StdCall)]
        private static extern void TerminalClearSelection(IntPtr terminal);

        public static void Clear(TerminalControl control)
        {
            try
            {
                var container = ContainerField?.GetValue(control);
                if (container is null) return;
                _terminalHandleField ??= container.GetType().GetField("terminal", BindingFlags.NonPublic | BindingFlags.Instance);
                if (_terminalHandleField?.GetValue(container) is IntPtr h && h != IntPtr.Zero)
                    TerminalClearSelection(h);
            }
            catch { }
        }
    }
}

/// <summary>Campbell colours on the app's dark background. COLORREF order (0x00BBGGRR).</summary>
public static class RcmTerminalTheme
{
    public static readonly TerminalTheme Default = new()
    {
        DefaultBackground = 0x0C0C0C,
        DefaultForeground = 0xCCCCCC,
        DefaultSelectionBackground = 0x3D3D3D,
        CursorStyle = CursorStyle.BlinkingBar,
        ColorTable = new uint[]
        {
            0x0C0C0C, 0x1F0FC5, 0x0EA113, 0x009CC1, 0xDA3700, 0x981788, 0xDD963A, 0xCCCCCC,
            0x767676, 0x5648E7, 0x0CC616, 0xA5F1F9, 0xFF783B, 0x9E00B4, 0xD6D661, 0xF2F2F2,
        },
    };
}

/// <summary>A new session for <see cref="TerminalHost.ConnectionCreated"/>, with the size it starts at.</summary>
public sealed class TerminalConnectionCreatedEventArgs(IRcmTerminalConnection connection, int columns, int rows) : EventArgs
{
    public IRcmTerminalConnection Connection { get; } = connection;
    public int Columns { get; } = columns;
    public int Rows { get; } = rows;
}
