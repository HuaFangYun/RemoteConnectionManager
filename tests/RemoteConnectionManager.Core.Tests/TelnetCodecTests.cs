using RemoteConnectionManager.Core.Telnet;
using Xunit;
using static RemoteConnectionManager.Core.Telnet.TelnetCodec;

namespace RemoteConnectionManager.Core.Tests;

public class TelnetCodecTests
{
    private static (List<byte> Data, List<byte> Reply) Feed(TelnetCodec codec, params byte[] input)
    {
        var data = new List<byte>();
        var reply = new List<byte>();
        codec.Receive(input, data, reply);
        return (data, reply);
    }

    [Fact]
    public void PlainDataPassesThrough()
    {
        var (data, reply) = Feed(new TelnetCodec(), "Username: "u8.ToArray());
        Assert.Equal("Username: "u8.ToArray(), data);
        Assert.Empty(reply);
    }

    [Fact]
    public void AcceptsServerEchoAndSuppressGoAhead()
    {
        var codec = new TelnetCodec();
        var (data, reply) = Feed(codec, Iac, Will, OptEcho, Iac, Will, OptSuppressGoAhead, (byte)'>');
        Assert.Equal(new[] { (byte)'>' }, data);
        Assert.Equal(new byte[] { Iac, Do, OptEcho, Iac, Do, OptSuppressGoAhead }, reply);
        Assert.True(codec.ServerEchoes);

        // Repeating WILL ECHO must not start a negotiation loop.
        Assert.Empty(Feed(codec, Iac, Will, OptEcho).Reply);
    }

    [Fact]
    public void RefusesUnknownOptions()
    {
        var (_, reply) = Feed(new TelnetCodec(), Iac, Do, 39, Iac, Will, 5);
        Assert.Equal(new byte[] { Iac, Wont, 39, Iac, Dont, 5 }, reply);
    }

    [Fact]
    public void SendsWindowSizeWhenAskedAndOnResize()
    {
        var codec = new TelnetCodec();
        Assert.Empty(codec.Resize(120, 40)); // not asked yet
        var (_, reply) = Feed(codec, Iac, Do, OptWindowSize);
        Assert.Equal(new byte[] { Iac, Will, OptWindowSize, Iac, Sb, OptWindowSize, 0, 120, 0, 40, Iac, Se }, reply);
        Assert.Equal(new byte[] { Iac, Sb, OptWindowSize, 1, 0, 0, 50, Iac, Se }, codec.Resize(256, 50));
    }

    [Fact]
    public void WindowSizeOf255IsEscaped()
    {
        var codec = new TelnetCodec();
        Feed(codec, Iac, Do, OptWindowSize);
        Assert.Equal(new byte[] { Iac, Sb, OptWindowSize, 0, 255, 255, 0, 24, Iac, Se }, codec.Resize(255, 24));
    }

    [Fact]
    public void AnswersTerminalTypeRequest()
    {
        var codec = new TelnetCodec();
        Feed(codec, Iac, Do, OptTerminalType);
        var (_, reply) = Feed(codec, Iac, Sb, OptTerminalType, 1, Iac, Se);
        Assert.Equal(new byte[] { Iac, Sb, OptTerminalType, 0, (byte)'X', (byte)'T', (byte)'E', (byte)'R', (byte)'M', Iac, Se }, reply);
    }

    [Fact]
    public void CommandSplitAcrossReadsAndEscaped255()
    {
        var codec = new TelnetCodec();
        var first = Feed(codec, (byte)'a', Iac);
        var second = Feed(codec, Will, OptEcho, Iac, Iac, (byte)'b', (byte)'\r', 0);
        Assert.Equal(new[] { (byte)'a' }, first.Data);
        Assert.Equal(new byte[] { 255, (byte)'b', (byte)'\r' }, second.Data);
        Assert.Equal(new byte[] { Iac, Do, OptEcho }, second.Reply);
    }

    [Fact]
    public void EnterBecomesCrLfAnd255IsDoubled()
    {
        Assert.Equal("show ver\r\n"u8.ToArray(), EncodeInput("show ver\r"));
        Assert.Equal("a\r\nb"u8.ToArray(), EncodeInput("a\r\nb"));
        Assert.Equal(new byte[] { 0xC3, 0xBF }, EncodeInput("ÿ")); // UTF-8 never produces a lone 255
    }
}
