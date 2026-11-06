using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using RemoteConnectionManager.KeyVault;

namespace RemoteConnectionManager.App.ViewModels;

public enum VaultEditorMode
{
    NewSecret,
    NewSecretVersion,
    EditProperties,
    NewKey,
    NewCertificate,
    ImportCertificate,
}

/// <summary>
/// One dialog for every vault write. The mode decides which sections show. Holds a secret value
/// only while the dialog is open; the caller reads it once and drops the view model.
/// </summary>
public sealed partial class VaultItemEditorViewModel : ObservableObject
{
    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _value = string.Empty;
    [ObservableProperty] private string _contentType = string.Empty;
    [ObservableProperty] private string _expires = string.Empty;
    [ObservableProperty] private bool _enabled = true;

    // keys
    [ObservableProperty] private NewKeyType _keyType = NewKeyType.Rsa;
    [ObservableProperty] private int _rsaSize = 2048;
    [ObservableProperty] private string _ecCurve = "P-256";

    // certificates
    [ObservableProperty] private string _subject = string.Empty;
    [ObservableProperty] private string _dnsNames = string.Empty;
    [ObservableProperty] private string _validityMonths = "12";
    [ObservableProperty] private bool _pemFormat;
    [ObservableProperty] private string _importFile = string.Empty;

    [ObservableProperty] private string _validationMessage = string.Empty;

    public VaultEditorMode Mode { get; private init; }
    public VaultItemKind Kind { get; private init; }
    public string WindowTitle { get; private init; } = string.Empty;
    public string SaveLabel { get; private init; } = "Save";

    /// <summary>Set by the window from its PasswordBox; never bound.</summary>
    public string? ImportPassword { get; set; }

    public bool NameEditable => Mode is not (VaultEditorMode.NewSecretVersion or VaultEditorMode.EditProperties);
    public bool NameReadOnly => !NameEditable;
    public bool ShowValue => Mode is VaultEditorMode.NewSecret or VaultEditorMode.NewSecretVersion;
    public bool ShowContentType => Kind == VaultItemKind.Secret;
    public bool ShowExpires => Mode is VaultEditorMode.NewSecret or VaultEditorMode.NewSecretVersion or VaultEditorMode.NewKey
                               || (Mode == VaultEditorMode.EditProperties && Kind != VaultItemKind.Certificate);
    public bool ShowEnabled => Mode is VaultEditorMode.NewSecret or VaultEditorMode.NewSecretVersion or VaultEditorMode.EditProperties;
    public bool ShowKey => Mode == VaultEditorMode.NewKey;
    public bool ShowNewCertificate => Mode == VaultEditorMode.NewCertificate;
    public bool ShowImport => Mode == VaultEditorMode.ImportCertificate;
    public bool IsRsa => KeyType == NewKeyType.Rsa;
    public bool IsEc => KeyType == NewKeyType.Ec;

    public IReadOnlyList<NewKeyType> KeyTypes { get; } = [NewKeyType.Rsa, NewKeyType.Ec];
    public IReadOnlyList<int> RsaSizes { get; } = [2048, 3072, 4096];
    public IReadOnlyList<string> EcCurves { get; } = ["P-256", "P-384", "P-521", "P-256K"];

    partial void OnKeyTypeChanged(NewKeyType value)
    {
        OnPropertyChanged(nameof(IsRsa));
        OnPropertyChanged(nameof(IsEc));
    }

    partial void OnNameChanged(string value)
    {
        if (Mode == VaultEditorMode.NewCertificate && (Subject.Length == 0 || Subject == $"CN={_lastAutoSubject}"))
        {
            _lastAutoSubject = value;
            Subject = $"CN={value}";
        }
    }
    private string _lastAutoSubject = string.Empty;

    // ---------- factories ----------

    public static VaultItemEditorViewModel NewSecret() => new()
    {
        Mode = VaultEditorMode.NewSecret, Kind = VaultItemKind.Secret, WindowTitle = "New secret", SaveLabel = "Create",
    };

    public static VaultItemEditorViewModel NewSecretVersion(VaultItem item) => new()
    {
        Mode = VaultEditorMode.NewSecretVersion, Kind = VaultItemKind.Secret, WindowTitle = $"New version of {item.Name}", SaveLabel = "Save new version",
        Name = item.Name, ContentType = item.ContentType ?? string.Empty, Expires = FormatDate(item.ExpiresOn), Enabled = true,
    };

    public static VaultItemEditorViewModel EditProperties(VaultItem item) => new()
    {
        Mode = VaultEditorMode.EditProperties, Kind = item.Kind, WindowTitle = $"Properties of {item.Name}",
        Name = item.Name, ContentType = item.ContentType ?? string.Empty, Expires = FormatDate(item.ExpiresOn), Enabled = item.Enabled,
    };

    public static VaultItemEditorViewModel NewKey() => new()
    {
        Mode = VaultEditorMode.NewKey, Kind = VaultItemKind.Key, WindowTitle = "New key", SaveLabel = "Create",
    };

    public static VaultItemEditorViewModel NewCertificate() => new()
    {
        Mode = VaultEditorMode.NewCertificate, Kind = VaultItemKind.Certificate, WindowTitle = "New self signed certificate", SaveLabel = "Create",
    };

    public static VaultItemEditorViewModel ImportCertificate() => new()
    {
        Mode = VaultEditorMode.ImportCertificate, Kind = VaultItemKind.Certificate, WindowTitle = "Import certificate", SaveLabel = "Import",
    };

    // ---------- validation and results ----------

    public bool Validate()
    {
        ValidationMessage = Problem() ?? string.Empty;
        return ValidationMessage.Length == 0;
    }

    private string? Problem()
    {
        if (NameEditable && VaultNames.ItemNameProblem(Name) is { } nameProblem) return nameProblem;
        if (ShowValue && Value.Length == 0) return "Enter a value.";
        if (ShowValue && Value.Length > 25 * 1024) return "Secret values can be at most 25 KB.";
        if (ShowExpires && !TryParseDate(Expires, out _)) return "Expires must be a date such as 2027-06-30, or blank for never.";
        if (ShowNewCertificate)
        {
            if (!Subject.Trim().StartsWith("CN=", StringComparison.OrdinalIgnoreCase)) return "Subject must start with CN=, for example CN=app.contoso.com.";
            if (!int.TryParse(ValidityMonths, out var months) || months is < 1 or > 120) return "Validity must be between 1 and 120 months.";
        }
        if (ShowImport)
        {
            if (!File.Exists(ImportFile)) return "Choose a .pfx or .p12 file to import.";
            if (new FileInfo(ImportFile).Length > 200 * 1024) return "That file is too large to be a certificate.";
        }
        return null;
    }

    public DateTimeOffset? ExpiresOn => TryParseDate(Expires, out var d) ? d : null;

    public SecretWrite ToSecretWrite() => new(Name.Trim(), Value, Blank(ContentType), ExpiresOn, Enabled);

    public ItemPropertiesUpdate ToPropertiesUpdate() => new(Enabled, ExpiresOn, Blank(ContentType));

    public KeyCreate ToKeyCreate() => new(Name.Trim(), KeyType, RsaSize, EcCurve, ExpiresOn);

    public SelfSignedCertificateCreate ToCertificateCreate() =>
        new(Name.Trim(), Subject.Trim(), VaultNames.SplitList(DnsNames), int.Parse(ValidityMonths, CultureInfo.InvariantCulture), PemFormat);

    public CertificateImport ToCertificateImport() => new(Name.Trim(), File.ReadAllBytes(ImportFile), ImportPassword);

    /// <summary>Blank is "never". Accepts ISO dates and the local short date format. Expires at the end of that day, local time.</summary>
    public static bool TryParseDate(string text, out DateTimeOffset? result)
    {
        result = null;
        if (string.IsNullOrWhiteSpace(text)) return true;
        if (!DateOnly.TryParseExact(text.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) &&
            !DateOnly.TryParse(text.Trim(), CultureInfo.CurrentCulture, DateTimeStyles.None, out date))
            return false;
        var local = date.ToDateTime(new TimeOnly(23, 59, 59), DateTimeKind.Local);
        result = new DateTimeOffset(local);
        return true;
    }

    public static string FormatDate(DateTimeOffset? value) => value is { } v ? v.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : string.Empty;

    private static string? Blank(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
