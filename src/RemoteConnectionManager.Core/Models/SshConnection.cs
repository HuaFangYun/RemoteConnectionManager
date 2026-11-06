using System.Text.Json.Serialization;

namespace RemoteConnectionManager.Core.Models;

public enum TerminalProtocol
{
    Ssh,
    Telnet,
}

/// <summary>
/// A router, switch or Linux host reached over SSH (Windows' own ssh.exe in a terminal tab) or
/// telnet (RCM's built in client). Never stores a password: SSH uses keys or the prompt, and an
/// optional vault <see cref="Credential"/> is only read when the user presses Send password.
/// Nullable settings inherit from the folder chain.
/// </summary>
public sealed class SshConnection : ConnectionEntry
{
    public const int DefaultSshPort = 22;
    public const int DefaultTelnetPort = 23;

    public TerminalProtocol Protocol { get; set; } = TerminalProtocol.Ssh;
    public string Host { get; set; } = string.Empty;
    /// <summary>Null means 22 for SSH, 23 for telnet.</summary>
    public int? Port { get; set; }
    /// <summary>Null inherits the folder's SSH user name. Blank all the way up lets ssh use the Windows user name.</summary>
    public string? UserName { get; set; }
    /// <summary>Private key file passed to ssh -i. Environment variables are expanded. SSH only.</summary>
    public string? IdentityFile { get; set; }
    /// <summary>Jump host for ssh -J, for example admin@bastion. SSH only.</summary>
    public string? JumpHost { get; set; }
    /// <summary>Switches on old key exchange, host key, cipher and MAC algorithms that stock OpenSSH turns off. For older switches and routers.</summary>
    public bool LegacyAlgorithms { get; set; }
    /// <summary>Extra ssh.exe arguments, placed before the host.</summary>
    public string? Arguments { get; set; }
    /// <summary>Vault credential for the Send password button. Null inherits; Source Windows means none (type it at the prompt).</summary>
    public CredentialReference? Credential { get; set; }
    /// <summary>Null inherits from the folder chain, then the app setting.</summary>
    public bool? SessionLogging { get; set; }

    [JsonIgnore]
    public override ConnectionKind Kind => ConnectionKind.Ssh;
    [JsonIgnore]
    public override string Prefix => Protocol == TerminalProtocol.Telnet ? "(TELNET)" : "(SSH)";

    [JsonIgnore]
    public int EffectivePort => Port is > 0 ? Port.Value : Protocol == TerminalProtocol.Telnet ? DefaultTelnetPort : DefaultSshPort;

    public override ConnectionEntry Clone()
    {
        var copy = new SshConnection
        {
            Protocol = Protocol,
            Host = Host,
            Port = Port,
            UserName = UserName,
            IdentityFile = IdentityFile,
            JumpHost = JumpHost,
            LegacyAlgorithms = LegacyAlgorithms,
            Arguments = Arguments,
            Credential = Credential?.Clone(),
            SessionLogging = SessionLogging,
        };
        CopyBaseTo(copy);
        return copy;
    }
}
