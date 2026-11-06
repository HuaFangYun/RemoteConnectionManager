using System.Text.RegularExpressions;

namespace RemoteConnectionManager.Plugins.OnePassword;

/// <summary>Pure: turns op.exe's stderr into words the user can act on.</summary>
public static partial class OnePasswordErrors
{
    public static string Describe(string stderr, OnePasswordTarget target)
    {
        var text = Clean(stderr);
        var lower = text.ToLowerInvariant();

        if (lower.Contains("dismissed") || lower.Contains("authorization timeout") || lower.Contains("authorization denied") || lower.Contains("canceled") || lower.Contains("cancelled"))
            return "1Password was not unlocked, so the credential was not read. Reconnect and approve the 1Password prompt.";
        if (lower.Contains("not currently signed in") || lower.Contains("no accounts configured") || lower.Contains("account is not signed in")
            || lower.Contains("connecting to desktop app") || lower.Contains("cannot connect to 1password app") || lower.Contains("1password app is not running"))
            return "The 1Password CLI could not reach the 1Password app. Open 1Password, then in Settings > Developer turn on 'Integrate with 1Password CLI', and reconnect.";
        if (lower.Contains("multiple accounts") || lower.Contains("more than one account"))
            return "More than one 1Password account is set up. Edit the entry and fill in the 1Password account (its sign in address, for example my.1password.com, or the account email).";
        if (lower.Contains("isn't a vault") || lower.Contains("is not a vault") || lower.Contains("vault not found"))
            return $"1Password has no vault named '{target.Vault}' that you can see. Check the vault name on the entry or its folder.";
        if (lower.Contains("more than one item matches"))
            return $"More than one 1Password item matches {target.Display}. Use the item's id, or set the vault, so only one matches.";
        if (lower.Contains("isn't an item") || lower.Contains("is not an item") || lower.Contains("item not found") || lower.Contains("no item found"))
            return $"1Password has no item {target.Display}. Check the item name on the entry or its folder.";
        if (lower.Contains("no account found") || lower.Contains("account not found"))
            return $"1Password has no account matching '{target.Account}'. Use the sign in address shown in 1Password, for example my.1password.com.";

        return text.Length == 0 ? "The 1Password CLI failed without saying why." : $"1Password CLI: {text}";
    }

    /// <summary>Drops the "[ERROR] 2026/11/05 10:00:00" prefix op puts on each line.</summary>
    public static string Clean(string stderr)
    {
        var lines = stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(l => Prefix().Replace(l, string.Empty).Trim())
            .Where(l => l.Length > 0);
        var text = string.Join(" ", lines);
        return text.Length > 400 ? text[..400] + "..." : text;
    }

    [GeneratedRegex(@"^\[(ERROR|WARN|INFO)\]\s*(\d{4}/\d{2}/\d{2}\s+\d{2}:\d{2}:\d{2}\s*)?", RegexOptions.IgnoreCase)]
    private static partial Regex Prefix();

    public static bool IsUnknownRevealFlag(string stderr) =>
        stderr.Contains("unknown flag", StringComparison.OrdinalIgnoreCase) && stderr.Contains("reveal", StringComparison.OrdinalIgnoreCase);
}
