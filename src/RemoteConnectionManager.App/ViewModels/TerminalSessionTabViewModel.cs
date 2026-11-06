using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteConnectionManager.App.Services;
using RemoteConnectionManager.App.Terminal;
using RemoteConnectionManager.Core.Models;
using RemoteConnectionManager.Core.Services;
using RemoteConnectionManager.Core.SessionLogging;
using RemoteConnectionManager.Plugins;

namespace RemoteConnectionManager.App.ViewModels;

public enum TerminalSessionState
{
    Starting,
    Running,
    Exited,
    Failed,
}

/// <summary>Everything a terminal tab needs to know about what it runs. Built by the shell for each kind of entry.</summary>
public sealed record TerminalSessionSpec
{
    /// <summary>First part of the toolbar text, for example "PowerShell 7" or "SSH netadmin@10.22.22.1".</summary>
    public required string ToolbarText { get; init; }
    /// <summary>Second, muted part, for example " in C:\Users\me". May be empty.</summary>
    public string DetailText { get; init; } = string.Empty;
    /// <summary>What the copy command line button copies.</summary>
    public required string CopyText { get; init; }
    public required string Glyph { get; init; }
    public required string IconBrushKey { get; init; }
    /// <summary>Name in the ended bar and the "[... exited]" notice.</summary>
    public required string SessionName { get; init; }
    /// <summary>A ConPTY command line, or null with <see cref="ConnectionFactory"/> set.</summary>
    public TerminalLaunchSpec? Launch { get; init; }
    public Func<IRcmTerminalConnection>? ConnectionFactory { get; init; }
    /// <summary>Null when this session is not logged.</summary>
    public SessionLogInfo? Log { get; init; }
    /// <summary>Vault credential behind Send password; null hides the button.</summary>
    public CredentialReference? Credential { get; init; }
    public string? CredentialDescription { get; init; }
    /// <summary>Shows Send user name (telnet devices ask for it; ssh already passes it).</summary>
    public bool OffersUserName { get; init; }
    public string? UserName { get; init; }
    /// <summary>The session is unencrypted (telnet), so sending a password asks first.</summary>
    public bool IsPlainText { get; init; }
}

/// <summary>
/// One open terminal: a local shell, ssh.exe, or a telnet connection. Owns the
/// <see cref="TerminalHost"/> (the view parents it into the tab) and the session log.
/// Built on the UI thread because the control is a WPF element.
/// </summary>
public sealed partial class TerminalSessionTabViewModel : TabViewModelBase, IDisposable
{
    private readonly AppLog _log;
    private readonly SessionLogService _logs;
    private readonly RdpCredentialService _credentials;
    private readonly IDialogService _dialogs;
    private SessionLogWriter? _writer;
    private bool _sending;

    [ObservableProperty] private TerminalSessionState _state = TerminalSessionState.Starting;
    [ObservableProperty] private string _statusMessage = "Starting";
    [ObservableProperty] private bool _showEndedBar;
    [ObservableProperty] private string _endedMessage = string.Empty;
    [ObservableProperty] private bool _isLogging;
    [ObservableProperty] private string _logToolTip = "This session is not logged";

    public ConnectionEntry Entry { get; }
    public TerminalSessionSpec Spec { get; }
    public TerminalHost Control { get; }

    public string ShellText => Spec.ToolbarText;
    public string WorkingDirectoryText => Spec.DetailText;
    public bool IsRunning => State == TerminalSessionState.Running;
    public bool HasCredential => Spec.Credential is not null;
    public bool ShowSendUserName => Spec.OffersUserName && (Spec.UserName is not null || Spec.Credential is not null);
    public string SendPasswordToolTip => $"Send password: types the password from {Spec.CredentialDescription ?? "the vault"} and presses Enter. Use it at the password prompt.";
    public string? LogFilePath => _writer?.FilePath;

    public override Guid? EntryId => Entry.Id;
    public override bool IsLive => State is TerminalSessionState.Starting or TerminalSessionState.Running;

    public event EventHandler? StateChanged;

    public TerminalSessionTabViewModel(ConnectionEntry entry, TerminalSessionSpec spec, AppLog log, SessionLogService logs,
        RdpCredentialService credentials, IDialogService dialogs)
    {
        Entry = entry;
        Spec = spec;
        _log = log;
        _logs = logs;
        _credentials = credentials;
        _dialogs = dialogs;
        Title = $"{entry.Prefix} {entry.Name}";
        Glyph = spec.Glyph;
        IconBrushKey = spec.IconBrushKey;

        Control = new TerminalHost { Launch = spec.Launch, ConnectionFactory = spec.ConnectionFactory, SessionName = spec.SessionName };
        Control.Started += OnStarted;
        Control.Exited += OnExited;
        Control.StartFailed += OnStartFailed;
        Control.ConnectionCreated += OnConnectionCreated;
        Control.Trace += (_, s) => _log.Info($"[{Entry.Name}] pty: {s}");
        if (spec.ConnectionFactory is not null) StatusMessage = $"Connecting to {spec.ToolbarText}";
    }

    // ---------- session log ----------

    private void OnConnectionCreated(object? sender, TerminalConnectionCreatedEventArgs e)
    {
        if (Spec.Log is null) return;
        if (_writer is null)
        {
            _writer = _logs.Start(Spec.Log, e.Columns, e.Rows);
            IsLogging = _writer is not null;
            LogToolTip = _writer is null
                ? "Could not start the session log (see the app log)"
                : $"Logging to {_writer.FilePath}. Click to open it.";
            OnPropertyChanged(nameof(LogFilePath));
        }
        else
        {
            _writer.AddNote("restarted");
            _writer.Resize(e.Columns, e.Rows);
        }
        if (_writer is not { } writer) return;
        e.Connection.TerminalOutput += (_, output) => writer.Write(output.Data);
        e.Connection.Resized += writer.Resize;
    }

    [RelayCommand]
    private async Task OpenLogAsync()
    {
        if (_writer is null)
        {
            _logs.OpenFolder();
            return;
        }
        await _writer.FlushAsync();
        _logs.OpenFile(_writer.FilePath);
    }

    [RelayCommand]
    private void OpenLogFolder() => _logs.OpenFolder(_writer?.FilePath);

    // ---------- lifecycle ----------

    private void OnStarted(object? sender, EventArgs e)
    {
        SetState(TerminalSessionState.Running, $"{Spec.SessionName} running");
        ShowEndedBar = false;
        _log.Info($"[{Entry.Name}] {Spec.SessionName} started");
    }

    private void OnExited(object? sender, TerminalExitedEventArgs e)
    {
        var why = e.Message ?? (e.ExitCode is { } c ? $"exit code {c}" : "exit code unknown");
        SetState(TerminalSessionState.Exited, $"Ended, {why}");
        EndedMessage = $"{Spec.SessionName} ended ({why}).";
        ShowEndedBar = true;
        _writer?.EndRun($"ended, {why}");
        _log.Info($"[{Entry.Name}] {Spec.SessionName} ended, {why}");
    }

    private void OnStartFailed(object? sender, Exception ex)
    {
        SetState(TerminalSessionState.Failed, "Failed to start");
        EndedMessage = Spec.ConnectionFactory is null ? $"Could not start {Spec.SessionName}: {ex.Message}" : ex.Message;
        ShowEndedBar = true;
        _writer?.EndRun($"failed: {ex.Message}");
        _log.Error($"[{Entry.Name}] failed to start {Spec.CopyText}", ex);
    }

    private void SetState(TerminalSessionState state, string message)
    {
        State = state;
        StatusMessage = message;
        OnPropertyChanged(nameof(IsRunning));
        RestartCommand.NotifyCanExecuteChanged();
        SendPasswordCommand.NotifyCanExecuteChanged();
        SendUserNameCommand.NotifyCanExecuteChanged();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand(CanExecute = nameof(CanRestart))]
    private void Restart()
    {
        ShowEndedBar = false;
        SetState(TerminalSessionState.Starting, Spec.ConnectionFactory is null ? "Starting" : $"Connecting to {Spec.ToolbarText}");
        Control.Restart();
        Control.FocusTerminal();
    }
    private bool CanRestart() => State is TerminalSessionState.Exited or TerminalSessionState.Failed;

    // ---------- credentials ----------

    [RelayCommand(CanExecute = nameof(CanSendPassword))]
    private async Task SendPasswordAsync()
    {
        if (Spec.Credential is not { } reference) return;
        if (Spec.IsPlainText && !_dialogs.Confirm(
                "Telnet is not encrypted: anyone on the network path can read the password. Send it anyway?", "Send password"))
            return;

        _sending = true;
        SendPasswordCommand.NotifyCanExecuteChanged();
        var previous = StatusMessage;
        StatusMessage = $"Reading {Spec.CredentialDescription ?? "credential"}";
        try
        {
            var credential = await _credentials.ReadAsync(reference, Entry.Name, CancellationToken.None);
            // Mask it in the log first, in case the device echoes it (wrong prompt, bad config).
            _writer?.AddSecret(credential.Password);
            Control.SendText(credential.Password + "\r");
            StatusMessage = "Password sent";
        }
        catch (PluginException ex)
        {
            StatusMessage = previous;
            _dialogs.Error(ex.Message, "Send password");
        }
        catch (Exception ex)
        {
            StatusMessage = previous;
            _log.Error($"[{Entry.Name}] Send password failed", ex);
            _dialogs.Error($"Could not read the credential: {ex.Message}", "Send password");
        }
        finally
        {
            _sending = false;
            SendPasswordCommand.NotifyCanExecuteChanged();
            Control.FocusTerminal();
        }
    }
    private bool CanSendPassword() => Spec.Credential is not null && IsRunning && !_sending;

    [RelayCommand(CanExecute = nameof(CanSendUserName))]
    private async Task SendUserNameAsync()
    {
        var user = Spec.UserName;
        if (user is null && Spec.Credential is { } reference)
        {
            try
            {
                user = (await _credentials.ReadAsync(reference, Entry.Name, CancellationToken.None)).Account;
            }
            catch (Exception ex)
            {
                _dialogs.Error($"Could not read the credential: {ex.Message}", "Send user name");
                return;
            }
        }
        if (string.IsNullOrEmpty(user))
        {
            _dialogs.Info("Neither the entry nor its credential has a user name. Add one in the entry.", "Send user name");
            return;
        }
        Control.SendText(user + "\r");
        Control.FocusTerminal();
    }
    private bool CanSendUserName() => ShowSendUserName && IsRunning;

    // ---------- clipboard ----------

    [RelayCommand]
    private void Copy()
    {
        Control.CopySelection();
        Control.FocusTerminal();
    }

    [RelayCommand]
    private void Paste()
    {
        Control.PasteFromClipboard();
        Control.FocusTerminal();
    }

    [RelayCommand]
    private void CopyCommandLine()
    {
        try { System.Windows.Clipboard.SetText(Spec.CopyText); } catch { }
    }

    public void Dispose()
    {
        Control.Started -= OnStarted;
        Control.Exited -= OnExited;
        Control.StartFailed -= OnStartFailed;
        Control.ConnectionCreated -= OnConnectionCreated;
        Control.Dispose();
        _writer?.Dispose();
        _writer = null;
    }
}
