using System.Text.RegularExpressions;

namespace RemoteConnectionManager.Plugins;

/// <summary>Why a service is in the list the user sees.</summary>
public enum ServiceListReason
{
    /// <summary>Matches the entry's watch list.</summary>
    Watched,
    /// <summary>Set to start automatically but not running.</summary>
    AutomaticNotRunning,
    /// <summary>Automatic and stopped, but known to start on demand and stop again, so not a problem.</summary>
    OnDemand,
    /// <summary>Only shown because "show all" is on.</summary>
    Other,
}

public sealed record ServiceRow(ServiceInfo Service, ServiceListReason Reason)
{
    /// <summary>A watched service that is not running, or an automatic one that stopped.</summary>
    public bool NeedsAttention => !Service.IsRunning && Reason is ServiceListReason.Watched or ServiceListReason.AutomaticNotRunning;
}

/// <summary>
/// Pure: picks which services to show. Watched services first (by watch list order), then automatic
/// services that are not running, then, with showAll, everything else by display name.
/// </summary>
public static class ServiceFilter
{
    /// <summary>
    /// Automatic services that normally sit stopped (trigger start, or start, do their job and stop).
    /// Listing them as "needs attention" would be noise on every host. A watch list entry still wins.
    /// </summary>
    public static readonly IReadOnlyList<string> KnownOnDemand =
    [
        "sppsvc", "MapsBroker", "RemoteRegistry", "gpsvc", "BITS", "wuauserv", "UsoSvc", "WbioSrvc",
        "tiledatamodelsvc", "TrustedInstaller", "sedsvc", "clr_optimization_*", "gupdate", "gupdatem",
        "edgeupdate", "edgeupdatem", "GoogleUpdater*", "brave*", "MozillaMaintenance", "AdobeARMservice",
        "CDPSvc", "DoSvc", "WaaSMedicSvc", "IntelAudioService", "SysMain",
    ];

    /// <summary>
    /// True when the pattern matches the service's short name or display name, ignoring case.
    /// '*' and '?' are wildcards, so "SQL*" covers every SQL Server service.
    /// </summary>
    public static bool Matches(string pattern, ServiceInfo service)
    {
        var p = pattern.Trim();
        if (p.Length == 0) return false;
        if (!p.Contains('*') && !p.Contains('?'))
            return string.Equals(p, service.Name, StringComparison.OrdinalIgnoreCase)
                || string.Equals(p, service.DisplayName, StringComparison.OrdinalIgnoreCase);
        var regex = "^" + Regex.Escape(p).Replace(@"\*", ".*").Replace(@"\?", ".") + "$";
        return Regex.IsMatch(service.Name, regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            || Regex.IsMatch(service.DisplayName, regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    public static IReadOnlyList<ServiceRow> Select(IEnumerable<ServiceInfo> services, IReadOnlyList<string> watchList, bool showAll) =>
        Select(services, watchList, showAll, KnownOnDemand);

    public static IReadOnlyList<ServiceRow> Select(IEnumerable<ServiceInfo> services, IReadOnlyList<string> watchList, bool showAll, IReadOnlyList<string> onDemand)
    {
        var all = services.ToList();
        var rows = new List<ServiceRow>();
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var pattern in watchList)
            foreach (var s in all.Where(s => Matches(pattern, s)).OrderBy(s => s.DisplayName, StringComparer.OrdinalIgnoreCase))
                if (taken.Add(s.Name)) rows.Add(new ServiceRow(s, ServiceListReason.Watched));

        var stoppedAuto = all.Where(s => s.IsAutomatic && !s.IsRunning).OrderBy(s => s.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var s in stoppedAuto.Where(s => !onDemand.Any(p => Matches(p, s))))
            if (taken.Add(s.Name)) rows.Add(new ServiceRow(s, ServiceListReason.AutomaticNotRunning));

        if (showAll)
            foreach (var s in stoppedAuto)
                if (taken.Add(s.Name)) rows.Add(new ServiceRow(s, ServiceListReason.OnDemand));

        if (showAll)
            foreach (var s in all.OrderBy(s => s.DisplayName, StringComparer.OrdinalIgnoreCase))
                if (taken.Add(s.Name)) rows.Add(new ServiceRow(s, ServiceListReason.Other));

        return rows;
    }

    /// <summary>How many stopped automatic services were left out because they start on demand.</summary>
    public static int CountOnDemandHidden(IEnumerable<ServiceInfo> services, IReadOnlyList<string> watchList) =>
        services.Count(s => s.IsAutomatic && !s.IsRunning && KnownOnDemand.Any(p => Matches(p, s)) && !watchList.Any(p => Matches(p, s)));

    /// <summary>Watch list patterns that matched nothing, so the user can spot a typo or a missing role.</summary>
    public static IReadOnlyList<string> Unmatched(IEnumerable<ServiceInfo> services, IReadOnlyList<string> watchList)
    {
        var all = services.ToList();
        return watchList.Where(p => !string.IsNullOrWhiteSpace(p) && !all.Any(s => Matches(p, s))).Select(p => p.Trim()).ToList();
    }

    /// <summary>Splits the editor's text ("MSSQLSERVER, SQLSERVERAGENT; W3SVC") into patterns.</summary>
    public static List<string> ParseList(string? text) =>
        (text ?? string.Empty).Split([',', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
}
