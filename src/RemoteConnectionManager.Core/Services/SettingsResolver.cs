using RemoteConnectionManager.Core.Models;

namespace RemoteConnectionManager.Core.Services;

/// <summary>
/// Resolves inherited settings. Precedence: the entry, then the nearest folder upwards,
/// then the document defaults, then built in defaults.
/// </summary>
public static class SettingsResolver
{
    /// <param name="personal">Looks up the user's own settings for an entry or folder id; null when there are none.</param>
    public static EffectiveRdpSettings Resolve(RdpConnection entry, IReadOnlyList<ConnectionFolder> chain, ConnectionDefaults documentDefaults,
        Func<Guid, PersonalEntrySettings?>? personal = null)
    {
        // chain is outermost first; search innermost first.
        var folders = chain.Reverse().Select(f => f.Defaults).Append(documentDefaults).ToArray();

        T? Pick<T>(T? own, Func<ConnectionDefaults, T?> selector) where T : struct
        {
            if (own.HasValue) return own;
            foreach (var d in folders)
            {
                var v = selector(d);
                if (v.HasValue) return v;
            }
            return null;
        }

        string? PickString(string? own, Func<ConnectionDefaults, string?> selector)
        {
            if (!string.IsNullOrWhiteSpace(own)) return own;
            foreach (var d in folders)
            {
                var v = selector(d);
                if (!string.IsNullOrWhiteSpace(v)) return v;
            }
            return null;
        }

        CredentialReference? credential = entry.Credential;
        if (credential is null)
            foreach (var d in folders)
                if (d.Credential is { } c) { credential = c; break; }
        var vaultCredential = credential is { Source: not CredentialSource.Windows } ? credential.Clone() : null;

        var watched = entry.WatchedServices;
        if (watched is null)
            foreach (var d in folders)
                if (d.WatchedServices is { } w) { watched = w; break; }

        var watchList = MergeWatchList(watched, entry, chain, personal);

        var protection = Pick(entry.CredentialProtection, d => d.CredentialProtection) ?? CredentialProtection.None;
        var gatewayHost = PickString(entry.GatewayHost, d => d.GatewayHost)?.Trim();
        // A gateway host with no usage set means "always"; an explicit Never on the entry switches an inherited gateway off.
        var gateway = Pick(entry.GatewayUsage, d => d.GatewayUsage) ?? (gatewayHost is null ? GatewayUsage.Never : GatewayUsage.Always);
        if (gatewayHost is null) gateway = GatewayUsage.Never;

        return new EffectiveRdpSettings(
            Host: entry.Host.Trim(),
            Port: Pick(entry.Port, d => d.Port) ?? EffectiveRdpSettings.DefaultPort,
            Domain: PickString(entry.Domain, d => d.Domain),
            UserName: PickString(entry.UserName, d => d.UserName),
            AdminSession: Pick(entry.AdminSession, d => d.AdminSession) ?? false,
            KeyboardPassThrough: Pick(entry.KeyboardPassThrough, d => d.KeyboardPassThrough) ?? true,
            RedirectClipboard: Pick(entry.RedirectClipboard, d => d.RedirectClipboard) ?? true,
            SmartSizing: Pick(entry.SmartSizing, d => d.SmartSizing) ?? false)
        {
            VaultCredential = vaultCredential,
            Protection = protection,
            GatewayHost = gateway == GatewayUsage.Never ? null : gatewayHost,
            Gateway = gateway,
            WatchedServices = watchList,
            IgnoreCertificateErrors = Pick(entry.IgnoreCertificateErrors, d => d.IgnoreCertificateErrors) ?? false,
        };
    }

    /// <summary>User name and vault credential for an SSH or telnet entry: the entry, then the nearest folder, then the document.</summary>
    public static EffectiveSshSettings Resolve(SshConnection entry, IReadOnlyList<ConnectionFolder> chain, ConnectionDefaults documentDefaults)
    {
        var folders = chain.Reverse().Select(f => f.Defaults).Append(documentDefaults).ToArray();

        var user = string.IsNullOrWhiteSpace(entry.UserName) ? null : entry.UserName.Trim();
        if (user is null)
            foreach (var d in folders)
                if (!string.IsNullOrWhiteSpace(d.SshUserName)) { user = d.SshUserName.Trim(); break; }

        var credential = entry.Credential;
        if (credential is null)
            foreach (var d in folders)
                if (d.SshCredential is { } c) { credential = c; break; }

        return new EffectiveSshSettings(
            entry.Protocol,
            entry.Host.Trim(),
            entry.EffectivePort,
            user,
            credential is { Source: not CredentialSource.Windows } ? credential.Clone() : null);
    }

    public static EffectiveSshSettings Resolve(ConnectionsDocument document, SshConnection entry) =>
        Resolve(entry, document.ChainFor(entry), document.Defaults);

    /// <summary>
    /// Whether a terminal, SSH or telnet session is logged: the entry, then the nearest folder,
    /// then the document defaults, then the app setting. Other entry types are never logged.
    /// </summary>
    public static bool ResolveSessionLogging(ConnectionEntry entry, IReadOnlyList<ConnectionFolder> chain, ConnectionDefaults documentDefaults, bool appDefault)
    {
        bool? own = entry switch
        {
            TerminalConnection t => t.SessionLogging,
            SshConnection s => s.SessionLogging,
            _ => false,
        };
        if (own.HasValue) return own.Value;
        foreach (var d in chain.Reverse().Select(f => f.Defaults).Append(documentDefaults))
            if (d.SessionLogging is { } v) return v;
        return appDefault;
    }

    /// <summary>
    /// The icon colour for an entry: its own, then the nearest folder with one (chain is outermost first).
    /// Null means use the type's colour. Unreadable values are skipped, as if not set.
    /// </summary>
    public static string? ResolveIconColour(ConnectionEntry entry, IReadOnlyList<ConnectionFolder> chain) =>
        IconColours.Normalise(entry.IconColour) ?? ResolveIconColour(chain);

    /// <summary>The colour a folder chain passes down: the innermost folder with one set wins.</summary>
    public static string? ResolveIconColour(IReadOnlyList<ConnectionFolder> chain)
    {
        for (var i = chain.Count - 1; i >= 0; i--)
            if (IconColours.Normalise(chain[i].Defaults.IconColour) is { } colour) return colour;
        return null;
    }

    public static EffectiveRdpSettings Resolve(ConnectionsDocument document, RdpConnection entry, Func<Guid, PersonalEntrySettings?>? personal = null) =>
        Resolve(entry, document.ChainFor(entry), document.Defaults, personal);

    /// <summary>
    /// The shared list (nearest setting wins) plus the user's own patterns from the entry and every
    /// folder above it. A personal setting marked "replace" anywhere in the chain drops the shared list.
    /// </summary>
    private static List<string> MergeWatchList(List<string>? shared, RdpConnection entry, IReadOnlyList<ConnectionFolder> chain,
        Func<Guid, PersonalEntrySettings?>? personal)
    {
        var mine = new List<PersonalEntrySettings>();
        if (personal is not null)
        {
            if (personal(entry.Id) is { } own) mine.Add(own);
            foreach (var folder in chain.Reverse())
                if (personal(folder.Id) is { } p) mine.Add(p);
        }

        var replace = mine.Any(p => p.ReplaceSharedWatchList);
        var all = (replace ? [] : shared ?? []).Concat(mine.SelectMany(p => p.WatchedServices ?? []));
        return all.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
}
