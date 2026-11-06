using Azure.Core;
using Azure.Identity;
using Azure.Identity.Broker;

namespace RemoteConnectionManager.KeyVault;

/// <summary>
/// Azure sign in through the Windows broker (WAM). The first sign in tries the account Windows is
/// already signed in with, so on a PAW there is usually no prompt. Only the
/// <see cref="AuthenticationRecord"/> is saved (user name, tenant and account id, no tokens) so the
/// next start is silent. Tokens stay in the broker and the DPAPI protected MSAL cache.
/// </summary>
public sealed class AzureAuth
{
    public const string ArmScope = "https://management.azure.com/.default";
    public const string VaultScope = "https://vault.azure.net/.default";

    private readonly Func<IntPtr> _windowHandle;
    private readonly string _recordPath;
    private readonly Dictionary<string, InteractiveBrowserCredential> _credentials = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _signInGate = new(1, 1);
    private AuthenticationRecord? _record;

    /// <param name="windowHandle">Owner window for the broker's account picker. Called on the UI thread.</param>
    /// <param name="recordPath">Where the authentication record is kept between runs.</param>
    public AzureAuth(Func<IntPtr> windowHandle, string recordPath)
    {
        _windowHandle = windowHandle;
        _recordPath = recordPath;
    }

    public bool IsSignedIn => _record is not null;
    public string? AccountName => _record?.Username;
    public string? HomeTenantId => _record?.TenantId;

    /// <summary>Raised on sign in and sign out.</summary>
    public event EventHandler? Changed;

    /// <summary>Reads the saved record, if any. Does not contact Azure.</summary>
    public async Task LoadAsync(CancellationToken ct = default)
    {
        try
        {
            if (!File.Exists(_recordPath)) return;
            await using var stream = File.OpenRead(_recordPath);
            _record = await AuthenticationRecord.DeserializeAsync(stream, ct).ConfigureAwait(false);
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch
        {
            _record = null; // a damaged record only costs one prompt
        }
    }

    /// <summary>
    /// Credential for a tenant (null means the signed in account's home tenant). Never prompts by
    /// itself: token requests that need a prompt throw <see cref="AuthenticationRequiredException"/>,
    /// and <see cref="EnsureSignedInAsync"/> is the one place that may show the broker.
    /// Must be first called on the UI thread so the broker gets a valid owner window.
    /// </summary>
    public TokenCredential Credential(string? tenantId = null)
    {
        var key = tenantId ?? string.Empty;
        lock (_credentials)
        {
            if (_credentials.TryGetValue(key, out var existing)) return existing;
            var credential = Create(tenantId, useDefaultAccount: true);
            _credentials[key] = credential;
            return credential;
        }
    }

    /// <summary>
    /// Makes sure a token for <paramref name="scope"/> can be had for the tenant, showing the broker
    /// only when a silent attempt fails. Call from the UI thread.
    /// </summary>
    public async Task EnsureSignedInAsync(string? tenantId, string scope, CancellationToken ct)
    {
        var credential = (InteractiveBrowserCredential)Credential(tenantId);
        var context = new TokenRequestContext([scope], tenantId: tenantId);

        if (_record is not null)
        {
            try
            {
                await credential.GetTokenAsync(context, ct).ConfigureAwait(true);
                return;
            }
            catch (AuthenticationRequiredException)
            {
                // fall through to the broker
            }
        }

        await _signInGate.WaitAsync(ct).ConfigureAwait(true);
        try
        {
            var record = await credential.AuthenticateAsync(context, ct).ConfigureAwait(true);
            await SaveRecordAsync(record, ct).ConfigureAwait(true);
        }
        finally
        {
            _signInGate.Release();
        }
    }

    /// <summary>Signs in with the broker's account picker instead of the Windows default account.</summary>
    public async Task SignInWithAnotherAccountAsync(CancellationToken ct)
    {
        await _signInGate.WaitAsync(ct).ConfigureAwait(true);
        try
        {
            var credential = Create(null, useDefaultAccount: false);
            var record = await credential.AuthenticateAsync(new TokenRequestContext([ArmScope]), ct).ConfigureAwait(true);
            lock (_credentials) _credentials.Clear();
            await SaveRecordAsync(record, ct).ConfigureAwait(true);
        }
        finally
        {
            _signInGate.Release();
        }
    }

    /// <summary>Forgets the account in this app. The account stays signed in to Windows.</summary>
    public void SignOut()
    {
        _record = null;
        lock (_credentials) _credentials.Clear();
        try { File.Delete(_recordPath); } catch { }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private InteractiveBrowserCredential Create(string? tenantId, bool useDefaultAccount)
    {
        var options = new InteractiveBrowserCredentialBrokerOptions(_windowHandle())
        {
            UseDefaultBrokerAccount = useDefaultAccount,
            DisableAutomaticAuthentication = true,
            AuthenticationRecord = useDefaultAccount ? _record : null,
            TokenCachePersistenceOptions = new TokenCachePersistenceOptions { Name = "RemoteConnectionManager" },
        };
        if (!string.IsNullOrWhiteSpace(tenantId)) options.TenantId = tenantId;
        // Subscriptions and vaults can sit in tenants where the account is a guest.
        options.AdditionallyAllowedTenants.Add("*");
        return new InteractiveBrowserCredential(options);
    }

    private async Task SaveRecordAsync(AuthenticationRecord record, CancellationToken ct)
    {
        var changed = _record?.Username != record.Username || _record?.HomeAccountId != record.HomeAccountId;
        _record = record;
        try
        {
            var dir = Path.GetDirectoryName(_recordPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            await using var stream = File.Create(_recordPath);
            await record.SerializeAsync(stream, ct).ConfigureAwait(true);
        }
        catch
        {
            // Not fatal: the next start just signs in again.
        }
        if (changed)
        {
            // Credentials built before sign in have no record; rebuild them with it.
            lock (_credentials) _credentials.Clear();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
