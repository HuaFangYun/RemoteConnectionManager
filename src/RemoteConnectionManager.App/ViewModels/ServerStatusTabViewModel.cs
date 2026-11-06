using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteConnectionManager.App.Services;
using RemoteConnectionManager.Plugins;

namespace RemoteConnectionManager.App.ViewModels;

public sealed record ServiceRowViewModel(ServiceRow Row)
{
    public string Name => Row.Service.Name;
    public string DisplayName => Row.Service.DisplayName;
    public string State => Row.Service.State;
    public string StartModeText => Row.Service.StartMode switch
    {
        "Auto" => Row.Service.DelayedStart ? "Automatic (delayed)" : "Automatic",
        var other => other,
    };
    public string Account => Row.Service.Account ?? string.Empty;
    public string ReasonText => Row.Reason switch
    {
        ServiceListReason.Watched => "Watched",
        ServiceListReason.AutomaticNotRunning => "Automatic, not running",
        ServiceListReason.OnDemand => "Starts on demand",
        _ => string.Empty,
    };
    public bool IsRunning => Row.Service.IsRunning;
    public bool NeedsAttention => Row.NeedsAttention;
    public bool IsWatched => Row.Reason == ServiceListReason.Watched;
}

/// <summary>A labelled bar: CPU, memory, a disk.</summary>
public sealed record UsageRow(string Label, string Detail, double Percent)
{
    public bool IsHigh => Percent >= 90;
}

/// <summary>
/// Services and, on request, hardware figures for one host, read through the Server checks plugin.
/// Read only. One tab per host; asking again switches to it and refreshes.
/// </summary>
public sealed partial class ServerStatusTabViewModel : TabViewModelBase, IDisposable
{
    private readonly IServerInspector _inspector;
    private readonly AppLog _log;
    private CancellationTokenSource? _servicesCts;
    private CancellationTokenSource? _hardwareCts;
    private IReadOnlyList<ServiceInfo> _services = Array.Empty<ServiceInfo>();

    public string Host { get; }
    public string EntryName { get; }
    public IReadOnlyList<string> WatchList { get; private set; }
    public ObservableCollection<ServiceRowViewModel> Rows { get; } = new();
    public ObservableCollection<UsageRow> Usage { get; } = new();

    [ObservableProperty] private bool _isLoadingServices;
    [ObservableProperty] private bool _isLoadingHardware;
    [ObservableProperty] private bool _showHardware;
    [ObservableProperty] private bool _showAll;
    [ObservableProperty] private string _filterText = string.Empty;
    [ObservableProperty] private string _summaryText = string.Empty;
    [ObservableProperty] private string _unmatchedText = string.Empty;
    [ObservableProperty] private string _hiddenText = string.Empty;
    [ObservableProperty] private string _readText = string.Empty;
    [ObservableProperty] private string _errorText = string.Empty;
    [ObservableProperty] private string _hardwareError = string.Empty;
    [ObservableProperty] private IReadOnlyList<PropertyRow> _hardwareRows = Array.Empty<PropertyRow>();
    [ObservableProperty] private string _hardwareReadText = string.Empty;

    public bool HasError => ErrorText.Length > 0;
    public string WatchListText => WatchList.Count == 0
        ? "No services on the watch list. Edit the entry or its folder to add some."
        : "Watching: " + string.Join(", ", WatchList);

    public ServerStatusTabViewModel(string host, string entryName, IReadOnlyList<string> watchList, IServerInspector inspector, AppLog log)
    {
        Host = host;
        EntryName = entryName;
        WatchList = watchList;
        _inspector = inspector;
        _log = log;
        Title = $"(Status) {entryName}";
        Glyph = "";
        IconBrushKey = "Icon.Server";
    }

    partial void OnErrorTextChanged(string value) => OnPropertyChanged(nameof(HasError));
    partial void OnShowAllChanged(bool value) => ApplyFilter();
    partial void OnFilterTextChanged(string value) => ApplyFilter();

    /// <summary>Called when the tab is asked for again, possibly with a changed watch list.</summary>
    public void UpdateWatchList(IReadOnlyList<string> watchList)
    {
        WatchList = watchList;
        OnPropertyChanged(nameof(WatchListText));
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        var services = LoadServicesAsync();
        if (ShowHardware) await Task.WhenAll(services, LoadHardwareCoreAsync());
        else await services;
    }

    /// <summary>Services and hardware together, for the Hardware stats command.</summary>
    public Task RefreshAndHardwareAsync()
    {
        ShowHardware = true;
        return RefreshAsync();
    }

    [RelayCommand]
    public async Task LoadHardwareAsync()
    {
        ShowHardware = true;
        await LoadHardwareCoreAsync();
    }

    [RelayCommand]
    private void HideHardware()
    {
        _hardwareCts?.Cancel();
        ShowHardware = false;
    }

    [RelayCommand]
    private void DismissError() => ErrorText = string.Empty;

    private async Task LoadServicesAsync()
    {
        _servicesCts?.Cancel();
        var cts = _servicesCts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        IsLoadingServices = true;
        ErrorText = string.Empty;
        try
        {
            var report = await _inspector.GetServicesAsync(Host, cts.Token);
            if (cts.IsCancellationRequested) return;
            _services = report.Services;
            ReadText = $"{report.Services.Count} services read over {report.Protocol} at {report.ReadAt:HH:mm:ss}";
            var unmatched = ServiceFilter.Unmatched(_services, WatchList);
            UnmatchedText = unmatched.Count == 0 ? string.Empty : "Not on this host: " + string.Join(", ", unmatched);
            ApplyFilter();
            _log.Info($"[Server checks] {Host}: {report.Services.Count} services over {report.Protocol}, {SummaryText}");
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            ErrorText = ex is PluginException ? ex.Message : $"Reading services on {Host} failed: {ex.Message}";
            _log.Warn($"[Server checks] {Host}: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            if (ReferenceEquals(_servicesCts, cts)) IsLoadingServices = false;
        }
    }

    private async Task LoadHardwareCoreAsync()
    {
        _hardwareCts?.Cancel();
        var cts = _hardwareCts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        IsLoadingHardware = true;
        HardwareError = string.Empty;
        try
        {
            var h = await _inspector.GetHardwareAsync(Host, cts.Token);
            if (cts.IsCancellationRequested) return;

            var rows = new List<PropertyRow>
            {
                new("Computer", h.ComputerName),
                new("Windows", h.OsVersion is null ? h.OperatingSystem : $"{h.OperatingSystem} ({h.OsVersion})"),
            };
            if (h.Manufacturer is not null || h.Model is not null)
                rows.Add(new("Model", $"{h.Manufacturer} {h.Model}".Trim()));
            if (h.Processor is not null) rows.Add(new("Processor", h.Processor));
            if (h.Cores is not null || h.LogicalProcessors is not null)
                rows.Add(new("Cores", $"{h.Cores?.ToString() ?? "?"} cores, {h.LogicalProcessors?.ToString() ?? "?"} logical"));
            if (h.LastBoot is { } boot)
                rows.Add(new("Up for", $"{Uptime(DateTimeOffset.Now - boot)} (since {boot.LocalDateTime:yyyy-MM-dd HH:mm})"));
            HardwareRows = rows;

            Usage.Clear();
            if (h.CpuLoadPercent is { } cpu) Usage.Add(new UsageRow("CPU", $"{cpu}% now", cpu));
            if (h.TotalMemoryBytes > 0)
                Usage.Add(new UsageRow("Memory", $"{Gb(h.TotalMemoryBytes - h.FreeMemoryBytes)} of {Gb(h.TotalMemoryBytes)} used", h.MemoryUsedPercent));
            foreach (var d in h.Disks)
                Usage.Add(new UsageRow(d.Label is null ? d.Drive : $"{d.Drive} {d.Label}", $"{Gb(d.FreeBytes)} free of {Gb(d.SizeBytes)}", d.UsedPercent));

            HardwareReadText = $"Read over {h.Protocol} at {h.ReadAt:HH:mm:ss}";
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            HardwareError = ex is PluginException ? ex.Message : $"Reading hardware on {Host} failed: {ex.Message}";
            _log.Warn($"[Server checks] {Host} hardware: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            if (ReferenceEquals(_hardwareCts, cts)) IsLoadingHardware = false;
        }
    }

    private void ApplyFilter()
    {
        var rows = ServiceFilter.Select(_services, WatchList, ShowAll || FilterText.Trim().Length > 0);
        var filter = FilterText.Trim();
        if (filter.Length > 0)
            rows = rows.Where(r => r.Service.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
                                || r.Service.DisplayName.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();

        Rows.Clear();
        foreach (var r in rows) Rows.Add(new ServiceRowViewModel(r));

        var watched = rows.Count(r => r.Reason == ServiceListReason.Watched);
        var attention = rows.Count(r => r.NeedsAttention);
        var hidden = ShowAll || filter.Length > 0 ? 0 : ServiceFilter.CountOnDemandHidden(_services, WatchList);
        HiddenText = hidden == 0 ? string.Empty : $"{hidden} stopped automatic {(hidden == 1 ? "service starts" : "services start")} on demand and {(hidden == 1 ? "is" : "are")} hidden. Show all to see {(hidden == 1 ? "it" : "them")}.";
        SummaryText = attention switch
        {
            0 when watched == 0 => "Every automatic service is running",
            0 => $"{watched} watched {(watched == 1 ? "service is" : "services are")} running, and every automatic service is running",
            1 => "1 service needs attention",
            _ => $"{attention} services need attention",
        };
    }

    private static string Gb(ulong bytes) => bytes >= 1L << 40
        ? $"{bytes / (double)(1L << 40):0.0} TB"
        : $"{bytes / (double)(1L << 30):0.0} GB";

    private static string Uptime(TimeSpan t) => t.TotalDays >= 1
        ? $"{(int)t.TotalDays} d {t.Hours} h"
        : $"{t.Hours} h {t.Minutes} min";

    public void Dispose()
    {
        _servicesCts?.Cancel();
        _hardwareCts?.Cancel();
    }
}
