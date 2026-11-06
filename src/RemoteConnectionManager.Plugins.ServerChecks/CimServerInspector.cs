using System.Collections.Concurrent;
using System.Net.Sockets;
using Microsoft.Management.Infrastructure;
using Microsoft.Management.Infrastructure.Options;

namespace RemoteConnectionManager.Plugins.ServerChecks;

/// <summary>
/// Reads Win32_Service and a handful of hardware classes from a remote host. Tries WinRM (WS-Man,
/// port 5985) first because it is on by default on servers and crosses firewalls more easily,
/// then DCOM (RPC 135 plus dynamic ports). Both run as the logged on user. Read only.
/// </summary>
public sealed class CimServerInspector : IServerInspector
{
    private const string Namespace = @"root\cimv2";
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromMilliseconds(1500);
    private const int WinRmPort = 5985;

    /// <summary>Hosts where WinRM failed, so later reads go straight to DCOM. Cleared when DCOM fails too.</summary>
    private readonly ConcurrentDictionary<string, bool> _dcomOnly = new(StringComparer.OrdinalIgnoreCase);

    public string Id => "serverchecks";
    public string Name => "Server checks";
    public string Description =>
        "Check services, hardware stats and who is signed in on an RDP entry's host without signing in to it. " +
        "Uses WinRM, or DCOM when WinRM is not listening, and SMB for sessions, as your logged on account. Read only.";

    public Task<PluginStatus> GetStatusAsync(CancellationToken ct) =>
        Task.FromResult(PluginStatus.Ok("Ready. Needs admin rights (or Remote Management Users for WinRM) on the hosts you check."));

    public Task<ServiceReport> GetServicesAsync(string host, CancellationToken ct) => Task.Run(() =>
    {
        var (services, protocol) = WithSession(host, ct, session =>
            Query(session, "SELECT * FROM Win32_Service", ct).Select(ToService).ToList());
        return new ServiceReport(host, protocol, DateTimeOffset.Now, services);
    }, ct);

    public Task<HardwareReport> GetHardwareAsync(string host, CancellationToken ct) => Task.Run(() =>
    {
        var (report, protocol) = WithSession(host, ct, session =>
        {
            var os = Query(session, "SELECT * FROM Win32_OperatingSystem", ct).FirstOrDefault();
            var cs = Query(session, "SELECT * FROM Win32_ComputerSystem", ct).FirstOrDefault();
            var cpus = Query(session, "SELECT * FROM Win32_Processor", ct).ToList();
            var disks = Query(session, "SELECT * FROM Win32_LogicalDisk WHERE DriveType = 3", ct)
                .Select(d => new DiskInfo(
                    Str(d, "DeviceID") ?? "?",
                    Str(d, "VolumeName") is { Length: > 0 } label ? label : null,
                    U64(d, "Size") ?? 0,
                    U64(d, "FreeSpace") ?? 0))
                .Where(d => d.SizeBytes > 0)
                .OrderBy(d => d.Drive, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var loads = cpus.Select(c => U64(c, "LoadPercentage")).Where(v => v.HasValue).Select(v => (int)v!.Value).ToList();
            var cores = cpus.Select(c => U64(c, "NumberOfCores")).Where(v => v.HasValue).Sum(v => (int)v!.Value);
            var boot = os is null ? null : Get<DateTime?>(os, "LastBootUpTime");

            return new HardwareReport(
                Host: host,
                Protocol: string.Empty,
                ReadAt: DateTimeOffset.Now,
                ComputerName: (os is null ? null : Str(os, "CSName")) ?? host,
                OperatingSystem: (os is null ? null : Str(os, "Caption"))?.Trim() ?? "Unknown",
                OsVersion: os is null ? null : Str(os, "Version"),
                Manufacturer: cs is null ? null : Str(cs, "Manufacturer"),
                Model: cs is null ? null : Str(cs, "Model"),
                Processor: cpus.Select(c => Str(c, "Name")?.Trim()).FirstOrDefault(n => !string.IsNullOrEmpty(n)) is { } name
                    ? (cpus.Count > 1 ? $"{cpus.Count} x {name}" : name) : null,
                Cores: cores > 0 ? cores : null,
                LogicalProcessors: cs is null ? null : (int?)U64(cs, "NumberOfLogicalProcessors"),
                CpuLoadPercent: loads.Count > 0 ? (int)Math.Round(loads.Average()) : null,
                // Both are reported in KB.
                TotalMemoryBytes: (os is null ? 0 : U64(os, "TotalVisibleMemorySize") ?? 0) * 1024,
                FreeMemoryBytes: (os is null ? 0 : U64(os, "FreePhysicalMemory") ?? 0) * 1024,
                LastBoot: boot is { } b ? new DateTimeOffset(b) : null,
                Disks: disks);
        });
        return report with { Protocol = protocol };
    }, ct);

    public Task<SessionReport> GetSessionsAsync(string host, CancellationToken ct) =>
        Task.Run(() => WtsSessionReader.Read(host, ct), ct);

    /// <summary>Runs the work over WinRM, then over DCOM if WinRM fails. Reports both failures if neither works.</summary>
    /// <remarks>
    /// A closed WinRM port makes WS-Man wait out its whole timeout, so a short TCP probe decides
    /// first, and a host that needed DCOM once goes straight to DCOM after that.
    /// </remarks>
    private (T Result, string Protocol) WithSession<T>(string host, CancellationToken ct, Func<CimSession, T> work)
    {
        CimException? winrmError = null;
        string? winrmSkipped = null;
        if (_dcomOnly.ContainsKey(host)) winrmSkipped = "not used, it failed earlier";
        else if (!PortOpen(host, WinRmPort, ct)) winrmSkipped = $"port {WinRmPort} is not open";

        if (winrmSkipped is null)
        {
            try
            {
                using var session = CimSession.Create(host, new WSManSessionOptions { Timeout = ConnectTimeout });
                return (work(session), "WinRM");
            }
            catch (CimException ex) when (!ct.IsCancellationRequested)
            {
                winrmError = ex;
            }
        }

        ct.ThrowIfCancellationRequested();
        try
        {
            using var session = CimSession.Create(host, new DComSessionOptions { Timeout = ConnectTimeout });
            var result = work(session);
            _dcomOnly[host] = true;
            return (result, "DCOM");
        }
        catch (CimException ex) when (!ct.IsCancellationRequested)
        {
            _dcomOnly.TryRemove(host, out _);
            throw new PluginException(CimErrors.Describe(host, winrmError, winrmSkipped, ex), ex);
        }
    }

    private static bool PortOpen(string host, int port, CancellationToken ct)
    {
        try
        {
            using var client = new TcpClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(ProbeTimeout);
            client.ConnectAsync(host, port, cts.Token).AsTask().GetAwaiter().GetResult();
            return true;
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return false;
        }
    }

    private static IEnumerable<CimInstance> Query(CimSession session, string wql, CancellationToken ct)
    {
        var options = new CimOperationOptions { Timeout = QueryTimeout, CancellationToken = ct };
        // Materialise inside the session's lifetime.
        return session.QueryInstances(Namespace, "WQL", wql, options).ToList();
    }

    private static ServiceInfo ToService(CimInstance s) => new(
        Name: Str(s, "Name") ?? "?",
        DisplayName: Str(s, "DisplayName") ?? Str(s, "Name") ?? "?",
        State: Str(s, "State") ?? "Unknown",
        StartMode: Str(s, "StartMode") ?? "Unknown",
        // DelayedAutoStart is missing on older hosts, so it is read rather than selected.
        DelayedStart: Get<bool?>(s, "DelayedAutoStart") ?? false,
        Account: Str(s, "StartName"),
        ProcessId: U64(s, "ProcessId") is { } pid and > 0 ? (uint)pid : null);

    private static object? Raw(CimInstance i, string name)
    {
        var p = i.CimInstanceProperties[name];
        return p?.Value;
    }

    private static string? Str(CimInstance i, string name) => Raw(i, name)?.ToString();

    private static T? Get<T>(CimInstance i, string name) => Raw(i, name) is T v ? v : default;

    private static ulong? U64(CimInstance i, string name)
    {
        try
        {
            return Raw(i, name) is { } v ? Convert.ToUInt64(v, System.Globalization.CultureInfo.InvariantCulture) : null;
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
        {
            return null;
        }
    }
}
