using RemoteConnectionManager.Core.Models;
using RemoteConnectionManager.Core.Services;
using Xunit;

namespace RemoteConnectionManager.Core.Tests;

public class ConnectionSearchTests
{
    private static readonly RdpConnection Dc = new() { Name = "DC01", Host = "dc01.clienta.local", Tags = { "domain-controller", "prod" }, Description = "Primary DC" };

    [Theory]
    [InlineData("dc01", true)]
    [InlineData("CLIENTA", true)]
    [InlineData("prod", true)]
    [InlineData("primary dc", true)]
    [InlineData("rdp", true)]
    [InlineData("sql", false)]
    [InlineData("", true)]
    public void MatchesAcrossFields(string filter, bool expected) => Assert.Equal(expected, ConnectionSearch.Matches(Dc, filter));

    [Fact]
    public void FolderMatchesWhenAnyDescendantMatches()
    {
        var folder = new ConnectionFolder { Name = "Client A", Folders = { new ConnectionFolder { Name = "Servers", Connections = { Dc } } } };
        Assert.True(ConnectionSearch.Matches(folder, "dc01"));
        Assert.True(ConnectionSearch.Matches(folder, "client"));
        Assert.False(ConnectionSearch.Matches(folder, "nothing"));
    }
}
