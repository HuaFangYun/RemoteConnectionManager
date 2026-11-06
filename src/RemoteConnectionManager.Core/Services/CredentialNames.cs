namespace RemoteConnectionManager.Core.Services;

/// <summary>Pure helpers for turning a user name plus optional domain into what the RDP control wants.</summary>
public static class CredentialNames
{
    /// <summary>Secret tags checked, in order, for the account a vault password belongs to.</summary>
    public static readonly string[] UserNameTags = ["username", "user", "account", "login"];

    /// <summary>
    /// Splits "DOMAIN\user" into its parts. A UPN ("user@domain") stays whole with no domain, which
    /// is how CredSSP expects it. Otherwise the separate domain is used as given.
    /// </summary>
    public static (string UserName, string? Domain) Split(string userName, string? domain)
    {
        var user = userName.Trim();
        var slash = user.IndexOf('\\');
        if (slash > 0 && slash < user.Length - 1)
            return (user[(slash + 1)..], user[..slash]);
        if (user.Contains('@'))
            return (user, null);
        return (user, string.IsNullOrWhiteSpace(domain) ? null : domain.Trim());
    }

    /// <summary>The account name from a secret's tags, matching tag names without regard to case.</summary>
    public static string? FromTags(IReadOnlyDictionary<string, string>? tags)
    {
        if (tags is null || tags.Count == 0) return null;
        foreach (var name in UserNameTags)
            foreach (var (key, value) in tags)
                if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(value))
                    return value.Trim();
        return null;
    }

    public static string Display(string userName, string? domain) =>
        domain is null ? userName : $"{domain}\\{userName}";
}
