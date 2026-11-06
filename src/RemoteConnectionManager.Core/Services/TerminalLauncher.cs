using System.Text;
using RemoteConnectionManager.Core.Models;

namespace RemoteConnectionManager.Core.Services;

/// <summary>
/// Everything the terminal host needs to start a shell: the full command line for CreateProcess,
/// the working directory, and a human readable summary for the dashboard and log.
/// </summary>
public sealed record TerminalLaunchSpec(
    TerminalShell Shell,
    string Executable,
    string CommandLine,
    string WorkingDirectory,
    string? StartScript)
{
    /// <summary>Overrides the shell's name in the toolbar and dashboard, for example "SSH".</summary>
    public string? Title { get; init; }

    public string ShellDisplayName => Title ?? TerminalLauncher.DisplayName(Shell);

    /// <summary>Swaps the bare executable name at the front of the command line for a full, quoted path.</summary>
    public TerminalLaunchSpec WithExecutablePath(string fullPath)
    {
        var rest = CommandLine.StartsWith(Executable, StringComparison.OrdinalIgnoreCase) ? CommandLine[Executable.Length..] : " " + CommandLine;
        return this with { CommandLine = $"\"{fullPath}\"{rest}" };
    }
}

/// <summary>
/// Turns a <see cref="TerminalConnection"/> into a command line. The start script is passed to
/// PowerShell as an encoded command so quoting, newlines and <c>$</c> never need escaping; cmd gets
/// its lines joined with <c>&amp;</c> because it has no equivalent.
/// </summary>
public static class TerminalLauncher
{
    public static string DisplayName(TerminalShell shell) => shell switch
    {
        TerminalShell.Pwsh => "PowerShell 7",
        TerminalShell.WindowsPowerShell => "Windows PowerShell",
        _ => "Command Prompt",
    };

    public static string ExecutableName(TerminalShell shell) => shell switch
    {
        TerminalShell.Pwsh => "pwsh.exe",
        TerminalShell.WindowsPowerShell => "powershell.exe",
        _ => "cmd.exe",
    };

    public static TerminalLaunchSpec Build(TerminalConnection entry)
    {
        var exe = ExecutableName(entry.Shell);
        var args = entry.Arguments?.Trim();
        var script = NormaliseScript(entry.StartScript);
        var sb = new StringBuilder(exe);

        switch (entry.Shell)
        {
            case TerminalShell.Pwsh:
            case TerminalShell.WindowsPowerShell:
                sb.Append(" -NoLogo");
                if (!string.IsNullOrEmpty(args)) sb.Append(' ').Append(args);
                if (script is not null)
                    sb.Append(" -NoExit -EncodedCommand ").Append(EncodeForPowerShell(script));
                break;

            case TerminalShell.Cmd:
                if (!string.IsNullOrEmpty(args)) sb.Append(' ').Append(args);
                if (script is not null)
                    sb.Append(" /S /K \"").Append(JoinForCmd(script)).Append('"');
                break;
        }

        return new TerminalLaunchSpec(entry.Shell, exe, sb.ToString(), ResolveWorkingDirectory(entry.WorkingDirectory), script);
    }

    /// <summary>Blank means the user's profile folder. Environment variables such as %USERPROFILE% are expanded.</summary>
    public static string ResolveWorkingDirectory(string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured))
            return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Environment.ExpandEnvironmentVariables(configured.Trim());
    }

    /// <summary>Looks in the usual install folders then PATH. Null when the shell is not installed (pwsh is the usual miss).</summary>
    public static string? FindExecutable(TerminalShell shell)
    {
        var name = ExecutableName(shell);
        var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var wellKnown = shell switch
        {
            TerminalShell.Cmd => new[] { Path.Combine(system, name) },
            TerminalShell.WindowsPowerShell => new[] { Path.Combine(system, "WindowsPowerShell", "v1.0", name) },
            _ => new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7", name),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", name),
            },
        };
        foreach (var candidate in wellKnown)
            if (File.Exists(candidate)) return candidate;

        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var candidate = Path.Combine(dir, name);
                if (File.Exists(candidate)) return candidate;
            }
            catch { /* malformed PATH entry */ }
        }
        return null;
    }

    public static string EncodeForPowerShell(string script) =>
        Convert.ToBase64String(Encoding.Unicode.GetBytes(script));

    public static string DecodeFromPowerShell(string encoded) =>
        Encoding.Unicode.GetString(Convert.FromBase64String(encoded));

    /// <summary>Trims trailing spaces, drops leading and trailing blank lines, normalises line endings. Null when nothing is left.</summary>
    internal static string? NormaliseScript(string? script)
    {
        if (string.IsNullOrWhiteSpace(script)) return null;
        var lines = script.Replace("\r\n", "\n").Split('\n')
            .Select(l => l.TrimEnd())
            .SkipWhile(string.IsNullOrWhiteSpace)
            .ToList();
        while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[^1])) lines.RemoveAt(lines.Count - 1);
        return lines.Count == 0 ? null : string.Join("\n", lines);
    }

    /// <summary>cmd has no multi line script argument, so each non blank line runs in turn.</summary>
    internal static string JoinForCmd(string script) =>
        string.Join(" & ", script.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0));
}
