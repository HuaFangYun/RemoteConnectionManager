using System.Xml.Linq;
using RemoteConnectionManager.Core.Import;
using RemoteConnectionManager.Core.Models;
using Xunit;

namespace RemoteConnectionManager.Core.Tests;

public class RoyalTsImporterTests
{
    private const string DocId = "00000000-0000-0000-0000-0000000000d0";
    private const string FolderA = "00000000-0000-0000-0000-0000000000f1";
    private const string FolderB = "00000000-0000-0000-0000-0000000000f2";
    private const string CredFolder = "00000000-0000-0000-0000-0000000000f3";
    private const string TrashId = "00000000-0000-0000-0000-0000000000e0";
    private const string CredId = "00000000-0000-0000-0000-0000000000c1";

    // Same flat layout as a real .rtsz: every object under the root, linked by ParentID.
    private static readonly string Sample = $$"""
        <?xml version="1.0" encoding="utf-8"?>
        <RTSZDocument>
          <RoyalDocument><ID>{{DocId}}</ID><Name>Team</Name><PositionNr>0</PositionNr></RoyalDocument>
          <RoyalFolder><ID>{{FolderA}}</ID><Name>Client A</Name><ParentID>{{DocId}}</ParentID><PositionNr>2</PositionNr>
            <CredentialMode>3</CredentialMode><CredentialId>{{CredId}}</CredentialId></RoyalFolder>
          <RoyalFolder><ID>{{FolderB}}</ID><Name>Web only</Name><ParentID>{{DocId}}</ParentID><PositionNr>4</PositionNr></RoyalFolder>
          <RoyalFolder><ID>{{CredFolder}}</ID><Name>Credentials</Name><ParentID>{{DocId}}</ParentID><PositionNr>6</PositionNr></RoyalFolder>
          <RoyalCredential><ID>{{CredId}}</ID><Name>clienta admin</Name><ParentID>{{CredFolder}}</ParentID>
            <UserName>clienta\admin</UserName><Password>ENCRYPTEDJUNK</Password></RoyalCredential>
          <RoyalRDSConnection><ID>00000000-0000-0000-0000-000000000101</ID><Name>DC01</Name><ParentID>{{FolderA}}</ParentID><PositionNr>4</PositionNr>
            <URI>dc01.clienta.local</URI><RDPPort>3389</RDPPort><CredentialFromParent>True</CredentialFromParent>
            <ConnectToAdministerOrConsole>True</ConnectToAdministerOrConsole><EnableWindowsKey>False</EnableWindowsKey><Description>Primary DC</Description></RoyalRDSConnection>
          <RoyalRDSConnection><ID>00000000-0000-0000-0000-000000000102</ID><Name>APP01</Name><ParentID>{{FolderA}}</ParentID><PositionNr>2</PositionNr>
            <URI>10.0.0.5:3395</URI><RDPPort>3395</RDPPort><CredentialMode>2</CredentialMode><CredentialUsername>.\administrator</CredentialUsername>
            <CredentialPassword>ENCRYPTEDJUNK</CredentialPassword><SmartSizing>True</SmartSizing></RoyalRDSConnection>
          <RoyalRDSConnection><ID>00000000-0000-0000-0000-000000000103</ID><Name>Lost cred</Name><ParentID>{{FolderA}}</ParentID><PositionNr>6</PositionNr>
            <URI>lost1</URI><RDPPort>3400</RDPPort><CredentialMode>3</CredentialMode><CredentialId>00000000-0000-0000-0000-0000000000c9</CredentialId></RoyalRDSConnection>
          <RoyalWebConnection><ID>00000000-0000-0000-0000-000000000201</ID><Name>Portal</Name><ParentID>{{FolderB}}</ParentID><URI>https://x</URI></RoyalWebConnection>
          <RoyalSSHConnection><ID>00000000-0000-0000-0000-000000000401</ID><Name>Core router</Name><ParentID>{{FolderA}}</ParentID><PositionNr>8</PositionNr>
            <URI>10.22.22.1</URI><Port>22</Port><IsTelnetConnection>False</IsTelnetConnection><IsSerialPortConnection>False</IsSerialPortConnection>
            <AllowInsecureSshKeyExchangeAlgorithms>True</AllowInsecureSshKeyExchangeAlgorithms><CredentialMode>2</CredentialMode><CredentialUsername>netadmin</CredentialUsername></RoyalSSHConnection>
          <RoyalSSHConnection><ID>00000000-0000-0000-0000-000000000402</ID><Name>Old switch</Name><ParentID>{{FolderA}}</ParentID><PositionNr>9</PositionNr>
            <URI>10.22.22.2</URI><Port>23</Port><IsTelnetConnection>True</IsTelnetConnection><IsSerialPortConnection>False</IsSerialPortConnection></RoyalSSHConnection>
          <RoyalSSHConnection><ID>00000000-0000-0000-0000-000000000403</ID><Name>Console cable</Name><ParentID>{{FolderA}}</ParentID><PositionNr>10</PositionNr>
            <URI>COM3</URI><Port>22</Port><IsSerialPortConnection>True</IsSerialPortConnection></RoyalSSHConnection>
          <RoyalTrash><ID>{{TrashId}}</ID><Name>Trashcan</Name><ParentID>{{DocId}}</ParentID></RoyalTrash>
          <RoyalRDSConnection><ID>00000000-0000-0000-0000-000000000301</ID><Name>Deleted</Name><ParentID>{{TrashId}}</ParentID><URI>gone</URI></RoyalRDSConnection>
        </RTSZDocument>
        """;

    private static ImportResult Run() => RoyalTsImporter.Import(XDocument.Parse(Sample));

    [Fact]
    public void BuildsTreeFromParentIds()
    {
        var result = Run();
        Assert.Equal("Team", result.Root.Name);
        Assert.Equal(6, result.Imported);
        Assert.Equal(1, result.Skipped);

        // The Credentials folder ends up empty and is dropped; the trash is ignored.
        Assert.Equal(new[] { "Client A", "Web only" }, result.Root.Folders.Select(f => f.Name));
        var clientA = result.Root.Folders[0];
        Assert.Equal(new[] { "APP01", "DC01", "Lost cred", "Core router", "Old switch" }, clientA.Connections.Select(c => c.Name));
        Assert.Contains(result.Notes, n => n.Contains("1 serial port connections"));
    }

    [Fact]
    public void ResolvesCredentialUserNamesWithoutPasswords()
    {
        var clientA = Run().Root.Folders[0];
        Assert.Equal("clienta", clientA.Defaults.Domain);
        Assert.Equal("admin", clientA.Defaults.UserName);

        var dc = Assert.IsType<RdpConnection>(clientA.Connections.Single(c => c.Name == "DC01"));
        Assert.Null(dc.UserName); // inherits the folder credential
        var app = Assert.IsType<RdpConnection>(clientA.Connections.Single(c => c.Name == "APP01"));
        Assert.Equal(".\\administrator", app.UserName);
        Assert.Null(app.Domain);

        var json = System.Text.Json.JsonSerializer.Serialize(Run().Root, Services.JsonDefaults.Options);
        Assert.DoesNotContain("ENCRYPTEDJUNK", json);
    }

    [Fact]
    public void ImportsSshTelnetAndWebConnections()
    {
        var result = Run();
        var clientA = result.Root.Folders[0];

        var router = Assert.IsType<SshConnection>(clientA.Connections.Single(c => c.Name == "Core router"));
        Assert.Equal(TerminalProtocol.Ssh, router.Protocol);
        Assert.Equal("10.22.22.1", router.Host);
        Assert.Null(router.Port);
        Assert.True(router.LegacyAlgorithms);
        Assert.Equal("netadmin", router.UserName);

        var telnet = Assert.IsType<SshConnection>(clientA.Connections.Single(c => c.Name == "Old switch"));
        Assert.Equal(TerminalProtocol.Telnet, telnet.Protocol);
        Assert.Null(telnet.Port);
        Assert.False(telnet.LegacyAlgorithms);

        var portal = Assert.IsType<WebConnection>(Assert.Single(result.Root.Folders[1].Connections));
        Assert.Equal("https://x", portal.Url);
    }

    [Fact]
    public void MapsRdpSettings()
    {
        var result = Run();
        var clientA = result.Root.Folders[0];
        var dc = Assert.IsType<RdpConnection>(clientA.Connections.Single(c => c.Name == "DC01"));
        Assert.Equal("dc01.clienta.local", dc.Host);
        Assert.Null(dc.Port);
        Assert.True(dc.AdminSession);
        Assert.False(dc.KeyboardPassThrough);
        Assert.Equal("Primary DC", dc.Description);

        var app = Assert.IsType<RdpConnection>(clientA.Connections.Single(c => c.Name == "APP01"));
        Assert.Equal("10.0.0.5", app.Host);
        Assert.Equal(3395, app.Port);
        Assert.True(app.SmartSizing);

        var lost = Assert.IsType<RdpConnection>(clientA.Connections.Single(c => c.Name == "Lost cred"));
        Assert.Equal(3400, lost.Port);
        Assert.Null(lost.UserName);
        Assert.Contains(result.Notes, n => n.StartsWith("1 entries or folders point at a credential that is not in this document"));
    }

    [Fact]
    public void RejectsOtherXml()
    {
        Assert.Throws<InvalidDataException>(() => RoyalTsImporter.Import(XDocument.Parse("<ArrayOfConnection />")));
    }
}
