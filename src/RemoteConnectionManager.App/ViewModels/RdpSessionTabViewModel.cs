using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteConnectionManager.App.Services;
using RemoteConnectionManager.Core.Models;
using RemoteConnectionManager.Plugins;
using RemoteConnectionManager.Rdp;

namespace RemoteConnectionManager.App.ViewModels;

/// <summary>
/// One open RDP session. Owns the WinForms control; whichever view currently hosts the session
/// (the tab, or a detached / full screen window) parents it into its WindowsFormsHost.
/// The control must be created on the UI thread, which is where this view model is built.
/// </summary>
public sealed partial class RdpSessionTabViewModel : TabViewModelBase, IDisposable
{
    private readonly AppLog _log;
    private readonly RdpCredentialService _credentials;
    private readonly ISessionWindows _windows;
    private CancellationTokenSource? _connectCts;
    private SessionPlacement _beforeFullScreen = SessionPlacement.Tab;
    private bool _connectStarted;
    private bool _disposed;

    [ObservableProperty] private RdpSessionState _state = RdpSessionState.Idle;
    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private bool _keyboardPassThrough;
    [ObservableProperty] private bool _showOverlay;
    [ObservableProperty] private string _overlayTitle = string.Empty;
    [ObservableProperty] private string _overlayDetail = string.Empty;
    [ObservableProperty] private bool _isResolvingCredential;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDetached), nameof(IsFullScreen), nameof(PlacementText))]
    private SessionPlacement _placement = SessionPlacement.Tab;

    public RdpConnection Entry { get; }
    public EffectiveRdpSettings Settings { get; }
    public RdpSessionControl Control { get; }

    /// <summary>The display a full screen session covers. Null unless full screen.</summary>
    public MonitorChoice? FullScreenMonitor { get; private set; }

    public string TargetText => Settings.UsesGateway ? $"{Settings.DisplayTarget} via {Settings.GatewayHost}" : Settings.DisplayTarget;
    public bool IsConnected => State == RdpSessionState.Connected;
    public bool IsDetached => Placement != SessionPlacement.Tab;
    public bool IsFullScreen => Placement == SessionPlacement.FullScreen;
    public string PlacementText => Placement switch
    {
        SessionPlacement.FullScreen => $"This session is full screen on {FullScreenMonitor?.Label ?? "another display"}.",
        _ => "This session is open in its own window.",
    };

    /// <summary>Short description of the sign in for the toolbar tooltip.</summary>
    public string SignInText
    {
        get
        {
            var protection = Settings.Protection switch
            {
                CredentialProtection.RestrictedAdmin => ", Restricted Admin",
                CredentialProtection.RemoteCredentialGuard => ", Remote Credential Guard",
                _ => string.Empty,
            };
            if (Settings.Protection == CredentialProtection.RemoteCredentialGuard) return "Logged on user" + protection;
            if (Settings.VaultCredential is { } v) return _credentials.Describe(v) + protection;
            return (Settings.UserName is null ? "Logged on user" : $"{Settings.UserName} (Windows prompts)") + protection;
        }
    }

    public IReadOnlyList<MonitorChoice> AvailableMonitors => Monitors.All();

    public override Guid? EntryId => Entry.Id;
    public override bool IsLive => State is RdpSessionState.Connected or RdpSessionState.Connecting;

    public event EventHandler? StateChanged;
    /// <summary>The session asked to close its tab (the Close button on the disconnected panel).</summary>
    public event EventHandler? CloseRequested;

    public RdpSessionTabViewModel(RdpConnection entry, EffectiveRdpSettings settings, AppLog log, RdpCredentialService credentials, ISessionWindows windows)
    {
        Entry = entry;
        Settings = settings;
        _log = log;
        _credentials = credentials;
        _windows = windows;
        Title = $"{entry.Prefix} {entry.Name}";
        Glyph = "";
        IconBrushKey = "Icon.RemoteDesktop";
        KeyboardPassThrough = settings.KeyboardPassThrough;

        Control = new RdpSessionControl();
        Control.StateChanged += OnControlStateChanged;
        Control.Disconnected += OnDisconnected;
        Control.Log += (_, e) => _log.Info($"[{entry.Name}] {e.Message}");

        if (settings.ConflictWarning is { } warning) _log.Warn($"[{entry.Name}] {warning}");
    }

    /// <summary>Called by the view once the host has a real size, so DesktopWidth/Height are right. Only the first call connects.</summary>
    public void ConnectIfIdle()
    {
        if (_connectStarted || State != RdpSessionState.Idle) return;
        _connectStarted = true;
        _ = ConnectAsync(reconnect: false);
    }

    private async Task ConnectAsync(bool reconnect)
    {
        RdpSignIn? signIn = null;
        if (Settings.VaultCredential is { } reference && Settings.Protection != CredentialProtection.RemoteCredentialGuard)
        {
            _connectCts?.Cancel();
            var cts = _connectCts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            IsResolvingCredential = true;
            OverlayTitle = "Getting the credential";
            OverlayDetail = $"Reading {_credentials.Describe(reference)}";
            StatusMessage = "Reading the credential";
            ShowOverlay = true;
            NotifyCommands();
            try
            {
                signIn = await _credentials.ResolveAsync(Settings, Entry.Name, cts.Token);
            }
            catch (Exception ex)
            {
                if (_disposed) return;
                _log.Warn($"[{Entry.Name}] Credential not read: {ex.GetType().Name}: {ex.Message}");
                OverlayTitle = "Could not get the credential";
                OverlayDetail = ex is PluginException or InvalidOperationException ? ex.Message : $"Reading the credential failed: {ex.Message}";
                StatusMessage = "Not connected";
                return;
            }
            finally
            {
                IsResolvingCredential = false;
                NotifyCommands();
            }
            if (_disposed) return;

            // Let layout give the host its size back before the control reads it.
            ShowOverlay = false;
            await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);
            if (_disposed) return;
        }

        if (reconnect) Control.Reconnect(signIn); else Control.Connect(Settings, signIn);
    }

    private void OnControlStateChanged(object? sender, EventArgs e)
    {
        State = Control.State;
        KeyboardPassThrough = Control.KeyboardPassThrough;
        StatusMessage = State switch
        {
            RdpSessionState.Connecting => $"Connecting to {TargetText}",
            RdpSessionState.Connected => $"Connected to {TargetText}",
            RdpSessionState.Disconnected => "Disconnected",
            _ => string.Empty,
        };
        if (State is RdpSessionState.Connecting or RdpSessionState.Connected) ShowOverlay = false;
        OnPropertyChanged(nameof(IsConnected));
        NotifyCommands();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnDisconnected(object? sender, RdpDisconnectedEventArgs e)
    {
        OverlayTitle = e.Requested ? "Disconnected" : "Connection lost";
        OverlayDetail = e.Description;
        ShowOverlay = true;
    }

    private void NotifyCommands()
    {
        ReconnectCommand.NotifyCanExecuteChanged();
        DisconnectCommand.NotifyCanExecuteChanged();
    }

    // ---------- session commands ----------

    /// <summary>Reads the vault credential again when there is one: the password is never kept between attempts.</summary>
    [RelayCommand(CanExecute = nameof(CanReconnect))]
    private void Reconnect()
    {
        _connectStarted = true;
        _ = ConnectAsync(reconnect: true);
    }
    private bool CanReconnect() => !IsResolvingCredential && State is RdpSessionState.Disconnected or RdpSessionState.Idle;

    [RelayCommand(CanExecute = nameof(CanDisconnect))]
    private void Disconnect() => Control.Disconnect();
    private bool CanDisconnect() => State is RdpSessionState.Connected or RdpSessionState.Connecting;

    [RelayCommand]
    private void ToggleKeyboard() => Control.SetKeyboardPassThrough(!Control.KeyboardPassThrough);

    [RelayCommand]
    private void Fit() => Control.FitNow();

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke(this, EventArgs.Empty);

    // ---------- placement: tab, window, full screen ----------

    /// <summary>Moves the session into its own window. The tab stays, showing where the session went.</summary>
    [RelayCommand]
    private void Detach()
    {
        if (Placement != SessionPlacement.Tab) { _windows.Activate(this); return; }
        _log.Info($"[{Entry.Name}] Moved to its own window");
        Placement = SessionPlacement.Window; // the tab's view lets go of the control first
        _windows.Show(this);                  // then the window's view takes it
    }

    /// <summary>Brings the session back into its tab.</summary>
    [RelayCommand]
    private void Dock()
    {
        if (Placement == SessionPlacement.Tab) return;
        _windows.Close(this); // the window releases the control and calls OnWindowClosed
    }

    /// <summary>Full screen on the given display, or the one the session is on now.</summary>
    [RelayCommand]
    private void FullScreen(MonitorChoice? monitor)
    {
        monitor ??= _windows.CurrentMonitor(this);
        if (monitor is null) return;
        if (Placement != SessionPlacement.FullScreen) _beforeFullScreen = Placement;
        FullScreenMonitor = monitor;
        _log.Info($"[{Entry.Name}] Full screen on {monitor.Label}");
        Placement = SessionPlacement.FullScreen;
        OnPropertyChanged(nameof(PlacementText));
        _windows.Show(this);
    }

    /// <summary>Leaves full screen, back to where the session was before: its tab or its window.</summary>
    [RelayCommand]
    private void ExitFullScreen()
    {
        if (Placement != SessionPlacement.FullScreen) return;
        if (_beforeFullScreen == SessionPlacement.Tab)
        {
            _windows.Close(this);
            return;
        }
        FullScreenMonitor = null;
        Placement = SessionPlacement.Window;
        _windows.Show(this);
    }

    [RelayCommand]
    private void ShowWindow() => _windows.Activate(this);

    /// <summary>Called by the session window once it has released the control and closed.</summary>
    public void OnWindowClosed()
    {
        FullScreenMonitor = null;
        if (_disposed) return;
        Placement = SessionPlacement.Tab; // the tab's view takes the control back
        _log.Info($"[{Entry.Name}] Back in its tab");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _connectCts?.Cancel();
        _windows.Close(this);
        Control.StateChanged -= OnControlStateChanged;
        Control.Disconnected -= OnDisconnected;
        Control.Dispose();
    }
}
