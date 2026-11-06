namespace RemoteConnectionManager.Core.Telnet;

/// <summary>
/// The telnet protocol (RFC 854) for RCM's built in client: separates option negotiation from the
/// data stream and answers it. Agrees to the server echoing and suppress go ahead, sends the window
/// size (NAWS) and a terminal type of XTERM, and refuses everything else. Stateful across reads,
/// so an IAC sequence split over two network reads is handled. Not thread safe: one reader.
/// </summary>
public sealed class TelnetCodec
{
    public const byte Iac = 255, Dont = 254, Do = 253, Wont = 252, Will = 251, Sb = 250, Se = 240;
    public const byte OptEcho = 1, OptSuppressGoAhead = 3, OptTerminalType = 24, OptWindowSize = 31;
    private const byte TtypeIs = 0, TtypeSend = 1;

    public const string TerminalType = "XTERM";

    private enum State { Data, Iac, Command, Sub, SubIac }

    private State _state = State.Data;
    private byte _command;
    private readonly List<byte> _sub = new();
    // Options we have agreed to do ourselves, and ones we asked the server to do.
    private readonly HashSet<byte> _local = new();
    private readonly HashSet<byte> _remote = new();
    private int _columns = 80;
    private int _rows = 24;

    /// <summary>True once the server agreed to echo, so the terminal shows what is typed.</summary>
    public bool ServerEchoes => _remote.Contains(OptEcho);

    /// <summary>
    /// Feeds bytes from the network. Plain data goes to <paramref name="data"/>; replies the server
    /// should get go to <paramref name="reply"/>.
    /// </summary>
    public void Receive(ReadOnlySpan<byte> input, List<byte> data, List<byte> reply)
    {
        foreach (var b in input)
        {
            switch (_state)
            {
                case State.Data:
                    if (b == Iac) _state = State.Iac;
                    else if (b != 0) data.Add(b); // CR NUL is a bare carriage return
                    break;

                case State.Iac:
                    switch (b)
                    {
                        case Iac: data.Add(Iac); _state = State.Data; break; // escaped 255
                        case Will or Wont or Do or Dont: _command = b; _state = State.Command; break;
                        case Sb: _sub.Clear(); _state = State.Sub; break;
                        default: _state = State.Data; break; // NOP, GA, AYT and friends
                    }
                    break;

                case State.Command:
                    Negotiate(_command, b, reply);
                    _state = State.Data;
                    break;

                case State.Sub:
                    if (b == Iac) _state = State.SubIac;
                    else _sub.Add(b);
                    break;

                case State.SubIac:
                    if (b == Se)
                    {
                        SubNegotiation(reply);
                        _state = State.Data;
                    }
                    else
                    {
                        if (b == Iac) _sub.Add(Iac);
                        _state = State.Sub;
                    }
                    break;
            }
        }
    }

    private void Negotiate(byte command, byte option, List<byte> reply)
    {
        switch (command)
        {
            case Will:
                if (option is OptEcho or OptSuppressGoAhead)
                {
                    if (_remote.Add(option)) Send(reply, Do, option);
                }
                else Send(reply, Dont, option);
                break;

            case Wont:
                if (_remote.Remove(option)) Send(reply, Dont, option);
                break;

            case Do:
                if (option is OptWindowSize or OptTerminalType or OptSuppressGoAhead)
                {
                    if (_local.Add(option)) Send(reply, Will, option);
                    if (option == OptWindowSize) AppendWindowSize(reply);
                }
                else Send(reply, Wont, option);
                break;

            case Dont:
                if (_local.Remove(option)) Send(reply, Wont, option);
                break;
        }
    }

    private void SubNegotiation(List<byte> reply)
    {
        if (_sub.Count >= 2 && _sub[0] == OptTerminalType && _sub[1] == TtypeSend && _local.Contains(OptTerminalType))
        {
            reply.AddRange([Iac, Sb, OptTerminalType, TtypeIs]);
            reply.AddRange(System.Text.Encoding.ASCII.GetBytes(TerminalType));
            reply.AddRange([Iac, Se]);
        }
    }

    /// <summary>Remembers the size and returns the NAWS message to send, or nothing when the server did not ask for sizes.</summary>
    public byte[] Resize(int columns, int rows)
    {
        _columns = Math.Clamp(columns, 1, ushort.MaxValue);
        _rows = Math.Clamp(rows, 1, ushort.MaxValue);
        if (!_local.Contains(OptWindowSize)) return [];
        var reply = new List<byte>();
        AppendWindowSize(reply);
        return reply.ToArray();
    }

    private void AppendWindowSize(List<byte> reply)
    {
        reply.AddRange([Iac, Sb, OptWindowSize]);
        AddEscaped(reply, (byte)(_columns >> 8));
        AddEscaped(reply, (byte)(_columns & 0xFF));
        AddEscaped(reply, (byte)(_rows >> 8));
        AddEscaped(reply, (byte)(_rows & 0xFF));
        reply.AddRange([Iac, Se]);
    }

    private static void AddEscaped(List<byte> buffer, byte b)
    {
        buffer.Add(b);
        if (b == Iac) buffer.Add(Iac);
    }

    private static void Send(List<byte> reply, byte command, byte option) => reply.AddRange([Iac, command, option]);

    /// <summary>
    /// Turns typed input into bytes for the wire: UTF-8, 255 doubled, and Enter (a bare CR) sent as
    /// CR LF, the telnet end of line that Cisco, HP and Linux all accept.
    /// </summary>
    public static byte[] EncodeInput(string text)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(text);
        var output = new List<byte>(bytes.Length + 4);
        for (var i = 0; i < bytes.Length; i++)
        {
            var b = bytes[i];
            if (b == Iac) { output.Add(Iac); output.Add(Iac); continue; }
            output.Add(b);
            if (b == '\r' && (i + 1 >= bytes.Length || bytes[i + 1] != '\n')) output.Add((byte)'\n');
        }
        return output.ToArray();
    }
}
