using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteConnectionManager.App.Services;
using RemoteConnectionManager.KeyVault;

namespace RemoteConnectionManager.App.ViewModels;

public enum AzureNodeKind
{
    Subscription,
    ResourceGroup,
    Vault,
    Message,
}

/// <summary>A row in the Azure browser tree. Property names match the shared TreeViewItem style.</summary>
public sealed partial class AzureNodeViewModel : ObservableObject
{
    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private bool _isVisible = true;
    [ObservableProperty] private string _displayName;
    [ObservableProperty] private string _detail = string.Empty;

    public AzureNodeKind Kind { get; }
    public VaultInfo? Vault { get; init; }
    public SubscriptionInfo? Subscription { get; init; }
    public ObservableCollection<AzureNodeViewModel> Children { get; } = new();

    public bool IsVault => Kind == AzureNodeKind.Vault;
    public string Glyph => Kind switch
    {
        AzureNodeKind.Subscription => "",
        AzureNodeKind.ResourceGroup => "",
        AzureNodeKind.Vault => "",
        _ => "",
    };

    /// <summary>Theme brush key for the glyph colour (Icon.* tokens in Themes/Rcm.xaml).</summary>
    public string IconBrushKey => Kind switch
    {
        AzureNodeKind.Subscription => "Icon.Subscription",
        AzureNodeKind.ResourceGroup => "Icon.ResourceGroup",
        AzureNodeKind.Vault => "Icon.Vault",
        _ => "Icon.Default",
    };

    public AzureNodeViewModel(AzureNodeKind kind, string displayName)
    {
        Kind = kind;
        _displayName = displayName;
    }

    public bool ApplyFilter(string filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
        {
            IsVisible = true;
            foreach (var child in Children) child.ApplyFilter(filter);
            return true;
        }
        var self = DisplayName.Contains(filter, StringComparison.OrdinalIgnoreCase);
        var any = false;
        foreach (var child in Children) any |= child.ApplyFilter(filter);
        if (self && !any)
            foreach (var child in Children) child.ShowAll();
        IsVisible = self || any;
        if (any) IsExpanded = true;
        return IsVisible;
    }

    private void ShowAll()
    {
        IsVisible = true;
        foreach (var child in Children) child.ShowAll();
    }

    public IEnumerable<AzureNodeViewModel> Descendants()
    {
        foreach (var child in Children)
        {
            yield return child;
            foreach (var grand in child.Descendants()) yield return grand;
        }
    }
}

/// <summary>
/// The "Azure Key Vaults" tab: sign in, list every vault the account can see through Azure
/// Resource Manager, open one or pin it to the connections tree. Opening by name works without
/// any ARM permission, for accounts that only have data plane roles on a vault.
/// </summary>
public sealed partial class AzureBrowserTabViewModel : TabViewModelBase, IDisposable
{
    private readonly ShellViewModel _shell;
    private readonly AzureAuth _auth;
    private readonly VaultCatalog _catalog;
    private readonly AppLog _log;
    private CancellationTokenSource? _loadCts;

    [ObservableProperty] private string _filterText = string.Empty;
    [ObservableProperty] private string _openByName = string.Empty;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _busyText = string.Empty;
    [ObservableProperty] private string _errorText = string.Empty;
    [ObservableProperty] private string _summaryText = string.Empty;
    [ObservableProperty] private AzureNodeViewModel? _selectedNode;

    public ObservableCollection<AzureNodeViewModel> Roots { get; } = new();

    public bool IsSignedIn => _auth.IsSignedIn;
    public string AccountText => _auth.AccountName is { } name ? $"Signed in as {name}" : "Not signed in to Azure";
    public bool HasError => ErrorText.Length > 0;
    public bool CanUseSelectedVault => SelectedNode?.Vault is not null;

    public AzureBrowserTabViewModel(ShellViewModel shell, AzureAuth auth, VaultCatalog catalog, AppLog log)
    {
        _shell = shell;
        _auth = auth;
        _catalog = catalog;
        _log = log;
        Title = "Azure Key Vaults";
        Glyph = "";
        IconBrushKey = "Icon.Azure";
        _auth.Changed += OnAuthChanged;
        SummaryText = _auth.IsSignedIn ? "Press Refresh to list the vaults your account can see." : "Sign in to list the vaults your account can see.";
        if (_auth.IsSignedIn) _ = LoadAsync();
    }

    private void OnAuthChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(IsSignedIn));
        OnPropertyChanged(nameof(AccountText));
    }

    partial void OnErrorTextChanged(string value) => OnPropertyChanged(nameof(HasError));

    partial void OnFilterTextChanged(string value)
    {
        foreach (var root in Roots) root.ApplyFilter(value);
    }

    partial void OnSelectedNodeChanged(AzureNodeViewModel? value)
    {
        OnPropertyChanged(nameof(CanUseSelectedVault));
        OpenSelectedCommand.NotifyCanExecuteChanged();
        PinSelectedCommand.NotifyCanExecuteChanged();
    }

    // ---------- sign in ----------

    [RelayCommand]
    private async Task SignIn()
    {
        if (await RunAsync("Signing in", "Signing in", ct => _auth.EnsureSignedInAsync(null, AzureAuth.ArmScope, ct)))
            await LoadAsync();
    }

    [RelayCommand]
    private async Task SignInAnother()
    {
        if (await RunAsync("Signing in", "Signing in", ct => _auth.SignInWithAnotherAccountAsync(ct)))
            await LoadAsync();
    }

    [RelayCommand]
    private void SignOut()
    {
        _loadCts?.Cancel();
        _auth.SignOut();
        Roots.Clear();
        SummaryText = "Signed out. Open vault tabs stop working until you sign in again.";
        _log.Info("Signed out of Azure");
    }

    // ---------- listing ----------

    [RelayCommand]
    private Task Refresh() => LoadAsync();

    private async Task LoadAsync()
    {
        _loadCts?.Cancel();
        var cts = new CancellationTokenSource();
        _loadCts = cts;

        IReadOnlyList<SubscriptionInfo> subscriptions = Array.Empty<SubscriptionInfo>();
        var ok = await RunAsync("Listing subscriptions", "Listing subscriptions", async ct =>
        {
            await _auth.EnsureSignedInAsync(null, AzureAuth.ArmScope, ct);
            subscriptions = await _catalog.GetSubscriptionsAsync(ct);
        }, cts.Token);
        if (!ok || cts.IsCancellationRequested) return;

        Roots.Clear();
        var vaultCount = 0;
        var done = 0;
        BusyText = $"Looking for vaults in {subscriptions.Count} subscriptions...";
        IsBusy = true;
        using var gate = new SemaphoreSlim(4);

        var tasks = subscriptions.Select(async sub =>
        {
            await gate.WaitAsync(cts.Token);
            try
            {
                var vaults = await _catalog.GetVaultsAsync(sub, cts.Token);
                if (cts.IsCancellationRequested || vaults.Count == 0) return;
                vaultCount += vaults.Count;
                AddSubscription(sub, vaults, null);
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                AddSubscription(sub, Array.Empty<VaultInfo>(), KeyVaultErrors.Describe(ex, "Listing vaults"));
                _log.Warn($"Listing vaults in {sub.Name} failed: {ex.GetType().Name}: {ex.Message.Split('\n')[0]}");
            }
            finally
            {
                gate.Release();
                done++;
                if (!cts.IsCancellationRequested) BusyText = $"Looking for vaults... {done} of {subscriptions.Count} subscriptions";
            }
        }).ToList();

        try
        {
            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException)
        {
        }
        if (cts.IsCancellationRequested) return;

        IsBusy = false;
        BusyText = string.Empty;
        SummaryText = $"{vaultCount} vaults in {Roots.Count(r => r.Children.Any(c => c.Kind != AzureNodeKind.Message))} of {subscriptions.Count} subscriptions. Subscriptions with no vaults are hidden.";
        if (!string.IsNullOrWhiteSpace(FilterText))
            foreach (var root in Roots) root.ApplyFilter(FilterText);
        if (Roots.Count == 1) Roots[0].IsExpanded = true;
        _log.Info($"Azure browser found {vaultCount} vaults in {subscriptions.Count} subscriptions");
    }

    private void AddSubscription(SubscriptionInfo sub, IReadOnlyList<VaultInfo> vaults, string? error)
    {
        var node = new AzureNodeViewModel(AzureNodeKind.Subscription, sub.Name) { Subscription = sub, Detail = sub.Id };
        if (error is not null)
            node.Children.Add(new AzureNodeViewModel(AzureNodeKind.Message, error));
        foreach (var group in vaults.GroupBy(v => v.ResourceGroup, StringComparer.OrdinalIgnoreCase))
        {
            var rg = new AzureNodeViewModel(AzureNodeKind.ResourceGroup, group.Key) { IsExpanded = true };
            foreach (var vault in group)
                rg.Children.Add(new AzureNodeViewModel(AzureNodeKind.Vault, vault.Name) { Vault = vault, Detail = vault.Location });
            node.Children.Add(rg);
        }

        // Keep subscriptions in name order as they arrive.
        var index = 0;
        while (index < Roots.Count && string.Compare(Roots[index].DisplayName, node.DisplayName, StringComparison.OrdinalIgnoreCase) < 0) index++;
        Roots.Insert(index, node);
        if (!string.IsNullOrWhiteSpace(FilterText)) node.ApplyFilter(FilterText);
    }

    // ---------- open and pin ----------

    [RelayCommand(CanExecute = nameof(CanUseSelectedVault))]
    private void OpenSelected()
    {
        if (SelectedNode?.Vault is { } vault) _shell.OpenVault(vault.VaultUri, vault.TenantId, vault.Name, null);
    }

    [RelayCommand(CanExecute = nameof(CanUseSelectedVault))]
    private void PinSelected()
    {
        if (SelectedNode?.Vault is { } vault) _shell.PinVault(vault);
    }

    [RelayCommand]
    private void OpenNamed()
    {
        var uri = VaultNames.NormaliseVaultUri(OpenByName);
        if (uri is null)
        {
            ErrorText = "Enter a vault name such as kv-prod, or an address such as https://kv-prod.vault.azure.net.";
            return;
        }
        ErrorText = string.Empty;
        _shell.OpenVault(uri, null, VaultNames.VaultNameOf(uri), null);
        OpenByName = string.Empty;
    }

    [RelayCommand]
    private void DismissError() => ErrorText = string.Empty;

    private async Task<bool> RunAsync(string busy, string action, Func<CancellationToken, Task> work, CancellationToken ct = default)
    {
        ErrorText = string.Empty;
        BusyText = busy + "...";
        IsBusy = true;
        try
        {
            await work(ct);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex)
        {
            ErrorText = KeyVaultErrors.Describe(ex, action);
            _log.Warn($"{action} failed: {ex.GetType().Name}: {ex.Message.Split('\n')[0]}");
            return false;
        }
        finally
        {
            IsBusy = false;
            BusyText = string.Empty;
        }
    }

    public void Dispose()
    {
        _loadCts?.Cancel();
        _auth.Changed -= OnAuthChanged;
    }
}
