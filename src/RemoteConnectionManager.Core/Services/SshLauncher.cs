using System.Text;
using RemoteConnectionManager.Core.Models;

namespace RemoteConnectionManager.Core.Services;

/// <summary>What the local ssh.exe supports, from <c>ssh -Q</c>. Used to keep the legacy list to names it accepts.</summary>
public sealed record SshAlgorithms(
    IReadOnlySet<string> Kex,
    IReadOnlySet<string> HostKeys,
    IReadOnlySet<string> Ciphers,
    IReadOnlySet<string> Macs);

/// <summary>
/// Builds the ssh.exe command line for an SSH entry. Windows' own OpenSSH client runs in the
/// ConPTY terminal tab, so host key checks, known_hosts, keys, ssh-agent and the password prompt
/// all behave exactly as they do in a normal console.
/// </summary>
public static class SshLauncher
{
    public const string ExecutableName = "ssh.exe";

    // Older Cisco, HP and Dell switches only offer these. Stock OpenSSH has them compiled in but off.
    internal static readonly string[] LegacyKex = ["diffie-hellman-group14-sha1", "diffie-hellman-group-exchange-sha1", "diffie-hellman-group1-sha1"];
    internal static readonly string[] LegacyHostKeys = ["ssh-rsa", "ssh-dss"];
    internal static readonly string[] LegacyCiphers = ["aes128-cbc", "aes192-cbc", "aes256-cbc", "3des-cbc"];
    internal static readonly string[] LegacyMacs = ["hmac-sha1", "hmac-sha1-96", "hmac-md5"];

    /// <summary>
    /// The command line, starting with the bare executable name. Pass <paramref name="supported"/>
    /// from <c>ssh -Q</c> so names this ssh.exe does not know are left out (OpenSSH stops with
    /// "Bad key types" otherwise). Without it, ssh-dss is left out because OpenSSH 10 can drop it.
    /// </summary>
    public static TerminalLaunchSpec Build(SshConnection entry, EffectiveSshSettings settings, SshAlgorithms? supported = null)
    {
        var sb = new StringBuilder(ExecutableName);
        if (settings.Port != SshConnection.DefaultSshPort) sb.Append(" -p ").Append(settings.Port);
        if (settings.UserName is { } user) sb.Append(" -l ").Append(Quote(user));
        if (!string.IsNullOrWhiteSpace(entry.IdentityFile))
            sb.Append(" -i ").Append(Quote(Environment.ExpandEnvironmentVariables(entry.IdentityFile.Trim())));
        if (!string.IsNullOrWhiteSpace(entry.JumpHost)) sb.Append(" -J ").Append(Quote(entry.JumpHost.Trim()));

        if (entry.LegacyAlgorithms)
        {
            AppendAlgorithms(sb, "KexAlgorithms", LegacyKex, supported?.Kex);
            var hostKeys = Filter(LegacyHostKeys, supported?.HostKeys);
            AppendList(sb, "HostKeyAlgorithms", hostKeys);
            // Old devices with an RSA user key also need ssh-rsa signatures accepted for public key sign in.
            AppendList(sb, "PubkeyAcceptedAlgorithms", hostKeys.Where(k => k == "ssh-rsa").ToArray());
            AppendAlgorithms(sb, "Ciphers", LegacyCiphers, supported?.Ciphers);
            AppendAlgorithms(sb, "MACs", LegacyMacs, supported?.Macs);
        }

        if (!string.IsNullOrWhiteSpace(entry.Arguments)) sb.Append(' ').Append(entry.Arguments.Trim());
        sb.Append(' ').Append(Quote(settings.Host));

        return new TerminalLaunchSpec(TerminalShell.Cmd, ExecutableName, sb.ToString(),
            TerminalLauncher.ResolveWorkingDirectory(null), StartScript: null)
        {
            Title = "SSH",
        };
    }

    private static void AppendAlgorithms(StringBuilder sb, string option, string[] legacy, IReadOnlySet<string>? supported) =>
        AppendList(sb, option, Filter(legacy, supported));

    private static string[] Filter(string[] legacy, IReadOnlySet<string>? supported) =>
        supported is null
            ? legacy.Where(n => n != "ssh-dss").ToArray()
            : legacy.Where(supported.Contains).ToArray();

    private static void AppendList(StringBuilder sb, string option, string[] names)
    {
        if (names.Length == 0) return;
        sb.Append(" -o ").Append(option).Append("=+").Append(string.Join(',', names));
    }

    /// <summary>ssh.exe in Windows' OpenSSH folder first (not Git's copy), then PATH. Null when there is none.</summary>
    public static string? FindExecutable()
    {
        var system = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "OpenSSH", ExecutableName);
        if (File.Exists(system)) return system;
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var candidate = Path.Combine(dir, ExecutableName);
                if (File.Exists(candidate)) return candidate;
            }
            catch { /* malformed PATH entry */ }
        }
        return null;
    }

    /// <summary>Parses <c>ssh -Q</c> output: one name per line.</summary>
    public static IReadOnlySet<string> ParseQuery(string output) =>
        output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.Ordinal);

    /// <summary>Quotes one argument for CreateProcess the way CommandLineToArgvW reads it back.</summary>
    public static string Quote(string value)
    {
        if (value.Length > 0 && value.IndexOfAny([' ', '\t', '"']) < 0) return value;
        var sb = new StringBuilder("\"");
        var backslashes = 0;
        foreach (var c in value)
        {
            if (c == '\\') { backslashes++; continue; }
            if (c == '"') sb.Append('\\', backslashes * 2 + 1).Append('"');
            else sb.Append('\\', backslashes).Append(c);
            backslashes = 0;
        }
        sb.Append('\\', backslashes * 2).Append('"');
        return sb.ToString();
    }
}
