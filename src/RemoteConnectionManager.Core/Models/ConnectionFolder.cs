using System.Text.Json.Serialization;

namespace RemoteConnectionManager.Core.Models;

/// <summary>
/// Defaults a folder passes down to everything beneath it. Entries override per field.
/// Null means "not set here, keep looking up the chain".
/// </summary>
public sealed class ConnectionDefaults
{
    public string? Domain { get; set; }
    public string? UserName { get; set; }
    public int? Port { get; set; }
    public bool? AdminSession { get; set; }
    public bool? KeyboardPassThrough { get; set; }
    public bool? RedirectClipboard { get; set; }
    public bool? SmartSizing { get; set; }
    /// <summary>Connect without the "identity of the remote computer cannot be verified" prompt. For lab or internal hosts with self signed RDP certificates.</summary>
    public bool? IgnoreCertificateErrors { get; set; }
    public CredentialReference? Credential { get; set; }
    public CredentialProtection? CredentialProtection { get; set; }
    public string? GatewayHost { get; set; }
    public GatewayUsage? GatewayUsage { get; set; }
    /// <summary>Services the Check services view always lists for hosts in this folder.</summary>
    public List<string>? WatchedServices { get; set; }
    /// <summary>User name for SSH and telnet entries. Separate from the RDP one: network gear rarely shares the AD account.</summary>
    public string? SshUserName { get; set; }
    /// <summary>Vault credential for the Send password button on SSH and telnet entries.</summary>
    public CredentialReference? SshCredential { get; set; }
    /// <summary>Log terminal, SSH and telnet sessions to a file. Null keeps looking up the chain, then the app setting.</summary>
    public bool? SessionLogging { get; set; }
    /// <summary>Icon colour ("#RRGGBB") for this folder and everything beneath it, for example red for production.</summary>
    public string? IconColour { get; set; }

    [JsonIgnore]
    public bool IsEmpty =>
        Domain is null && UserName is null && Port is null && AdminSession is null &&
        KeyboardPassThrough is null && RedirectClipboard is null && SmartSizing is null && IgnoreCertificateErrors is null &&
        Credential is null && CredentialProtection is null && GatewayHost is null && GatewayUsage is null &&
        WatchedServices is null && SshUserName is null && SshCredential is null && SessionLogging is null &&
        IconColour is null;

    public ConnectionDefaults Clone()
    {
        var copy = (ConnectionDefaults)MemberwiseClone();
        copy.Credential = Credential?.Clone();
        copy.SshCredential = SshCredential?.Clone();
        copy.WatchedServices = WatchedServices is null ? null : new List<string>(WatchedServices);
        return copy;
    }
}

public sealed class ConnectionFolder
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public ConnectionDefaults Defaults { get; set; } = new();
    public List<ConnectionFolder> Folders { get; set; } = new();
    public List<ConnectionEntry> Connections { get; set; } = new();

    public ConnectionFolder Clone()
    {
        return new ConnectionFolder
        {
            Name = Name,
            Description = Description,
            Defaults = Defaults.Clone(),
            Folders = Folders.Select(f => f.Clone()).ToList(),
            Connections = Connections.Select(c => c.Clone()).ToList(),
        };
    }
}
