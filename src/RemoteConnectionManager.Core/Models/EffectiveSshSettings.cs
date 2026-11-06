namespace RemoteConnectionManager.Core.Models;

/// <summary>An SSH or telnet entry with inheritance applied.</summary>
/// <param name="UserName">Null lets ssh use the Windows user name, or the device prompt for telnet.</param>
/// <param name="VaultCredential">Credential for the Send password button; null when there is none.</param>
public sealed record EffectiveSshSettings(
    TerminalProtocol Protocol,
    string Host,
    int Port,
    string? UserName,
    CredentialReference? VaultCredential)
{
    public string DisplayTarget
    {
        get
        {
            var host = Host.Contains(':') && !Host.StartsWith('[') ? $"[{Host}]" : Host;
            var defaultPort = Protocol == TerminalProtocol.Telnet ? SshConnection.DefaultTelnetPort : SshConnection.DefaultSshPort;
            var target = Port == defaultPort ? Host : $"{host}:{Port}";
            return UserName is null ? target : $"{UserName}@{target}";
        }
    }
}
