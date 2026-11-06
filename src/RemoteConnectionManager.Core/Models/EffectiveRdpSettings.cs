namespace RemoteConnectionManager.Core.Models;

/// <summary>
/// The fully resolved settings handed to the RDP session. Built by
/// <see cref="Services.SettingsResolver"/> from entry, folder chain and document defaults.
/// Holds references only; a password is fetched separately at connect time by the credential's plugin.
/// </summary>
public sealed record EffectiveRdpSettings(
    string Host,
    int Port,
    string? Domain,
    string? UserName,
    bool AdminSession,
    bool KeyboardPassThrough,
    bool RedirectClipboard,
    bool SmartSizing)
{
    public const int DefaultPort = 3389;

    /// <summary>
    /// Set when the password comes from a vault (Azure Key Vault or 1Password); null means Windows
    /// handles sign in. The source says which plugin reads it.
    /// </summary>
    public CredentialReference? VaultCredential { get; init; }
    /// <summary>Service name patterns the Check services view always lists, nearest setting wins.</summary>
    public IReadOnlyList<string> WatchedServices { get; init; } = [];
    public CredentialProtection Protection { get; init; } = CredentialProtection.None;
    /// <summary>RD Gateway host, or null for a direct connection.</summary>
    public string? GatewayHost { get; init; }
    public GatewayUsage Gateway { get; init; } = GatewayUsage.Never;

    public bool UsesGateway => Gateway != GatewayUsage.Never && !string.IsNullOrWhiteSpace(GatewayHost);

    public string DisplayTarget => Port == DefaultPort ? Host : $"{Host}:{Port}";

    /// <summary>Server string in the form the RDP control expects.</summary>
    public string ServerString => Port == DefaultPort ? Host : $"{Host}:{Port}";

    /// <summary>
    /// Remote Credential Guard only works with the logged on user's Kerberos ticket, so an explicit
    /// credential and RCG cannot both apply. Returns the reason, or null when the combination is fine.
    /// </summary>
    public string? ConflictWarning =>
        Protection == CredentialProtection.RemoteCredentialGuard && (VaultCredential is not null || UserName is not null)
            ? "Remote Credential Guard signs in as the logged on user, so the user name and vault credential are ignored."
            : null;
}
