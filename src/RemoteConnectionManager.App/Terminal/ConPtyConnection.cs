using System.IO;
using System.Text;
using Microsoft.Terminal.Wpf;

namespace RemoteConnectionManager.App.Terminal;

public sealed class TerminalExitedEventArgs : EventArgs
{
    public int? ExitCode { get; init; }
    /// <summary>Why it ended, in words, for a network session (for example "connection closed by the device").</summary>
    public string? Message { get; init; }
}

/// <summary>
/// <see cref="ITerminalConnection"/> backed by Windows ConPTY. Owns the pseudoconsole, the
/// input/output pipes and the child process; raises <see cref="ITerminalConnection.TerminalOutput"/>
/// from the read loop thread. Copied from Drover and trimmed to what a plain shell needs.
/// </summary>
public sealed class ConPtyConnection : IRcmTerminalConnection
{
    private readonly Core.Services.TerminalLaunchSpec? _launch;

    public ConPtyConnection() { }

    /// <summary>A connection that <see cref="Run"/> starts with this command line and working directory.</summary>
    public ConPtyConnection(Core.Services.TerminalLaunchSpec launch) => _launch = launch;

    public event EventHandler<TerminalOutputEventArgs>? TerminalOutput;
    public event Action<int, int>? Resized;
    public bool UsesWin32InputMode => true;

    public void Run(int columns, int rows)
    {
        if (_launch is null) throw new InvalidOperationException("No command line to run.");
        Start(_launch.CommandLine, columns, rows, _launch.WorkingDirectory);
    }

    /// <summary>
    /// Raised once the pseudoconsole and child process are up. Subscribers must be synchronous:
    /// the read loop starts as soon as this returns. The host uses it to hop to the UI thread and
    /// set Terminal.Connection before any output is pumped.
    /// </summary>
    public event EventHandler? Ready;

    /// <summary>Raised on the read loop thread after the child exits and the output pipe closes.</summary>
    public event EventHandler<TerminalExitedEventArgs>? Exited;

    /// <summary>Lifecycle and failure notes for the app log (never content). Raised on whichever thread did the work.</summary>
    public event Action<string>? Trace;

    public bool IsStarted { get; private set; }
    public bool HasExited { get; private set; }
    public int ProcessId => _process?.Pid ?? 0;

    /// <summary>
    /// True while the foreground program has turned on bracketed paste (DECSET 2004). Pastes are
    /// only wrapped in the paste markers when it has, so cmd and older hosts never see them as text.
    /// </summary>
    public bool BracketedPasteEnabled => _bracketedPaste;

    private const int ReadBufferSize = 16 * 1024;

    // Output coalescing: batch VT text before raising TerminalOutput so the renderer, which marshals
    // to the UI thread on every raise, cannot flood the dispatcher. Background tabs flush less often.
    private const int ForegroundCoalesceMs = 8;
    private const int BackgroundCoalesceMs = 200;
    private const int CoalesceMaxChars = ReadBufferSize * 8;

    private volatile bool _isForeground = true;
    private volatile bool _bracketedPaste;

    public bool IsForeground
    {
        get => _isForeground;
        set => _isForeground = value;
    }

    private PseudoConsole? _console;
    private PseudoConsolePipe? _inputPipe;
    private PseudoConsolePipe? _outputPipe;
    private ChildProcess? _process;
    private FileStream? _outputStream;
    private StreamWriter? _inputWriter;
    private bool _disposed;
    private readonly object _gate = new();
    private readonly ManualResetEventSlim _readLoopDone = new();

    /// <summary>
    /// Spins up the pseudoconsole, child process and stdin writer; raises <see cref="Ready"/>
    /// synchronously, then runs the read loop on the caller's thread until the child exits.
    /// Call from a worker thread.
    /// </summary>
    public void Start(string commandLine, int columns, int rows, string? workingDirectory)
    {
        if (IsStarted) throw new InvalidOperationException("ConPtyConnection already started.");

        _inputPipe = new PseudoConsolePipe();
        _outputPipe = new PseudoConsolePipe();
        _console = PseudoConsole.Create(_inputPipe.ReadSide, _outputPipe.WriteSide, columns, rows);

        // The pseudoconsole duplicated its ends of both pipes. Drop ours, otherwise the output pipe
        // can never reach end of file after the shell exits and the tab would stay "running" forever.
        _inputPipe.ReadSide.Dispose();
        _outputPipe.WriteSide.Dispose();

        _process = ChildProcess.Start(commandLine, _console, workingDirectory);
        _outputStream = new FileStream(_outputPipe.ReadSide, FileAccess.Read);
        _inputWriter = new StreamWriter(new FileStream(_inputPipe.WriteSide, FileAccess.Write)) { AutoFlush = true };

        IsStarted = true;
        Trace?.Invoke($"started pid {_process.Pid} at {columns}x{rows}");
        Ready?.Invoke(this, EventArgs.Empty);

        var process = _process;
        Task.Run(() => WatchProcessExit(process));

        ReadLoop();
        _readLoopDone.Set();

        HasExited = true;
        var code = process.ExitCode;
        Trace?.Invoke($"read loop ended, exit code {code?.ToString() ?? "unknown"}");
        try { Exited?.Invoke(this, new TerminalExitedEventArgs { ExitCode = code }); } catch { }
    }

    /// <summary>
    /// Conhost normally closes the output pipe shortly after its last client exits. If it does not,
    /// closing the pseudoconsole forces it, which ends the read loop.
    /// </summary>
    private void WatchProcessExit(ChildProcess process)
    {
        try
        {
            process.WaitForExit(NativeMethods.INFINITE);
            if (_readLoopDone.Wait(1500)) return;
            Trace?.Invoke("shell exited but the output pipe stayed open, closing the pseudoconsole");
            ClosePseudoConsole();
        }
        catch { }
    }

    private void ClosePseudoConsole()
    {
        lock (_gate)
        {
            try { _console?.Dispose(); } catch { }
        }
    }

    private void ReadLoop()
    {
        if (_outputStream is null) return;

        var buffer = new char[ReadBufferSize];
        var coalesce = new StringBuilder(ReadBufferSize * 4);
        using var reader = new StreamReader(_outputStream);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var lastFlushMs = 0L;

        while (true)
        {
            int read;
            try { read = reader.Read(buffer, 0, buffer.Length); }
            catch { break; }
            if (read == 0) break;

            var span = new ReadOnlySpan<char>(buffer, 0, read);
            TrackBracketedPaste(span);
            coalesce.Append(span);

            var nowMs = sw.ElapsedMilliseconds;
            var coalesceMs = _isForeground ? ForegroundCoalesceMs : BackgroundCoalesceMs;
            if (coalesce.Length > 0 && (nowMs - lastFlushMs >= coalesceMs || coalesce.Length >= CoalesceMaxChars))
            {
                FlushCoalesced(coalesce);
                lastFlushMs = nowMs;
            }
        }

        if (coalesce.Length > 0) FlushCoalesced(coalesce);
    }

    private void TrackBracketedPaste(ReadOnlySpan<char> chunk)
    {
        // Cheap scan; a marker split across two reads is missed, which only costs one unwrapped paste.
        var on = chunk.LastIndexOf("\x1b[?2004h".AsSpan());
        var off = chunk.LastIndexOf("\x1b[?2004l".AsSpan());
        if (on < 0 && off < 0) return;
        _bracketedPaste = on > off;
    }

    private void FlushCoalesced(StringBuilder sb)
    {
        var s = sb.ToString();
        sb.Clear();
        try { TerminalOutput?.Invoke(this, new TerminalOutputEventArgs(s)); }
        catch (Exception ex) { Trace?.Invoke($"renderer threw: {ex.Message}"); }
    }

    /// <summary>Writes straight to the child's stdin.</summary>
    public void WriteToTerm(ReadOnlySpan<char> input)
    {
        if (_inputWriter is null) return;
        if (_console is null || _console.IsDisposed) return;
        try { _inputWriter.Write(input); }
        catch (Exception ex) { Trace?.Invoke($"write failed: {ex.Message}"); }
    }

    /// <summary>Pushes text to the renderer only, never to the child. Used for mode switches and the exit notice.</summary>
    public void RaiseRendererOutput(string s)
    {
        if (string.IsNullOrEmpty(s)) return;
        TerminalOutput?.Invoke(this, new TerminalOutputEventArgs(s));
    }

    public void Resize(int columns, int rows)
    {
        _console?.Resize(columns, rows);
        Resized?.Invoke(columns, rows);
    }

    void ITerminalConnection.Start()
    {
        // No-op. The renderer calls this when Connection is assigned; the PTY was started by the host already.
    }

    void ITerminalConnection.WriteInput(string data)
    {
        if (string.IsNullOrEmpty(data)) return;
        WriteToTerm(data.AsSpan());
    }

    void ITerminalConnection.Resize(uint rows, uint columns) => Resize((int)columns, (int)rows);

    void ITerminalConnection.Close() => Dispose();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try { _inputWriter?.Dispose(); } catch { }
        _inputWriter = null;

        try { _process?.Kill(); } catch { }

        // Closing the pseudoconsole ends conhost, which closes the output pipe and lets the read loop
        // finish on its own thread. Never wait for it here: the renderer marshals output to the UI
        // thread, so waiting on the UI thread would deadlock until the timeout.
        ClosePseudoConsole();

        try { _process?.Dispose(); } catch { }
        _process = null;

        try { _outputStream?.Dispose(); } catch { }
        _outputStream = null;

        lock (_gate) { _console = null; }

        try { _inputPipe?.Dispose(); } catch { }
        _inputPipe = null;

        try { _outputPipe?.Dispose(); } catch { }
        _outputPipe = null;
    }
}
