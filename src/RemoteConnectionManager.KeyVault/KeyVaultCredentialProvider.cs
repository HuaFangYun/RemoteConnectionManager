using RemoteConnectionManager.Core.Models;
using RemoteConnectionManager.Core.Services;
using RemoteConnectionManager.Plugins;

namespace RemoteConnectionManager.KeyVault;

/// <summary>
/// The Azure Key Vault credential source as a plugin. Signs in through the broker if needed, reads
/// the newest enabled version of the secret and takes the account from its 'username' tag.
/// </summary>
public sealed class KeyVaultCredentialProvider : ICredentialProvider
{
    private readonly AzureAuth _azure;

    public KeyVaultCredentialProvider(AzureAuth azure)
    {
        _azure = azure;
    }

    public string Id => "keyvault";
    public string Name => "Azure Key Vault";
    public string Description =>
        "Browse and edit Key Vault secrets, keys and certificates, and use a secret as an RDP password, read when the session connects. " +
        "Signs in through the Windows broker as your Entra account.";

    public CredentialSource Source => CredentialSource.KeyVault;
    public string SourceName => "Key Vault";

    public string Describe(CredentialReference reference) =>
        $"Key Vault secret '{reference.SecretName}' in {VaultNames.NormaliseVaultUri(reference.VaultUri ?? string.Empty)?.Host ?? reference.VaultUri}";

    public string? Validate(CredentialReference reference)
    {
        if (VaultNames.NormaliseVaultUri(reference.VaultUri ?? string.Empty) is null)
            return "Enter the credential's vault name or its https://name.vault.azure.net address.";
        if (VaultNames.ItemNameProblem(reference.SecretName ?? string.Empty) is { } problem)
            return $"Secret name: {problem}";
        return null;
    }

    public Task<PluginStatus> GetStatusAsync(CancellationToken ct) => Task.FromResult(
        _azure.AccountName is { } name ? PluginStatus.Ok($"Signed in as {name}") : PluginStatus.Ok("Not signed in yet. Sign in happens the first time a vault is used."));

    public async Task<ResolvedCredential> ResolveAsync(CredentialReference reference, CancellationToken ct)
    {
        var vaultUri = VaultNames.NormaliseVaultUri(reference.VaultUri ?? string.Empty)
            ?? throw new PluginException("The credential has no valid vault address. Edit the entry or its folder and set the vault.");
        if (string.IsNullOrWhiteSpace(reference.SecretName))
            throw new PluginException("The credential has no secret name. Edit the entry or its folder and set it.");

        var tenant = string.IsNullOrWhiteSpace(reference.TenantId) ? null : reference.TenantId.Trim();
        try
        {
            await _azure.EnsureSignedInAsync(tenant, AzureAuth.VaultScope, ct).ConfigureAwait(true);
            var client = new VaultClient(vaultUri, _azure.Credential(tenant));
            var secret = await client.GetSignInSecretAsync(reference.SecretName.Trim(), ct).ConfigureAwait(true);
            return new ResolvedCredential(CredentialNames.FromTags(secret.Tags), secret.Value);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (InvalidOperationException ex)
        {
            throw new PluginException(ex.Message, ex);
        }
        catch (Exception ex) when (ex is not PluginException)
        {
            throw new PluginException(KeyVaultErrors.Describe(ex, "Reading the credential"), ex);
        }
    }
}
