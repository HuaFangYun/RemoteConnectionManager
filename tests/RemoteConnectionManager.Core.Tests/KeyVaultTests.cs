using Azure;
using Azure.Identity;
using RemoteConnectionManager.KeyVault;
using Xunit;

namespace RemoteConnectionManager.Core.Tests;

public class ExpiryRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 5, 12, 0, 0, TimeSpan.FromHours(11));

    [Fact]
    public void NoExpiry_IsNone() => Assert.Equal(ExpiryState.None, ExpiryRules.StateOf(null, Now));

    [Fact]
    public void Past_IsExpired() => Assert.Equal(ExpiryState.Expired, ExpiryRules.StateOf(Now.AddMinutes(-1), Now));

    [Fact]
    public void WithinThirtyDays_IsSoon()
    {
        Assert.Equal(ExpiryState.Soon, ExpiryRules.StateOf(Now.AddDays(1), Now));
        Assert.Equal(ExpiryState.Soon, ExpiryRules.StateOf(Now.AddDays(30), Now));
    }

    [Fact]
    public void BeyondThirtyDays_IsOk() => Assert.Equal(ExpiryState.Ok, ExpiryRules.StateOf(Now.AddDays(31), Now));

    [Fact]
    public void NeedsAttention_CoversSoonAndExpiredOnly()
    {
        Assert.True(ExpiryRules.NeedsAttention(Now.AddDays(-3), Now));
        Assert.True(ExpiryRules.NeedsAttention(Now.AddDays(3), Now));
        Assert.False(ExpiryRules.NeedsAttention(Now.AddDays(90), Now));
        Assert.False(ExpiryRules.NeedsAttention(null, Now));
    }

    [Theory]
    [InlineData(null, "never")]
    [InlineData(0.5, "today")]
    [InlineData(1.5, "in 1 day")]
    [InlineData(12.2, "in 12 days")]
    [InlineData(-0.5, "today")]
    [InlineData(-1.5, "1 day ago")]
    [InlineData(-3.5, "3 days ago")]
    public void Describe_ReadsNaturally(double? days, string expected)
    {
        DateTimeOffset? expires = days is { } d ? Now.AddDays(d) : null;
        Assert.Equal(expected, ExpiryRules.Describe(expires, Now));
    }
}

public class VaultNamesTests
{
    [Theory]
    [InlineData("kv-prod", "https://kv-prod.vault.azure.net/")]
    [InlineData("KV-Prod", "https://kv-prod.vault.azure.net/")]
    [InlineData("kv-prod.vault.azure.net", "https://kv-prod.vault.azure.net/")]
    [InlineData("https://kv-prod.vault.azure.net", "https://kv-prod.vault.azure.net/")]
    [InlineData("https://kv-prod.vault.azure.net/secrets/x", "https://kv-prod.vault.azure.net/")]
    [InlineData("  https://KV-PROD.vault.azure.net/  ", "https://kv-prod.vault.azure.net/")]
    [InlineData("kv-gov.vault.usgovcloudapi.net", "https://kv-gov.vault.usgovcloudapi.net/")]
    [InlineData("hsm1.managedhsm.azure.net", "https://hsm1.managedhsm.azure.net/")]
    public void NormaliseVaultUri_AcceptsNamesHostsAndUris(string input, string expected) =>
        Assert.Equal(expected, VaultNames.NormaliseVaultUri(input)?.ToString());

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ab")]                              // too short
    [InlineData("1vault")]                          // must start with a letter
    [InlineData("kv--prod")]                        // no double dash
    [InlineData("kv-prod-")]                        // no trailing dash
    [InlineData("http://kv-prod.vault.azure.net")]  // https only
    [InlineData("https://example.com")]             // not a vault host
    [InlineData("kv_prod")]
    public void NormaliseVaultUri_RejectsNonVaults(string? input) => Assert.Null(VaultNames.NormaliseVaultUri(input));

    [Fact]
    public void VaultNameOf_TakesFirstLabel() =>
        Assert.Equal("kv-prod", VaultNames.VaultNameOf(new Uri("https://kv-prod.vault.azure.net/")));

    [Theory]
    [InlineData("sql-admin", null)]
    [InlineData("A1", null)]
    [InlineData("", "Enter a name.")]
    [InlineData("has space", "Names can only use letters, digits and dashes.")]
    [InlineData("under_score", "Names can only use letters, digits and dashes.")]
    public void ItemNameProblem(string name, string? expected) => Assert.Equal(expected, VaultNames.ItemNameProblem(name));

    [Fact]
    public void ItemNameProblem_RejectsOver127() =>
        Assert.Equal("Names can be at most 127 characters.", VaultNames.ItemNameProblem(new string('a', 128)));

    [Fact]
    public void SplitList_TrimsAndDropsBlanks() =>
        Assert.Equal(new[] { "a.contoso.com", "b.contoso.com", "c" }, VaultNames.SplitList(" a.contoso.com, b.contoso.com;\n c ,, "));
}

public class KeyVaultErrorsTests
{
    [Fact]
    public void Firewall_IsExplained()
    {
        var ex = new RequestFailedException(403, "Client address is not authorized", "ForbiddenByFirewall", null);
        Assert.Contains("network rules", KeyVaultErrors.Describe(ex, "List secrets"));
    }

    [Fact]
    public void Rbac_NamesTheRoleProblem()
    {
        var ex = new RequestFailedException(403, "Caller is not authorized", "ForbiddenByRbac", null);
        var text = KeyVaultErrors.Describe(ex, "Reading the secret");
        Assert.StartsWith("Reading the secret was denied.", text);
        Assert.Contains("Azure role", text);
    }

    [Fact]
    public void Conflict_PointsAtSoftDelete()
    {
        var ex = new RequestFailedException(409, "Conflict", "Conflict", null);
        Assert.Contains("deleted item with this name", KeyVaultErrors.Describe(ex, "Creating the secret"));
    }

    [Fact]
    public void CredentialUnavailable_IsNotReportedAsFailedSignIn()
    {
        var text = KeyVaultErrors.Describe(new CredentialUnavailableException("broker missing"), "Signing in");
        Assert.StartsWith("Azure sign in is not available", text);
    }

    [Fact]
    public void LongMessages_KeepFirstLineOnly()
    {
        var ex = new InvalidOperationException("first line\nsecond line with detail");
        Assert.Equal("Doing it failed: first line", KeyVaultErrors.Describe(ex, "Doing it"));
    }
}
