using RemoteConnectionManager.Core.Models;
using RemoteConnectionManager.Core.Services;
using RemoteConnectionManager.Plugins;
using RemoteConnectionManager.Rdp;

namespace RemoteConnectionManager.App.Services;

/// <summary>
/// Turns a session's credential reference into a one off <see cref="RdpSignIn"/> through the
/// credential plugin for its source (Key Vault, 1Password). The password is handed to the RDP
/// control and not kept anywhere else.
/// </summary>
public sealed class RdpCredentialService
{
    private readonly PluginRegistry _plugins;
    private readonly AppLog _log;

    public RdpCredentialService(PluginRegistry plugins, AppLog log)
    {
        _plugins = plugins;
        _log = log;
    }

    /// <summary>Plain words for the dashboard, toolbar tooltip and the overlay while the credential is read.</summary>
    public string Describe(CredentialReference reference) =>
        _plugins.FindCredentialProvider(reference.Source) is { } provider
            ? provider.Describe(reference)
            : $"{reference.Source} credential (no plugin for it)";

    /// <summary>
    /// Reads a vault credential for the terminal's Send password button. Call on the UI thread
    /// (the source may show its own unlock prompt). The caller types the password and drops it.
    /// Throws <see cref="PluginException"/> with a readable message.
    /// </summary>
    public async Task<ResolvedCredential> ReadAsync(CredentialReference reference, string entryName, CancellationToken ct)
    {
        var provider = _plugins.FindCredentialProvider(reference.Source)
            ?? throw new PluginException($"No plugin reads {reference.Source} credentials in this build.");
        if (!_plugins.IsEnabled(provider))
            throw new PluginException($"The {provider.Name} plugin is switched off in Settings, so the credential cannot be read. Turn it on, or change the entry's credential.");
        if (provider.Validate(reference) is { } problem)
            throw new PluginException(problem);
        var resolved = await provider.ResolveAsync(reference, ct).ConfigureAwait(true);
        _log.Info($"[{entryName}] Read credential from {provider.Describe(reference)} for Send password");
        return resolved;
    }

    /// <summary>
    /// Null when Windows should handle sign in. Call on the UI thread: the source may need to show
    /// its own sign in or unlock prompt. Throws <see cref="PluginException"/> with a readable message.
    /// </summary>
    public async Task<RdpSignIn?> ResolveAsync(EffectiveRdpSettings settings, string entryName, CancellationToken ct)
    {
        if (settings.VaultCredential is not { } reference) return null;
        if (settings.Protection == CredentialProtection.RemoteCredentialGuard)
        {
            _log.Warn($"[{entryName}] Remote Credential Guard is on, so the stored credential reference is not read");
            return null;
        }

        var provider = _plugins.FindCredentialProvider(reference.Source)
            ?? throw new PluginException($"No plugin reads {reference.Source} credentials in this build.");
        if (!_plugins.IsEnabled(provider))
            throw new PluginException($"The {provider.Name} plugin is switched off in Settings, so the credential cannot be read. Turn it on, or change the entry's credential.");
        if (provider.Validate(reference) is { } problem)
            throw new PluginException(problem);

        var resolved = await provider.ResolveAsync(reference, ct).ConfigureAwait(true);

        // The account: one named by the source wins, so one vault item describes the whole credential.
        var account = resolved.Account ?? settings.UserName
            ?? throw new PluginException(
                $"{provider.Describe(reference)} has no user name and the entry has no user name either, so the account is unknown. Add one of them.");
        var (user, domain) = CredentialNames.Split(account, settings.Domain);

        _log.Info($"[{entryName}] Read credential for {CredentialNames.Display(user, domain)} from {provider.Describe(reference)}");
        return new RdpSignIn(user, domain, resolved.Password);
    }
}
