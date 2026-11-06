using RemoteConnectionManager.Core.Models;
using RemoteConnectionManager.Core.Services;
using Xunit;

namespace RemoteConnectionManager.Core.Tests;

/// <summary>Phase 4: vault credentials, credential protection and RD Gateway inheritance.</summary>
public class SignInSettingsTests
{
    private static CredentialReference Vault(string secret) => new()
    {
        Source = CredentialSource.KeyVault,
        VaultUri = "https://kv-a.vault.azure.net/",
        SecretName = secret,
    };

    [Fact]
    public void NothingSet_IsWindowsSignInDirectNoProtection()
    {
        var s = SettingsResolver.Resolve(new RdpConnection { Host = "h" }, Array.Empty<ConnectionFolder>(), new ConnectionDefaults());
        Assert.Null(s.VaultCredential);
        Assert.Equal(CredentialProtection.None, s.Protection);
        Assert.False(s.UsesGateway);
        Assert.Null(s.GatewayHost);
        Assert.Null(s.ConflictWarning);
    }

    [Fact]
    public void VaultCredential_InheritsFromNearestFolder()
    {
        var outer = new ConnectionFolder { Defaults = new ConnectionDefaults { Credential = Vault("outer-admin") } };
        var inner = new ConnectionFolder { Defaults = new ConnectionDefaults { Credential = Vault("inner-admin") } };
        var s = SettingsResolver.Resolve(new RdpConnection { Host = "h" }, new[] { outer, inner }, new ConnectionDefaults());
        Assert.Equal("inner-admin", s.VaultCredential?.SecretName);
    }

    [Fact]
    public void WindowsOnEntry_OptsOutOfInheritedVaultCredential()
    {
        var folder = new ConnectionFolder { Defaults = new ConnectionDefaults { Credential = Vault("admin") } };
        var entry = new RdpConnection { Host = "h", Credential = CredentialReference.Windows() };
        var s = SettingsResolver.Resolve(entry, new[] { folder }, new ConnectionDefaults());
        Assert.Null(s.VaultCredential);
    }

    [Fact]
    public void ResolvedCredential_IsACopy()
    {
        var folder = new ConnectionFolder { Defaults = new ConnectionDefaults { Credential = Vault("admin") } };
        var s = SettingsResolver.Resolve(new RdpConnection { Host = "h" }, new[] { folder }, new ConnectionDefaults());
        folder.Defaults.Credential!.SecretName = "changed";
        Assert.Equal("admin", s.VaultCredential?.SecretName);
    }

    [Fact]
    public void Gateway_HostAloneMeansAlways_AndEntryNeverSwitchesItOff()
    {
        var folder = new ConnectionFolder { Defaults = new ConnectionDefaults { GatewayHost = " rdgw.contoso.com " } };

        var on = SettingsResolver.Resolve(new RdpConnection { Host = "h" }, new[] { folder }, new ConnectionDefaults());
        Assert.True(on.UsesGateway);
        Assert.Equal(GatewayUsage.Always, on.Gateway);
        Assert.Equal("rdgw.contoso.com", on.GatewayHost);

        var off = SettingsResolver.Resolve(new RdpConnection { Host = "h", GatewayUsage = GatewayUsage.Never }, new[] { folder }, new ConnectionDefaults());
        Assert.False(off.UsesGateway);
        Assert.Null(off.GatewayHost);
    }

    [Fact]
    public void Gateway_UsageWithoutHostIsDirect()
    {
        var s = SettingsResolver.Resolve(new RdpConnection { Host = "h", GatewayUsage = GatewayUsage.Always }, Array.Empty<ConnectionFolder>(), new ConnectionDefaults());
        Assert.False(s.UsesGateway);
        Assert.Equal(GatewayUsage.Never, s.Gateway);
    }

    [Fact]
    public void Gateway_BypassLocalFromDocumentDefaults()
    {
        var doc = new ConnectionDefaults { GatewayHost = "rdgw", GatewayUsage = GatewayUsage.BypassLocal };
        var s = SettingsResolver.Resolve(new RdpConnection { Host = "h" }, Array.Empty<ConnectionFolder>(), doc);
        Assert.Equal(GatewayUsage.BypassLocal, s.Gateway);
        Assert.True(s.UsesGateway);
    }

    [Fact]
    public void Protection_Inherits_AndRcgWithCredentialWarns()
    {
        var folder = new ConnectionFolder { Defaults = new ConnectionDefaults { CredentialProtection = CredentialProtection.RemoteCredentialGuard } };
        var plain = SettingsResolver.Resolve(new RdpConnection { Host = "h" }, new[] { folder }, new ConnectionDefaults());
        Assert.Equal(CredentialProtection.RemoteCredentialGuard, plain.Protection);
        Assert.Null(plain.ConflictWarning);

        var withVault = SettingsResolver.Resolve(new RdpConnection { Host = "h", Credential = Vault("x") }, new[] { folder }, new ConnectionDefaults());
        Assert.NotNull(withVault.ConflictWarning);

        var restricted = SettingsResolver.Resolve(new RdpConnection { Host = "h", Credential = Vault("x"), CredentialProtection = CredentialProtection.RestrictedAdmin }, new[] { folder }, new ConnectionDefaults());
        Assert.Equal(CredentialProtection.RestrictedAdmin, restricted.Protection);
        Assert.Null(restricted.ConflictWarning);
    }

    [Fact]
    public void Clone_CopiesSignInSettingsDeeply()
    {
        var entry = new RdpConnection { Host = "h", Credential = Vault("a"), CredentialProtection = CredentialProtection.RestrictedAdmin, GatewayHost = "gw", GatewayUsage = GatewayUsage.Always };
        var copy = (RdpConnection)entry.Clone();
        Assert.Equal("a", copy.Credential?.SecretName);
        Assert.NotSame(entry.Credential, copy.Credential);
        Assert.Equal(CredentialProtection.RestrictedAdmin, copy.CredentialProtection);
        Assert.Equal("gw", copy.GatewayHost);
        Assert.Equal(GatewayUsage.Always, copy.GatewayUsage);

        var defaults = new ConnectionDefaults { Credential = Vault("b") };
        Assert.NotSame(defaults.Credential, defaults.Clone().Credential);
        Assert.False(defaults.IsEmpty);
    }

    [Fact]
    public void Json_RoundTripsReferencesAndHoldsNoSecret()
    {
        var doc = new ConnectionsDocument
        {
            Folders =
            {
                new ConnectionFolder
                {
                    Name = "Prod",
                    Defaults = new ConnectionDefaults { Credential = Vault("prod-admin"), GatewayHost = "rdgw", GatewayUsage = GatewayUsage.BypassLocal },
                    Connections = { new RdpConnection { Name = "dc", Host = "dc", CredentialProtection = CredentialProtection.RestrictedAdmin } },
                },
            },
        };
        var json = System.Text.Json.JsonSerializer.Serialize(doc, JsonDefaults.Options);
        Assert.Contains("\"secretName\": \"prod-admin\"", json);
        Assert.Contains("\"source\": \"keyVault\"", json);
        Assert.Contains("\"gatewayUsage\": \"bypassLocal\"", json);
        Assert.Contains("\"credentialProtection\": \"restrictedAdmin\"", json);
        Assert.DoesNotContain("password", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("usesGateway", json, StringComparison.OrdinalIgnoreCase);

        var back = System.Text.Json.JsonSerializer.Deserialize<ConnectionsDocument>(json, JsonDefaults.Options)!;
        var folder = Assert.Single(back.Folders);
        Assert.Equal(CredentialSource.KeyVault, folder.Defaults.Credential?.Source);
        Assert.Equal("prod-admin", folder.Defaults.Credential?.SecretName);
        Assert.Equal(GatewayUsage.BypassLocal, folder.Defaults.GatewayUsage);
        var entry = Assert.IsType<RdpConnection>(Assert.Single(folder.Connections));
        Assert.Equal(CredentialProtection.RestrictedAdmin, entry.CredentialProtection);
        Assert.Null(entry.Credential);
    }

    [Theory]
    [InlineData("CONTOSO\\admin", null, "admin", "CONTOSO")]
    [InlineData("CONTOSO\\admin", "other", "admin", "CONTOSO")]
    [InlineData("admin@contoso.com", "ignored", "admin@contoso.com", null)]
    [InlineData("admin", "contoso.local", "admin", "contoso.local")]
    [InlineData(" admin ", " ", "admin", null)]
    public void Split_HandlesDownLevelUpnAndPlainNames(string input, string? domain, string user, string? expectedDomain)
    {
        var (u, d) = CredentialNames.Split(input, domain);
        Assert.Equal(user, u);
        Assert.Equal(expectedDomain, d);
    }

    [Fact]
    public void FromTags_FindsUserNameTagIgnoringCase()
    {
        Assert.Equal("svc-rdp", CredentialNames.FromTags(new Dictionary<string, string> { ["UserName"] = " svc-rdp " }));
        Assert.Equal("a", CredentialNames.FromTags(new Dictionary<string, string> { ["owner"] = "x", ["Account"] = "a" }));
        // "username" is preferred over the looser names.
        Assert.Equal("u", CredentialNames.FromTags(new Dictionary<string, string> { ["account"] = "a", ["username"] = "u" }));
        Assert.Null(CredentialNames.FromTags(new Dictionary<string, string> { ["username"] = "  " }));
        Assert.Null(CredentialNames.FromTags(null));
    }
}
