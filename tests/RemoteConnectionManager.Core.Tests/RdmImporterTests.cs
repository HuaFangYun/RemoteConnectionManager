using System.Xml.Linq;
using RemoteConnectionManager.Core.Import;
using RemoteConnectionManager.Core.Models;
using Xunit;

namespace RemoteConnectionManager.Core.Tests;

public class RdmImporterTests
{
    // Shape follows RDM's ArrayOfConnection export: folders are Group entries whose Group value is
    // their own full path, connections carry their parent path in Group.
    private const string Sample = """
        <?xml version="1.0"?>
        <ArrayOfConnection>
          <Connection>
            <ConnectionType>RDPConfigured</ConnectionType>
            <Group>Client A\Servers</Group>
            <ID>3f2504e0-4f89-11d3-9a0c-0305e82c3301</ID>
            <Name>DC01</Name>
            <Url>dc01.clienta.local</Url>
            <Description>Primary DC</Description>
            <CredentialConnectionID>8a3c0e1d-0000-4000-8000-000000000001</CredentialConnectionID>
            <MetaInformation><Keywords>prod dc</Keywords></MetaInformation>
            <RDP>
              <Domain>CLIENTA</Domain>
              <UserName>admin.james</UserName>
              <SafePassword>ENCRYPTEDJUNK</SafePassword>
              <AdminMode>true</AdminMode>
              <UsesClipboard>false</UsesClipboard>
              <KeyboardHook>OnTheLocalComputer</KeyboardHook>
              <GatewayHostname>rdg.clienta.com</GatewayHostname>
              <GatewayUsageMethod>ModeUDGAlways</GatewayUsageMethod>
            </RDP>
          </Connection>
          <Connection>
            <ConnectionType>RDPConfigured</ConnectionType>
            <Group>Client A</Group>
            <Name>APP01</Name>
            <Url>app01.clienta.local:3390</Url>
          </Connection>
          <Connection>
            <ConnectionType>Group</ConnectionType>
            <Group>Client A</Group>
            <Name>Client A</Name>
            <Description>Client A estate</Description>
          </Connection>
          <Connection>
            <ConnectionType>SSHShell</ConnectionType>
            <Group>Client A</Group>
            <Name>Switch</Name>
            <Url>sw01</Url>
          </Connection>
          <Connection>
            <ConnectionType>WebBrowser</ConnectionType>
            <Group>Client A</Group>
            <Name>Firewall</Name>
            <Url>https://fw01.clienta.local</Url>
          </Connection>
          <Connection>
            <ConnectionType>Credential</ConnectionType>
            <Name>Domain admin</Name>
          </Connection>
          <Connection>
            <ConnectionType>RDPConfigured</ConnectionType>
            <Name>NoHost</Name>
          </Connection>
          <Connection>
            <ConnectionType>RDPConfigured</ConnectionType>
            <Name>Jump</Name>
            <Url>[fe80::1]:3391</Url>
          </Connection>
        </ArrayOfConnection>
        """;

    [Fact]
    public void BuildsFolderTreeFromGroupPaths()
    {
        var result = RdmImporter.Import(XDocument.Parse(Sample), "export");

        Assert.Equal(5, result.Imported);
        Assert.Equal(2, result.Skipped);
        Assert.Equal("export", result.Root.Name);

        var clientA = Assert.Single(result.Root.Folders);
        Assert.Equal("Client A", clientA.Name);
        Assert.Equal("Client A estate", clientA.Description);
        var servers = Assert.Single(clientA.Folders);
        Assert.Equal("Servers", servers.Name);

        Assert.Equal(new[] { "APP01", "Switch", "Firewall" }, clientA.Connections.Select(c => c.Name));
        var app = Assert.IsType<RdpConnection>(clientA.Connections[0]);
        Assert.Equal("app01.clienta.local", app.Host);
        Assert.Equal(3390, app.Port);

        var jump = Assert.IsType<RdpConnection>(Assert.Single(result.Root.Connections));
        Assert.Equal("fe80::1", jump.Host);
        Assert.Equal(3391, jump.Port);
    }

    [Fact]
    public void MapsRdpSettings()
    {
        var result = RdmImporter.Import(XDocument.Parse(Sample));
        var dc = Assert.IsType<RdpConnection>(Assert.Single(result.Root.Folders[0].Folders[0].Connections));

        Assert.Equal("dc01.clienta.local", dc.Host);
        Assert.Null(dc.Port);
        Assert.Equal("Primary DC", dc.Description);
        Assert.Equal("CLIENTA", dc.Domain);
        Assert.Equal("admin.james", dc.UserName);
        Assert.True(dc.AdminSession);
        Assert.False(dc.RedirectClipboard);
        Assert.False(dc.KeyboardPassThrough);
        Assert.Equal("rdg.clienta.com", dc.GatewayHost);
        Assert.Equal(GatewayUsage.Always, dc.GatewayUsage);
        Assert.Equal(new[] { "prod", "dc" }, dc.Tags);
        Assert.Null(dc.Credential);
    }

    [Fact]
    public void ImportsSshAndWebEntries()
    {
        var clientA = RdmImporter.Import(XDocument.Parse(Sample)).Root.Folders[0];
        var ssh = Assert.IsType<SshConnection>(clientA.Connections.Single(c => c.Name == "Switch"));
        Assert.Equal("sw01", ssh.Host);
        Assert.Equal(TerminalProtocol.Ssh, ssh.Protocol);
        var web = Assert.IsType<WebConnection>(clientA.Connections.Single(c => c.Name == "Firewall"));
        Assert.Equal("https://fw01.clienta.local", web.Url);
    }

    [Fact]
    public void NotesSkippedAndCredentialLinkedEntries()
    {
        var result = RdmImporter.Import(XDocument.Parse(Sample));
        Assert.Contains(result.Notes, n => n.Contains("credential entries"));
        Assert.Contains(result.Notes, n => n.Contains("NoHost"));
        Assert.Contains(result.Notes, n => n.Contains("'DC01': linked RDM credential"));
    }

    [Fact]
    public void NeverCarriesPasswords()
    {
        var result = RdmImporter.Import(XDocument.Parse(Sample));
        var json = System.Text.Json.JsonSerializer.Serialize(result.Root, Services.JsonDefaults.Options);
        Assert.DoesNotContain("ENCRYPTEDJUNK", json);
    }

    [Fact]
    public void ParentOnlyGroupPathStillNestsFolder()
    {
        var xml = XDocument.Parse("""
            <ArrayOfConnection>
              <Connection><ConnectionType>Group</ConnectionType><Group>Parent</Group><Name>Child</Name><Description>d</Description></Connection>
              <Connection><ConnectionType>RDPConfigured</ConnectionType><Group>Parent/Child</Group><Name>S</Name><Url>s1</Url></Connection>
            </ArrayOfConnection>
            """);
        var result = RdmImporter.Import(xml);
        var child = Assert.Single(Assert.Single(result.Root.Folders).Folders);
        Assert.Equal("Child", child.Name);
        Assert.Equal("d", child.Description);
        Assert.Single(child.Connections);
    }

    [Fact]
    public void RejectsFilesWithoutEntries()
    {
        Assert.Throws<InvalidDataException>(() => RdmImporter.Import(XDocument.Parse("<Something><Else /></Something>")));
    }
}
