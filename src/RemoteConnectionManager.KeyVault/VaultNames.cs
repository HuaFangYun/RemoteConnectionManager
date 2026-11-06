using System.Text.RegularExpressions;

namespace RemoteConnectionManager.KeyVault;

/// <summary>Validation for vault URIs and item names, so bad input fails before a round trip. Pure, unit tested.</summary>
public static partial class VaultNames
{
    // Secret, key and certificate names: 1 to 127 characters, letters, digits and dashes.
    [GeneratedRegex("^[0-9a-zA-Z-]{1,127}$")]
    private static partial Regex ItemNameRegex();

    // Vault names: 3 to 24 characters, start with a letter, letters digits dashes, no double dash, no trailing dash.
    [GeneratedRegex("^(?!.*--)[a-zA-Z][a-zA-Z0-9-]{1,22}[a-zA-Z0-9]$")]
    private static partial Regex VaultNameRegex();

    public static string? ItemNameProblem(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "Enter a name.";
        if (name.Length > 127) return "Names can be at most 127 characters.";
        return ItemNameRegex().IsMatch(name) ? null : "Names can only use letters, digits and dashes.";
    }

    /// <summary>
    /// Accepts a full URI ("https://kv-a.vault.azure.net/"), a host ("kv-a.vault.azure.net")
    /// or a bare vault name ("kv-a", assumed public cloud). Returns null when it cannot be a vault.
    /// </summary>
    public static Uri? NormaliseVaultUri(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        var text = input.Trim().TrimEnd('/');

        if (!text.Contains('.') && !text.Contains(':'))
            return VaultNameRegex().IsMatch(text) ? new Uri($"https://{text.ToLowerInvariant()}.vault.azure.net/") : null;

        if (!text.Contains("://", StringComparison.Ordinal)) text = "https://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme != Uri.UriSchemeHttps) return null;
        if (!uri.Host.Contains(".vault.", StringComparison.OrdinalIgnoreCase) &&
            !uri.Host.Contains(".managedhsm.", StringComparison.OrdinalIgnoreCase)) return null;
        return new Uri($"https://{uri.Host.ToLowerInvariant()}/");
    }

    /// <summary>"kv-a" from "https://kv-a.vault.azure.net/".</summary>
    public static string VaultNameOf(Uri vaultUri) => vaultUri.Host.Split('.')[0];

    /// <summary>Splits a comma, semicolon or newline separated list, trimming blanks.</summary>
    public static IReadOnlyList<string> SplitList(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? Array.Empty<string>()
            : text.Split([',', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
