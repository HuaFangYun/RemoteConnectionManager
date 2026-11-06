using RemoteConnectionManager.Core.Models;

namespace RemoteConnectionManager.Core.Services;

/// <summary>Case insensitive match over name, description, tags, host, URL and vault URI.</summary>
public static class ConnectionSearch
{
    public static bool Matches(ConnectionEntry entry, string filter)
    {
        if (string.IsNullOrWhiteSpace(filter)) return true;
        var terms = filter.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return terms.All(term => Fields(entry).Any(f => f.Contains(term, StringComparison.OrdinalIgnoreCase)));
    }

    public static bool Matches(ConnectionFolder folder, string filter)
    {
        if (string.IsNullOrWhiteSpace(filter)) return true;
        return folder.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
               || folder.Connections.Any(c => Matches(c, filter))
               || folder.Folders.Any(f => Matches(f, filter));
    }

    private static IEnumerable<string> Fields(ConnectionEntry entry)
    {
        yield return entry.Name;
        yield return entry.Prefix;
        if (entry.Favourite) yield return "favourite";
        if (entry.Description is { } d) yield return d;
        foreach (var tag in entry.Tags) yield return tag;
        switch (entry)
        {
            case RdpConnection rdp:
                yield return rdp.Host;
                break;
            case KeyVaultConnection kv:
                yield return kv.VaultUri;
                break;
            case SshConnection ssh:
                yield return ssh.Host;
                yield return ssh.Protocol.ToString();
                if (ssh.UserName is { } u) yield return u;
                break;
            case WebConnection web:
                yield return web.Url;
                break;
            case TerminalConnection t:
                yield return t.Shell.ToString();
                if (t.WorkingDirectory is { } wd) yield return wd;
                break;
        }
    }
}
