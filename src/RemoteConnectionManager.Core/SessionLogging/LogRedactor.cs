using System.Text.RegularExpressions;

namespace RemoteConnectionManager.Core.SessionLogging;

/// <summary>
/// Masks secrets in session log lines before they are written: network device passwords, secrets,
/// SNMP communities and keys (Cisco IOS/NX-OS, HP/Aruba ProCurve), key=value passwords, tokens,
/// private key blocks, and any value RCM itself typed in (the Send password button).
/// Best effort, line by line: a secret printed on its own with nothing around it (for example
/// Get-AzKeyVaultSecret -AsPlainText) cannot be recognised, so turn logging off for that work.
/// </summary>
public sealed partial class LogRedactor
{
    public const string Mask = "********";

    private readonly HashSet<string> _secrets = new(StringComparer.Ordinal);
    private bool _inPrivateKey;

    /// <summary>A value to mask wherever it appears, for example a password RCM just typed. Short values are ignored to keep logs readable.</summary>
    public void AddSecret(string? secret)
    {
        if (!string.IsNullOrEmpty(secret) && secret.Length >= 4) _secrets.Add(secret);
    }

    /// <summary>The line with secrets masked, or null when the line is part of a private key and should be dropped.</summary>
    public string? Redact(string line)
    {
        if (_inPrivateKey)
        {
            if (PrivateKeyEnd().IsMatch(line)) _inPrivateKey = false;
            return null;
        }
        if (PrivateKeyBegin().IsMatch(line))
        {
            _inPrivateKey = !PrivateKeyEnd().IsMatch(line);
            return "[private key removed from log]";
        }

        foreach (var secret in _secrets)
            line = line.Replace(secret, Mask, StringComparison.Ordinal);

        // Order matters: the specific device rules run before the general "password <value>" rule.
        line = HpPassword().Replace(line, m => m.Groups[1].Value + Mask);
        line = CiscoUsernameSecret().Replace(line, m => m.Groups[1].Value + Mask);
        line = CiscoSecret().Replace(line, m => m.Groups[1].Value + Mask);
        line = SnmpCommunity().Replace(line, m => m.Groups[1].Value + Mask);
        line = DeviceKey().Replace(line, m => m.Groups[1].Value + Mask);
        line = KeyValue().Replace(line, m => m.Groups[1].Value + Mask);
        line = SecureString().Replace(line, m => m.Groups[1].Value + Mask);
        line = Jwt().Replace(line, Mask);
        return line;
    }

    // HP/Aruba: password manager user-name admin sha1 0123abcd...
    [GeneratedRegex(@"(\bpassword\s+(?:manager|operator)\s+(?:user-name\s+\S+\s+)?(?:sha1|sha256|plaintext)\s+)(""[^""]*""|\S+)", RegexOptions.IgnoreCase)]
    private static partial Regex HpPassword();

    // Cisco: username admin privilege 15 secret 9 $9$..., username bob password 7 0822455D0A16
    [GeneratedRegex(@"(\busername\s+\S+\s+(?:.*?\s)?(?:secret|password)\s+(?:\d{1,2}\s+)?)(\S+)", RegexOptions.IgnoreCase)]
    private static partial Regex CiscoUsernameSecret();

    // Cisco: enable secret 5 $1$..., enable password x, password 7 0822..., secret 9 $9$...
    // Words that follow "password" in ordinary sentences are not treated as values.
    [GeneratedRegex(@"(\b(?:enable\s+)?(?:secret|password)(?:\s+level\s+\d+)?\s+(?:\d{1,2}\s+)?)(?!(?:for|is|to|of|and|or|was|has|have|had|must|will|can|cannot|could|should|may|not|never|change|changed|expired|expires|incorrect|invalid|again|required|policy|authentication|manager|operator|prompt|length|history|age|complexity|the|a|an|your|my|this|that|with|from|in|on|at|by)\b)([^\s:]\S*)", RegexOptions.IgnoreCase)]
    private static partial Regex CiscoSecret();

    // snmp-server community public RO, snmp-server community "public" operator
    [GeneratedRegex(@"(\bsnmp-server\s+community\s+)(""[^""]*""|\S+)", RegexOptions.IgnoreCase)]
    private static partial Regex SnmpCommunity();

    // tacacs-server key 7 ..., radius-server host x key ..., key-string ..., pre-shared-key ..., wpa-psk ascii ..., authentication-key 1 md5 ...
    [GeneratedRegex(@"(\b(?:(?:tacacs|radius)(?:-server)?\b.*?\bkey|key-string|pre-shared-key(?:\s+(?:ascii|hex|local|remote|address\s+\S+))*|wpa-psk\s+(?:ascii|hex)|authentication-key\s+\d+\s+md5|key\s+\d+\s+md5)\s+(?:\d\s+)?)(""[^""]*""|\S+)", RegexOptions.IgnoreCase)]
    private static partial Regex DeviceKey();

    // Password=x; in connection strings, password: x, token=x, client_secret=x, AccountKey=x, sig=x in a SAS URL.
    [GeneratedRegex(@"(\b(?:password|passwd|pwd|passphrase|secret|client_secret|token|access_token|refresh_token|apikey|api_key|api-key|accountkey|sharedaccesskey|sig)\s*[:=]\s*)(""[^""]*""|'[^']*'|[^\s;,&""']+)", RegexOptions.IgnoreCase)]
    private static partial Regex KeyValue();

    // ConvertTo-SecureString 'x' -AsPlainText, ConvertTo-SecureString -String "x"
    [GeneratedRegex(@"(\bConvertTo-SecureString\s+(?:-String\s+)?)(""[^""]*""|'[^']*'|\S+)", RegexOptions.IgnoreCase)]
    private static partial Regex SecureString();

    // JSON web tokens (Entra access tokens and the like).
    [GeneratedRegex(@"\beyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{5,}")]
    private static partial Regex Jwt();

    [GeneratedRegex(@"-----BEGIN [A-Z0-9 ]*PRIVATE KEY( BLOCK)?-----")]
    private static partial Regex PrivateKeyBegin();

    [GeneratedRegex(@"-----END [A-Z0-9 ]*PRIVATE KEY( BLOCK)?-----")]
    private static partial Regex PrivateKeyEnd();
}
