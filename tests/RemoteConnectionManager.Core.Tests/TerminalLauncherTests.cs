using RemoteConnectionManager.Core.Models;
using RemoteConnectionManager.Core.Services;
using Xunit;

namespace RemoteConnectionManager.Core.Tests;

public class TerminalLauncherTests
{
    [Fact]
    public void PwshWithoutScriptIsJustTheShell()
    {
        var spec = TerminalLauncher.Build(new TerminalConnection { Name = "t", Shell = TerminalShell.Pwsh });
        Assert.Equal("pwsh.exe -NoLogo", spec.CommandLine);
        Assert.Equal("pwsh.exe", spec.Executable);
        Assert.Null(spec.StartScript);
        Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), spec.WorkingDirectory);
    }

    [Fact]
    public void PwshScriptIsEncodedAndRoundTrips()
    {
        var script = "Import-Module Az\r\nConnect-AzAccount -Subscription 'Client \"A\" Prod'\r\n$x = 1\r\n";
        var spec = TerminalLauncher.Build(new TerminalConnection { Name = "t", Shell = TerminalShell.Pwsh, StartScript = script });

        Assert.StartsWith("pwsh.exe -NoLogo -NoExit -EncodedCommand ", spec.CommandLine);
        var encoded = spec.CommandLine.Split(' ')[^1];
        Assert.Equal("Import-Module Az\nConnect-AzAccount -Subscription 'Client \"A\" Prod'\n$x = 1", TerminalLauncher.DecodeFromPowerShell(encoded));
        Assert.Equal(spec.StartScript, TerminalLauncher.DecodeFromPowerShell(encoded));
    }

    [Fact]
    public void WindowsPowerShellUsesPowershellExe()
    {
        var spec = TerminalLauncher.Build(new TerminalConnection { Name = "t", Shell = TerminalShell.WindowsPowerShell, StartScript = "Get-Date" });
        Assert.StartsWith("powershell.exe -NoLogo -NoExit -EncodedCommand ", spec.CommandLine);
    }

    [Fact]
    public void ArgumentsGoBeforeTheScript()
    {
        var spec = TerminalLauncher.Build(new TerminalConnection { Name = "t", Shell = TerminalShell.Pwsh, Arguments = "-NoProfile", StartScript = "Get-Date" });
        Assert.StartsWith("pwsh.exe -NoLogo -NoProfile -NoExit -EncodedCommand ", spec.CommandLine);
    }

    [Fact]
    public void CmdJoinsLinesWithAmpersand()
    {
        var spec = TerminalLauncher.Build(new TerminalConnection { Name = "t", Shell = TerminalShell.Cmd, StartScript = "  cd /d C:\\work \r\n\r\n dir \r\n" });
        Assert.Equal("cmd.exe /S /K \"cd /d C:\\work & dir\"", spec.CommandLine);
    }

    [Fact]
    public void CmdWithoutScriptIsBare()
    {
        var spec = TerminalLauncher.Build(new TerminalConnection { Name = "t", Shell = TerminalShell.Cmd, Arguments = "/V:ON" });
        Assert.Equal("cmd.exe /V:ON", spec.CommandLine);
    }

    [Fact]
    public void BlankScriptCountsAsNone()
    {
        var spec = TerminalLauncher.Build(new TerminalConnection { Name = "t", Shell = TerminalShell.Pwsh, StartScript = " \r\n \n" });
        Assert.Equal("pwsh.exe -NoLogo", spec.CommandLine);
        Assert.Null(spec.StartScript);
    }

    [Fact]
    public void WorkingDirectoryExpandsEnvironmentVariables()
    {
        var spec = TerminalLauncher.Build(new TerminalConnection { Name = "t", WorkingDirectory = "%SystemRoot%\\System32" });
        Assert.Equal(Path.Combine(Environment.GetEnvironmentVariable("SystemRoot")!, "System32"), spec.WorkingDirectory);
    }

    [Fact]
    public void ExecutablePathReplacesTheBareName()
    {
        var spec = TerminalLauncher.Build(new TerminalConnection { Name = "t", Shell = TerminalShell.Pwsh, Arguments = "-NoProfile" });
        var resolved = spec.WithExecutablePath(@"C:\Program Files\PowerShell\7\pwsh.exe");
        Assert.Equal("\"C:\\Program Files\\PowerShell\\7\\pwsh.exe\" -NoLogo -NoProfile", resolved.CommandLine);
        Assert.Equal("pwsh.exe", resolved.Executable);
    }

    [Fact]
    public void BuiltInShellsAreFound()
    {
        Assert.NotNull(TerminalLauncher.FindExecutable(TerminalShell.Cmd));
        Assert.NotNull(TerminalLauncher.FindExecutable(TerminalShell.WindowsPowerShell));
    }
}
