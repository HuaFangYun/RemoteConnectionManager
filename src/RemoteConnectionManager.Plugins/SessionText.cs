using System.Globalization;

namespace RemoteConnectionManager.Plugins;

/// <summary>Plain words for a session list, shown on the dashboard.</summary>
public static class SessionText
{
    /// <summary>"Nobody is signed in", "1 active", "2 active, 1 disconnected".</summary>
    public static string Summary(SessionReport report)
    {
        if (report.Sessions.Count == 0) return "Nobody is signed in";
        var active = report.Sessions.Count(s => s.IsActive);
        var disconnected = report.Sessions.Count(s => s.IsDisconnected);
        var other = report.Sessions.Count - active - disconnected;
        var parts = new List<string>();
        if (active > 0) parts.Add($"{active} active");
        if (disconnected > 0) parts.Add($"{disconnected} disconnected");
        if (other > 0) parts.Add($"{other} other");
        return string.Join(", ", parts);
    }

    /// <summary>"from PAW01, console, idle 2h 5m, signed in 6 Nov 09:12".</summary>
    public static string Detail(UserSession s, DateTimeOffset now)
    {
        var parts = new List<string>();
        if (s.ClientName is { } client) parts.Add($"from {client}");
        else if (string.Equals(s.SessionName, "console", StringComparison.OrdinalIgnoreCase)) parts.Add("on the console");

        if (s.IsDisconnected && s.DisconnectTime is { } gone) parts.Add($"disconnected {Ago(now - gone)}");
        else if (s.IsActive && s.LastInputTime is { } input && now - input >= TimeSpan.FromMinutes(1)) parts.Add($"idle {Span(now - input)}");

        if (s.LogonTime is { } logon) parts.Add($"signed in {When(logon, now)}");
        return string.Join(", ", parts);
    }

    /// <summary>True when the session belongs to the person running RCM.</summary>
    public static bool IsCurrentUser(UserSession s, string userName, string? domain) =>
        string.Equals(s.UserName, userName, StringComparison.OrdinalIgnoreCase)
        && (s.Domain is null || domain is null || string.Equals(s.Domain, domain, StringComparison.OrdinalIgnoreCase));

    public static string Span(TimeSpan span)
    {
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;
        if (span.TotalMinutes < 1) return "under a minute";
        if (span.TotalHours < 1) return $"{(int)span.TotalMinutes}m";
        if (span.TotalDays < 1) return span.Minutes == 0 ? $"{(int)span.TotalHours}h" : $"{(int)span.TotalHours}h {span.Minutes}m";
        return span.Hours == 0 ? $"{(int)span.TotalDays}d" : $"{(int)span.TotalDays}d {span.Hours}h";
    }

    private static string Ago(TimeSpan span) => span.TotalMinutes < 1 ? "just now" : $"{Span(span)} ago";

    private static string When(DateTimeOffset at, DateTimeOffset now) =>
        at.Date == now.Date
            ? "today " + at.ToString("HH:mm", CultureInfo.InvariantCulture)
            : at.ToString("d MMM HH:mm", CultureInfo.InvariantCulture);
}
