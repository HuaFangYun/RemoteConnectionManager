namespace RemoteConnectionManager.Plugins;

/// <summary>
/// Reads service state and basic hardware figures from a remote Windows host without opening a
/// session on it. Read only: nothing is started, stopped or changed.
/// </summary>
public interface IServerInspector : IRcmPlugin
{
    Task<ServiceReport> GetServicesAsync(string host, CancellationToken ct);
    Task<HardwareReport> GetHardwareAsync(string host, CancellationToken ct);
    /// <summary>Who has a Remote Desktop or console session on the host, like quser /server:.</summary>
    Task<SessionReport> GetSessionsAsync(string host, CancellationToken ct);
}

public sealed record ServiceInfo(
    string Name,
    string DisplayName,
    // Running, Stopped, Start Pending and so on, as Windows reports it.
    string State,
    // Auto, Manual, Disabled, Boot or System.
    string StartMode,
    bool DelayedStart,
    string? Account,
    uint? ProcessId)
{
    public bool IsRunning => string.Equals(State, "Running", StringComparison.OrdinalIgnoreCase);
    public bool IsAutomatic => string.Equals(StartMode, "Auto", StringComparison.OrdinalIgnoreCase);
}

/// <param name="Protocol">How the host was reached, "WinRM" or "DCOM".</param>
public sealed record ServiceReport(string Host, string Protocol, DateTimeOffset ReadAt, IReadOnlyList<ServiceInfo> Services);

public sealed record DiskInfo(string Drive, string? Label, ulong SizeBytes, ulong FreeBytes)
{
    public double UsedPercent => SizeBytes == 0 ? 0 : 100.0 * (SizeBytes - FreeBytes) / SizeBytes;
}

public sealed record HardwareReport(
    string Host,
    string Protocol,
    DateTimeOffset ReadAt,
    string ComputerName,
    string OperatingSystem,
    string? OsVersion,
    string? Manufacturer,
    string? Model,
    string? Processor,
    int? Cores,
    int? LogicalProcessors,
    // Average across processors at the moment of the read, 0 to 100.
    int? CpuLoadPercent,
    ulong TotalMemoryBytes,
    ulong FreeMemoryBytes,
    DateTimeOffset? LastBoot,
    IReadOnlyList<DiskInfo> Disks)
{
    public double MemoryUsedPercent => TotalMemoryBytes == 0 ? 0 : 100.0 * (TotalMemoryBytes - FreeMemoryBytes) / TotalMemoryBytes;
}


/// <summary>One signed in user on a host. Times are local; null when Windows did not report them.</summary>
public sealed record UserSession(
    int SessionId,
    string UserName,
    string? Domain,
    // Active or Disconnected (other states are reported as Windows names them).
    string State,
    // rdp-tcp#12, console and so on.
    string? SessionName,
    // The PC the user connected from, when they connected over RDP.
    string? ClientName,
    DateTimeOffset? LogonTime,
    DateTimeOffset? LastInputTime,
    DateTimeOffset? DisconnectTime)
{
    public bool IsActive => string.Equals(State, "Active", StringComparison.OrdinalIgnoreCase);
    public bool IsDisconnected => string.Equals(State, "Disconnected", StringComparison.OrdinalIgnoreCase);
    public string Account => string.IsNullOrEmpty(Domain) ? UserName : $"{Domain}\\{UserName}";
}

public sealed record SessionReport(string Host, DateTimeOffset ReadAt, IReadOnlyList<UserSession> Sessions);
