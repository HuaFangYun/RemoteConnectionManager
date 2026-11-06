namespace RemoteConnectionManager.KeyVault;

public enum ExpiryState
{
    None,
    Ok,
    Soon,
    Expired,
}

/// <summary>How the lists colour and filter expiry dates. Pure, unit tested.</summary>
public static class ExpiryRules
{
    /// <summary>"Expiring soon" means within this many days.</summary>
    public const int SoonDays = 30;

    public static ExpiryState StateOf(DateTimeOffset? expiresOn, DateTimeOffset now)
    {
        if (expiresOn is not { } e) return ExpiryState.None;
        if (e <= now) return ExpiryState.Expired;
        return e - now <= TimeSpan.FromDays(SoonDays) ? ExpiryState.Soon : ExpiryState.Ok;
    }

    /// <summary>Short text for the list column, for example "in 12 days" or "3 days ago".</summary>
    public static string Describe(DateTimeOffset? expiresOn, DateTimeOffset now)
    {
        if (expiresOn is not { } e) return "never";
        if (e <= now)
        {
            var ago = (int)Math.Floor((now - e).TotalDays);
            return ago == 0 ? "today" : ago == 1 ? "1 day ago" : $"{ago} days ago";
        }
        var days = (int)Math.Floor((e - now).TotalDays);
        return days == 0 ? "today" : days == 1 ? "in 1 day" : $"in {days} days";
    }

    /// <summary>True when the item counts towards the "expiring soon" list (soon or already expired).</summary>
    public static bool NeedsAttention(DateTimeOffset? expiresOn, DateTimeOffset now) =>
        StateOf(expiresOn, now) is ExpiryState.Soon or ExpiryState.Expired;
}
