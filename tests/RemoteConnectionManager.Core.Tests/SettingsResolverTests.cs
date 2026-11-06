using RemoteConnectionManager.Core.Models;
using RemoteConnectionManager.Core.Services;
using Xunit;

namespace RemoteConnectionManager.Core.Tests;

public class SettingsResolverTests
{
    [Fact]
    public void EntryOverridesFolderOverridesDocument()
    {
        var entry = new RdpConnection { Name = "x", Host = "x", Port = 3390 };
        var outer = new ConnectionFolder { Name = "outer", Defaults = new ConnectionDefaults { Domain = "outer.local", Port = 3391, AdminSession = true } };
        var inner = new ConnectionFolder { Name = "inner", Defaults = new ConnectionDefaults { Domain = "inner.local" } };
        var docDefaults = new ConnectionDefaults { Domain = "doc.local", KeyboardPassThrough = false, RedirectClipboard = false };

        var s = SettingsResolver.Resolve(entry, new[] { outer, inner }, docDefaults);

        Assert.Equal(3390, s.Port);
        Assert.Equal("inner.local", s.Domain);
        Assert.True(s.AdminSession);
        Assert.False(s.KeyboardPassThrough);
        Assert.False(s.RedirectClipboard);
        Assert.False(s.SmartSizing);
        Assert.Equal("x:3390", s.DisplayTarget);
    }

    [Fact]
    public void IgnoreCertificateErrorsInheritsAndEntryCanSwitchItOff()
    {
        var folder = new ConnectionFolder { Name = "lab", Defaults = new ConnectionDefaults { IgnoreCertificateErrors = true } };

        Assert.False(SettingsResolver.Resolve(new RdpConnection { Host = "h" }, Array.Empty<ConnectionFolder>(), new ConnectionDefaults()).IgnoreCertificateErrors);
        Assert.True(SettingsResolver.Resolve(new RdpConnection { Host = "h" }, new[] { folder }, new ConnectionDefaults()).IgnoreCertificateErrors);
        Assert.False(SettingsResolver.Resolve(new RdpConnection { Host = "h", IgnoreCertificateErrors = false }, new[] { folder }, new ConnectionDefaults()).IgnoreCertificateErrors);

        var copy = (RdpConnection)new RdpConnection { Host = "h", IgnoreCertificateErrors = true }.Clone();
        Assert.True(copy.IgnoreCertificateErrors);
        Assert.False(folder.Defaults.IsEmpty);
    }

    [Fact]
    public void BuiltInDefaultsApplyWhenNothingSet()
    {
        var s = SettingsResolver.Resolve(new RdpConnection { Host = " host " }, Array.Empty<ConnectionFolder>(), new ConnectionDefaults());
        Assert.Equal("host", s.Host);
        Assert.Equal(3389, s.Port);
        Assert.Null(s.Domain);
        Assert.Null(s.UserName);
        Assert.False(s.AdminSession);
        Assert.True(s.KeyboardPassThrough);
        Assert.True(s.RedirectClipboard);
        Assert.Equal("host", s.DisplayTarget);
    }

    [Fact]
    public void ResolveFromDocumentFindsChain()
    {
        var entry = new RdpConnection { Name = "x", Host = "x" };
        var doc = new ConnectionsDocument
        {
            Folders = { new ConnectionFolder { Name = "a", Defaults = new ConnectionDefaults { UserName = "svc" }, Folders = { new ConnectionFolder { Name = "b", Connections = { entry } } } } },
        };
        var s = SettingsResolver.Resolve(doc, entry);
        Assert.Equal("svc", s.UserName);
    }
}
