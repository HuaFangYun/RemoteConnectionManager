using System.IO;
using System.Net.Sockets;
using System.Text;
using Microsoft.Terminal.Wpf;
using RemoteConnectionManager.Core.Telnet;

namespace RemoteConnectionManager.App.Terminal;

/// <summary>
/// RCM's own telnet client, so telnet works without the optional Windows telnet feature (which
/// needs admin rights to add). The device's output goes straight to the renderer; option
/// negotiation is handled by <see cref="TelnetCodec"/>. Plain text on the wire: telnet has no
/// encryption, so the Send password button warns before using it here.
/// </summary>
public sealed class TelnetConnection : IRcmTerminalConnection
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);

    private readonly string _host;
    private readonly int _port;
    private readonly TelnetCodec _codec = new();
    private readonly object _codecGate = new();
    private readonly object _writeGate = new();
    private readonly Decoder _decoder = new UTF8Encoding(false, false).GetDecoder();
    private TcpClient? _client;
    private NetworkStream? _stream;
    private volatile bool _disposed;

    public event EventHandler<TerminalOutputEventArgs>? TerminalOutput;
    public event EventHandler? Ready;
    public event EventHandler<TerminalExitedEventArgs>? Exited;
    public event Action<string>? Trace;
    public event Action<int, int>? Resized;

    public bool IsStarted { get; private set; }
    public bool HasExited { get; private set; }
    public bool IsForeground { get; set; } = true;
    public bool BracketedPasteEnabled => false;
    public bool UsesWin32InputMode => false;

    public TelnetConnection(string host, int port)
    {
        _host = host;
        _port = port;
    }

    public void Run(int columns, int rows)
    {
        lock (_codecGate) _codec.Resize(columns, rows);

        var client = new TcpClient { NoDelay = true };
        _client = client;
        try
        {
            using var cts = new CancellationTokenSource(ConnectTimeout);
            client.ConnectAsync(_host, _port, cts.Token).AsTask().GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            throw new IOException($"{_host}:{_port} did not answer within {ConnectTimeout.TotalSeconds:0} seconds.");
        }
        catch (SocketException ex)
        {
            throw new IOException($"Could not connect to {_host}:{_port}: {ex.Message}", ex);
        }

        _stream = client.GetStream();
        IsStarted = true;
        Trace?.Invoke($"telnet connected to {_host}:{_port}");
        Ready?.Invoke(this, EventArgs.Empty);

        var message = ReadLoop();
        HasExited = true;
        Trace?.Invoke($"telnet ended: {message}");
        try { Exited?.Invoke(this, new TerminalExitedEventArgs { Message = message }); } catch { }
    }

    private string ReadLoop()
    {
        var buffer = new byte[16 * 1024];
        var data = new List<byte>(buffer.Length);
        var reply = new List<byte>();
        var chars = new char[buffer.Length + 16];
        try
        {
            while (!_disposed)
            {
                var read = _stream!.Read(buffer, 0, buffer.Length);
                if (read == 0) return "connection closed by the device";

                data.Clear();
                reply.Clear();
                lock (_codecGate) _codec.Receive(buffer.AsSpan(0, read), data, reply);
                if (reply.Count > 0) Send(reply.ToArray());
                if (data.Count == 0) continue;

                var count = _decoder.GetChars(data.ToArray(), 0, data.Count, chars, 0, flush: false);
                if (count > 0) Raise(new string(chars, 0, count));
            }
            return "closed";
        }
        catch (Exception) when (_disposed)
        {
            return "closed";
        }
        catch (IOException ex)
        {
            return $"connection lost: {ex.InnerException?.Message ?? ex.Message}";
        }
        catch (ObjectDisposedException)
        {
            return "closed";
        }
    }

    private void Raise(string text)
    {
        try { TerminalOutput?.Invoke(this, new TerminalOutputEventArgs(text)); }
        catch (Exception ex) { Trace?.Invoke($"renderer threw: {ex.Message}"); }
    }

    private void Send(byte[] bytes)
    {
        if (bytes.Length == 0 || _stream is not { } stream) return;
        try
        {
            lock (_writeGate) stream.Write(bytes, 0, bytes.Length);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            Trace?.Invoke($"telnet write failed: {ex.Message}");
        }
    }

    public void WriteToTerm(ReadOnlySpan<char> input)
    {
        if (input.IsEmpty || !IsStarted || HasExited) return;
        Send(TelnetCodec.EncodeInput(input.ToString()));
    }

    public void RaiseRendererOutput(string s)
    {
        if (!string.IsNullOrEmpty(s)) Raise(s);
    }

    public void Resize(int columns, int rows)
    {
        byte[] message;
        lock (_codecGate) message = _codec.Resize(columns, rows);
        Send(message);
        Resized?.Invoke(columns, rows);
    }

    void ITerminalConnection.Start() { }

    void ITerminalConnection.WriteInput(string data)
    {
        if (!string.IsNullOrEmpty(data)) WriteToTerm(data.AsSpan());
    }

    void ITerminalConnection.Resize(uint rows, uint columns) => Resize((int)columns, (int)rows);

    void ITerminalConnection.Close() => Dispose();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _stream?.Dispose(); } catch { }
        try { _client?.Dispose(); } catch { }
    }
}
