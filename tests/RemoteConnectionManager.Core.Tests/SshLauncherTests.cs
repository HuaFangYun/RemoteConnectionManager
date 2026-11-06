using System.Text.Json;
using RemoteConnectionManager.Core.Models;
using RemoteConnectionManager.Core.Services;
using Xunit;

namespace RemoteConnectionManager.Core.Tests;

public class SshLauncherTests
{
    private static TerminalLaunchSpec Build(SshConnection entry, SshAlgorithms? supported = null) =>
        SshLauncher.Build(entry, SettingsResolver.Resolve(entry, [], new ConnectionDefaults()), supported);

    [Fact]
    public void PlainHostIsJustSshAndTheHost()
    {
        var spec = Build(new SshConnection { Name = "sw", Host = "10.22.22.1" });
        Assert.Equal("ssh.exe 10.22.22.1", spec.CommandLine);
        Assert.Equal("SSH", spec.ShellDisplayName);
        Assert.Null(spec.StartScript);
    }

    [Fact]
    public void PortUserKeyAndJumpHost()
    {
        var spec = Build(new SshConnection
        {
            Name = "sw", Host = "core-sw", Port = 2222, UserName = "net admin",
            IdentityFile = @"C:\Keys\my key", JumpHost = "admin@bastion", Arguments = "-o ServerAliveInterval=30",
        });
        Assert.Equal(@"ssh.exe -p 2222 -l ""net admin"" -i ""C:\Keys\my key"" -J admin@bastion -o ServerAliveInterval=30 core-sw", spec.CommandLine);
    }

    [Fact]
    public void LegacyAlgorithmsKeepToWhatSshSupports()
    {
        var supported = new SshAlgorithms(
            Kex: new HashSet<string> { "diffie-hellman-group14-sha1", "curve25519-sha256" },
            HostKeys: new HashSet<string> { "ssh-rsa", "ssh-ed25519" },
            Ciphers: new HashSet<string> { "aes128-cbc", "aes256-cbc", "aes128-ctr" },
            Macs: new HashSet<string> { "hmac-sha1" });
        var spec = Build(new SshConnection { Name = "old", Host = "sw", LegacyAlgorithms = true }, supported);

        Assert.Contains("-o KexAlgorithms=+diffie-hellman-group14-sha1 ", spec.CommandLine);
        Assert.Contains("-o HostKeyAlgorithms=+ssh-rsa ", spec.CommandLine);
        Assert.Contains("-o PubkeyAcceptedAlgorithms=+ssh-rsa ", spec.CommandLine);
        Assert.Contains("-o Ciphers=+aes128-cbc,aes256-cbc ", spec.CommandLine);
        Assert.Contains("-o MACs=+hmac-sha1 ", spec.CommandLine);
        Assert.DoesNotContain("group1-sha1", spec.CommandLine);
        Assert.DoesNotContain("ssh-dss", spec.CommandLine);
        Assert.EndsWith(" sw", spec.CommandLine);
    }

    [Fact]
    public void LegacyWithoutAQueryLeavesOutDss()
    {
        var spec = Build(new SshConnection { Name = "old", Host = "sw", LegacyAlgorithms = true });
        Assert.Contains("HostKeyAlgorithms=+ssh-rsa ", spec.CommandLine);
        Assert.DoesNotContain("ssh-dss", spec.CommandLine);
        Assert.Contains("diffie-hellman-group1-sha1", spec.CommandLine);
    }

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("two words", "\"two words\"")]
    [InlineData("say \"hi\"", "\"say \\\"hi\\\"\"")]
    [InlineData(@"C:\dir with space\", @"""C:\dir with space\\""")]
    [InlineData("", "\"\"")]
    public void QuoteMatchesCommandLineToArgv(string value, string expected) =>
        Assert.Equal(expected, SshLauncher.Quote(value));

    [Fact]
    public void ParsesSshQueryOutput() =>
        Assert.Equal(new[] { "a", "b" }, SshLauncher.ParseQuery("a\r\nb\r\n\r\n").Order());

    [Fact]
    public void UserNameAndCredentialInheritFromFolders()
    {
        var vault = new CredentialReference { Source = CredentialSource.KeyVault, VaultUri = "https://kv.vault.azure.net/", SecretName = "switch-admin" };
        var outer = new ConnectionFolder { Name = "Network", Defaults = { SshUserName = "netadmin", SshCredential = vault, UserName = "rdp-user" } };
        var inner = new ConnectionFolder { Name = "Site A" };
        var entry = new SshConnection { Name = "sw", Host = "sw01" };

        var s = SettingsResolver.Resolve(entry, [outer, inner], new ConnectionDefaults());
        Assert.Equal("netadmin", s.UserName); // the SSH name, never the RDP one
        Assert.Equal("switch-admin", s.VaultCredential!.SecretName);
        Assert.Equal(22, s.Port);
        Assert.Equal("netadmin@sw01", s.DisplayTarget);

        // Source Windows on the entry opts out of the inherited vault credential.
        entry.Credential = CredentialReference.Windows();
        entry.UserName = "local";
        s = SettingsResolver.Resolve(entry, [outer, inner], new ConnectionDefaults());
        Assert.Null(s.VaultCredential);
        Assert.Equal("local", s.UserName);
    }

    [Fact]
    public void TelnetDefaultsToPort23()
    {
        var entry = new SshConnection { Name = "t", Host = "sw", Protocol = TerminalProtocol.Telnet };
        Assert.Equal(23, entry.EffectivePort);
        Assert.Equal("(TELNET)", entry.Prefix);
        Assert.Equal("sw:2323", SettingsResolver.Resolve(new SshConnection { Name = "t", Host = "sw", Protocol = TerminalProtocol.Telnet, Port = 2323 }, [], new()).DisplayTarget);
    }

    [Fact]
    public void SessionLoggingResolvesEntryThenFoldersThenApp()
    {
        var folder = new ConnectionFolder { Name = "Scratch" };
        var entry = new TerminalConnection { Name = "t" };
        Assert.True(SettingsResolver.ResolveSessionLogging(entry, [folder], new(), appDefault: true));

        folder.Defaults.SessionLogging = false;
        Assert.False(SettingsResolver.ResolveSessionLogging(entry, [folder], new(), appDefault: true));

        entry.SessionLogging = true;
        Assert.True(SettingsResolver.ResolveSessionLogging(entry, [folder], new(), appDefault: false));

        Assert.False(SettingsResolver.ResolveSessionLogging(new RdpConnection { Name = "r" }, [], new(), appDefault: true));
    }

    [Theory]
    [InlineData("fw01.contoso.local", "https://fw01.contoso.local/")]
    [InlineData("http://10.0.11.10/reports/Pages/Folder.aspx", "http://10.0.11.10/reports/Pages/Folder.aspx")]
    [InlineData("https://ilo01:8443", "https://ilo01:8443/")]
    [InlineData("javascript:alert(1)", null)]
    [InlineData("file:///c:/windows", null)]
    [InlineData("", null)]
    public void WebAddressesNormalise(string input, string? expected) =>
        Assert.Equal(expected, WebConnection.Normalise(input)?.ToString());

    [Fact]
    public void NewEntryTypesRoundTripThroughJson()
    {
        var doc = new ConnectionsDocument();
        doc.Connections.Add(new SshConnection
        {
            Name = "sw", Host = "10.22.22.1", Protocol = TerminalProtocol.Telnet, Port = 2323, LegacyAlgorithms = true,
            Credential = new CredentialReference { Source = CredentialSource.OnePassword, OnePasswordItem = "switch" }, SessionLogging = false,
        });
        doc.Connections.Add(new WebConnection { Name = "fw", Url = "https://fw01", OpenIn = WebOpenMode.Tab });
        doc.Folders.Add(new ConnectionFolder { Name = "Net", Defaults = { SshUserName = "netadmin", SessionLogging = true } });

        var json = JsonSerializer.Serialize(doc, JsonDefaults.Options);
        Assert.Contains("\"type\": \"ssh\"", json);
        Assert.Contains("\"type\": \"web\"", json);
        Assert.DoesNotContain("\"kind\"", json);
        Assert.DoesNotContain("\"prefix\"", json);
        Assert.DoesNotContain("effectivePort", json);

        var back = JsonSerializer.Deserialize<ConnectionsDocument>(json, JsonDefaults.Options)!;
        var ssh = Assert.IsType<SshConnection>(back.Connections[0]);
        Assert.Equal(TerminalProtocol.Telnet, ssh.Protocol);
        Assert.Equal(2323, ssh.Port);
        Assert.True(ssh.LegacyAlgorithms);
        Assert.False(ssh.SessionLogging);
        Assert.Equal("switch", ssh.Credential!.OnePasswordItem);
        var web = Assert.IsType<WebConnection>(back.Connections[1]);
        Assert.Equal(WebOpenMode.Tab, web.OpenIn);
        Assert.Equal("netadmin", back.Folders[0].Defaults.SshUserName);
        Assert.False(back.Folders[0].Defaults.IsEmpty);

        var copy = Assert.IsType<SshConnection>(ssh.Clone());
        Assert.NotEqual(ssh.Id, copy.Id);
        Assert.NotSame(ssh.Credential, copy.Credential);
        Assert.Equal(ssh.Host, copy.Host);
    }
}
