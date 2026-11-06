namespace RemoteConnectionManager.KeyVault;

public enum VaultItemKind
{
    Secret,
    Key,
    Certificate,
}

public sealed record SubscriptionInfo(string Id, string Name, string? TenantId);

/// <summary>A vault found through Azure Resource Manager.</summary>
public sealed record VaultInfo(
    string Name,
    Uri VaultUri,
    string SubscriptionId,
    string SubscriptionName,
    string ResourceGroup,
    string Location,
    string? TenantId);

/// <summary>
/// One secret, key or certificate as listed. Never holds a secret value: values are fetched on
/// demand and live only in the view model that asked for them.
/// </summary>
public sealed record VaultItem(
    VaultItemKind Kind,
    string Name,
    bool Enabled,
    string? ContentType,
    DateTimeOffset? NotBefore,
    DateTimeOffset? ExpiresOn,
    DateTimeOffset? CreatedOn,
    DateTimeOffset? UpdatedOn,
    string? Version,
    IReadOnlyDictionary<string, string> Tags)
{
    public ExpiryState Expiry(DateTimeOffset now) => ExpiryRules.StateOf(ExpiresOn, now);
}

/// <summary>An older or current version of an item.</summary>
public sealed record VaultItemVersion(string Version, bool Enabled, DateTimeOffset? CreatedOn, DateTimeOffset? ExpiresOn);

/// <summary>Key details shown in the detail pane. Public material only.</summary>
public sealed record KeyDetails(string KeyType, string? CurveOrSize, IReadOnlyList<string> Operations, bool? Exportable);

/// <summary>Certificate details shown in the detail pane. Public material only.</summary>
public sealed record CertificateDetails(
    string? Subject,
    string? Issuer,
    string Thumbprint,
    IReadOnlyList<string> SubjectAlternativeNames,
    string? KeyType,
    int? KeySize,
    bool? Exportable,
    string? ContentType,
    byte[] Cer);

/// <summary>What the editor dialog hands back for a new secret or a new secret version.</summary>
public sealed record SecretWrite(string Name, string Value, string? ContentType, DateTimeOffset? ExpiresOn, bool Enabled);

/// <summary>Property changes that do not create a new version.</summary>
public sealed record ItemPropertiesUpdate(bool Enabled, DateTimeOffset? ExpiresOn, string? ContentType);

public enum NewKeyType
{
    Rsa,
    Ec,
}

public sealed record KeyCreate(string Name, NewKeyType Type, int RsaSize, string EcCurve, DateTimeOffset? ExpiresOn);

public sealed record SelfSignedCertificateCreate(string Name, string Subject, IReadOnlyList<string> DnsNames, int ValidityMonths, bool Pem);

public sealed record CertificateImport(string Name, byte[] Content, string? Password);

/// <summary>A password read for an RDP sign in, with the secret's tags (one may name the account).</summary>
public sealed record SignInSecret(string Value, IReadOnlyDictionary<string, string> Tags)
{
    // Keep the value out of logs and debugger displays.
    public override string ToString() => $"SignInSecret {{ Tags = {Tags.Count} }}";
}
