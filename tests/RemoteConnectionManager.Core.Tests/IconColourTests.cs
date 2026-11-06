using System.Text.Json;
using RemoteConnectionManager.Core.Models;
using RemoteConnectionManager.Core.Services;
using Xunit;

namespace RemoteConnectionManager.Core.Tests;

public class IconColourTests
{
    [Theory]
    [InlineData("#f85149", "#F85149")]
    [InlineData("f85149", "#F85149")]
    [InlineData(" #F00 ", "#FF0000")]
    [InlineData("red", "#F85149")]
    [InlineData("Green", "#3FB950")]
    [InlineData("", null)]
    [InlineData(null, null)]
    [InlineData("#12345", null)]
    [InlineData("#GGGGGG", null)]
    [InlineData("prod", null)]
    public void NormaliseAcceptsHexAndPresetNames(string? input, string? expected) =>
        Assert.Equal(expected, IconColours.Normalise(input));

    [Fact]
    public void EntryColourBeatsFolderColour()
    {
        var folder = new ConnectionFolder { Name = "Prod", Defaults = new ConnectionDefaults { IconColour = "#F85149" } };
        var entry = new RdpConnection { Name = "sql01", Host = "sql01", IconColour = "#3FB950" };

        Assert.Equal("#3FB950", SettingsResolver.ResolveIconColour(entry, new[] { folder }));
    }

    [Fact]
    public void NearestFolderWinsAndBlankInherits()
    {
        var outer = new ConnectionFolder { Name = "Customer", Defaults = new ConnectionDefaults { IconColour = "#58A6FF" } };
        var inner = new ConnectionFolder { Name = "Prod", Defaults = new ConnectionDefaults { IconColour = "#F85149" } };
        var plain = new ConnectionFolder { Name = "SQL" };
        var entry = new RdpConnection { Name = "sql01", Host = "sql01" };

        Assert.Equal("#F85149", SettingsResolver.ResolveIconColour(entry, new[] { outer, inner, plain }));
        Assert.Equal("#58A6FF", SettingsResolver.ResolveIconColour(entry, new[] { outer, plain }));
        Assert.Null(SettingsResolver.ResolveIconColour(entry, new[] { plain }));
    }

    [Fact]
    public void UnreadableColourIsTreatedAsNotSet()
    {
        var folder = new ConnectionFolder { Name = "Prod", Defaults = new ConnectionDefaults { IconColour = "#F85149" } };
        var entry = new RdpConnection { Name = "x", Host = "x", IconColour = "nonsense" };

        Assert.Equal("#F85149", SettingsResolver.ResolveIconColour(entry, new[] { folder }));
    }

    [Fact]
    public void ColourRoundTripsAndCopiesOnDuplicate()
    {
        var doc = new ConnectionsDocument();
        var folder = new ConnectionFolder { Name = "Prod", Defaults = new ConnectionDefaults { IconColour = "#F85149" } };
        folder.Connections.Add(new SshConnection { Name = "sw01", Host = "sw01", IconColour = "#E3A008" });
        doc.Folders.Add(folder);

        var json = JsonSerializer.Serialize(doc, JsonDefaults.Options);
        var back = JsonSerializer.Deserialize<ConnectionsDocument>(json, JsonDefaults.Options)!;

        Assert.Equal("#F85149", back.Folders[0].Defaults.IconColour);
        Assert.False(back.Folders[0].Defaults.IsEmpty);
        Assert.Equal("#E3A008", back.Folders[0].Connections[0].IconColour);
        Assert.Equal("#E3A008", back.Folders[0].Connections[0].Clone().IconColour);
        Assert.Equal("#F85149", folder.Clone().Defaults.IconColour);
    }
}
