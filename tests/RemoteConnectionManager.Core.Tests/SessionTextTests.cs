using RemoteConnectionManager.Plugins;
using Xunit;

namespace RemoteConnectionManager.Core.Tests;

public class SessionTextTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 6, 14, 30, 0, TimeSpan.FromHours(11));

    private static UserSession Session(string state, string user = "jsmith", string? domain = "SIVF0", string? client = null,
        string? name = "rdp-tcp#3", DateTimeOffset? logon = null, DateTimeOffset? input = null, DateTimeOffset? disconnect = null) =>
        new(2, user, domain, state, name, client, logon, input, disconnect);

    [Fact]
    public void Summary_counts_active_and_disconnected()
    {
        var report = new SessionReport("srv", Now, [Session("Active"), Session("Active"), Session("Disconnected")]);
        Assert.Equal("2 active, 1 disconnected", SessionText.Summary(report));
        Assert.Equal("Nobody is signed in", SessionText.Summary(new SessionReport("srv", Now, [])));
    }

    [Fact]
    public void Active_session_shows_client_idle_and_logon()
    {
        var s = Session("Active", client: "PAW01", logon: Now.AddHours(-3), input: Now.AddMinutes(-125));
        Assert.Equal("from PAW01, idle 2h 5m, signed in today 11:30", SessionText.Detail(s, Now));
    }

    [Fact]
    public void Recent_input_is_not_called_idle()
    {
        var s = Session("Active", client: "PAW01", input: Now.AddSeconds(-20));
        Assert.Equal("from PAW01", SessionText.Detail(s, Now));
    }

    [Fact]
    public void Disconnected_session_shows_how_long_ago()
    {
        var s = Session("Disconnected", logon: new DateTimeOffset(2026, 11, 4, 8, 5, 0, Now.Offset), disconnect: Now.AddDays(-1).AddHours(-2));
        Assert.Equal("disconnected 1d 2h ago, signed in 4 Nov 08:05", SessionText.Detail(s, Now));
    }

    [Fact]
    public void Console_session_says_so()
    {
        Assert.Equal("on the console", SessionText.Detail(Session("Active", name: "Console"), Now));
    }

    [Fact]
    public void Account_joins_domain_and_user()
    {
        Assert.Equal(@"SIVF0\jsmith", Session("Active").Account);
        Assert.Equal("jsmith", Session("Active", domain: null).Account);
    }

    [Theory]
    [InlineData("jsmith", "SIVF0", true)]
    [InlineData("JSMITH", "sivf0", true)]
    [InlineData("jsmith", "OTHER", false)]
    [InlineData("someone", "SIVF0", false)]
    public void Current_user_match_ignores_case(string user, string domain, bool expected)
    {
        Assert.Equal(expected, SessionText.IsCurrentUser(Session("Active"), user, domain));
    }
}
