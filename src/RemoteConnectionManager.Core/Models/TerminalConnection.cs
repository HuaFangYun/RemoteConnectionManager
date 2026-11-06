using System.Text.Json.Serialization;

namespace RemoteConnectionManager.Core.Models;

public enum TerminalShell
{
    Pwsh,
    WindowsPowerShell,
    Cmd,
}

/// <summary>A local shell. Phase two wires it to the ConPTY terminal; phase one only stores it.</summary>
public sealed class TerminalConnection : ConnectionEntry
{
    public TerminalShell Shell { get; set; } = TerminalShell.Pwsh;
    public string? WorkingDirectory { get; set; }
    public string? Arguments { get; set; }
    /// <summary>Script run after the shell starts, for example module imports and Connect-AzAccount.</summary>
    public string? StartScript { get; set; }
    /// <summary>Null inherits from the folder chain, then the app setting.</summary>
    public bool? SessionLogging { get; set; }

    [JsonIgnore]
    public override ConnectionKind Kind => ConnectionKind.Terminal;
    [JsonIgnore]
    public override string Prefix => Shell switch
    {
        TerminalShell.Pwsh => "(PWSH)",
        TerminalShell.WindowsPowerShell => "(PS)",
        _ => "(CMD)",
    };

    public override ConnectionEntry Clone()
    {
        var copy = new TerminalConnection
        {
            Shell = Shell,
            WorkingDirectory = WorkingDirectory,
            Arguments = Arguments,
            StartScript = StartScript,
            SessionLogging = SessionLogging,
        };
        CopyBaseTo(copy);
        return copy;
    }
}
