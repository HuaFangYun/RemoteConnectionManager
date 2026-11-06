using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteConnectionManager.Core.Models;
using RemoteConnectionManager.Core.Services;
using RemoteConnectionManager.Plugins;

namespace RemoteConnectionManager.App.ViewModels;

public sealed record PropertyRow(string Label, string Value);

/// <summary>One signed in user on the selected host.</summary>
public sealed record SessionRow(string Account, string State, string Detail, bool IsActive, bool IsYou)
{
    public string YouSuffix => IsYou ? " (you)" : string.Empty;
}

/// <summary>The pinned first tab. Shows the selected tree node and a Connect button.</summary>
public sealed partial class DashboardTabViewModel : TabViewModelBase
{
    private readonly ShellViewModel _shell;

    [ObservableProperty] private TreeNodeViewModel? _node;
    [ObservableProperty] private string _heading = "RemoteConnectionManager";
    [ObservableProperty] private string _subheading = string.Empty;
    [ObservableProperty] private string _description = string.Empty;
    [ObservableProperty] private bool _canConnect;
    [ObservableProperty] private string _connectLabel = "Connect";
    [ObservableProperty] private bool _canCheckServer;
    /// <summary>Folder in the shared database: offer Permissions (enabled when the user has Manage).</summary>
    [ObservableProperty] private bool _showPermissions;
    /// <summary>Folder or RDP entry: offer the user's own watched services.</summary>
    [ObservableProperty] private bool _showPersonal;

    /// <summary>The shell's commands carry the permission checks, so the dashboard binds to them directly.</summary>
    public ShellViewModel Shell => _shell;
    [ObservableProperty] private IReadOnlyList<PropertyRow> _properties = Array.Empty<PropertyRow>();

    // ---- Who is signed in (background check through the Server checks plugin) ----

    /// <summary>A short wait so arrowing down the tree does not query every host it passes.</summary>
    private static readonly TimeSpan SessionCheckDelay = TimeSpan.FromMilliseconds(400);
    /// <summary>Clicking back to a host within this time shows the last answer instead of asking again.</summary>
    private static readonly TimeSpan SessionCacheTime = TimeSpan.FromSeconds(60);
    private readonly Dictionary<string, SessionReport> _sessionCache = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _sessionsCts;
    private string? _sessionsHost;

    [ObservableProperty] private bool _showSessions;
    [ObservableProperty] private bool _isCheckingSessions;
    [ObservableProperty] private string _sessionsStatus = string.Empty;
    [ObservableProperty] private bool _sessionsFailed;
    [ObservableProperty] private IReadOnlyList<SessionRow> _sessions = Array.Empty<SessionRow>();

    public override bool CanClose => false;

    public DashboardTabViewModel(ShellViewModel shell)
    {
        _shell = shell;
        Title = "Dashboard";
        Glyph = "";
        IconBrushKey = "Icon.Dashboard";
        Refresh();
    }

    partial void OnNodeChanged(TreeNodeViewModel? value) => Refresh();

    public void Refresh()
    {
        var doc = _shell.Document;
        CanCheckServer = _shell.CanCheckServer();
        ShowPermissions = Node?.Folder is not null && _shell.IsSqlStore;
        ShowPersonal = Node is { Folder: not null } or { Entry: RdpConnection };
        UpdateSessionCheck(Node?.Entry is RdpConnection selected && CanCheckServer ? _shell.ResolveRdp(selected).Host : null);
        if (Node is null)
        {
            Heading = "RemoteConnectionManager";
            Subheading = $"{doc.EntryCount} entries in {doc.AllFolders().Count()} folders";
            Description = "Select an entry on the left, or type a host into the quick connect box at the bottom.";
            CanConnect = false;
            var rows = new List<PropertyRow>
            {
                new(_shell.IsSqlStore ? "Connections" : "Connections file", _shell.ConnectionsFilePath),
                new("Signed in as", _shell.UserText),
            };
            if (_shell.IsSqlStore && !string.IsNullOrEmpty(_shell.StoreDetailText)) rows.Add(new("Database", _shell.StoreDetailText));
            rows.Add(new("Azure account", _shell.AzureAccountText.Replace("Azure: ", string.Empty)));
            Properties = rows.ToArray();
            return;
        }

        if (Node.Folder is { } folder)
        {
            Heading = folder.Name;
            Subheading = string.IsNullOrEmpty(Node.Path) ? "Folder" : $"Folder in {Node.Path}";
            Description = folder.Description ?? string.Empty;
            CanConnect = false;
            var rows = new List<PropertyRow>
            {
                new("Entries", $"{Node.Descendants().Count(n => !n.IsFolder)}"),
            };
            var d = folder.Defaults;
            if (d.Domain is { } dom) rows.Add(new("Default domain", dom));
            if (d.UserName is { } user) rows.Add(new("Default user", user));
            if (d.Port is { } port) rows.Add(new("Default port", port.ToString()));
            if (d.AdminSession is { } admin) rows.Add(new("Admin session", admin ? "Yes" : "No"));
            if (d.KeyboardPassThrough is { } kb) rows.Add(new("Keyboard pass through", kb ? "Yes" : "No"));
            if (d.RedirectClipboard is { } clip) rows.Add(new("Clipboard", clip ? "Yes" : "No"));
            if (d.SmartSizing is { } smart) rows.Add(new("Smart sizing", smart ? "Yes" : "No"));
            if (d.Credential is { } cred) rows.Add(new("Credential", DescribeCredential(cred.Source == CredentialSource.Windows ? null : cred, null)));
            if (d.WatchedServices is { } watched) rows.Add(new("Watched services", watched.Count == 0 ? "none" : string.Join(", ", watched)));
            if (d.CredentialProtection is { } protection) rows.Add(new("Protection", DescribeProtection(protection)));
            if (d.GatewayHost is { } gw) rows.Add(new("RD Gateway", d.GatewayUsage == GatewayUsage.Never ? "Never" : gw));
            if (d.SshUserName is { } sshUser) rows.Add(new("SSH and telnet user", sshUser));
            if (d.SshCredential is { } sshCred) rows.Add(new("SSH and telnet password", sshCred.Source == CredentialSource.Windows ? "None (typed at the prompt)" : _shell.DescribeCredential(sshCred)));
            if (d.SessionLogging is { } logging) rows.Add(new("Session logs", logging ? "On" : "Off"));
            if (_shell.AccessTextFor(folder) is { } access) rows.Add(new("Your access", access));
            if (_shell.PersonalWatchText(folder.Id) is { } mine) rows.Add(new("My watched services", mine));
            Properties = rows;
            return;
        }

        var entry = Node.Entry!;
        Heading = entry.Name;
        Subheading = $"{entry.Prefix} {(string.IsNullOrEmpty(Node.Path) ? "" : "in " + Node.Path)}".Trim();
        Description = entry.Description ?? string.Empty;

        switch (entry)
        {
            case RdpConnection rdp:
            {
                var s = _shell.ResolveRdp(rdp);
                CanConnect = !string.IsNullOrWhiteSpace(s.Host);
                ConnectLabel = "Connect";
                Properties = new[]
                {
                    new PropertyRow("Host", s.DisplayTarget),
                    new PropertyRow("Sign in as", s.Protection == CredentialProtection.RemoteCredentialGuard
                        ? "Logged on user"
                        : DescribeCredential(s.VaultCredential, s.UserName is null ? null : CredentialNames.Display(s.UserName, s.Domain))),
                    new PropertyRow("Protection", DescribeProtection(s.Protection) + (s.ConflictWarning is { } warn ? $". {warn}" : string.Empty)),
                    new PropertyRow("RD Gateway", s.UsesGateway
                        ? s.GatewayHost + (s.Gateway == GatewayUsage.BypassLocal ? " (not for local addresses)" : string.Empty)
                        : "None, direct"),
                    new PropertyRow("Admin session", s.AdminSession ? "Yes" : "No"),
                    new PropertyRow("Watched services", (s.WatchedServices.Count == 0 ? "none" : string.Join(", ", s.WatchedServices))
                        + (_shell.PersonalWatchText(rdp.Id) is not null ? " (includes your own)" : string.Empty)),
                    new PropertyRow("Keyboard pass through", s.KeyboardPassThrough ? "Yes" : "No"),
                    new PropertyRow("Clipboard", s.RedirectClipboard ? "Yes" : "No"),
                    new PropertyRow("Resize", s.SmartSizing ? "Smart sizing (scaled)" : "Live resize"),
                    new PropertyRow("Tags", entry.Tags.Count == 0 ? "none" : string.Join(", ", entry.Tags)),
                };
                break;
            }
            case TerminalConnection t:
            {
                var launch = TerminalLauncher.Build(t);
                var exe = TerminalLauncher.FindExecutable(t.Shell);
                CanConnect = exe is not null;
                ConnectLabel = "Open terminal";
                Properties = new[]
                {
                    new PropertyRow("Shell", launch.ShellDisplayName),
                    new PropertyRow("Executable", exe ?? $"{launch.Executable} not found"),
                    new PropertyRow("Working directory", launch.WorkingDirectory),
                    new PropertyRow("Arguments", string.IsNullOrWhiteSpace(t.Arguments) ? "none" : t.Arguments),
                    new PropertyRow("Start script", launch.StartScript ?? "none"),
                    new PropertyRow("Session log", _shell.SessionLoggingText(t)),
                    new PropertyRow("Tags", entry.Tags.Count == 0 ? "none" : string.Join(", ", entry.Tags)),
                };
                break;
            }
            case KeyVaultConnection kv:
            {
                var uri = KeyVault.VaultNames.NormaliseVaultUri(kv.VaultUri);
                CanConnect = uri is not null;
                ConnectLabel = "Open vault";
                Properties = new[]
                {
                    new PropertyRow("Vault", uri?.ToString() ?? $"{kv.VaultUri} (not a valid vault address)"),
                    new PropertyRow("Tenant", string.IsNullOrWhiteSpace(kv.TenantId) ? "Home tenant of the signed in account" : kv.TenantId),
                    new PropertyRow("Azure account", _shell.AzureAccountText.Replace("Azure: ", string.Empty)),
                    new PropertyRow("Tags", entry.Tags.Count == 0 ? "none" : string.Join(", ", entry.Tags)),
                };
                break;
            }
            case SshConnection ssh:
            {
                var s = _shell.ResolveSsh(ssh);
                var telnet = s.Protocol == TerminalProtocol.Telnet;
                CanConnect = !string.IsNullOrWhiteSpace(s.Host);
                ConnectLabel = telnet ? "Open telnet" : "Open SSH";
                var rows = new List<PropertyRow>
                {
                    new("Target", s.DisplayTarget),
                    new("Protocol", telnet ? "Telnet (not encrypted)" : "SSH, Windows OpenSSH client"),
                    new("User name", s.UserName ?? (telnet ? "typed at the device prompt" : $"{Environment.UserName} (Windows user)")),
                    new("Send password", s.VaultCredential is { } c ? _shell.DescribeCredential(c) : "No vault credential: type it at the prompt"),
                };
                if (!telnet)
                {
                    if (!string.IsNullOrWhiteSpace(ssh.IdentityFile)) rows.Add(new("Key file", ssh.IdentityFile));
                    if (!string.IsNullOrWhiteSpace(ssh.JumpHost)) rows.Add(new("Jump host", ssh.JumpHost));
                    rows.Add(new("Legacy algorithms", ssh.LegacyAlgorithms ? "On (for older switches)" : "Off"));
                    if (!string.IsNullOrWhiteSpace(ssh.Arguments)) rows.Add(new("Extra arguments", ssh.Arguments));
                }
                rows.Add(new("Session log", _shell.SessionLoggingText(ssh)));
                rows.Add(new("Tags", entry.Tags.Count == 0 ? "none" : string.Join(", ", entry.Tags)));
                Properties = rows;
                break;
            }
            case WebConnection web:
            {
                var uri = WebConnection.Normalise(web.Url);
                CanConnect = uri is not null;
                ConnectLabel = web.OpenIn == WebOpenMode.Tab ? "Open in a tab" : "Open in browser";
                Properties = new[]
                {
                    new PropertyRow("Address", uri?.ToString() ?? $"{web.Url} (not a valid http or https address)"),
                    new PropertyRow("Opens in", web.OpenIn == WebOpenMode.Tab ? "A tab in RCM (right click for the browser)" : "The default browser (right click for a tab)"),
                    new PropertyRow("Tags", entry.Tags.Count == 0 ? "none" : string.Join(", ", entry.Tags)),
                };
                break;
            }
        }
    }

    private string DescribeCredential(CredentialReference? vault, string? userName)
    {
        if (vault is not null)
            return _shell.DescribeCredential(vault) + (userName is null ? string.Empty : $" for {userName}");
        return userName is null ? "Logged on user" : $"{userName} (Windows prompts for the password)";
    }

    private static string DescribeProtection(CredentialProtection protection) => protection switch
    {
        CredentialProtection.RestrictedAdmin => "Restricted Admin",
        CredentialProtection.RemoteCredentialGuard => "Remote Credential Guard",
        _ => "Normal",
    };

    [RelayCommand]
    private void Connect()
    {
        if (Node?.Entry is { } entry) _shell.OpenEntry(entry);
    }

    /// <summary>
    /// Starts a background read of who is signed in when the selected host changes. Refresh runs
    /// often (saves, settings), so the same host keeps its current answer.
    /// </summary>
    private void UpdateSessionCheck(string? host)
    {
        if (string.IsNullOrWhiteSpace(host) || _shell.ServerInspector is null)
        {
            _sessionsCts?.Cancel();
            _sessionsHost = null;
            ShowSessions = false;
            IsCheckingSessions = false;
            return;
        }
        ShowSessions = true;
        if (string.Equals(host, _sessionsHost, StringComparison.OrdinalIgnoreCase)) return;
        _sessionsHost = host;

        if (_sessionCache.TryGetValue(host, out var cached) && DateTimeOffset.Now - cached.ReadAt < SessionCacheTime)
        {
            _sessionsCts?.Cancel();
            IsCheckingSessions = false;
            ShowReport(cached);
            return;
        }
        _ = CheckSessionsAsync(host, SessionCheckDelay);
    }

    [RelayCommand]
    private Task RefreshSessions() => _sessionsHost is { } host ? CheckSessionsAsync(host, TimeSpan.Zero) : Task.CompletedTask;

    private async Task CheckSessionsAsync(string host, TimeSpan delay)
    {
        if (_shell.ServerInspector is not { } inspector) return;
        _sessionsCts?.Cancel();
        var cts = new CancellationTokenSource();
        _sessionsCts = cts;

        IsCheckingSessions = true;
        SessionsFailed = false;
        Sessions = Array.Empty<SessionRow>();
        SessionsStatus = $"Checking who is signed in to {host}...";
        try
        {
            if (delay > TimeSpan.Zero) await Task.Delay(delay, cts.Token);
            // The native call cannot be stopped part way, so WaitAsync lets a newer check take over.
            var report = await inspector.GetSessionsAsync(host, cts.Token).WaitAsync(cts.Token);
            if (cts.IsCancellationRequested) return;
            _sessionCache[host] = report;
            ShowReport(report);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (!cts.IsCancellationRequested)
        {
            SessionsFailed = true;
            SessionsStatus = ex is PluginException ? ex.Message : $"Could not read sessions on {host}: {ex.Message}";
        }
        finally
        {
            if (ReferenceEquals(_sessionsCts, cts)) IsCheckingSessions = false;
        }
    }

    private void ShowReport(SessionReport report)
    {
        var now = DateTimeOffset.Now;
        var me = Environment.UserName;
        var myDomain = Environment.UserDomainName;
        Sessions = report.Sessions
            .Select(s => new SessionRow(s.Account, s.State, SessionText.Detail(s, now), s.IsActive, SessionText.IsCurrentUser(s, me, myDomain)))
            .ToArray();
        SessionsFailed = false;
        SessionsStatus = $"{SessionText.Summary(report)}. Checked {report.ReadAt:HH:mm:ss}.";
    }

    [RelayCommand]
    private void CheckServices()
    {
        if (Node?.Entry is RdpConnection rdp) _shell.OpenServerStatus(rdp, hardware: false);
    }

    [RelayCommand]
    private void HardwareStats()
    {
        if (Node?.Entry is RdpConnection rdp) _shell.OpenServerStatus(rdp, hardware: true);
    }
}
