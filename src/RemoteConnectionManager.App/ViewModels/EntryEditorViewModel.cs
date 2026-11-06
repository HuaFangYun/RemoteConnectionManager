using CommunityToolkit.Mvvm.ComponentModel;
using RemoteConnectionManager.Core.Models;
using RemoteConnectionManager.Plugins;
using RemoteConnectionManager.Plugins.OnePassword;

namespace RemoteConnectionManager.App.ViewModels;

/// <summary>A combo box option. A null value means "inherit".</summary>
public sealed record Choice<T>(string Label, T Value)
{
    public override string ToString() => Label;
}

/// <summary>
/// Edit form for an entry or a folder. Works on copies of the values and writes back on ApplyTo,
/// so Cancel leaves the model untouched. Three state checkboxes map to nullable bools: null = inherit.
/// </summary>
public sealed partial class EntryEditorViewModel : ObservableObject
{
    [ObservableProperty] private string _windowTitle = string.Empty;
    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _description = string.Empty;
    [ObservableProperty] private string _tags = string.Empty;
    [ObservableProperty] private bool _favourite;
    /// <summary>Icon colour as typed: a hex value or a preset name. Blank inherits from the folder, then the type's colour.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedColour), nameof(IconColourPreview))]
    private string _iconColour = string.Empty;

    /// <summary>Inherit, then the presets. A custom hex value shows no selection here; the text box holds it.</summary>
    public IReadOnlyList<Choice<string?>> ColourChoices { get; } =
        new[] { new Choice<string?>("Inherit (folder or type colour)", null) }
            .Concat(IconColours.Presets.Select(p => new Choice<string?>(p.Hint.Length > 0 ? $"{p.Name} ({p.Hint})" : p.Name, p.Hex)))
            .ToList();

    public Choice<string?>? SelectedColour
    {
        get
        {
            if (string.IsNullOrWhiteSpace(IconColour)) return ColourChoices[0];
            var hex = IconColours.Normalise(IconColour);
            return hex is null ? null : ColourChoices.FirstOrDefault(c => c.Value == hex);
        }
        set { if (value is not null) IconColour = value.Value ?? string.Empty; }
    }

    /// <summary>The entry's tree glyph, shown in the colour swatch.</summary>
    public string PreviewGlyph { get; private init; } = TreeNodeViewModel.GlyphFor(null);

    private string TypeBrushKey { get; init; } = TreeNodeViewModel.TypeBrushKeyFor(null);

    /// <summary>The colour the folders above pass down ("#RRGGBB"), or null. Only used for the preview.</summary>
    public string? InheritedColour
    {
        get => _inheritedColour;
        set { _inheritedColour = value; OnPropertyChanged(nameof(IconColourPreview)); }
    }
    private string? _inheritedColour;

    /// <summary>What the swatch shows: the colour typed here, else the inherited one, else the type's own (a brush key).</summary>
    public string IconColourPreview => IconColours.Normalise(IconColour) ?? InheritedColour ?? TypeBrushKey;

    // RDP and folder defaults
    [ObservableProperty] private string _host = string.Empty;
    [ObservableProperty] private string _port = string.Empty;
    [ObservableProperty] private string _domain = string.Empty;
    [ObservableProperty] private string _userName = string.Empty;
    [ObservableProperty] private bool? _adminSession;
    [ObservableProperty] private bool? _keyboardPassThrough;
    [ObservableProperty] private bool? _redirectClipboard;
    [ObservableProperty] private bool? _smartSizing;
    [ObservableProperty] private bool? _ignoreCertificateErrors;

    // Sign in, gateway and credential protection (RDP and folder defaults)
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowVaultFields), nameof(ShowOnePasswordFields), nameof(ShowCredentialNote))]
    private Choice<CredentialSource?> _selectedCredential = null!;
    [ObservableProperty] private string _credentialVault = string.Empty;
    [ObservableProperty] private string _credentialSecret = string.Empty;
    [ObservableProperty] private string _credentialTenant = string.Empty;
    [ObservableProperty] private string _onePasswordItem = string.Empty;
    [ObservableProperty] private string _onePasswordVault = string.Empty;
    [ObservableProperty] private string _onePasswordAccount = string.Empty;
    /// <summary>Comma separated service names or wildcards. Blank inherits.</summary>
    [ObservableProperty] private string _watchedServices = string.Empty;
    [ObservableProperty] private Choice<CredentialProtection?> _selectedProtection = null!;
    [ObservableProperty] private string _gatewayHost = string.Empty;
    [ObservableProperty] private Choice<GatewayUsage?> _selectedGateway = null!;

    // Terminal
    [ObservableProperty] private TerminalShell _shell = TerminalShell.Pwsh;
    [ObservableProperty] private string _workingDirectory = string.Empty;
    [ObservableProperty] private string _arguments = string.Empty;
    [ObservableProperty] private string _startScript = string.Empty;

    // Key Vault
    [ObservableProperty] private string _vaultUri = string.Empty;
    [ObservableProperty] private string _tenantId = string.Empty;

    // SSH and telnet (Host is shared with RDP)
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSshProtocol), nameof(SshPortHint), nameof(SelectedProtocol))]
    private TerminalProtocol _sshProtocol = TerminalProtocol.Ssh;
    [ObservableProperty] private string _sshPort = string.Empty;
    /// <summary>The entry's SSH user, or the folder's SSH default.</summary>
    [ObservableProperty] private string _sshUserName = string.Empty;
    [ObservableProperty] private string _identityFile = string.Empty;
    [ObservableProperty] private string _jumpHost = string.Empty;
    [ObservableProperty] private bool _legacyAlgorithms;
    [ObservableProperty] private string _sshArguments = string.Empty;
    /// <summary>Vault credential for Send password, on SSH and telnet entries and folders.</summary>
    public CredentialPickerViewModel SshCredential { get; } = new();

    // Website
    [ObservableProperty] private string _url = string.Empty;
    [ObservableProperty] private WebOpenMode _openIn = WebOpenMode.Browser;

    // Session log (terminal, SSH, telnet, folders): null = inherit
    [ObservableProperty] private bool? _sessionLogging;

    [ObservableProperty] private string _validationMessage = string.Empty;

    public bool IsFolder { get; private init; }
    public bool IsRdp { get; private init; }
    public bool IsTerminal { get; private init; }
    public bool IsKeyVault { get; private init; }
    public bool IsSsh { get; private init; }
    public bool IsWeb { get; private init; }
    public bool IsSshProtocol => SshProtocol == TerminalProtocol.Ssh;
    public string SshPortHint => SshProtocol == TerminalProtocol.Telnet ? "Blank = 23" : "Blank = 22";
    public bool ShowSshCredential => IsSsh || IsFolder;
    public bool ShowSessionLogging => IsTerminal || IsSsh || IsFolder;
    public IReadOnlyList<Choice<TerminalProtocol>> ProtocolChoices { get; } =
    [
        new("SSH", TerminalProtocol.Ssh),
        new("Telnet (not encrypted)", TerminalProtocol.Telnet),
    ];
    public Choice<TerminalProtocol> SelectedProtocol
    {
        get => ProtocolChoices.First(c => c.Value == SshProtocol);
        set { if (value is not null) SshProtocol = value.Value; }
    }
    public IReadOnlyList<Choice<WebOpenMode>> OpenInChoices { get; } =
    [
        new("Default browser (keeps SSO, saved passwords and extensions)", WebOpenMode.Browser),
        new("A tab in RCM", WebOpenMode.Tab),
    ];
    public Choice<WebOpenMode> SelectedOpenIn
    {
        get => OpenInChoices.First(c => c.Value == OpenIn);
        set { if (value is not null) OpenIn = value.Value; }
    }
    public bool ShowRdpFields => IsRdp || IsFolder;
    public bool ShowTags => !IsFolder;
    public string RdpSectionTitle => IsFolder ? "Defaults for everything in this folder" : "RDP";
    public Array Shells => Enum.GetValues<TerminalShell>();

    private static readonly IReadOnlyList<Choice<CredentialSource?>> AllCredentialChoices =
    [
        new("Inherit", null),
        new("Windows: logged on user, or a prompt for the user name above", CredentialSource.Windows),
        new("Key Vault secret, read at connect time", CredentialSource.KeyVault),
        new("1Password item, read at connect time (you approve the unlock)", CredentialSource.OnePassword),
    ];

    /// <summary>Inherit, Windows, and the sources whose plugin is switched on (plus the current one, so it is never lost).</summary>
    public IReadOnlyList<Choice<CredentialSource?>> CredentialChoices { get; private set; } = AllCredentialChoices;

    public IReadOnlyList<Choice<CredentialProtection?>> ProtectionChoices { get; } =
    [
        new("Inherit", null),
        new("Normal (credential is delegated to the host)", CredentialProtection.None),
        new("Restricted Admin (nothing delegated, needs admin on the host)", CredentialProtection.RestrictedAdmin),
        new("Remote Credential Guard (Kerberos stays on this PC)", CredentialProtection.RemoteCredentialGuard),
    ];

    public IReadOnlyList<Choice<GatewayUsage?>> GatewayChoices { get; } =
    [
        new("Inherit", null),
        new("Never (direct)", GatewayUsage.Never),
        new("Always", GatewayUsage.Always),
        new("Not for local addresses", GatewayUsage.BypassLocal),
    ];

    /// <summary>Vaults pinned in the tree, offered in the credential's vault box.</summary>
    public IReadOnlyList<string> KnownVaults
    {
        get => _knownVaults;
        set { _knownVaults = value; SshCredential.KnownVaults = value; }
    }
    private IReadOnlyList<string> _knownVaults = Array.Empty<string>();

    public bool ShowVaultFields => SelectedCredential?.Value == CredentialSource.KeyVault;
    public bool ShowOnePasswordFields => SelectedCredential?.Value == CredentialSource.OnePassword;
    public bool ShowCredentialNote => ShowVaultFields || ShowOnePasswordFields;

    private EntryEditorViewModel()
    {
        _selectedCredential = AllCredentialChoices[0];
        _selectedProtection = ProtectionChoices[0];
        _selectedGateway = GatewayChoices[0];
    }

    /// <summary>Limits the credential list to sources with a switched on plugin. Call before loading values.</summary>
    private void OfferSources(IReadOnlyCollection<CredentialSource>? enabled, CredentialSource? current)
    {
        if (enabled is null) return;
        CredentialChoices = AllCredentialChoices
            .Where(c => c.Value is null or CredentialSource.Windows || enabled.Contains(c.Value.Value) || c.Value == current)
            .ToList();
    }

    private void LoadSignIn(CredentialReference? credential, CredentialProtection? protection, string? gatewayHost, GatewayUsage? gateway, List<string>? watched)
    {
        SelectedCredential = CredentialChoices.First(c => c.Value == credential?.Source);
        OnePasswordItem = credential?.OnePasswordItem ?? string.Empty;
        OnePasswordVault = credential?.OnePasswordVault ?? string.Empty;
        OnePasswordAccount = credential?.OnePasswordAccount ?? string.Empty;
        WatchedServices = watched is null ? string.Empty : string.Join(Environment.NewLine, watched);
        CredentialVault = credential?.VaultUri ?? string.Empty;
        CredentialSecret = credential?.SecretName ?? string.Empty;
        CredentialTenant = credential?.TenantId ?? string.Empty;
        SelectedProtection = ProtectionChoices.First(c => c.Value == protection);
        GatewayHost = gatewayHost ?? string.Empty;
        SelectedGateway = GatewayChoices.First(c => c.Value == gateway);
    }

    private CredentialReference? BuildCredential() => SelectedCredential.Value switch
    {
        null => null,
        CredentialSource.Windows => CredentialReference.Windows(),
        CredentialSource.OnePassword => new CredentialReference
        {
            Source = CredentialSource.OnePassword,
            OnePasswordItem = Blank(OnePasswordItem),
            OnePasswordVault = Blank(OnePasswordVault),
            OnePasswordAccount = Blank(OnePasswordAccount),
        },
        _ => new CredentialReference
        {
            Source = CredentialSource.KeyVault,
            VaultUri = KeyVault.VaultNames.NormaliseVaultUri(CredentialVault)?.ToString() ?? CredentialVault.Trim(),
            SecretName = Blank(CredentialSecret),
            TenantId = Blank(CredentialTenant),
        },
    };

    private List<string>? BuildWatchedServices() => ServiceFilter.ParseList(WatchedServices) is { Count: > 0 } list ? list : null;

    public static EntryEditorViewModel ForFolder(ConnectionFolder folder, bool isNew, IReadOnlyCollection<CredentialSource>? enabledSources = null)
    {
        var d = folder.Defaults;
        var vm = new EntryEditorViewModel
        {
            IsFolder = true,
            WindowTitle = isNew ? "New folder" : $"Edit folder: {folder.Name}",
            Name = folder.Name,
            Description = folder.Description ?? string.Empty,
            Port = d.Port?.ToString() ?? string.Empty,
            Domain = d.Domain ?? string.Empty,
            UserName = d.UserName ?? string.Empty,
            AdminSession = d.AdminSession,
            KeyboardPassThrough = d.KeyboardPassThrough,
            RedirectClipboard = d.RedirectClipboard,
            SmartSizing = d.SmartSizing,
            IgnoreCertificateErrors = d.IgnoreCertificateErrors,
        };
        vm.OfferSources(enabledSources, d.Credential?.Source);
        vm.LoadSignIn(d.Credential, d.CredentialProtection, d.GatewayHost, d.GatewayUsage, d.WatchedServices);
        vm.SshUserName = d.SshUserName ?? string.Empty;
        vm.SshCredential.Load(d.SshCredential, enabledSources);
        vm.SessionLogging = d.SessionLogging;
        vm.IconColour = d.IconColour ?? string.Empty;
        return vm;
    }

    partial void OnOpenInChanged(WebOpenMode value) => OnPropertyChanged(nameof(SelectedOpenIn));

    public static EntryEditorViewModel ForEntry(ConnectionEntry entry, bool isNew, IReadOnlyCollection<CredentialSource>? enabledSources = null)
    {
        var vm = new EntryEditorViewModel
        {
            IsRdp = entry is RdpConnection,
            IsTerminal = entry is TerminalConnection,
            IsKeyVault = entry is KeyVaultConnection,
            IsSsh = entry is SshConnection,
            IsWeb = entry is WebConnection,
            WindowTitle = isNew ? $"New {entry.Prefix} entry" : $"Edit: {entry.Name}",
            Name = entry.Name,
            Description = entry.Description ?? string.Empty,
            Tags = string.Join(", ", entry.Tags),
            Favourite = entry.Favourite,
            IconColour = entry.IconColour ?? string.Empty,
            PreviewGlyph = TreeNodeViewModel.GlyphFor(entry.Kind),
            TypeBrushKey = TreeNodeViewModel.TypeBrushKeyFor(entry.Kind),
        };
        switch (entry)
        {
            case RdpConnection rdp:
                vm.Host = rdp.Host;
                vm.Port = rdp.Port?.ToString() ?? string.Empty;
                vm.Domain = rdp.Domain ?? string.Empty;
                vm.UserName = rdp.UserName ?? string.Empty;
                vm.AdminSession = rdp.AdminSession;
                vm.KeyboardPassThrough = rdp.KeyboardPassThrough;
                vm.RedirectClipboard = rdp.RedirectClipboard;
                vm.SmartSizing = rdp.SmartSizing;
                vm.IgnoreCertificateErrors = rdp.IgnoreCertificateErrors;
                vm.OfferSources(enabledSources, rdp.Credential?.Source);
                vm.LoadSignIn(rdp.Credential, rdp.CredentialProtection, rdp.GatewayHost, rdp.GatewayUsage, rdp.WatchedServices);
                break;
            case TerminalConnection t:
                vm.Shell = t.Shell;
                vm.WorkingDirectory = t.WorkingDirectory ?? string.Empty;
                vm.Arguments = t.Arguments ?? string.Empty;
                vm.StartScript = t.StartScript ?? string.Empty;
                vm.SessionLogging = t.SessionLogging;
                break;
            case SshConnection ssh:
                vm.SshProtocol = ssh.Protocol;
                vm.Host = ssh.Host;
                vm.SshPort = ssh.Port?.ToString() ?? string.Empty;
                vm.SshUserName = ssh.UserName ?? string.Empty;
                vm.IdentityFile = ssh.IdentityFile ?? string.Empty;
                vm.JumpHost = ssh.JumpHost ?? string.Empty;
                vm.LegacyAlgorithms = ssh.LegacyAlgorithms;
                vm.SshArguments = ssh.Arguments ?? string.Empty;
                vm.SshCredential.Load(ssh.Credential, enabledSources);
                vm.SessionLogging = ssh.SessionLogging;
                break;
            case WebConnection web:
                vm.Url = web.Url;
                vm.OpenIn = web.OpenIn;
                break;
            case KeyVaultConnection kv:
                vm.VaultUri = kv.VaultUri;
                vm.TenantId = kv.TenantId ?? string.Empty;
                break;
        }
        return vm;
    }

    public bool Validate()
    {
        if (string.IsNullOrWhiteSpace(Name)) { ValidationMessage = "Name is required."; return false; }
        if (!string.IsNullOrWhiteSpace(IconColour) && IconColours.Normalise(IconColour) is null)
        { ValidationMessage = "Icon colour must be a hex colour such as #F85149, or blank to inherit."; return false; }
        if (IsRdp && string.IsNullOrWhiteSpace(Host)) { ValidationMessage = "Host is required."; return false; }
        if (ShowRdpFields && !string.IsNullOrWhiteSpace(Port) && (!int.TryParse(Port, out var p) || p is < 1 or > 65535))
        { ValidationMessage = "Port must be a number between 1 and 65535."; return false; }
        if (ShowRdpFields && ShowVaultFields)
        {
            if (KeyVault.VaultNames.NormaliseVaultUri(CredentialVault) is null) { ValidationMessage = "Enter the credential's vault name or its https://name.vault.azure.net address."; return false; }
            if (KeyVault.VaultNames.ItemNameProblem(CredentialSecret) is { } problem) { ValidationMessage = $"Secret name: {problem}"; return false; }
        }
        if (ShowRdpFields && ShowOnePasswordFields)
        {
            OnePasswordReference.TryParse(BuildCredential()!, out var problem);
            if (problem is not null) { ValidationMessage = problem; return false; }
        }
        if (ShowRdpFields && ShowCredentialNote && SelectedProtection.Value == CredentialProtection.RemoteCredentialGuard)
        { ValidationMessage = "Remote Credential Guard signs in as the logged on user, so it cannot use a vault credential. Pick one or the other."; return false; }
        if (ShowRdpFields && !string.IsNullOrWhiteSpace(GatewayHost) && Uri.CheckHostName(GatewayHost.Trim().Split(':')[0]) == UriHostNameType.Unknown)
        { ValidationMessage = "Gateway must be a host name, for example rdgw.contoso.com."; return false; }
        if (IsSsh && string.IsNullOrWhiteSpace(Host)) { ValidationMessage = "Host is required."; return false; }
        if (IsSsh && Host.Trim().Contains(' ')) { ValidationMessage = "Host must be a single name or address, with no spaces. Put a user name in User name."; return false; }
        if (IsSsh && !string.IsNullOrWhiteSpace(SshPort) && (!int.TryParse(SshPort, out var sp) || sp is < 1 or > 65535))
        { ValidationMessage = "Port must be a number between 1 and 65535."; return false; }
        if (ShowSshCredential && SshCredential.Validate() is { } sshProblem) { ValidationMessage = sshProblem; return false; }
        if (IsWeb && Core.Models.WebConnection.Normalise(Url) is null)
        { ValidationMessage = "Enter a web address, for example https://fw01.contoso.local. Only http and https are allowed."; return false; }
        if (IsKeyVault && KeyVault.VaultNames.NormaliseVaultUri(VaultUri) is null) { ValidationMessage = "Enter the vault name or its https://name.vault.azure.net address."; return false; }
        ValidationMessage = string.Empty;
        return true;
    }

    public void ApplyTo(ConnectionFolder folder)
    {
        folder.Name = Name.Trim();
        folder.Description = Blank(Description);
        folder.Defaults.Port = int.TryParse(Port, out var p) ? p : null;
        folder.Defaults.Domain = Blank(Domain);
        folder.Defaults.UserName = Blank(UserName);
        folder.Defaults.AdminSession = AdminSession;
        folder.Defaults.KeyboardPassThrough = KeyboardPassThrough;
        folder.Defaults.RedirectClipboard = RedirectClipboard;
        folder.Defaults.SmartSizing = SmartSizing;
        folder.Defaults.IgnoreCertificateErrors = IgnoreCertificateErrors;
        folder.Defaults.Credential = BuildCredential();
        folder.Defaults.CredentialProtection = SelectedProtection.Value;
        folder.Defaults.GatewayHost = Blank(GatewayHost);
        folder.Defaults.GatewayUsage = SelectedGateway.Value;
        folder.Defaults.WatchedServices = BuildWatchedServices();
        folder.Defaults.SshUserName = Blank(SshUserName);
        folder.Defaults.SshCredential = SshCredential.Build();
        folder.Defaults.SessionLogging = SessionLogging;
        folder.Defaults.IconColour = IconColours.Normalise(IconColour);
    }

    public void ApplyTo(ConnectionEntry entry)
    {
        entry.Name = Name.Trim();
        entry.Description = Blank(Description);
        entry.Favourite = Favourite;
        entry.IconColour = IconColours.Normalise(IconColour);
        entry.Tags = Tags.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        switch (entry)
        {
            case RdpConnection rdp:
                rdp.Host = Host.Trim();
                rdp.Port = int.TryParse(Port, out var p) ? p : null;
                rdp.Domain = Blank(Domain);
                rdp.UserName = Blank(UserName);
                rdp.AdminSession = AdminSession;
                rdp.KeyboardPassThrough = KeyboardPassThrough;
                rdp.RedirectClipboard = RedirectClipboard;
                rdp.SmartSizing = SmartSizing;
                rdp.IgnoreCertificateErrors = IgnoreCertificateErrors;
                rdp.Credential = BuildCredential();
                rdp.CredentialProtection = SelectedProtection.Value;
                rdp.GatewayHost = Blank(GatewayHost);
                rdp.GatewayUsage = SelectedGateway.Value;
                rdp.WatchedServices = BuildWatchedServices();
                break;
            case TerminalConnection t:
                t.Shell = Shell;
                t.WorkingDirectory = Blank(WorkingDirectory);
                t.Arguments = Blank(Arguments);
                t.StartScript = Blank(StartScript);
                t.SessionLogging = SessionLogging;
                break;
            case SshConnection ssh:
                ssh.Protocol = SshProtocol;
                ssh.Host = Host.Trim();
                ssh.Port = int.TryParse(SshPort, out var sshPort) ? sshPort : null;
                ssh.UserName = Blank(SshUserName);
                ssh.IdentityFile = Blank(IdentityFile);
                ssh.JumpHost = Blank(JumpHost);
                ssh.LegacyAlgorithms = LegacyAlgorithms;
                ssh.Arguments = Blank(SshArguments);
                ssh.Credential = SshCredential.Build();
                ssh.SessionLogging = SessionLogging;
                break;
            case WebConnection web:
                web.Url = Core.Models.WebConnection.Normalise(Url)?.ToString() ?? Url.Trim();
                web.OpenIn = OpenIn;
                break;
            case KeyVaultConnection kv:
                kv.VaultUri = KeyVault.VaultNames.NormaliseVaultUri(VaultUri)?.ToString() ?? VaultUri.Trim();
                kv.TenantId = Blank(TenantId);
                break;
        }
    }

    private static string? Blank(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
