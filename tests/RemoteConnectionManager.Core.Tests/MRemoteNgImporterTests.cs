using System.Xml.Linq;
using RemoteConnectionManager.Core.Import;
using RemoteConnectionManager.Core.Models;
using Xunit;

namespace RemoteConnectionManager.Core.Tests;

public class MRemoteNgImporterTests
{
    private const string Sample = """
        <?xml version="1.0" encoding="utf-8"?>
        <mrng:Connections xmlns:mrng="http://mremoteng.org" Name="Connections" Export="false" EncryptionEngine="AES" BlockCipherMode="GCM" KdfIterations="1000" FullFileEncryption="false" Protected="abc" ConfVersion="2.6">
          <Node Name="Client A" Type="Container" Descr="Client A estate" Domain="clienta.local" InheritDomain="false" Username="" Password="" Expanded="true">
            <Node Name="DC01" Type="Connection" Descr="Primary DC" Protocol="RDP" Hostname="dc01.clienta.local" Port="3389" Username="" Password="ENCRYPTEDJUNK" Domain="" InheritDomain="true" InheritPort="false" UseConsoleSession="true" InheritUseConsoleSession="false" RedirectKeys="true" InheritRedirectKeys="false" RedirectClipboard="true" InheritRedirectClipboard="false" EnvironmentTags="prod, dc" />
            <Node Name="APP01" Type="Connection" Protocol="RDP" Hostname="app01.clienta.local" Port="3390" InheritPort="false" Favorite="true" />
            <Node Name="Switch" Type="Connection" Protocol="SSH2" Hostname="sw01" Username="netadmin" InheritUsername="false" />
            <Node Name="Old switch" Type="Connection" Protocol="Telnet" Hostname="sw02" Port="2323" />
            <Node Name="iLO" Type="Connection" Protocol="HTTPS" Hostname="ilo01" Port="8443" />
            <Node Name="VNC box" Type="Connection" Protocol="VNC" Hostname="vnc01" />
            <Node Name="Nested" Type="Container">
              <Node Name="NoHost" Type="Connection" Protocol="RDP" Hostname="" />
            </Node>
          </Node>
        </mrng:Connections>
        """;

    [Fact]
    public void ImportsFoldersRdpEntriesAndSkipsOthers()
    {
        var result = MRemoteNgImporter.Import(XDocument.Parse(Sample), "confCons");

        Assert.Equal(5, result.Imported);
        Assert.Equal(2, result.Skipped);
        var clientA = Assert.Single(result.Root.Folders);
        Assert.Equal("Client A", clientA.Name);
        Assert.Equal("clienta.local", clientA.Defaults.Domain);

        var dc = Assert.IsType<RdpConnection>(clientA.Connections[0]);
        Assert.Equal("dc01.clienta.local", dc.Host);
        Assert.Null(dc.Port);
        Assert.True(dc.AdminSession);
        Assert.Null(dc.KeyboardPassThrough);
        Assert.Null(dc.RedirectClipboard);
        Assert.Equal(new[] { "prod", "dc" }, dc.Tags);
        Assert.Equal("Primary DC", dc.Description);

        var app = Assert.IsType<RdpConnection>(clientA.Connections[1]);
        Assert.Equal(3390, app.Port);
        Assert.True(app.Favourite);

        Assert.Single(clientA.Folders);
        Assert.Contains(result.Notes, n => n.Contains("VNC"));
    }

    [Fact]
    public void ImportsSshTelnetAndWebEntries()
    {
        var clientA = MRemoteNgImporter.Import(XDocument.Parse(Sample)).Root.Folders[0];

        var ssh = Assert.IsType<SshConnection>(clientA.Connections.Single(c => c.Name == "Switch"));
        Assert.Equal(TerminalProtocol.Ssh, ssh.Protocol);
        Assert.Equal("sw01", ssh.Host);
        Assert.Null(ssh.Port);
        Assert.Equal("netadmin", ssh.UserName);

        var telnet = Assert.IsType<SshConnection>(clientA.Connections.Single(c => c.Name == "Old switch"));
        Assert.Equal(TerminalProtocol.Telnet, telnet.Protocol);
        Assert.Equal(2323, telnet.Port);

        var web = Assert.IsType<WebConnection>(clientA.Connections.Single(c => c.Name == "iLO"));
        Assert.Equal("https://ilo01:8443", web.Url);
        Assert.Equal(WebOpenMode.Browser, web.OpenIn);
    }

    [Fact]
    public void NeverCarriesPasswords()
    {
        var result = MRemoteNgImporter.Import(XDocument.Parse(Sample));
        var json = System.Text.Json.JsonSerializer.Serialize(result.Root, Services.JsonDefaults.Options);
        Assert.DoesNotContain("ENCRYPTEDJUNK", json);
    }

    [Fact]
    public void RejectsFullFileEncryption()
    {
        var xml = XDocument.Parse("""<Connections FullFileEncryption="true" ConfVersion="2.6">blob</Connections>""");
        Assert.Throws<InvalidDataException>(() => MRemoteNgImporter.Import(xml));
    }
}
