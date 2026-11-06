using RemoteConnectionManager.Core.Models;

namespace RemoteConnectionManager.Plugins;

/// <summary>
/// Turns a <see cref="CredentialReference"/> into a password at connect time. The reference is all
/// that lives in connections.json; the password is handed to the RDP control and dropped.
/// </summary>
public interface ICredentialProvider : IRcmPlugin
{
    /// <summary>The reference source this provider handles.</summary>
    CredentialSource Source { get; }

    /// <summary>Short name for menus, for example "1Password".</summary>
    string SourceName { get; }

    /// <summary>Plain words for the dashboard and the "getting the credential" overlay.</summary>
    string Describe(CredentialReference reference);

    /// <summary>Null when the reference is complete, otherwise what is missing. Called by the entry editor.</summary>
    string? Validate(CredentialReference reference);

    /// <summary>
    /// Reads the credential. May show the provider's own unlock or sign in prompt, so call it from
    /// the UI thread. Throws <see cref="PluginException"/> with a readable message on failure.
    /// </summary>
    Task<ResolvedCredential> ResolveAsync(CredentialReference reference, CancellationToken ct);
}

/// <summary>A password and, when the source knows it, the account it belongs to.</summary>
public sealed record ResolvedCredential(string? Account, string Password)
{
    /// <summary>Never print the password, even in a debugger or a log line by accident.</summary>
    public override string ToString() => $"ResolvedCredential {{ Account = {Account ?? "(none)"} }}";
}
