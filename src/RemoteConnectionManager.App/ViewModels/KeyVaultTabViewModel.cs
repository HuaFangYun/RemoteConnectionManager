using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteConnectionManager.App.Services;
using RemoteConnectionManager.Core.Models;
using RemoteConnectionManager.KeyVault;

namespace RemoteConnectionManager.App.ViewModels;

public sealed record VaultKindChoice(VaultItemKind Kind, string Title, string Glyph, string IconBrushKey);

/// <summary>One row in a vault list. Wraps the listed item; never holds a value.</summary>
public sealed class VaultItemRowViewModel
{
    public VaultItemRowViewModel(VaultItem item, DateTimeOffset now)
    {
        Item = item;
        Expiry = item.Expiry(now);
        ExpiresText = item.ExpiresOn is { } e ? $"{e.ToLocalTime():yyyy-MM-dd} ({ExpiryRules.Describe(e, now)})" : "never";
    }

    public VaultItem Item { get; }
    public string Name => Item.Name;
    public bool IsDisabled => !Item.Enabled;
    public string StatusText => Item.Enabled ? "Enabled" : "Disabled";
    public ExpiryState Expiry { get; }
    public bool IsExpiringSoon => Expiry == ExpiryState.Soon;
    public bool IsExpired => Expiry == ExpiryState.Expired;
    public string ExpiresText { get; }
    public string UpdatedText => Item.UpdatedOn is { } u ? u.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) : string.Empty;
    public string ContentTypeText => Item.ContentType ?? string.Empty;
}

/// <summary>
/// An open vault: secrets, keys and certificates lists with a detail pane. All writes go through
/// the dialog service and Azure decides whether they are allowed. Secret values are fetched only
/// when shown or copied, are dropped when the selection changes, and are never logged.
/// </summary>
public sealed partial class KeyVaultTabViewModel : TabViewModelBase, IDisposable
{
    private static readonly TimeSpan ValueVisibleFor = TimeSpan.FromSeconds(60);

    private readonly KeyVaultConnection? _entry;
    private readonly string? _tenantId;
    private readonly AzureAuth _auth;
    private readonly ClipboardGuard _clipboard;
    private readonly IDialogService _dialogs;
    private readonly AppLog _log;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Dictionary<VaultItemKind, IReadOnlyList<VaultItem>> _cache = new();
    private readonly DispatcherTimer _hideTimer;
    private CancellationTokenSource? _detailCts;
    private VaultClient? _client;
    private object? _clientCredential;
    private CertificateDetails? _certificateDetails;

    [ObservableProperty] private VaultKindChoice _selectedKind;
    [ObservableProperty] private string _filterText = string.Empty;
    [ObservableProperty] private bool _expiringOnly;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _busyText = string.Empty;
    [ObservableProperty] private string _errorText = string.Empty;
    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private string _countsText = string.Empty;
    [ObservableProperty] private VaultItemRowViewModel? _selectedRow;
    [ObservableProperty] private IReadOnlyList<PropertyRow> _detailRows = Array.Empty<PropertyRow>();
    [ObservableProperty] private IReadOnlyList<VaultItemVersion> _versions = Array.Empty<VaultItemVersion>();
    [ObservableProperty] private bool _detailBusy;
    [ObservableProperty] private string? _secretValue;

    public Uri VaultUri { get; }
    public string VaultName { get; }
    public string VaultUriText => VaultUri.ToString();
    public ObservableCollection<VaultItemRowViewModel> Rows { get; } = new();

    public IReadOnlyList<VaultKindChoice> Kinds { get; } =
    [
        new(VaultItemKind.Secret, "Secrets", "", "Icon.Secret"),
        new(VaultItemKind.Key, "Keys", "", "Icon.Key"),
        new(VaultItemKind.Certificate, "Certificates", "", "Icon.Certificate"),
    ];

    public bool HasSelection => SelectedRow is not null;
    public bool IsSecrets => SelectedKind.Kind == VaultItemKind.Secret;
    public bool IsKeys => SelectedKind.Kind == VaultItemKind.Key;
    public bool IsCertificates => SelectedKind.Kind == VaultItemKind.Certificate;
    public bool IsSecretSelected => SelectedRow?.Item.Kind == VaultItemKind.Secret;
    public bool IsCertificateSelected => SelectedRow?.Item.Kind == VaultItemKind.Certificate;
    public bool IsValueShown => SecretValue is not null;
    public bool HasError => ErrorText.Length > 0;
    public string NewLabel => SelectedKind.Kind switch
    {
        VaultItemKind.Secret => "New secret",
        VaultItemKind.Key => "New key",
        _ => "New certificate",
    };
    public string EmptyText => IsBusy || Rows.Count > 0 ? string.Empty
        : _cache.ContainsKey(SelectedKind.Kind) ? (ExpiringOnly ? "Nothing expires in the next 30 days." : FilterText.Length > 0 ? "Nothing matches the filter." : $"This vault has no {SelectedKind.Title.ToLowerInvariant()}.")
        : string.Empty;

    public override Guid? EntryId => _entry?.Id;
    public override bool IsLive => true;

    public KeyVaultTabViewModel(Uri vaultUri, string? tenantId, string title, KeyVaultConnection? entry,
        AzureAuth auth, ClipboardGuard clipboard, IDialogService dialogs, AppLog log)
    {
        VaultUri = vaultUri;
        VaultName = VaultNames.VaultNameOf(vaultUri);
        _tenantId = string.IsNullOrWhiteSpace(tenantId) ? null : tenantId;
        _entry = entry;
        _auth = auth;
        _clipboard = clipboard;
        _dialogs = dialogs;
        _log = log;
        Title = $"(KV) {title}";
        Glyph = "";
        IconBrushKey = "Icon.Vault";

        _hideTimer = new DispatcherTimer { Interval = ValueVisibleFor };
        _hideTimer.Tick += (_, _) => HideValue();

        _selectedKind = Kinds[0];
        _ = LoadKindAsync(force: false);
    }

    // ---------- loading ----------

    partial void OnSelectedKindChanged(VaultKindChoice value)
    {
        OnPropertyChanged(nameof(IsSecrets));
        OnPropertyChanged(nameof(IsKeys));
        OnPropertyChanged(nameof(IsCertificates));
        OnPropertyChanged(nameof(NewLabel));
        SelectedRow = null;
        _ = LoadKindAsync(force: false);
    }

    partial void OnFilterTextChanged(string value) => ApplyFilter();
    partial void OnExpiringOnlyChanged(bool value) => ApplyFilter();
    partial void OnErrorTextChanged(string value) => OnPropertyChanged(nameof(HasError));
    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(EmptyText));
    partial void OnSecretValueChanged(string? value) => OnPropertyChanged(nameof(IsValueShown));

    [RelayCommand]
    private Task Refresh() => LoadKindAsync(force: true);

    private async Task LoadKindAsync(bool force)
    {
        var kind = SelectedKind.Kind;
        if (force || !_cache.ContainsKey(kind))
        {
            var reselect = SelectedRow?.Name;
            await RunAsync($"Loading {SelectedKind.Title.ToLowerInvariant()}", $"List {SelectedKind.Title.ToLowerInvariant()}", async (client, ct) =>
            {
                _cache[kind] = await client.ListAsync(kind, ct);
            });
            if (kind != SelectedKind.Kind) return;
            ApplyFilter();
            if (reselect is not null) SelectedRow = Rows.FirstOrDefault(r => r.Name == reselect);
            return;
        }
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var now = DateTimeOffset.Now;
        var all = _cache.TryGetValue(SelectedKind.Kind, out var items) ? items : Array.Empty<VaultItem>();
        var selected = SelectedRow?.Name;
        Rows.Clear();
        foreach (var item in all)
        {
            if (FilterText.Length > 0 && !Matches(item, FilterText)) continue;
            if (ExpiringOnly && !ExpiryRules.NeedsAttention(item.ExpiresOn, now)) continue;
            Rows.Add(new VaultItemRowViewModel(item, now));
        }
        if (selected is not null) SelectedRow = Rows.FirstOrDefault(r => r.Name == selected);

        var attention = all.Count(i => ExpiryRules.NeedsAttention(i.ExpiresOn, now));
        var noun = SelectedKind.Title.ToLowerInvariant();
        CountsText = _cache.ContainsKey(SelectedKind.Kind)
            ? $"{all.Count} {noun}" + (attention > 0 ? $", {attention} expired or expiring within {ExpiryRules.SoonDays} days" : string.Empty)
            : string.Empty;
        OnPropertyChanged(nameof(EmptyText));
    }

    private static bool Matches(VaultItem item, string filter) =>
        item.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
        (item.ContentType?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false) ||
        item.Tags.Any(t => t.Key.Contains(filter, StringComparison.OrdinalIgnoreCase) || t.Value.Contains(filter, StringComparison.OrdinalIgnoreCase));

    // ---------- selection and detail ----------

    partial void OnSelectedRowChanged(VaultItemRowViewModel? value)
    {
        HideValue();
        _certificateDetails = null;
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(IsSecretSelected));
        OnPropertyChanged(nameof(IsCertificateSelected));
        _ = LoadDetailAsync(value);
    }

    private async Task LoadDetailAsync(VaultItemRowViewModel? row)
    {
        _detailCts?.Cancel();
        _detailCts = null;
        Versions = Array.Empty<VaultItemVersion>();
        if (row is null)
        {
            DetailRows = Array.Empty<PropertyRow>();
            return;
        }

        var item = row.Item;
        DetailRows = BaseRows(row, null, null);
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _detailCts = cts;
        DetailBusy = true;
        try
        {
            var client = Client();
            KeyDetails? key = null;
            CertificateDetails? cert = null;
            if (item.Kind == VaultItemKind.Key) key = await client.GetKeyDetailsAsync(item.Name, cts.Token);
            if (item.Kind == VaultItemKind.Certificate) cert = await client.GetCertificateDetailsAsync(item.Name, cts.Token);
            var versions = await client.ListVersionsAsync(item.Kind, item.Name, cts.Token);
            if (cts.IsCancellationRequested) return;
            _certificateDetails = cert;
            DetailRows = BaseRows(row, key, cert);
            Versions = versions;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!cts.IsCancellationRequested)
                DetailRows = BaseRows(row, null, null).Append(new PropertyRow("Details", KeyVaultErrors.Describe(ex, "Reading details"))).ToList();
        }
        finally
        {
            if (ReferenceEquals(_detailCts, cts)) DetailBusy = false;
        }
    }

    private List<PropertyRow> BaseRows(VaultItemRowViewModel row, KeyDetails? key, CertificateDetails? cert)
    {
        var item = row.Item;
        var rows = new List<PropertyRow>
        {
            new("Name", item.Name),
            new("Status", row.StatusText),
            new("Expires", row.ExpiresText),
        };
        if (item.NotBefore is { } nb) rows.Add(new("Not before", Format(nb)));
        if (item.CreatedOn is { } c) rows.Add(new("Created", Format(c)));
        if (item.UpdatedOn is { } u) rows.Add(new("Updated", Format(u)));
        if (item.Kind == VaultItemKind.Secret) rows.Add(new("Content type", item.ContentType ?? "none"));
        if (key is not null)
        {
            rows.Add(new("Key type", key.KeyType + (key.CurveOrSize is null ? string.Empty : $", {key.CurveOrSize}")));
            rows.Add(new("Operations", key.Operations.Count == 0 ? "none" : string.Join(", ", key.Operations)));
            if (key.Exportable is { } exp) rows.Add(new("Exportable", exp ? "Yes" : "No"));
        }
        if (cert is not null)
        {
            rows.Add(new("Subject", cert.Subject ?? string.Empty));
            if (cert.Issuer is { } issuer) rows.Add(new("Issuer", issuer));
            rows.Add(new("Thumbprint", cert.Thumbprint));
            if (cert.SubjectAlternativeNames.Count > 0) rows.Add(new("DNS names", string.Join(", ", cert.SubjectAlternativeNames)));
            if (cert.KeyType is { } kt) rows.Add(new("Key", kt + (cert.KeySize is { } ks ? $" {ks}" : string.Empty)));
            if (cert.ContentType is { } ct) rows.Add(new("Format", ct));
            if (cert.Exportable is { } ex) rows.Add(new("Private key exportable", ex ? "Yes" : "No"));
        }
        rows.Add(new("Identifier", Identifier(item)));
        if (item.Tags.Count > 0) rows.Add(new("Tags", string.Join(", ", item.Tags.Select(t => $"{t.Key}={t.Value}"))));
        return rows;
    }

    private static string Format(DateTimeOffset value) => value.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    private string Identifier(VaultItem item) => $"{VaultUri}{item.Kind switch { VaultItemKind.Secret => "secrets", VaultItemKind.Key => "keys", _ => "certificates" }}/{item.Name}";

    // ---------- secret values ----------

    [RelayCommand]
    private async Task ToggleValue()
    {
        if (IsValueShown)
        {
            HideValue();
            return;
        }
        if (SelectedRow?.Item is not { Kind: VaultItemKind.Secret } item) return;
        string? value = null;
        await RunAsync("Reading secret", "Reading the secret", async (client, ct) => value = await client.GetSecretValueAsync(item.Name, null, ct));
        if (value is null || SelectedRow?.Item != item) return;
        SecretValue = value;
        _hideTimer.Stop();
        _hideTimer.Start();
        _log.Info($"[{VaultName}] showed secret '{item.Name}'");
    }

    private void HideValue()
    {
        _hideTimer.Stop();
        SecretValue = null;
    }

    [RelayCommand]
    private async Task CopyValue()
    {
        if (SelectedRow?.Item is not { Kind: VaultItemKind.Secret } item) return;
        var value = SecretValue;
        if (value is null)
            await RunAsync("Reading secret", "Reading the secret", async (client, ct) => value = await client.GetSecretValueAsync(item.Name, null, ct));
        if (value is null) return;
        StatusMessage = _clipboard.CopySecret(value)
            ? $"Copied '{item.Name}'. The clipboard clears in {ClipboardGuard.ClearAfter.TotalSeconds:0} seconds."
            : "The clipboard is busy, try again.";
        _log.Info($"[{VaultName}] copied secret '{item.Name}'");
    }

    [RelayCommand]
    private void CopyName()
    {
        if (SelectedRow is { } row && _clipboard.CopyPlain(row.Name)) StatusMessage = $"Copied the name '{row.Name}'.";
    }

    [RelayCommand]
    private void CopyIdentifier()
    {
        if (SelectedRow is { } row && _clipboard.CopyPlain(Identifier(row.Item))) StatusMessage = "Copied the identifier.";
    }

    [RelayCommand]
    private void CopyVaultUri()
    {
        if (_clipboard.CopyPlain(VaultUriText)) StatusMessage = "Copied the vault URI.";
    }

    // ---------- writes ----------

    [RelayCommand]
    private async Task New()
    {
        var editor = SelectedKind.Kind switch
        {
            VaultItemKind.Secret => VaultItemEditorViewModel.NewSecret(),
            VaultItemKind.Key => VaultItemEditorViewModel.NewKey(),
            _ => VaultItemEditorViewModel.NewCertificate(),
        };
        if (!_dialogs.EditVaultItem(editor)) return;

        var name = editor.Name.Trim();
        var ok = SelectedKind.Kind switch
        {
            VaultItemKind.Secret => await WriteAsync($"Creating secret '{name}'", "Creating the secret", (c, ct) => c.SetSecretAsync(editor.ToSecretWrite(), ct)),
            VaultItemKind.Key => await WriteAsync($"Creating key '{name}'", "Creating the key", (c, ct) => c.CreateKeyAsync(editor.ToKeyCreate(), ct)),
            _ => await WriteAsync($"Creating certificate '{name}'", "Creating the certificate", (c, ct) => c.CreateSelfSignedCertificateAsync(editor.ToCertificateCreate(), ct)),
        };
        editor.Value = string.Empty;
        if (ok) await AfterWriteAsync(name, $"Created '{name}'.");
    }

    [RelayCommand]
    private async Task ImportCertificate()
    {
        var editor = VaultItemEditorViewModel.ImportCertificate();
        if (!_dialogs.EditVaultItem(editor)) return;
        var name = editor.Name.Trim();
        CertificateImport import;
        try
        {
            import = editor.ToCertificateImport();
        }
        catch (Exception ex)
        {
            ErrorText = $"Could not read {editor.ImportFile}: {ex.Message}";
            return;
        }
        editor.ImportPassword = null;
        if (await WriteAsync($"Importing '{name}'", "Importing the certificate", (c, ct) => c.ImportCertificateAsync(import, ct)))
            await AfterWriteAsync(name, $"Imported '{name}'.");
    }

    [RelayCommand]
    private async Task NewVersion()
    {
        if (SelectedRow?.Item is not { Kind: VaultItemKind.Secret } item) return;
        var editor = VaultItemEditorViewModel.NewSecretVersion(item);
        if (!_dialogs.EditVaultItem(editor)) return;
        var ok = await WriteAsync($"Saving a new version of '{item.Name}'", "Saving the new version", (c, ct) => c.SetSecretAsync(editor.ToSecretWrite(), ct));
        editor.Value = string.Empty;
        if (ok) await AfterWriteAsync(item.Name, $"Saved a new version of '{item.Name}'.");
    }

    [RelayCommand]
    private async Task EditProperties()
    {
        if (SelectedRow?.Item is not { } item) return;
        var editor = VaultItemEditorViewModel.EditProperties(item);
        if (!_dialogs.EditVaultItem(editor)) return;
        var update = editor.ToPropertiesUpdate();
        var ok = item.Kind switch
        {
            VaultItemKind.Secret => await WriteAsync($"Updating '{item.Name}'", "Updating the secret", (c, ct) => c.UpdateSecretAsync(item.Name, update, ct)),
            VaultItemKind.Key => await WriteAsync($"Updating '{item.Name}'", "Updating the key", (c, ct) => c.UpdateKeyAsync(item.Name, update, ct)),
            _ => await WriteAsync($"Updating '{item.Name}'", "Updating the certificate", (c, ct) => c.UpdateCertificateAsync(item.Name, update, ct)),
        };
        if (ok) await AfterWriteAsync(item.Name, $"Updated '{item.Name}'.");
    }

    [RelayCommand]
    private async Task Delete()
    {
        if (SelectedRow?.Item is not { } item) return;
        var noun = item.Kind switch { VaultItemKind.Secret => "secret", VaultItemKind.Key => "key", _ => "certificate" };
        var extra = item.Kind == VaultItemKind.Certificate ? " Its key and secret go with it." : string.Empty;
        if (!_dialogs.Confirm(
                $"Delete the {noun} '{item.Name}' from {VaultName}?{extra}\n\nIf the vault has soft delete on (the default), it can be recovered in the Azure portal until the retention period ends. The name stays reserved until then.",
                $"Delete {noun}"))
            return;

        var ok = item.Kind switch
        {
            VaultItemKind.Secret => await WriteAsync($"Deleting '{item.Name}'", $"Deleting the {noun}", (c, ct) => c.DeleteSecretAsync(item.Name, ct)),
            VaultItemKind.Key => await WriteAsync($"Deleting '{item.Name}'", $"Deleting the {noun}", (c, ct) => c.DeleteKeyAsync(item.Name, ct)),
            _ => await WriteAsync($"Deleting '{item.Name}'", $"Deleting the {noun}", (c, ct) => c.DeleteCertificateAsync(item.Name, ct)),
        };
        if (!ok) return;
        SelectedRow = null;
        await AfterWriteAsync(null, $"Deleted '{item.Name}'.");
    }

    [RelayCommand]
    private void DownloadCer()
    {
        if (SelectedRow?.Item is not { Kind: VaultItemKind.Certificate } item || _certificateDetails is not { Cer.Length: > 0 } details) return;
        var path = _dialogs.PickSaveFile("Save certificate (public part)", "Certificate (*.cer)|*.cer", item.Name + ".cer");
        if (path is null) return;
        try
        {
            File.WriteAllBytes(path, details.Cer);
            StatusMessage = $"Saved {Path.GetFileName(path)}.";
        }
        catch (Exception ex)
        {
            ErrorText = $"Could not save {path}: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task DownloadWithPrivateKey()
    {
        if (SelectedRow?.Item is not { Kind: VaultItemKind.Certificate } item) return;
        if (!_dialogs.Confirm(
                $"Download '{item.Name}' with its private key?\n\nKey Vault hands it over without a password, so the file is as sensitive as the key itself. Delete it once it has been imported where it is needed.",
                "Download private key"))
            return;

        (byte[] Content, string Extension)? download = null;
        await RunAsync("Downloading", "Downloading the certificate", async (c, ct) => download = await c.DownloadWithPrivateKeyAsync(item.Name, ct));
        if (download is not { } d) return;

        var filter = d.Extension == ".pem" ? "PEM (*.pem)|*.pem" : "PFX (*.pfx)|*.pfx";
        var path = _dialogs.PickSaveFile("Save certificate with private key", filter, item.Name + d.Extension);
        try
        {
            if (path is null) return;
            File.WriteAllBytes(path, d.Content);
            StatusMessage = $"Saved {Path.GetFileName(path)}. It holds the private key with no password.";
            _log.Info($"[{VaultName}] downloaded certificate '{item.Name}' with private key");
        }
        catch (Exception ex)
        {
            ErrorText = $"Could not save {path}: {ex.Message}";
        }
        finally
        {
            Array.Clear(d.Content);
        }
    }

    [RelayCommand]
    private void DismissError() => ErrorText = string.Empty;

    private async Task AfterWriteAsync(string? select, string message)
    {
        _cache.Remove(SelectedKind.Kind);
        await LoadKindAsync(force: true);
        if (select is not null) SelectedRow = Rows.FirstOrDefault(r => r.Name == select);
        if (!HasError) StatusMessage = message;
    }

    // ---------- plumbing ----------

    private async Task<bool> WriteAsync(string busy, string action, Func<VaultClient, CancellationToken, Task> work)
    {
        var ok = false;
        await RunAsync(busy, action, async (c, ct) =>
        {
            await work(c, ct);
            ok = true;
        });
        if (ok) _log.Info($"[{VaultName}] {busy}: done");
        return ok;
    }

    /// <summary>Signs in if needed, runs the work, and turns any failure into the error bar.</summary>
    private async Task RunAsync(string busy, string action, Func<VaultClient, CancellationToken, Task> work)
    {
        ErrorText = string.Empty;
        StatusMessage = string.Empty;
        BusyText = busy + "...";
        IsBusy = true;
        try
        {
            var ct = _lifetime.Token;
            await _auth.EnsureSignedInAsync(_tenantId, AzureAuth.VaultScope, ct);
            await work(Client(), ct);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            ErrorText = KeyVaultErrors.Describe(ex, action);
            _log.Warn($"[{VaultName}] {action} failed: {ex.GetType().Name}: {ex.Message.Split('\n')[0]}");
        }
        finally
        {
            IsBusy = false;
            BusyText = string.Empty;
        }
    }

    /// <summary>Rebuilt when sign in hands out a new credential.</summary>
    private VaultClient Client()
    {
        var credential = _auth.Credential(_tenantId);
        if (_client is null || !ReferenceEquals(_clientCredential, credential))
        {
            _client = new VaultClient(VaultUri, credential);
            _clientCredential = credential;
        }
        return _client;
    }

    public void Dispose()
    {
        _hideTimer.Stop();
        SecretValue = null;
        _detailCts?.Cancel();
        _lifetime.Cancel();
        _lifetime.Dispose();
    }
}
