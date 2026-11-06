using System.Text.Json.Serialization;

namespace RemoteConnectionManager.Core.Models;

/// <summary>
/// An RDP target. Nullable settings mean "inherit from the folder chain, then app defaults".
/// Credentials are never stored: the session connects as the logged on user unless
/// UserName/Domain are set, in which case Windows prompts for the password, or
/// <see cref="Credential"/> points at a Key Vault secret that is read at connect time.
/// </summary>
public sealed class RdpConnection : ConnectionEntry
{
    public string Host { get; set; } = string.Empty;
    public int? Port { get; set; }
    public string? Domain { get; set; }
    public string? UserName { get; set; }
    public bool? AdminSession { get; set; }
    public bool? KeyboardPassThrough { get; set; }
    public bool? RedirectClipboard { get; set; }
    public bool? SmartSizing { get; set; }
    /// <summary>Null inherits from the folder chain. Source Windows opts out of an inherited vault credential.</summary>
    public CredentialReference? Credential { get; set; }
    public CredentialProtection? CredentialProtection { get; set; }
    public string? GatewayHost { get; set; }
    public GatewayUsage? GatewayUsage { get; set; }
    /// <summary>Services the Check services view always lists. Null inherits; an empty list stops inheriting. '*' wildcards allowed.</summary>
    public List<string>? WatchedServices { get; set; }

    [JsonIgnore]
    public override ConnectionKind Kind => ConnectionKind.Rdp;
    [JsonIgnore]
    public override string Prefix => "(RDP)";

    public override ConnectionEntry Clone()
    {
        var copy = new RdpConnection
        {
            Host = Host,
            Port = Port,
            Domain = Domain,
            UserName = UserName,
            AdminSession = AdminSession,
            KeyboardPassThrough = KeyboardPassThrough,
            RedirectClipboard = RedirectClipboard,
            SmartSizing = SmartSizing,
            Credential = Credential?.Clone(),
            CredentialProtection = CredentialProtection,
            GatewayHost = GatewayHost,
            GatewayUsage = GatewayUsage,
            WatchedServices = WatchedServices is null ? null : new List<string>(WatchedServices),
        };
        CopyBaseTo(copy);
        return copy;
    }
}
