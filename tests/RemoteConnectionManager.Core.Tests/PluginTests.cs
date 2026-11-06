using System.Text.Json;
using RemoteConnectionManager.Core.Models;
using RemoteConnectionManager.Core.Services;
using RemoteConnectionManager.Plugins;
using RemoteConnectionManager.Plugins.OnePassword;
using Xunit;

namespace RemoteConnectionManager.Core.Tests;

/// <summary>Plugins: registry, 1Password reference and output handling, service filtering, watch list inheritance.</summary>
public class PluginTests
{
    // ---------- registry ----------

    private sealed class FakeProvider(string id, CredentialSource source) : ICredentialProvider
    {
        public string Id => id;
        public string Name => id;
        public string Description => string.Empty;
        public CredentialSource Source => source;
        public string SourceName => id;
        public string Describe(CredentialReference reference) => id;
        public string? Validate(CredentialReference reference) => null;
        public Task<ResolvedCredential> ResolveAsync(CredentialReference reference, CancellationToken ct) => Task.FromResult(new ResolvedCredential(null, "x"));
        public Task<PluginStatus> GetStatusAsync(CancellationToken ct) => Task.FromResult(PluginStatus.Ok("ok"));
    }

    [Fact]
    public void Registry_DisabledListIsReadLive_AndIgnoresCase()
    {
        var disabled = new List<string>();
        var registry = new PluginRegistry([new FakeProvider("keyvault", CredentialSource.KeyVault), new FakeProvider("onepassword", CredentialSource.OnePassword)], () => disabled);

        Assert.Equal(2, registry.CredentialProviders.Count());
        disabled.Add("OnePassword");
        Assert.Single(registry.CredentialProviders);
        // Still findable, so the session can say "switched off" rather than "unknown".
        Assert.NotNull(registry.FindCredentialProvider(CredentialSource.OnePassword));
        Assert.False(registry.IsEnabled(registry.FindCredentialProvider(CredentialSource.OnePassword)!));
        Assert.Null(registry.ServerInspector);
    }

    [Fact]
    public void ResolvedCredential_ToStringNeverShowsThePassword()
    {
        Assert.DoesNotContain("hunter2", new ResolvedCredential("admin", "hunter2").ToString());
    }

    // ---------- 1Password reference ----------

    private static CredentialReference Op(string item, string? vault = null, string? account = null) => new()
    {
        Source = CredentialSource.OnePassword,
        OnePasswordItem = item,
        OnePasswordVault = vault,
        OnePasswordAccount = account,
    };

    [Fact]
    public void OpReference_PlainItemAndVault()
    {
        var t = OnePasswordReference.TryParse(Op(" DC01 admin ", " Servers "), out var problem);
        Assert.Null(problem);
        Assert.Equal(new OnePasswordTarget("DC01 admin", "Servers", null), t);
    }

    [Fact]
    public void OpReference_SecretReferenceIsSplit_FieldIgnored()
    {
        var t = OnePasswordReference.TryParse(Op("op://Servers/DC01 admin/password"), out _);
        Assert.Equal("Servers", t!.Vault);
        Assert.Equal("DC01 admin", t.Item);
    }

    [Fact]
    public void OpReference_VaultSlashItemShorthand()
    {
        var t = OnePasswordReference.TryParse(Op("Servers/DC01"), out _);
        Assert.Equal(("Servers", "DC01"), (t!.Vault, t.Item));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("op://OnlyVault")]
    [InlineData("op:///item")]
    public void OpReference_Incomplete_SaysWhy(string item)
    {
        Assert.Null(OnePasswordReference.TryParse(Op(item), out var problem));
        Assert.False(string.IsNullOrWhiteSpace(problem));
    }

    [Fact]
    public void OpArguments_AreSeparateTokens_WithOptionalParts()
    {
        var args = OnePasswordReference.ItemGetArguments(new OnePasswordTarget("DC01 admin", "Ser vers", "my.1password.com"), reveal: true);
        Assert.Equal(["item", "get", "DC01 admin", "--vault", "Ser vers", "--account", "my.1password.com", "--format", "json", "--reveal"], args);

        var minimal = OnePasswordReference.ItemGetArguments(new OnePasswordTarget("x", null, null), reveal: false);
        Assert.Equal(["item", "get", "x", "--format", "json"], minimal);
    }

    // ---------- 1Password output ----------

    [Fact]
    public void OpItem_UsesFieldPurpose()
    {
        const string json = """
        {"id":"abc","title":"DC01 admin","category":"LOGIN","fields":[
          {"id":"notesPlain","type":"STRING","purpose":"NOTES","label":"notesPlain","value":"password is not here"},
          {"id":"username","type":"STRING","purpose":"USERNAME","label":"username","value":" CONTOSO\\admin "},
          {"id":"password","type":"CONCEALED","purpose":"PASSWORD","label":"password","value":"s3cret!"}
        ]}
        """;
        var (user, password, title) = OnePasswordItemParser.Parse(json);
        Assert.Equal(@"CONTOSO\admin", user);
        Assert.Equal("s3cret!", password);
        Assert.Equal("DC01 admin", title);
    }

    [Fact]
    public void OpItem_FallsBackToLabels_ForCustomItems()
    {
        const string json = """
        {"title":"Server","category":"SERVER","fields":[
          {"id":"a","type":"STRING","label":"Username","value":"admin@contoso.com"},
          {"id":"b","type":"CONCEALED","label":"Password","value":"pw"}
        ]}
        """;
        var (user, password, _) = OnePasswordItemParser.Parse(json);
        Assert.Equal("admin@contoso.com", user);
        Assert.Equal("pw", password);
    }

    [Fact]
    public void OpItem_NoPassword_ReturnsNull()
    {
        var (_, password, _) = OnePasswordItemParser.Parse("""{"title":"x","fields":[{"type":"STRING","label":"username","value":"a"}]}""");
        Assert.Null(password);
    }

    [Fact]
    public void OpErrors_AreTranslated()
    {
        var target = new OnePasswordTarget("DC01", "Servers", null);
        Assert.Contains("not unlocked", OnePasswordErrors.Describe("[ERROR] 2026/11/05 10:00:00 authorization prompt dismissed, please try again", target));
        Assert.Contains("no item 'DC01' in vault 'Servers'", OnePasswordErrors.Describe("[ERROR] 2026/11/05 10:00:00 \"DC01\" isn't an item in the \"Servers\" vault.", target));
        Assert.Contains("Integrate with 1Password CLI", OnePasswordErrors.Describe("[ERROR] 2026/11/05 10:00:00 You are not currently signed in.", target));
        Assert.Equal("1Password CLI: something odd", OnePasswordErrors.Describe("[ERROR] 2026/11/05 10:00:00 something odd\n", target));
        Assert.True(OnePasswordErrors.IsUnknownRevealFlag("[ERROR] unknown flag: --reveal"));
    }

    // ---------- service filter ----------

    private static ServiceInfo Svc(string name, string display, string state = "Running", string mode = "Auto") =>
        new(name, display, state, mode, false, "LocalSystem", null);

    private static readonly ServiceInfo[] Services =
    [
        Svc("MSSQLSERVER", "SQL Server (MSSQLSERVER)"),
        Svc("SQLSERVERAGENT", "SQL Server Agent (MSSQLSERVER)", "Stopped", "Manual"),
        Svc("W3SVC", "World Wide Web Publishing Service", "Stopped"),
        Svc("Spooler", "Print Spooler"),
        Svc("sppsvc", "Software Protection", "Stopped"),
        Svc("Fax", "Fax", "Stopped", "Manual"),
    ];

    [Theory]
    [InlineData("mssqlserver", "MSSQLSERVER", true)]
    [InlineData("Print Spooler", "Spooler", true)]
    [InlineData("SQL*", "SQLSERVERAGENT", true)]
    [InlineData("*Agent*", "SQLSERVERAGENT", true)]
    [InlineData("W3SV?", "W3SVC", true)]
    [InlineData("SQL", "MSSQLSERVER", false)]
    [InlineData("  ", "Fax", false)]
    public void Matches_NameDisplayNameAndWildcards(string pattern, string name, bool expected)
    {
        Assert.Equal(expected, ServiceFilter.Matches(pattern, Services.Single(s => s.Name == name)));
    }

    [Fact]
    public void Select_WatchedFirst_ThenStoppedAutomatic_NoDuplicates()
    {
        var rows = ServiceFilter.Select(Services, ["SQL*"], showAll: false);
        Assert.Equal(["MSSQLSERVER", "SQLSERVERAGENT", "W3SVC"], rows.Select(r => r.Service.Name));
        Assert.Equal(ServiceListReason.AutomaticNotRunning, rows[2].Reason);
        Assert.Equal([false, true, true], rows.Select(r => r.NeedsAttention));

        // W3SVC watched: listed once, as watched.
        var watched = ServiceFilter.Select(Services, ["W3SVC"], showAll: false);
        Assert.Single(watched, r => r.Service.Name == "W3SVC" && r.Reason == ServiceListReason.Watched);
    }

    [Fact]
    public void Select_OnDemandServicesHiddenUnlessShowAllOrWatched()
    {
        // sppsvc is automatic and stopped, but it is a known on demand service.
        Assert.DoesNotContain(ServiceFilter.Select(Services, [], showAll: false), r => r.Service.Name == "sppsvc");
        Assert.Equal(1, ServiceFilter.CountOnDemandHidden(Services, []));

        var all = ServiceFilter.Select(Services, [], showAll: true);
        var spp = Assert.Single(all, r => r.Service.Name == "sppsvc");
        Assert.Equal(ServiceListReason.OnDemand, spp.Reason);
        Assert.False(spp.NeedsAttention);

        var watched = Assert.Single(ServiceFilter.Select(Services, ["sppsvc"], showAll: false), r => r.Service.Name == "sppsvc");
        Assert.True(watched.NeedsAttention);
        Assert.Equal(0, ServiceFilter.CountOnDemandHidden(Services, ["sppsvc"]));
    }

    [Fact]
    public void Select_ShowAll_AddsTheRestWithoutAttention()
    {
        var rows = ServiceFilter.Select(Services, [], showAll: true);
        Assert.Equal(Services.Length, rows.Count);
        Assert.All(rows.Where(r => r.Reason == ServiceListReason.Other), r => Assert.False(r.NeedsAttention));
    }

    [Fact]
    public void Unmatched_ListsTypos()
    {
        Assert.Equal(["MSSQLSRVER"], ServiceFilter.Unmatched(Services, ["MSSQLSRVER", "SQL*"]));
    }

    [Fact]
    public void ParseList_SplitsTrimsAndDedupes()
    {
        Assert.Equal(["MSSQLSERVER", "W3SVC", "SQL*"], ServiceFilter.ParseList(" MSSQLSERVER, W3SVC;\nSQL*, ,mssqlserver"));
        Assert.Empty(ServiceFilter.ParseList(null));
    }

    // ---------- model: inheritance and JSON ----------

    [Fact]
    public void WatchedServices_InheritFromNearestFolder_EntryOverrides()
    {
        var outer = new ConnectionFolder { Defaults = new ConnectionDefaults { WatchedServices = ["W3SVC"] } };
        var inner = new ConnectionFolder { Defaults = new ConnectionDefaults { WatchedServices = ["MSSQLSERVER", " "] } };

        var inherited = SettingsResolver.Resolve(new RdpConnection { Host = "h" }, [outer, inner], new ConnectionDefaults());
        Assert.Equal(["MSSQLSERVER"], inherited.WatchedServices);

        var own = SettingsResolver.Resolve(new RdpConnection { Host = "h", WatchedServices = ["Spooler"] }, [outer, inner], new ConnectionDefaults());
        Assert.Equal(["Spooler"], own.WatchedServices);

        var none = SettingsResolver.Resolve(new RdpConnection { Host = "h" }, [], new ConnectionDefaults());
        Assert.Empty(none.WatchedServices);
    }

    [Fact]
    public void OnePasswordCredential_InheritsLikeKeyVault()
    {
        var folder = new ConnectionFolder { Defaults = new ConnectionDefaults { Credential = Op("DC01", "Servers") } };
        var s = SettingsResolver.Resolve(new RdpConnection { Host = "h" }, [folder], new ConnectionDefaults());
        Assert.Equal(CredentialSource.OnePassword, s.VaultCredential?.Source);
        Assert.Equal("DC01", s.VaultCredential?.OnePasswordItem);
    }

    [Fact]
    public void Json_RoundTripsOnePasswordAndWatchList_WithNoEmptyNoise()
    {
        var entry = new RdpConnection { Name = "DC01", Host = "dc01", Credential = Op("DC01", "Servers"), WatchedServices = ["NTDS", "DNS"] };
        var json = JsonSerializer.Serialize<ConnectionEntry>(entry, JsonDefaults.Options);
        Assert.Contains("\"source\": \"onePassword\"", json);
        Assert.Contains("\"onePasswordItem\": \"DC01\"", json);
        Assert.DoesNotContain("onePasswordAccount", json);
        Assert.DoesNotContain("secretName", json);

        var back = (RdpConnection)JsonSerializer.Deserialize<ConnectionEntry>(json, JsonDefaults.Options)!;
        Assert.Equal("Servers", back.Credential?.OnePasswordVault);
        Assert.Equal(["NTDS", "DNS"], back.WatchedServices);

        var clone = (RdpConnection)back.Clone();
        clone.WatchedServices!.Add("x");
        Assert.Equal(2, back.WatchedServices!.Count);
    }

    [Fact]
    public void FolderDefaults_WatchListCountsAsSet()
    {
        Assert.True(new ConnectionDefaults().IsEmpty);
        Assert.False(new ConnectionDefaults { WatchedServices = [] }.IsEmpty);
    }
}
