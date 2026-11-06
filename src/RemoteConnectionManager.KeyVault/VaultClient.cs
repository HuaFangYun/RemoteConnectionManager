using System.Security.Cryptography.X509Certificates;
using System.Text;
using Azure.Core;
using Azure.Security.KeyVault.Certificates;
using Azure.Security.KeyVault.Keys;
using Azure.Security.KeyVault.Secrets;

namespace RemoteConnectionManager.KeyVault;

/// <summary>
/// Everything the app does inside one vault. Thin over the Azure SDK clients: no caching and no
/// secret values kept here. Azure RBAC or the vault access policy decides what is allowed.
/// </summary>
public sealed class VaultClient
{
    private readonly SecretClient _secrets;
    private readonly KeyClient _keys;
    private readonly CertificateClient _certificates;

    public Uri VaultUri { get; }

    public VaultClient(Uri vaultUri, TokenCredential credential)
    {
        VaultUri = vaultUri;
        _secrets = new SecretClient(vaultUri, credential);
        _keys = new KeyClient(vaultUri, credential);
        _certificates = new CertificateClient(vaultUri, credential);
    }

    // ---------- lists ----------

    public async Task<IReadOnlyList<VaultItem>> ListAsync(VaultItemKind kind, CancellationToken ct)
    {
        var list = new List<VaultItem>();
        switch (kind)
        {
            case VaultItemKind.Secret:
                // Managed secrets back certificates; they show under Certificates instead.
                await foreach (var p in _secrets.GetPropertiesOfSecretsAsync(ct).ConfigureAwait(true))
                    if (!p.Managed) list.Add(Map(p));
                break;
            case VaultItemKind.Key:
                await foreach (var p in _keys.GetPropertiesOfKeysAsync(ct).ConfigureAwait(true))
                    if (!p.Managed) list.Add(Map(p));
                break;
            case VaultItemKind.Certificate:
                await foreach (var p in _certificates.GetPropertiesOfCertificatesAsync(cancellationToken: ct).ConfigureAwait(true))
                    list.Add(Map(p));
                break;
        }
        return list.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task<IReadOnlyList<VaultItemVersion>> ListVersionsAsync(VaultItemKind kind, string name, CancellationToken ct)
    {
        var list = new List<VaultItemVersion>();
        switch (kind)
        {
            case VaultItemKind.Secret:
                await foreach (var p in _secrets.GetPropertiesOfSecretVersionsAsync(name, ct).ConfigureAwait(true))
                    list.Add(new VaultItemVersion(p.Version, p.Enabled ?? true, p.CreatedOn, p.ExpiresOn));
                break;
            case VaultItemKind.Key:
                await foreach (var p in _keys.GetPropertiesOfKeyVersionsAsync(name, ct).ConfigureAwait(true))
                    list.Add(new VaultItemVersion(p.Version, p.Enabled ?? true, p.CreatedOn, p.ExpiresOn));
                break;
            case VaultItemKind.Certificate:
                await foreach (var p in _certificates.GetPropertiesOfCertificateVersionsAsync(name, ct).ConfigureAwait(true))
                    list.Add(new VaultItemVersion(p.Version, p.Enabled ?? true, p.CreatedOn, p.ExpiresOn));
                break;
        }
        return list.OrderByDescending(v => v.CreatedOn).ToList();
    }

    // ---------- secrets ----------

    /// <summary>The secret value. The caller decides how long it lives; never log it.</summary>
    public async Task<string> GetSecretValueAsync(string name, string? version, CancellationToken ct)
    {
        var secret = await _secrets.GetSecretAsync(name, version, ct).ConfigureAwait(true);
        return secret.Value.Value;
    }

    /// <summary>
    /// The newest enabled value plus its tags, for signing an RDP session in. The caller hands the
    /// value to the RDP control and drops it; never log or keep it.
    /// </summary>
    public async Task<SignInSecret> GetSignInSecretAsync(string name, CancellationToken ct)
    {
        KeyVaultSecret secret = await _secrets.GetSecretAsync(name, cancellationToken: ct).ConfigureAwait(true);
        if (secret.Properties.Enabled == false)
            throw new InvalidOperationException($"Secret '{name}' is disabled in the vault.");
        if (secret.Properties.ExpiresOn is { } expires && expires < DateTimeOffset.UtcNow)
            throw new InvalidOperationException($"Secret '{name}' expired on {expires.ToLocalTime():yyyy-MM-dd}.");
        return new SignInSecret(secret.Value, new Dictionary<string, string>(secret.Properties.Tags));
    }

    /// <summary>Creates the secret, or a new version of it when it already exists.</summary>
    public async Task SetSecretAsync(SecretWrite write, CancellationToken ct)
    {
        var secret = new KeyVaultSecret(write.Name, write.Value);
        secret.Properties.ContentType = string.IsNullOrWhiteSpace(write.ContentType) ? null : write.ContentType;
        secret.Properties.ExpiresOn = write.ExpiresOn;
        secret.Properties.Enabled = write.Enabled;
        await _secrets.SetSecretAsync(secret, ct).ConfigureAwait(true);
    }

    public async Task UpdateSecretAsync(string name, ItemPropertiesUpdate update, CancellationToken ct)
    {
        // Updating needs a version; take the newest without reading any value.
        SecretProperties? latest = null;
        await foreach (var p in _secrets.GetPropertiesOfSecretVersionsAsync(name, ct).ConfigureAwait(true))
            if (latest is null || p.CreatedOn > latest.CreatedOn) latest = p;
        if (latest is null) throw new InvalidOperationException($"Secret '{name}' has no versions.");

        latest.Enabled = update.Enabled;
        latest.ExpiresOn = update.ExpiresOn;
        latest.ContentType = string.IsNullOrWhiteSpace(update.ContentType) ? null : update.ContentType;
        await _secrets.UpdateSecretPropertiesAsync(latest, ct).ConfigureAwait(true);
    }

    /// <summary>Soft delete. The secret can be recovered from the portal for the vault's retention period.</summary>
    public Task DeleteSecretAsync(string name, CancellationToken ct) => _secrets.StartDeleteSecretAsync(name, ct);

    // ---------- keys ----------

    public async Task<KeyDetails> GetKeyDetailsAsync(string name, CancellationToken ct)
    {
        KeyVaultKey key = await _keys.GetKeyAsync(name, cancellationToken: ct).ConfigureAwait(true);
        string? curveOrSize = key.Key.CurveName is { } curve
            ? curve.ToString()
            : key.Key.N is { Length: > 0 } n ? $"{n.Length * 8} bit" : null;
        return new KeyDetails(key.KeyType.ToString(), curveOrSize, key.KeyOperations.Select(o => o.ToString()).ToList(), key.Properties.Exportable);
    }

    public async Task CreateKeyAsync(KeyCreate create, CancellationToken ct)
    {
        if (create.Type == NewKeyType.Rsa)
        {
            var options = new CreateRsaKeyOptions(create.Name) { KeySize = create.RsaSize, ExpiresOn = create.ExpiresOn };
            await _keys.CreateRsaKeyAsync(options, ct).ConfigureAwait(true);
        }
        else
        {
            var options = new CreateEcKeyOptions(create.Name) { CurveName = new KeyCurveName(create.EcCurve), ExpiresOn = create.ExpiresOn };
            await _keys.CreateEcKeyAsync(options, ct).ConfigureAwait(true);
        }
    }

    public async Task UpdateKeyAsync(string name, ItemPropertiesUpdate update, CancellationToken ct)
    {
        KeyVaultKey key = await _keys.GetKeyAsync(name, cancellationToken: ct).ConfigureAwait(true);
        key.Properties.Enabled = update.Enabled;
        key.Properties.ExpiresOn = update.ExpiresOn;
        await _keys.UpdateKeyPropertiesAsync(key.Properties, cancellationToken: ct).ConfigureAwait(true);
    }

    public Task DeleteKeyAsync(string name, CancellationToken ct) => _keys.StartDeleteKeyAsync(name, ct);

    // ---------- certificates ----------

    public async Task<CertificateDetails> GetCertificateDetailsAsync(string name, CancellationToken ct)
    {
        KeyVaultCertificateWithPolicy cert = await _certificates.GetCertificateAsync(name, ct).ConfigureAwait(true);
        string? issuer = null;
        string thumbprint = cert.Properties.X509Thumbprint is { } tp ? Convert.ToHexString(tp) : string.Empty;
        string? subject = cert.Policy?.Subject;
        if (cert.Cer is { Length: > 0 })
        {
            using var x509 = X509CertificateLoader.LoadCertificate(cert.Cer);
            issuer = x509.Issuer;
            subject ??= x509.Subject;
            if (thumbprint.Length == 0) thumbprint = x509.Thumbprint;
        }
        var sans = cert.Policy?.SubjectAlternativeNames?.DnsNames?.ToList() ?? new List<string>();
        return new CertificateDetails(
            subject,
            issuer,
            thumbprint,
            sans,
            cert.Policy?.KeyType?.ToString(),
            cert.Policy?.KeySize,
            cert.Policy?.Exportable,
            cert.Policy?.ContentType?.ToString(),
            cert.Cer ?? Array.Empty<byte>());
    }

    /// <summary>
    /// The certificate with its private key, as Key Vault stores it: a PFX with no password, or a PEM.
    /// Needs secret read permission and an exportable certificate.
    /// </summary>
    public async Task<(byte[] Content, string Extension)> DownloadWithPrivateKeyAsync(string name, CancellationToken ct)
    {
        KeyVaultSecret secret = await _secrets.GetSecretAsync(name, cancellationToken: ct).ConfigureAwait(true);
        var isPem = string.Equals(secret.Properties.ContentType, CertificateContentType.Pem.ToString(), StringComparison.OrdinalIgnoreCase);
        return isPem
            ? (Encoding.UTF8.GetBytes(secret.Value), ".pem")
            : (Convert.FromBase64String(secret.Value), ".pfx");
    }

    public async Task CreateSelfSignedCertificateAsync(SelfSignedCertificateCreate create, CancellationToken ct)
    {
        CertificatePolicy policy;
        if (create.DnsNames.Count > 0)
        {
            var sans = new SubjectAlternativeNames();
            foreach (var dns in create.DnsNames) sans.DnsNames.Add(dns);
            policy = new CertificatePolicy(WellKnownIssuerNames.Self, create.Subject, sans);
        }
        else
        {
            policy = new CertificatePolicy(WellKnownIssuerNames.Self, create.Subject);
        }
        policy.ValidityInMonths = create.ValidityMonths;
        policy.ContentType = create.Pem ? CertificateContentType.Pem : CertificateContentType.Pkcs12;
        policy.Exportable = true;
        policy.KeyType = CertificateKeyType.Rsa;
        policy.KeySize = 2048;

        var operation = await _certificates.StartCreateCertificateAsync(create.Name, policy, cancellationToken: ct).ConfigureAwait(true);
        await operation.WaitForCompletionAsync(ct).ConfigureAwait(true);
    }

    public async Task ImportCertificateAsync(CertificateImport import, CancellationToken ct)
    {
        var options = new ImportCertificateOptions(import.Name, import.Content)
        {
            Password = string.IsNullOrEmpty(import.Password) ? null : import.Password,
        };
        await _certificates.ImportCertificateAsync(options, ct).ConfigureAwait(true);
    }

    public async Task UpdateCertificateAsync(string name, ItemPropertiesUpdate update, CancellationToken ct)
    {
        KeyVaultCertificateWithPolicy cert = await _certificates.GetCertificateAsync(name, ct).ConfigureAwait(true);
        cert.Properties.Enabled = update.Enabled;
        await _certificates.UpdateCertificatePropertiesAsync(cert.Properties, ct).ConfigureAwait(true);
    }

    public Task DeleteCertificateAsync(string name, CancellationToken ct) => _certificates.StartDeleteCertificateAsync(name, ct);

    // ---------- mapping ----------

    private static VaultItem Map(SecretProperties p) => new(
        VaultItemKind.Secret, p.Name, p.Enabled ?? true, p.ContentType, p.NotBefore, p.ExpiresOn, p.CreatedOn, p.UpdatedOn, p.Version,
        new Dictionary<string, string>(p.Tags));

    private static VaultItem Map(KeyProperties p) => new(
        VaultItemKind.Key, p.Name, p.Enabled ?? true, null, p.NotBefore, p.ExpiresOn, p.CreatedOn, p.UpdatedOn, p.Version,
        new Dictionary<string, string>(p.Tags));

    private static VaultItem Map(CertificateProperties p) => new(
        VaultItemKind.Certificate, p.Name, p.Enabled ?? true, null, p.NotBefore, p.ExpiresOn, p.CreatedOn, p.UpdatedOn, p.Version,
        new Dictionary<string, string>(p.Tags));
}
