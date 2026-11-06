using CommunityToolkit.Mvvm.ComponentModel;
using RemoteConnectionManager.Core.Models;
using RemoteConnectionManager.Plugins.OnePassword;

namespace RemoteConnectionManager.App.ViewModels;

/// <summary>
/// Picks the vault credential behind the terminal's Send password button (SSH and telnet entries
/// and their folder defaults). Inherit = null, None = Source Windows (stops an inherited one).
/// Kept apart from the RDP sign in fields because network gear rarely shares the RDP account.
/// </summary>
public sealed partial class CredentialPickerViewModel : ObservableObject
{
    private static readonly IReadOnlyList<Choice<CredentialSource?>> AllChoices =
    [
        new("Inherit", null),
        new("None: type it at the prompt", CredentialSource.Windows),
        new("Key Vault secret", CredentialSource.KeyVault),
        new("1Password item", CredentialSource.OnePassword),
    ];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowVaultFields), nameof(ShowOnePasswordFields), nameof(ShowNote))]
    private Choice<CredentialSource?> _selected = AllChoices[0];
    [ObservableProperty] private string _vault = string.Empty;
    [ObservableProperty] private string _secret = string.Empty;
    [ObservableProperty] private string _tenant = string.Empty;
    [ObservableProperty] private string _onePasswordItem = string.Empty;
    [ObservableProperty] private string _onePasswordVault = string.Empty;
    [ObservableProperty] private string _onePasswordAccount = string.Empty;

    public IReadOnlyList<Choice<CredentialSource?>> Choices { get; private set; } = AllChoices;
    public IReadOnlyList<string> KnownVaults { get; set; } = Array.Empty<string>();

    public bool ShowVaultFields => Selected.Value == CredentialSource.KeyVault;
    public bool ShowOnePasswordFields => Selected.Value == CredentialSource.OnePassword;
    public bool ShowNote => ShowVaultFields || ShowOnePasswordFields;

    public void Load(CredentialReference? credential, IReadOnlyCollection<CredentialSource>? enabledSources)
    {
        if (enabledSources is not null)
            Choices = AllChoices
                .Where(c => c.Value is null or CredentialSource.Windows || enabledSources.Contains(c.Value.Value) || c.Value == credential?.Source)
                .ToList();
        Selected = Choices.FirstOrDefault(c => c.Value == credential?.Source) ?? Choices[0];
        Vault = credential?.VaultUri ?? string.Empty;
        Secret = credential?.SecretName ?? string.Empty;
        Tenant = credential?.TenantId ?? string.Empty;
        OnePasswordItem = credential?.OnePasswordItem ?? string.Empty;
        OnePasswordVault = credential?.OnePasswordVault ?? string.Empty;
        OnePasswordAccount = credential?.OnePasswordAccount ?? string.Empty;
    }

    public CredentialReference? Build() => Selected.Value switch
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
            VaultUri = KeyVault.VaultNames.NormaliseVaultUri(Vault)?.ToString() ?? Vault.Trim(),
            SecretName = Blank(Secret),
            TenantId = Blank(Tenant),
        },
    };

    /// <summary>A problem to show, or null when the choice is complete.</summary>
    public string? Validate()
    {
        if (ShowVaultFields)
        {
            if (KeyVault.VaultNames.NormaliseVaultUri(Vault) is null) return "Send password: enter the vault name or its https://name.vault.azure.net address.";
            if (KeyVault.VaultNames.ItemNameProblem(Secret) is { } problem) return $"Send password secret name: {problem}";
        }
        if (ShowOnePasswordFields)
        {
            OnePasswordReference.TryParse(Build()!, out var problem);
            if (problem is not null) return $"Send password: {problem}";
        }
        return null;
    }

    private static string? Blank(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
