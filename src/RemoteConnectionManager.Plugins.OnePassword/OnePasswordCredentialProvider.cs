using System.Text.Json;
using RemoteConnectionManager.Core.Models;

namespace RemoteConnectionManager.Plugins.OnePassword;

/// <summary>
/// Reads an RDP password from a 1Password item at connect time. Unlocking is left to the 1Password
/// app: when it is locked, op.exe asks it to show its own prompt (Windows Hello or the account
/// password) and waits. Nothing is cached here; each connect reads the item again.
/// </summary>
public sealed class OnePasswordCredentialProvider : ICredentialProvider
{
    private static readonly TimeSpan ResolveTimeout = TimeSpan.FromMinutes(2);
    private readonly OnePasswordCli _cli;

    public OnePasswordCredentialProvider(OnePasswordCli cli)
    {
        _cli = cli;
    }

    public string Id => "onepassword";
    public string Name => "1Password";
    public string Description =>
        "RDP passwords from 1Password items, read through the 1Password CLI when a session connects. " +
        "Needs the 1Password app with 'Integrate with 1Password CLI' turned on; you approve the unlock in 1Password.";

    public CredentialSource Source => CredentialSource.OnePassword;
    public string SourceName => "1Password";

    public string Describe(CredentialReference reference) =>
        OnePasswordReference.TryParse(reference, out _) is { } t ? $"1Password item {t.Display}" : "1Password item (not set)";

    public string? Validate(CredentialReference reference)
    {
        OnePasswordReference.TryParse(reference, out var problem);
        return problem;
    }

    public async Task<PluginStatus> GetStatusAsync(CancellationToken ct)
    {
        var exe = _cli.Find();
        if (exe is null)
            return PluginStatus.Problem("1Password CLI (op.exe) not found. Install it with 'winget install AgileBits.1Password.CLI' or set its path below.");
        try
        {
            var r = await _cli.RunAsync(exe, ["--version"], TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
            return r.ExitCode == 0
                ? PluginStatus.Ok($"1Password CLI {r.StdOut.Trim()} at {exe}")
                : PluginStatus.Problem($"op.exe at {exe} did not run: {OnePasswordErrors.Clean(r.StdErr)}");
        }
        catch (PluginException ex)
        {
            return PluginStatus.Problem(ex.Message);
        }
    }

    public async Task<ResolvedCredential> ResolveAsync(CredentialReference reference, CancellationToken ct)
    {
        var target = OnePasswordReference.TryParse(reference, out var problem)
            ?? throw new PluginException(problem + " Edit the entry or its folder.");
        var exe = _cli.Find()
            ?? throw new PluginException("The 1Password CLI (op.exe) is not installed. Install it with 'winget install AgileBits.1Password.CLI', turn on 'Integrate with 1Password CLI' in the 1Password app (Settings > Developer), then reconnect.");

        var result = await _cli.RunAsync(exe, OnePasswordReference.ItemGetArguments(target, reveal: true), ResolveTimeout, ct).ConfigureAwait(false);
        if (result.ExitCode != 0 && OnePasswordErrors.IsUnknownRevealFlag(result.StdErr))
            result = await _cli.RunAsync(exe, OnePasswordReference.ItemGetArguments(target, reveal: false), ResolveTimeout, ct).ConfigureAwait(false);
        if (result.ExitCode != 0)
            throw new PluginException(OnePasswordErrors.Describe(result.StdErr, target));

        string? user, password, title;
        try
        {
            (user, password, title) = OnePasswordItemParser.Parse(result.StdOut);
        }
        catch (JsonException)
        {
            throw new PluginException("The 1Password CLI returned something that is not an item. Update the CLI and try again.");
        }
        if (string.IsNullOrEmpty(password))
            throw new PluginException($"1Password item {target.Display} has no password field.");

        return new ResolvedCredential(user, password);
    }
}
