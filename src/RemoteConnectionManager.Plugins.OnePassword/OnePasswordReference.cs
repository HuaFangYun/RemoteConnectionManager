using RemoteConnectionManager.Core.Models;

namespace RemoteConnectionManager.Plugins.OnePassword;

/// <summary>What to ask the CLI for. Vault and account are optional.</summary>
public sealed record OnePasswordTarget(string Item, string? Vault, string? Account)
{
    public string Display => Vault is null ? $"'{Item}'" : $"'{Item}' in vault '{Vault}'";
}

/// <summary>Pure: turns the stored reference into a CLI target, accepting an op:// reference in the item box.</summary>
public static class OnePasswordReference
{
    public const string Scheme = "op://";

    public static OnePasswordTarget? TryParse(CredentialReference reference, out string? problem)
    {
        var item = reference.OnePasswordItem?.Trim();
        var vault = Blank(reference.OnePasswordVault);
        var account = Blank(reference.OnePasswordAccount);
        problem = null;

        if (string.IsNullOrEmpty(item))
        {
            problem = "Enter the 1Password item name, or paste its op://vault/item reference.";
            return null;
        }

        if (item.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase))
        {
            // op://vault/item[/section]/field. The field part is ignored: the item's username and password fields are used.
            var parts = item[Scheme.Length..].Split('/', StringSplitOptions.TrimEntries);
            if (parts.Length < 2 || parts[0].Length == 0 || parts[1].Length == 0)
            {
                problem = "An op:// reference needs at least a vault and an item, for example op://Servers/DC01 admin.";
                return null;
            }
            return new OnePasswordTarget(parts[1], parts[0], account);
        }

        if (item.Contains('/') && vault is null && item.Count(c => c == '/') == 1 && !item.StartsWith('/') && !item.EndsWith('/'))
        {
            // "Vault/Item" typed without the scheme is common enough to accept.
            var slash = item.IndexOf('/');
            return new OnePasswordTarget(item[(slash + 1)..].Trim(), item[..slash].Trim(), account);
        }

        return new OnePasswordTarget(item, vault, account);
    }

    /// <summary>The op arguments for reading the item as JSON. Values travel in the argument list, never through a shell.</summary>
    public static List<string> ItemGetArguments(OnePasswordTarget target, bool reveal)
    {
        var args = new List<string> { "item", "get", target.Item };
        if (target.Vault is { } v) { args.Add("--vault"); args.Add(v); }
        if (target.Account is { } a) { args.Add("--account"); args.Add(a); }
        args.Add("--format");
        args.Add("json");
        if (reveal) args.Add("--reveal");
        return args;
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
