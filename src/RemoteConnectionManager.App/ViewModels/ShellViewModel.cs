using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteConnectionManager.App.Services;
using RemoteConnectionManager.Core.Import;
using RemoteConnectionManager.Core.Models;
using RemoteConnectionManager.Core.Services;
using RemoteConnectionManager.Core.SessionLogging;
using RemoteConnectionManager.Core.Storage;
using RemoteConnectionManager.Data.SqlServer;
using RemoteConnectionManager.KeyVault;
using RemoteConnectionManager.Plugins;

namespace RemoteConnectionManager.App.ViewModels;

/// <summary>
/// Central state: the loaded document, the navigation tree, the tab strip and every command
/// on the command bar. Saves the document after each edit.
/// </summary>
public sealed partial class ShellViewModel : ObservableObject
{
    private StorageBackend _backend;
    private IConnectionStore _store => _backend.Store;
    /// <summary>The person's own settings on entries and folders (watched services), by id.</summary>
    private IReadOnlyDictionary<Guid, PersonalEntrySettings> _personal = new Dictionary<Guid, PersonalEntrySettings>();
    /// <summary>True after a failed load, so an empty document is never saved over a database that could not be read.</summary>
    private bool _loadFailed;
    private readonly AppSettingsStore _settingsStore;
    private readonly AppSettings _settings;
    private readonly IDialogService _dialogs;
    private readonly AppLog _log;
    private readonly AzureAuth _azure;
    private readonly VaultCatalog _vaultCatalog;
    private readonly ClipboardGuard _clipboard;
    private readonly RdpCredentialService _credentials;
    private readonly ISessionWindows _sessionWindows;
    private readonly PluginRegistry _plugins;
    private readonly SessionLogService _sessionLogs;

    public ConnectionsDocument Document { get; private set; } = new();
    public ObservableCollection<TreeNodeViewModel> RootNodes { get; } = new();
    public ObservableCollection<TabViewModelBase> Tabs { get; } = new();
    public DashboardTabViewModel Dashboard { get; }

    [ObservableProperty] private string _filterText = string.Empty;
    [ObservableProperty] private TreeNodeViewModel? _selectedNode;
    [ObservableProperty] private TabViewModelBase? _selectedTab;
    [ObservableProperty] private string _quickConnectHost = string.Empty;
    [ObservableProperty] private string _entryCountText = string.Empty;
    [ObservableProperty] private string _statusText = string.Empty;
    [ObservableProperty] private bool _navigationVisible = true;
    [ObservableProperty] private double _navigationWidth = 300;
    [ObservableProperty] private string _azureAccountText = "Azure: not signed in";
    /// <summary>Shown above the tree when the shared database has something newer; cleared by a reload.</summary>
    [ObservableProperty] private string? _changesNotice;

    public string UserText => $"{Environment.UserDomainName}\\{Environment.UserName}";
    public string ConnectionsFilePath => _store.Location;
    public string ConnectionsFileName => _store.DisplayName;
    /// <summary>Second line under the data source: who the database knows you as, or why it is read only.</summary>
    public string StoreDetailText => _store.ReadOnlyReason ?? (_store.SignedInAs is { } who ? $"{who}{(_store.IsAdmin ? " (RCM admin)" : string.Empty)}" : string.Empty);
    public bool StoreIsReadOnly => _store.IsReadOnly;
    public bool IsSqlStore => _store.Kind == StorageKind.SqlServer;
    /// <summary>True when the Users and groups window is available (SQL Server, RCM admin, online).</summary>
    public bool CanAdminister() => _backend.Admin is not null && _store.IsAdmin && !_store.IsReadOnly;
    public bool ShowAdminButton => CanAdminister();
    public string VersionText => $"v{typeof(ShellViewModel).Assembly.GetName().Version?.ToString(3)} x64";

    public ShellViewModel(StorageBackend backend, AppSettingsStore settingsStore, AppSettings settings, IDialogService dialogs, AppLog log,
        AzureAuth azure, VaultCatalog vaultCatalog, ClipboardGuard clipboard, RdpCredentialService credentials, ISessionWindows sessionWindows,
        PluginRegistry plugins, SessionLogService sessionLogs)
    {
        _plugins = plugins;
        _sessionLogs = sessionLogs;
        _credentials = credentials;
        _sessionWindows = sessionWindows;
        _backend = backend;
        _settingsStore = settingsStore;
        _settings = settings;
        _dialogs = dialogs;
        _log = log;
        _azure = azure;
        _vaultCatalog = vaultCatalog;
        _clipboard = clipboard;

        _azure.Changed += (_, _) => Application.Current?.Dispatcher.BeginInvoke(UpdateAzureAccount);
        _clipboard.Cleared += (_, message) => StatusText = message;
        _ = _azure.LoadAsync();

        // Preferences that follow the person (SQL Server); a no-op for the file back end.
        _backend.Profile.ApplyPreferences(settings);
        NavigationVisible = settings.NavigationVisible;
        NavigationWidth = settings.NavigationWidth;

        Dashboard = new DashboardTabViewModel(this);
        Tabs.Add(Dashboard);
        SelectedTab = Dashboard;

        LoadDocument();
        AttachStore();
    }

    private void AttachStore()
    {
        _store.ExternalChange += OnStoreExternalChange;
        _store.StatusChanged += OnStoreStatusChanged;
        _store.StartWatching();
    }

    private void OnStoreExternalChange(object? sender, EventArgs e)
    {
        // A store swapped out in Settings may still fire once; ignore it.
        if (!ReferenceEquals(sender, _store)) return;
        // A file edited elsewhere is reloaded straight away (a later save would overwrite it otherwise).
        // The shared database only says so; the person reloads when ready, and row versions stop stale saves.
        if (_store.Kind == StorageKind.File) Application.Current?.Dispatcher.BeginInvoke(OnExternalChange);
        else Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            if (ChangesNotice is not null) return;
            ChangesNotice = _store.IsReadOnly
                ? "The database is reachable again. Reload to get the latest and make changes."
                : "There are newer changes in the database. Reload to see them.";
            StatusText = $"Newer changes in {ConnectionsFileName}. Press Reload (F5) to see them.";
        });
    }

    /// <summary>The store lost or regained the database without reloading: refresh the read-only note and what can be done.</summary>
    private void OnStoreStatusChanged(object? sender, EventArgs e)
    {
        if (!ReferenceEquals(sender, _store)) return;
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            OnPropertyChanged(nameof(StoreDetailText));
            OnPropertyChanged(nameof(StoreIsReadOnly));
            OnPropertyChanged(nameof(ShowAdminButton));
            RefreshCommandStates();
            StatusText = _store.IsReadOnly
                ? $"{ConnectionsFileName} is read only since {DateTime.Now:HH:mm:ss}"
                : $"{ConnectionsFileName} is back online at {DateTime.Now:HH:mm:ss}";
        });
    }

    // ---------- document and tree ----------

    private void LoadDocument()
    {
        _loadFailed = false;
        ChangesNotice = null;
        try
        {
            Document = _store.Load();
        }
        catch (Exception ex)
        {
            _log.Error("Failed to load connections", ex);
            var tail = _store.Kind == StorageKind.File
                ? "Starting with an empty list; the file is left untouched until you save a change."
                : "Starting with an empty list. Check Settings > Storage, then press Reload.";
            _dialogs.Error($"Could not load connections from {_store.Location}.\n\n{ex.Message}\n\n{tail}", "Connections");
            Document = new ConnectionsDocument();
            _loadFailed = true;
        }
        try
        {
            _personal = _backend.Profile.LoadEntrySettings();
        }
        catch (Exception ex)
        {
            _log.Warn($"Personal settings not loaded: {ex.Message}");
        }
        RebuildTree();
        StatusText = _store.IsReadOnly ? $"Loaded {ConnectionsFileName} (read only)" : $"Loaded {ConnectionsFileName}";
        OnPropertyChanged(nameof(ConnectionsFilePath));
        OnPropertyChanged(nameof(ConnectionsFileName));
        OnPropertyChanged(nameof(StoreDetailText));
        OnPropertyChanged(nameof(StoreIsReadOnly));
        OnPropertyChanged(nameof(IsSqlStore));
        OnPropertyChanged(nameof(ShowAdminButton));
        RefreshCommandStates();
    }

    private PersonalEntrySettings? PersonalFor(Guid id) => _personal.GetValueOrDefault(id);

    /// <summary>"View", "Edit" or "Manage" on a folder in the shared database; null for a file.</summary>
    public string? AccessTextFor(ConnectionFolder folder) => IsSqlStore
        ? _store.AccessFor(folder.Id) switch
        {
            AccessLevel.Manage => "Manage (edit and change permissions)",
            AccessLevel.Edit => "Edit",
            AccessLevel.View => "View only",
            _ => "None",
        }
        : null;

    /// <summary>Inherited settings for an RDP entry, including the user's own watched services.</summary>
    public EffectiveRdpSettings ResolveRdp(RdpConnection rdp) => SettingsResolver.Resolve(Document, rdp, PersonalFor);

    public EffectiveSshSettings ResolveSsh(SshConnection ssh) => SettingsResolver.Resolve(Document, ssh);

    /// <summary>Dashboard words for whether a terminal, SSH or telnet entry is logged, and where.</summary>
    public string SessionLoggingText(ConnectionEntry entry) =>
        SettingsResolver.ResolveSessionLogging(entry, Document.ChainFor(entry), Document.Defaults, _settings.SessionLogs.Enabled)
            ? $"On, to {_sessionLogs.Folder}" + (_settings.SessionLogs.Redact ? " (secrets masked)" : string.Empty)
            : "Off";

    /// <summary>The user's own watched services on an entry or folder, for the dashboard.</summary>
    public string? PersonalWatchText(Guid id) => PersonalFor(id) is { IsEmpty: false } p
        ? (p.ReplaceSharedWatchList ? "only: " : "plus: ") + string.Join(", ", p.WatchedServices ?? [])
        : null;

    /// <summary>Gives each open session tab its entry's icon colour, so an edit recolours tabs already open.</summary>
    private void RefreshTabColours()
    {
        if (!Tabs.Any(t => t.EntryId is not null)) return;
        var colours = Document.AllEntries().ToDictionary(e => e.Entry.Id, e => SettingsResolver.ResolveIconColour(e.Entry, e.Chain));
        foreach (var tab in Tabs)
            if (tab.EntryId is { } id && colours.TryGetValue(id, out var colour))
                tab.SetIconColour(colour);
    }

    private void RebuildTree(Guid? reselect = null)
    {
        var expanded = new HashSet<Guid>(RootNodes.SelectMany(r => r.Descendants().Append(r)).Where(n => n.IsFolder && n.IsExpanded).Select(n => n.Folder!.Id));
        if (expanded.Count == 0) expanded.UnionWith(_settings.ExpandedFolders);

        RootNodes.Clear();
        foreach (var folder in Document.Folders) RootNodes.Add(new TreeNodeViewModel(folder, null));
        foreach (var entry in Document.Connections) RootNodes.Add(new TreeNodeViewModel(entry, null));

        foreach (var node in AllNodes())
        {
            if (node.IsFolder && expanded.Contains(node.Folder!.Id)) node.IsExpanded = true;
            if (node.Entry is { } e) node.OpenSessions = Tabs.Count(t => t.EntryId == e.Id && t.IsLive);
        }

        if (!string.IsNullOrWhiteSpace(FilterText))
            foreach (var root in RootNodes) root.ApplyFilter(FilterText);

        EntryCountText = $"{Document.EntryCount} entries";
        Dashboard.Refresh();
        RefreshTabColours();

        if (reselect is { } id)
        {
            var node = AllNodes().FirstOrDefault(n => (n.Entry?.Id ?? n.Folder?.Id) == id);
            if (node is not null)
            {
                for (var p = node.Parent; p is not null; p = p.Parent) p.IsExpanded = true;
                node.IsSelected = true;
                SelectedNode = node;
            }
        }
    }

    private IEnumerable<TreeNodeViewModel> AllNodes() => RootNodes.SelectMany(r => r.Descendants().Prepend(r));

    private void SaveDocument(Guid? reselect = null)
    {
        if (_loadFailed && _store.Kind == StorageKind.SqlServer)
        {
            _dialogs.Error("The connections could not be loaded, so nothing was saved. Press Reload once the database is reachable.", "Connections");
            LoadDocument();
            return;
        }
        try
        {
            _store.Save(Document);
            StatusText = $"Saved to {ConnectionsFileName} at {DateTime.Now:HH:mm:ss}";
        }
        catch (Exception ex) when (ex is StoreConflictException or StorePermissionException)
        {
            // Nothing was saved. Reload so the tree shows what is really there.
            _log.Warn($"Save refused: {ex.Message}");
            var what = ex is StoreConflictException ? "Someone else changed this first, so your change was not saved" : "Your change was not saved";
            _dialogs.Error($"{what}.\n\n{ex.Message}\n\nThe tree will reload with the current version.", "Not saved");
            LoadDocument();
            RebuildTree(reselect);
            return;
        }
        catch (Exception ex)
        {
            _log.Error("Failed to save connections", ex);
            if (_store.Kind == StorageKind.File)
            {
                // Keep the in-memory change; the next successful save writes it.
                _dialogs.Error($"Could not save {_store.Location}.\n\n{ex.Message}", "Connections file");
                RebuildTree(reselect);
                return;
            }
            _dialogs.Error($"Could not save to {_store.Location}.\n\n{ex.Message}\n\nThe tree will reload.", "Connections");
            LoadDocument();
            RebuildTree(reselect);
            return;
        }
        RebuildTree(reselect);
    }

    private void OnExternalChange()
    {
        _log.Info($"Connections changed in {_store.DisplayName}, reloading");
        var selected = SelectedNode?.Entry?.Id ?? SelectedNode?.Folder?.Id;
        LoadDocument();
        if (selected is { } id) RebuildTree(id);
        StatusText = $"Reloaded {ConnectionsFileName} at {DateTime.Now:HH:mm:ss}";
    }

    partial void OnFilterTextChanged(string value)
    {
        foreach (var root in RootNodes) root.ApplyFilter(value);
    }

    partial void OnSelectedNodeChanged(TreeNodeViewModel? value)
    {
        Dashboard.Node = value;
        RefreshCommandStates();
    }

    private void RefreshCommandStates()
    {
        EditCommand.NotifyCanExecuteChanged();
        DeleteCommand.NotifyCanExecuteChanged();
        DuplicateCommand.NotifyCanExecuteChanged();
        ConnectCommand.NotifyCanExecuteChanged();
        CheckServicesCommand.NotifyCanExecuteChanged();
        HardwareStatsCommand.NotifyCanExecuteChanged();
        NewFolderCommand.NotifyCanExecuteChanged();
        NewRdpCommand.NotifyCanExecuteChanged();
        NewTerminalCommand.NotifyCanExecuteChanged();
        NewKeyVaultCommand.NotifyCanExecuteChanged();
        NewSshCommand.NotifyCanExecuteChanged();
        NewTelnetCommand.NotifyCanExecuteChanged();
        NewWebsiteCommand.NotifyCanExecuteChanged();
        OpenInBrowserCommand.NotifyCanExecuteChanged();
        OpenInTabCommand.NotifyCanExecuteChanged();
        ImportMRemoteNgCommand.NotifyCanExecuteChanged();
        ImportRdmCommand.NotifyCanExecuteChanged();
        ImportRoyalTsCommand.NotifyCanExecuteChanged();
        FolderPermissionsCommand.NotifyCanExecuteChanged();
        MyWatchedServicesCommand.NotifyCanExecuteChanged();
        OpenAdminCommand.NotifyCanExecuteChanged();
    }

    // ---------- permissions (always Manage for a file) ----------

    private AccessLevel AccessFor(ConnectionFolder? folder) => _store.AccessFor(folder?.Id);

    private bool CanChange(ConnectionFolder? folder) => AccessFor(folder) >= AccessLevel.Edit;

    /// <summary>The folder that holds the selected node, or null at the top level.</summary>
    private ConnectionFolder? ParentOfSelected => SelectedNode?.Parent?.Folder;

    partial void OnSelectedTabChanged(TabViewModelBase? value)
    {
        foreach (var tab in Tabs) tab.IsSelected = ReferenceEquals(tab, value);
    }

    // ---------- commands: entries ----------

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private void Connect()
    {
        if (SelectedNode?.Entry is { } entry) OpenEntry(entry);
    }
    private bool CanConnect() => SelectedNode?.Entry is RdpConnection or TerminalConnection or KeyVaultConnection or SshConnection or WebConnection;

    public void OpenEntry(ConnectionEntry entry)
    {
        switch (entry)
        {
            case RdpConnection rdp:
                OpenRdp(rdp);
                break;
            case TerminalConnection terminal:
                OpenTerminal(terminal);
                break;
            case SshConnection ssh:
                OpenSsh(ssh);
                break;
            case WebConnection web:
                OpenWeb(web, web.OpenIn);
                break;
            case KeyVaultConnection kv:
                var uri = VaultNames.NormaliseVaultUri(kv.VaultUri);
                if (uri is null)
                {
                    _dialogs.Error($"'{kv.Name}' has no valid vault address. Edit it and enter the vault name or https://name.vault.azure.net.", "Open vault");
                    return;
                }
                OpenVault(uri, kv.TenantId, kv.Name, kv);
                break;
        }
    }

    private void OpenRdp(RdpConnection rdp)
    {
        var settings = SettingsResolver.Resolve(Document, rdp, PersonalFor);
        if (string.IsNullOrWhiteSpace(settings.Host))
        {
            _dialogs.Error($"'{rdp.Name}' has no host.", "Connect");
            return;
        }

        var tab = new RdpSessionTabViewModel(rdp, settings, _log, _credentials, _sessionWindows);
        tab.StateChanged += (_, _) => UpdateOpenSessionCounts();
        tab.CloseRequested += (_, _) => CloseTab(tab);
        tab.SetIconColour(SettingsResolver.ResolveIconColour(rdp, Document.ChainFor(rdp)));
        Tabs.Add(tab);
        SelectedTab = tab;
        UpdateOpenSessionCounts();
        _log.Info($"Opened RDP tab for {rdp.Name} ({settings.DisplayTarget})");
    }

    private void OpenTerminal(TerminalConnection terminal)
    {
        var exePath = TerminalLauncher.FindExecutable(terminal.Shell);
        if (exePath is null)
        {
            var hint = terminal.Shell == TerminalShell.Pwsh ? " Install PowerShell 7 or change the entry to Windows PowerShell." : string.Empty;
            _dialogs.Error($"{TerminalLauncher.ExecutableName(terminal.Shell)} was not found on this machine.{hint}", "Open terminal");
            return;
        }

        var launch = TerminalLauncher.Build(terminal).WithExecutablePath(exePath);
        if (!Directory.Exists(launch.WorkingDirectory))
        {
            _log.Warn($"Working directory '{launch.WorkingDirectory}' for '{terminal.Name}' does not exist, using the profile folder");
            launch = launch with { WorkingDirectory = TerminalLauncher.ResolveWorkingDirectory(null) };
        }

        var spec = new TerminalSessionSpec
        {
            ToolbarText = launch.ShellDisplayName,
            DetailText = $" in {launch.WorkingDirectory}",
            CopyText = launch.CommandLine,
            Glyph = "",
            IconBrushKey = "Icon.Terminal",
            SessionName = launch.Executable,
            Launch = launch,
            Log = LogInfoFor(terminal, launch.ShellDisplayName, launch.Executable),
        };
        AddTerminalTab(terminal, spec);
        _log.Info($"Opened terminal tab for {terminal.Name}: {launch.CommandLine}");
    }

    private void OpenSsh(SshConnection ssh)
    {
        var settings = SettingsResolver.Resolve(Document, ssh);
        if (string.IsNullOrWhiteSpace(settings.Host))
        {
            _dialogs.Error($"'{ssh.Name}' has no host.", "Connect");
            return;
        }

        var kind = settings.Protocol == TerminalProtocol.Telnet ? "Telnet" : "SSH";
        var common = new TerminalSessionSpec
        {
            ToolbarText = $"{kind} {settings.DisplayTarget}",
            CopyText = string.Empty,
            Glyph = "",
            IconBrushKey = "Icon.NetworkDevice",
            SessionName = kind.ToLowerInvariant(),
            Log = LogInfoFor(ssh, kind, settings.DisplayTarget),
            Credential = settings.VaultCredential,
            CredentialDescription = settings.VaultCredential is { } c ? _credentials.Describe(c) : null,
        };

        TerminalSessionSpec spec;
        if (settings.Protocol == TerminalProtocol.Ssh)
        {
            var exe = SshLauncher.FindExecutable();
            if (exe is null)
            {
                _dialogs.Error("The Windows OpenSSH client (ssh.exe) is not installed. Add it in Settings > System > Optional features > OpenSSH Client.", "Connect");
                return;
            }
            var algorithms = ssh.LegacyAlgorithms ? SshAlgorithmProbe.Get(exe, _log) : null;
            var launch = SshLauncher.Build(ssh, settings, algorithms).WithExecutablePath(exe);
            spec = common with { Launch = launch, CopyText = launch.CommandLine, SessionName = "ssh" };
        }
        else
        {
            var host = settings.Host;
            var port = settings.Port;
            spec = common with
            {
                ConnectionFactory = () => new Terminal.TelnetConnection(host, port),
                CopyText = $"telnet {host} {port}",
                OffersUserName = true,
                UserName = settings.UserName,
                IsPlainText = true,
            };
        }

        AddTerminalTab(ssh, spec);
        _log.Info($"Opened {kind} tab for {ssh.Name}: {spec.CopyText}");
    }

    private void AddTerminalTab(ConnectionEntry entry, TerminalSessionSpec spec)
    {
        var tab = new TerminalSessionTabViewModel(entry, spec, _log, _sessionLogs, _credentials, _dialogs);
        tab.StateChanged += (_, _) => UpdateOpenSessionCounts();
        tab.SetIconColour(SettingsResolver.ResolveIconColour(entry, Document.ChainFor(entry)));
        Tabs.Add(tab);
        SelectedTab = tab;
        UpdateOpenSessionCounts();
    }

    /// <summary>What the session log header says, or null when this entry is not logged.</summary>
    private SessionLogInfo? LogInfoFor(ConnectionEntry entry, string kind, string target)
    {
        var chain = Document.ChainFor(entry);
        if (!SettingsResolver.ResolveSessionLogging(entry, chain, Document.Defaults, _settings.SessionLogs.Enabled)) return null;
        return new SessionLogInfo(entry.Name, kind, target, string.Join(" / ", chain.Select(f => f.Name)));
    }

    // ---------- websites ----------

    /// <summary>Opens a website entry in the default browser or in a tab (reusing the entry's tab if one is open).</summary>
    public void OpenWeb(WebConnection web, WebOpenMode mode)
    {
        if (WebConnection.Normalise(web.Url) is not { } uri)
        {
            _dialogs.Error($"'{web.Name}' has no valid web address. Edit it and enter an http:// or https:// address.", "Open website");
            return;
        }
        if (mode == WebOpenMode.Browser)
        {
            WebSessionTabViewModel.OpenExternally(uri, _log);
            StatusText = $"Opened {uri.Host} in the default browser";
            return;
        }

        var existing = Tabs.OfType<WebSessionTabViewModel>().FirstOrDefault(t => t.Entry.Id == web.Id);
        if (existing is not null)
        {
            SelectedTab = existing;
            return;
        }
        var tab = new WebSessionTabViewModel(web, uri, _log);
        tab.SetIconColour(SettingsResolver.ResolveIconColour(web, Document.ChainFor(web)));
        Tabs.Add(tab);
        SelectedTab = tab;
        UpdateOpenSessionCounts();
        _log.Info($"Opened website tab for {web.Name} ({uri})");
    }

    private bool SelectedIsWeb() => SelectedNode?.Entry is WebConnection;

    [RelayCommand(CanExecute = nameof(SelectedIsWeb))]
    private void OpenInBrowser()
    {
        if (SelectedNode?.Entry is WebConnection web) OpenWeb(web, WebOpenMode.Browser);
    }

    [RelayCommand(CanExecute = nameof(SelectedIsWeb))]
    private void OpenInTab()
    {
        if (SelectedNode?.Entry is WebConnection web) OpenWeb(web, WebOpenMode.Tab);
    }

    /// <summary>Plain words for a credential reference, from its plugin.</summary>
    public string DescribeCredential(CredentialReference reference) => _credentials.Describe(reference);

    // ---------- Server checks (plugin) ----------

    /// <summary>True when the selected entry is an RDP host and the Server checks plugin is on.</summary>
    /// <summary>The Server checks plugin when it is switched on, for the dashboard's background session check.</summary>
    public IServerInspector? ServerInspector => _plugins.ServerInspector;

    public bool CanCheckServer() => SelectedNode?.Entry is RdpConnection && _plugins.ServerInspector is not null;

    [RelayCommand(CanExecute = nameof(CanCheckServer))]
    private void CheckServices()
    {
        if (SelectedNode?.Entry is RdpConnection rdp) OpenServerStatus(rdp, hardware: false);
    }

    [RelayCommand(CanExecute = nameof(CanCheckServer))]
    private void HardwareStats()
    {
        if (SelectedNode?.Entry is RdpConnection rdp) OpenServerStatus(rdp, hardware: true);
    }

    /// <summary>Opens the status tab for the entry's host, or switches to and refreshes the one already open.</summary>
    public void OpenServerStatus(RdpConnection rdp, bool hardware)
    {
        if (_plugins.ServerInspector is not { } inspector)
        {
            _dialogs.Info("The Server checks plugin is switched off. Turn it on in Settings.", "Server checks");
            return;
        }
        var settings = SettingsResolver.Resolve(Document, rdp, PersonalFor);
        if (string.IsNullOrWhiteSpace(settings.Host))
        {
            _dialogs.Error($"'{rdp.Name}' has no host.", "Server checks");
            return;
        }

        var tab = Tabs.OfType<ServerStatusTabViewModel>().FirstOrDefault(t => string.Equals(t.Host, settings.Host, StringComparison.OrdinalIgnoreCase));
        if (tab is null)
        {
            tab = new ServerStatusTabViewModel(settings.Host, rdp.Name, settings.WatchedServices, inspector, _log);
            Tabs.Add(tab);
            _log.Info($"Opened server status tab for {settings.Host}");
        }
        else
        {
            tab.UpdateWatchList(settings.WatchedServices);
        }
        SelectedTab = tab;
        _ = hardware ? tab.RefreshAndHardwareAsync() : tab.RefreshAsync();
    }

    // ---------- Key Vault ----------

    /// <summary>Opens a vault tab, or switches to the one already open for that vault.</summary>
    public void OpenVault(Uri vaultUri, string? tenantId, string title, KeyVaultConnection? entry)
    {
        var existing = Tabs.OfType<KeyVaultTabViewModel>().FirstOrDefault(t => t.VaultUri == vaultUri);
        if (existing is not null)
        {
            SelectedTab = existing;
            return;
        }
        var tab = new KeyVaultTabViewModel(vaultUri, tenantId, title, entry, _azure, _clipboard, _dialogs, _log);
        Tabs.Add(tab);
        SelectedTab = tab;
        UpdateOpenSessionCounts();
        _log.Info($"Opened vault tab for {vaultUri}");
    }

    /// <summary>Adds a vault found in Azure to the connections tree, in the selected folder.</summary>
    public void PinVault(VaultInfo vault)
    {
        var existing = Document.AllEntries().Select(e => e.Entry).OfType<KeyVaultConnection>()
            .FirstOrDefault(k => VaultNames.NormaliseVaultUri(k.VaultUri) == vault.VaultUri);
        if (existing is not null)
        {
            StatusText = $"{vault.Name} is already in the tree as '{existing.Name}'";
            RebuildTree(existing.Id);
            return;
        }
        var entry = new KeyVaultConnection
        {
            Name = vault.Name,
            VaultUri = vault.VaultUri.ToString(),
            TenantId = string.Equals(vault.TenantId, _azure.HomeTenantId, StringComparison.OrdinalIgnoreCase) ? null : vault.TenantId,
            Description = $"{vault.SubscriptionName} / {vault.ResourceGroup} ({vault.Location})",
        };
        var parent = TargetFolderForNew();
        if (!CanChange(parent))
        {
            _dialogs.Error($"You cannot add entries to {(parent is null ? "the top level" : $"'{parent.Name}'")}. Select a folder you can edit first.", "Pin vault");
            return;
        }
        if (parent is null) Document.Connections.Add(entry); else parent.Connections.Add(entry);
        SaveDocument(entry.Id);
        StatusText = $"Pinned {vault.Name} to {(parent?.Name ?? "the top level")}";
    }

    [RelayCommand]
    private void OpenAzureBrowser()
    {
        var tab = Tabs.OfType<AzureBrowserTabViewModel>().FirstOrDefault();
        if (tab is null)
        {
            tab = new AzureBrowserTabViewModel(this, _azure, _vaultCatalog, _log);
            Tabs.Add(tab);
        }
        SelectedTab = tab;
    }

    private void UpdateAzureAccount()
    {
        AzureAccountText = _azure.AccountName is { } name ? $"Azure: {name}" : "Azure: not signed in";
        Dashboard.Refresh();
    }

    [RelayCommand]
    private void QuickConnect()
    {
        var host = QuickConnectHost.Trim();
        if (host.Length == 0) return;
        var entry = new RdpConnection { Name = host, Host = host };
        OpenRdp(entry);
        QuickConnectHost = string.Empty;
    }

    /// <summary>A throwaway shell tab, PowerShell 7 if installed, otherwise Windows PowerShell.</summary>
    [RelayCommand]
    private void QuickTerminal()
    {
        var shell = TerminalLauncher.FindExecutable(TerminalShell.Pwsh) is null ? TerminalShell.WindowsPowerShell : TerminalShell.Pwsh;
        OpenTerminal(new TerminalConnection { Name = TerminalLauncher.DisplayName(shell), Shell = shell });
    }

    [RelayCommand(CanExecute = nameof(CanAddHere))]
    private void NewFolder()
    {
        var folder = new ConnectionFolder { Name = "New folder" };
        if (!_dialogs.EditFolder(folder, isNew: true, KnownVaults(), ColourForNew())) return;
        var parent = TargetFolderForNew();
        if (parent is null) Document.Folders.Add(folder); else parent.Folders.Add(folder);
        SaveDocument(folder.Id);
    }

    [RelayCommand(CanExecute = nameof(CanAddHere))]
    private void NewRdp() => NewEntry(new RdpConnection { Name = "New RDP connection" });

    [RelayCommand(CanExecute = nameof(CanAddHere))]
    private void NewTerminal() => NewEntry(new TerminalConnection { Name = "New terminal" });

    [RelayCommand(CanExecute = nameof(CanAddHere))]
    private void NewKeyVault() => NewEntry(new KeyVaultConnection { Name = "New Key Vault" });

    [RelayCommand(CanExecute = nameof(CanAddHere))]
    private void NewSsh() => NewEntry(new SshConnection { Name = "New SSH connection" });

    [RelayCommand(CanExecute = nameof(CanAddHere))]
    private void NewTelnet() => NewEntry(new SshConnection { Name = "New telnet connection", Protocol = TerminalProtocol.Telnet });

    [RelayCommand(CanExecute = nameof(CanAddHere))]
    private void NewWebsite() => NewEntry(new WebConnection { Name = "New website" });

    private void NewEntry(ConnectionEntry entry)
    {
        if (!_dialogs.EditEntry(entry, isNew: true, KnownVaults(), ColourForNew())) return;
        var parent = TargetFolderForNew();
        if (parent is null) Document.Connections.Add(entry); else parent.Connections.Add(entry);
        SaveDocument(entry.Id);
    }

    /// <summary>New items land in the selected folder, or the folder of the selected entry.</summary>
    private ConnectionFolder? TargetFolderForNew() => SelectedNode?.Folder ?? SelectedNode?.Parent?.Folder;

    private bool CanAddHere() => CanChange(TargetFolderForNew());

    /// <summary>The icon colour a new item inherits from the folder it lands in.</summary>
    private string? ColourForNew() => SelectedNode is { IsFolder: true } ? SelectedNode.IconColour : SelectedNode?.Parent?.IconColour;

    [RelayCommand(CanExecute = nameof(CanEditSelected))]
    private void Edit()
    {
        if (SelectedNode is null) return;
        var changed = SelectedNode.Folder is { } folder
            ? _dialogs.EditFolder(folder, isNew: false, KnownVaults(), SelectedNode.Parent?.IconColour)
            : _dialogs.EditEntry(SelectedNode.Entry!, isNew: false, KnownVaults(), SelectedNode.Parent?.IconColour);
        if (!changed) return;
        SaveDocument(SelectedNode.Entry?.Id ?? SelectedNode.Folder?.Id);
    }

    [RelayCommand(CanExecute = nameof(CanDeleteSelected))]
    private void Delete()
    {
        if (SelectedNode is null) return;
        var what = SelectedNode.IsFolder ? $"folder '{SelectedNode.Folder!.Name}' and everything in it" : $"'{SelectedNode.Entry!.Name}'";
        if (!_dialogs.Confirm($"Delete {what}?", "Delete")) return;

        var parentId = SelectedNode.Parent?.Folder?.Id;
        if (SelectedNode.Folder is { } folder) Document.Remove(folder); else Document.Remove(SelectedNode.Entry!);
        SaveDocument(parentId);
    }

    [RelayCommand(CanExecute = nameof(CanDuplicateSelected))]
    private void Duplicate()
    {
        if (SelectedNode is null) return;
        var parent = SelectedNode.Parent?.Folder;
        if (SelectedNode.Folder is { } folder)
        {
            var copy = folder.Clone();
            copy.Name = folder.Name + " (copy)";
            if (parent is null) Document.Folders.Add(copy); else parent.Folders.Add(copy);
            SaveDocument(copy.Id);
        }
        else
        {
            var copy = SelectedNode.Entry!.Clone();
            copy.Name = SelectedNode.Entry.Name + " (copy)";
            if (parent is null) Document.Connections.Add(copy); else parent.Connections.Add(copy);
            SaveDocument(copy.Id);
        }
    }

    private bool CanEditSelected() => SelectedNode is not null && CanChange(SelectedNode.Folder ?? ParentOfSelected);

    /// <summary>Deleting a folder changes it and the folder it sits in.</summary>
    private bool CanDeleteSelected() => SelectedNode is not null && CanChange(ParentOfSelected) && (SelectedNode.Folder is null || CanChange(SelectedNode.Folder));

    private bool CanDuplicateSelected() => SelectedNode is not null && CanChange(ParentOfSelected);

    /// <summary>Imports land in a new top level folder.</summary>
    private bool CanAddTopLevel() => CanChange(null);

    /// <summary>Vaults pinned in the tree, offered when picking where a credential lives.</summary>
    private IReadOnlyList<string> KnownVaults() => Document.AllEntries().Select(e => e.Entry).OfType<KeyVaultConnection>()
        .Select(k => VaultNames.NormaliseVaultUri(k.VaultUri)?.ToString()).OfType<string>()
        .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();

    [RelayCommand(CanExecute = nameof(CanAddTopLevel))]
    private void ImportMRemoteNg() => RunImport(
        "Import mRemoteNG connections",
        "mRemoteNG connections (confCons.xml;*.xml)|confCons.xml;*.xml|All files|*.*",
        MRemoteNgImporter.Import);

    [RelayCommand(CanExecute = nameof(CanAddTopLevel))]
    private void ImportRdm() => RunImport(
        "Import Remote Desktop Manager connections",
        "RDM export (*.rdm;*.csv;*.xml)|*.rdm;*.csv;*.xml|All files|*.*",
        RdmImporter.Import);

    [RelayCommand(CanExecute = nameof(CanAddTopLevel))]
    private void ImportRoyalTs() => RunImport(
        "Import Royal TS document",
        "Royal TS document (*.rtsz)|*.rtsz|All files|*.*",
        RoyalTsImporter.Import);

    private void RunImport(string title, string filter, Func<string, ImportResult> import)
    {
        var file = _dialogs.PickOpenFile(title, filter);
        if (file is null) return;
        try
        {
            var result = import(file);
            Document.Folders.Add(result.Root);
            SaveDocument(result.Root.Id);
            var notes = result.Notes.Count == 0 ? string.Empty : "\n\n" + string.Join("\n", result.Notes.Take(15)) + (result.Notes.Count > 15 ? $"\n... and {result.Notes.Count - 15} more" : string.Empty);
            _dialogs.Info($"Imported {result.Imported} RDP entries into folder '{result.Root.Name}'. Skipped {result.Skipped}. No passwords were imported.{notes}", "Import complete");
            _log.Info($"Imported {result.Imported} entries from {file}, skipped {result.Skipped}");
        }
        catch (Exception ex)
        {
            _log.Error("Import failed", ex);
            _dialogs.Error(ex.Message, "Import failed");
        }
    }

    // ---------- commands: tabs ----------

    [RelayCommand]
    private void CloseTab(TabViewModelBase? tab)
    {
        if (tab is null || !tab.CanClose) return;
        var index = Tabs.IndexOf(tab);
        Tabs.Remove(tab);
        if (tab is IDisposable d) d.Dispose();
        if (ReferenceEquals(SelectedTab, tab) || SelectedTab is null)
            SelectedTab = Tabs[Math.Clamp(index - 1, 0, Tabs.Count - 1)];
        UpdateOpenSessionCounts();
    }

    [RelayCommand]
    private void CloseAllSessions()
    {
        foreach (var tab in Tabs.Where(t => t.CanClose).ToList()) CloseTab(tab);
    }

    private void UpdateOpenSessionCounts()
    {
        foreach (var node in AllNodes())
            if (node.Entry is { } e)
                node.OpenSessions = Tabs.Count(t => t.EntryId == e.Id && t.IsLive);
    }

    // ---------- commands: app ----------

    [RelayCommand]
    private void OpenSettings()
    {
        var before = StorageBackend.Identity(_settings);
        if (!_dialogs.EditSettings(_settings)) return;
        PersistSettings();
        // Plugins may have been switched on or off.
        Dashboard.Refresh();
        CheckServicesCommand.NotifyCanExecuteChanged();
        HardwareStatsCommand.NotifyCanExecuteChanged();
        if (before != StorageBackend.Identity(_settings)) SwitchStorage();
        else if (_store is SqlConnectionStore sql) sql.PollInterval = _settings.Sql.PollInterval;
    }

    /// <summary>Closes the current store and opens the one Settings now points at.</summary>
    private void SwitchStorage()
    {
        var old = _backend;
        old.Store.ExternalChange -= OnStoreExternalChange;
        old.Store.StatusChanged -= OnStoreStatusChanged;
        old.Dispose();
        _backend = StorageBackend.Create(_settings, _settingsStore, _log);
        _log.Info($"Storage switched to {_store.Location}");
        _backend.Profile.ApplyPreferences(_settings);
        LoadDocument();
        AttachStore();
    }

    [RelayCommand]
    private void Reload() => OnExternalChange();

    [RelayCommand]
    private void ToggleNavigation() => NavigationVisible = !NavigationVisible;

    [RelayCommand]
    private void OpenLogFolder()
    {
        try { System.Diagnostics.Process.Start("explorer.exe", AppPaths.LogDirectory); } catch { }
    }

    public void PersistSettings()
    {
        _settings.NavigationVisible = NavigationVisible;
        _settings.NavigationWidth = NavigationWidth;
        _settings.ExpandedFolders = AllNodes().Where(n => n.IsFolder && n.IsExpanded).Select(n => n.Folder!.Id).ToList();
        try { _settingsStore.Save(_settings); } catch (Exception ex) { _log.Warn($"Settings not saved: {ex.Message}"); }
        _backend.Profile.SavePreferences(_settings);
    }

    // ---------- shared database: permissions, admin, personal settings ----------

    private bool CanManageSelectedFolder() =>
        _backend.Admin is not null && !_store.IsReadOnly && SelectedNode?.Folder is { } f && AccessFor(f) >= AccessLevel.Manage;

    [RelayCommand(CanExecute = nameof(CanManageSelectedFolder))]
    private void FolderPermissions()
    {
        if (SelectedNode?.Folder is not { } folder || _backend.Admin is not { } admin) return;
        if (!_dialogs.EditFolderPermissions(admin, folder)) return;
        // Permissions change what everyone sees, including you.
        OnExternalChange();
    }

    [RelayCommand(CanExecute = nameof(CanAdminister))]
    private void OpenAdmin()
    {
        if (_backend.Admin is not { } admin) return;
        _dialogs.ShowAdmin(admin, _store.SignedInAs);
        OnExternalChange();
    }

    /// <summary>Personal watch lists apply to RDP entries and folders, whatever the user's level.</summary>
    private bool CanEditPersonal() => SelectedNode is { Folder: not null } or { Entry: RdpConnection };

    [RelayCommand(CanExecute = nameof(CanEditPersonal))]
    private void MyWatchedServices()
    {
        if (SelectedNode is null) return;
        var id = SelectedNode.Folder?.Id ?? SelectedNode.Entry!.Id;
        var name = SelectedNode.Folder?.Name ?? SelectedNode.Entry!.Name;
        var shared = SharedWatchList(SelectedNode);
        var current = PersonalFor(id) ?? new PersonalEntrySettings();
        var result = _dialogs.EditPersonalWatchList(name, shared, current, IsSqlStore);
        if (result is null) return;
        try
        {
            _backend.Profile.SaveEntrySettings(id, result);
            _personal = _backend.Profile.LoadEntrySettings();
            StatusText = $"Saved your watched services for {name}";
            RefreshOpenWatchLists();
            Dashboard.Refresh();
        }
        catch (Exception ex)
        {
            _log.Error("Personal settings not saved", ex);
            _dialogs.Error($"Your watched services were not saved.\n\n{ex.Message}", "My watched services");
        }
    }

    /// <summary>Open Check services tabs pick up a changed personal list straight away.</summary>
    private void RefreshOpenWatchLists()
    {
        var rdps = Document.AllEntries().Select(e => e.Entry).OfType<RdpConnection>().ToList();
        foreach (var tab in Tabs.OfType<ServerStatusTabViewModel>())
        {
            var rdp = rdps.FirstOrDefault(r => string.Equals(r.Host.Trim(), tab.Host, StringComparison.OrdinalIgnoreCase));
            if (rdp is not null) tab.UpdateWatchList(SettingsResolver.Resolve(Document, rdp, PersonalFor).WatchedServices);
        }
    }

    /// <summary>The shared list that applies at a node, before personal settings: nearest setting wins.</summary>
    private IReadOnlyList<string> SharedWatchList(TreeNodeViewModel node)
    {
        if (node.Entry is RdpConnection rdp) return SettingsResolver.Resolve(Document, rdp).WatchedServices;
        for (var n = node; n is not null; n = n.Parent)
            if (n.Folder?.Defaults.WatchedServices is { } list) return list;
        return Document.Defaults.WatchedServices ?? [];
    }

    [RelayCommand]
    private void ExportToFile()
    {
        var path = _dialogs.PickSaveFile("Export connections to a file", "JSON (*.json)|*.json", "connections-export.json");
        if (path is null) return;
        try
        {
            using var file = new FileConnectionStore(path);
            file.Save(Document);
            StatusText = $"Exported {Document.EntryCount} entries to {path}";
            var note = IsSqlStore ? "\n\nOnly folders you can see were exported." : string.Empty;
            _dialogs.Info($"Exported {Document.EntryCount} entries to {path}.{note}", "Export");
        }
        catch (Exception ex)
        {
            _log.Error("Export failed", ex);
            _dialogs.Error(ex.Message, "Export failed");
        }
    }

    /// <summary>Puts every detached or full screen session back in its tab. Called while the main window can still take them.</summary>
    public void DockAllSessions()
    {
        foreach (var tab in Tabs.OfType<RdpSessionTabViewModel>().Where(t => t.IsDetached).ToList())
            tab.DockCommand.Execute(null);
    }

    public void Shutdown()
    {
        PersistSettings();
        _clipboard.ClearNow();
        foreach (var tab in Tabs.OfType<IDisposable>().ToList()) tab.Dispose();
        _backend.Dispose();
    }
}
