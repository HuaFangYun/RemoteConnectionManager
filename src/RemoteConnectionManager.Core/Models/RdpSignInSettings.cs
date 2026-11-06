namespace RemoteConnectionManager.Core.Models;

/// <summary>Where the RDP sign in comes from.</summary>
public enum CredentialSource
{
    /// <summary>Windows handles it: the logged on user through CredSSP, or a prompt when a user name is set.</summary>
    Windows,
    /// <summary>The password is a Key Vault secret, read at connect time and never saved.</summary>
    KeyVault,
    /// <summary>The password is a 1Password item, read through the 1Password CLI at connect time and never saved.</summary>
    OnePassword,
}

/// <summary>
/// Points at a credential instead of holding one. Only the vault and item names live in
/// connections.json; the value is fetched by the source's plugin when the session connects and
/// dropped straight after. Fields for other sources are ignored.
/// </summary>
public sealed class CredentialReference
{
    public CredentialSource Source { get; set; } = CredentialSource.Windows;
    /// <summary>Vault name or https://name.vault.azure.net address.</summary>
    public string? VaultUri { get; set; }
    public string? SecretName { get; set; }
    /// <summary>Tenant of the vault when it is not the signed in account's home tenant.</summary>
    public string? TenantId { get; set; }

    /// <summary>1Password vault name or id.</summary>
    public string? OnePasswordVault { get; set; }
    /// <summary>1Password item name or id. An op://vault/item reference is also accepted.</summary>
    public string? OnePasswordItem { get; set; }
    /// <summary>1Password account (sign in address, email or id). Only needed with more than one account in the app.</summary>
    public string? OnePasswordAccount { get; set; }

    public CredentialReference Clone() => (CredentialReference)MemberwiseClone();

    public static CredentialReference Windows() => new() { Source = CredentialSource.Windows };
}

/// <summary>How much of the user's credential reaches the remote host.</summary>
public enum CredentialProtection
{
    /// <summary>Normal CredSSP: the credential is delegated to the host.</summary>
    None,
    /// <summary>Restricted Admin mode: nothing is delegated, the session runs as the host's machine account on the network. Needs admin rights on the host.</summary>
    RestrictedAdmin,
    /// <summary>Remote Credential Guard: Kerberos requests are sent back to this PC. Needs Kerberos and the logged on user, no explicit credential.</summary>
    RemoteCredentialGuard,
}

public enum GatewayUsage
{
    Never,
    Always,
    /// <summary>Use the gateway except for addresses on the local network.</summary>
    BypassLocal,
}
