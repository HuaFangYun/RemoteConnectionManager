using System.Text.Json.Serialization;

namespace RemoteConnectionManager.Core.Models;

/// <summary>An Azure Key Vault pinned in the tree. Phase three opens it; phase one only stores it.</summary>
public sealed class KeyVaultConnection : ConnectionEntry
{
    public string VaultUri { get; set; } = string.Empty;
    public string? TenantId { get; set; }

    [JsonIgnore]
    public override ConnectionKind Kind => ConnectionKind.KeyVault;
    [JsonIgnore]
    public override string Prefix => "(KV)";

    public override ConnectionEntry Clone()
    {
        var copy = new KeyVaultConnection { VaultUri = VaultUri, TenantId = TenantId };
        CopyBaseTo(copy);
        return copy;
    }
}
