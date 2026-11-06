using RemoteConnectionManager.Core.Import;
using RemoteConnectionManager.Core.Models;
using Xunit;

namespace RemoteConnectionManager.Core.Tests;

public class RdmCsvImporterTests
{
    // Trimmed copy of RDM's RDP CSV layout (the real export has ~60 columns, in this order).
    private const string Header =
        "\"Name\",\"Description\",\"CustomField1\",\"URI\",\"RDPPort\",\"ConnectToAdministerOrConsole\",\"SmartSizing\",\"EnableWindowsKey\"," +
        "\"CredentialMode\",\"CredentialUsername\",\"CredentialPassword\",\"RedirectClipboard\",\"GatewayUsageMethod\",\"GatewayHostName\",\"Path\"";

    private static ImportResult Run(params string[] rows) =>
        RdmCsvImporter.Import(new StringReader(Header + "\r\n" + string.Join("\r\n", rows) + "\r\n"), "rds");

    [Fact]
    public void BuildsFoldersFromSlashPaths()
    {
        var result = Run(
            "\"APP1\",\"\",\"\",\"10.0.0.1\",\"3389\",\"False\",\"False\",\"True\",\"0\",\"\",\"\",\"True\",\"0\",\"\",\"Corp/Site A\"",
            "\"APP2\",\"\",\"\",\"10.0.0.2\",\"3389\",\"False\",\"False\",\"True\",\"0\",\"\",\"\",\"True\",\"0\",\"\",\"Corp/Site A/Old\"",
            "\"TOP\",\"\",\"\",\"10.0.0.3\",\"3389\",\"False\",\"False\",\"True\",\"0\",\"\",\"\",\"True\",\"0\",\"\",\"\"");

        Assert.Equal(3, result.Imported);
        Assert.Equal("rds", result.Root.Name);
        Assert.Equal("TOP", Assert.Single(result.Root.Connections).Name);
        var siteA = Assert.Single(Assert.Single(result.Root.Folders).Folders);
        Assert.Equal("Site A", siteA.Name);
        Assert.Equal("APP1", Assert.Single(siteA.Connections).Name);
        Assert.Equal("Old", Assert.Single(siteA.Folders).Name);
    }

    [Fact]
    public void MapsSettingsAndPortInUri()
    {
        var result = Run(
            "\"DB1\",\"SQL, primary\",\"\",\"10.0.0.9:9389\",\"9389\",\"True\",\"True\",\"False\",\"2\",\"corp\\dba\",\"\",\"False\",\"1\",\"rdg.example.com\",\"\"");

        var db = Assert.IsType<RdpConnection>(Assert.Single(result.Root.Connections));
        Assert.Equal("10.0.0.9", db.Host);
        Assert.Equal(9389, db.Port);
        Assert.Equal("SQL, primary", db.Description);
        Assert.True(db.AdminSession);
        Assert.True(db.SmartSizing);
        Assert.False(db.KeyboardPassThrough);
        Assert.False(db.RedirectClipboard);
        Assert.Equal("corp", db.Domain);
        Assert.Equal("dba", db.UserName);
        Assert.Equal("rdg.example.com", db.GatewayHost);
        Assert.Equal(GatewayUsage.Always, db.GatewayUsage);
        Assert.Contains(result.Notes, n => n.StartsWith("1 entries used an RDM credential"));
    }

    [Fact]
    public void DefaultsStayInherited()
    {
        var result = Run("\"A\",\"\",\"\",\"a1\",\"3389\",\"False\",\"False\",\"True\",\"0\",\"\",\"\",\"True\",\"0\",\"\",\"\"");
        var a = Assert.IsType<RdpConnection>(Assert.Single(result.Root.Connections));
        Assert.Null(a.Port);
        Assert.Null(a.AdminSession);
        Assert.Null(a.SmartSizing);
        Assert.Null(a.KeyboardPassThrough);
        Assert.Null(a.RedirectClipboard);
        Assert.Null(a.GatewayHost);
        Assert.Empty(result.Notes);
    }

    [Theory]
    [InlineData(".\\administrator", null, ".\\administrator")]
    [InlineData("corp\\dba", "corp", "dba")]
    [InlineData("dba@corp.local", null, "dba@corp.local")]
    [InlineData("plain", null, "plain")]
    public void SplitsDomainUsers(string input, string? domain, string user)
    {
        Assert.Equal((domain, user), RdmCsvImporter.SplitUser(input, null));
    }

    [Fact]
    public void NeverCarriesPasswords()
    {
        var result = Run("\"A\",\"\",\"\",\"a1\",\"3389\",\"False\",\"False\",\"True\",\"2\",\"u\",\"SECRETJUNK\",\"True\",\"0\",\"\",\"\"");
        var json = System.Text.Json.JsonSerializer.Serialize(result.Root, Services.JsonDefaults.Options);
        Assert.DoesNotContain("SECRETJUNK", json);
    }

    [Fact]
    public void SkipsRowsWithoutHost()
    {
        var result = Run("\"Empty\",\"\",\"\",\"\",\"3389\",\"False\",\"False\",\"True\",\"0\",\"\",\"\",\"True\",\"0\",\"\",\"\"");
        Assert.Equal(0, result.Imported);
        Assert.Equal(1, result.Skipped);
    }

    [Fact]
    public void RejectsNameOnlyExport()
    {
        var csv = "\"Name\",\"Description\",\"CustomField1\"\r\n\"Folder\",\"\",\"\"\r\n";
        var ex = Assert.Throws<InvalidDataException>(() => RdmCsvImporter.Import(new StringReader(csv)));
        Assert.Contains("URI", ex.Message);
    }

    [Fact]
    public void ReadsQuotedCommasQuotesAndLineBreaks()
    {
        var rows = RdmCsvImporter.ReadCsv(new StringReader("a,\"b,c\",\"say \"\"hi\"\"\",\"two\nlines\"\r\nx,,z"));
        Assert.Equal(new[] { "a", "b,c", "say \"hi\"", "two\nlines" }, rows[0]);
        Assert.Equal(new[] { "x", "", "z" }, rows[1]);
    }
}
