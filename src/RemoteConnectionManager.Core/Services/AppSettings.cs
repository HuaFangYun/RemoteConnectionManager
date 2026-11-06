using System.Text.Json;
using System.Text.Json.Serialization;
using RemoteConnectionManager.Core.Models;
using RemoteConnectionManager.Core.Storage;

namespace RemoteConnectionManager.Core.Services;

/// <summary>User level settings. Separate from connections so the connections file can be synced or shared.</summary>
public sealed class AppSettings
{
    /// <summary>Local file or SQL Server. Kept per machine.</summary>
    public StorageKind Storage { get; set; } = StorageKind.File;
    /// <summary>Database to use when <see cref="Storage"/> is SQL Server.</summary>
    public SqlStorageSettings Sql { get; set; } = new();
    /// <summary>Personal settings on entries and folders when the file back end is used. SQL Server keeps them per user in the database.</summary>
    public Dictionary<Guid, PersonalEntrySettings> PersonalEntrySettings { get; set; } = new();

    /// <summary>Null means the default file under %APPDATA%.</summary>
    public string? ConnectionsFilePath { get; set; }
    public double NavigationWidth { get; set; } = 300;
    public bool NavigationVisible { get; set; } = true;
    public List<Guid> ExpandedFolders { get; set; } = new();
    /// <summary>Ids of plugins switched off in Settings, for example "onepassword".</summary>
    public List<string> DisabledPlugins { get; set; } = new();
    /// <summary>Full path to op.exe when it is not in a usual place. Null searches for it.</summary>
    public string? OnePasswordCliPath { get; set; }
    /// <summary>Terminal, SSH and telnet session logs.</summary>
    public SessionLogSettings SessionLogs { get; set; } = new();

    [JsonIgnore]
    public string EffectiveConnectionsFile =>
        string.IsNullOrWhiteSpace(ConnectionsFilePath) ? AppPaths.DefaultConnectionsFile : ConnectionsFilePath;
}

/// <summary>How to reach the shared database. No passwords: Windows or Microsoft Entra sign in only.</summary>
public sealed class SqlStorageSettings
{
    public string Server { get; set; } = string.Empty;
    public string Database { get; set; } = "RemoteConnectionManager";
    public SqlAuthentication Authentication { get; set; } = SqlAuthentication.Windows;
    /// <summary>Accept the server certificate without checking it. Only for servers with a self signed certificate.</summary>
    public bool TrustServerCertificate { get; set; }
    /// <summary>How often RCM checks the database for other people's changes and for a lost connection, in seconds.</summary>
    public int PollSeconds { get; set; } = DefaultPollSeconds;

    public const int DefaultPollSeconds = 60;
    public const int MinPollSeconds = 10;
    public const int MaxPollSeconds = 3600;

    /// <summary><see cref="PollSeconds"/> kept within <see cref="MinPollSeconds"/> and <see cref="MaxPollSeconds"/>, so a hand-edited settings file cannot set 0.</summary>
    [JsonIgnore]
    public TimeSpan PollInterval => TimeSpan.FromSeconds(Math.Clamp(PollSeconds, MinPollSeconds, MaxPollSeconds));

    [JsonIgnore]
    public bool IsConfigured => !string.IsNullOrWhiteSpace(Server) && !string.IsNullOrWhiteSpace(Database);

    public SqlStorageSettings Clone() => (SqlStorageSettings)MemberwiseClone();
}

/// <summary>
/// Plain text logs of terminal, SSH and telnet sessions, for looking back at what was done.
/// Kept on this PC only, never in the shared database.
/// </summary>
public sealed class SessionLogSettings
{
    /// <summary>The default for entries and folders that do not say. On unless switched off.</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>Null means %LOCALAPPDATA%\RemoteConnectionManager\session-logs.</summary>
    public string? Folder { get; set; }
    /// <summary>Logs older than this are deleted at start up. 0 keeps them forever.</summary>
    public int KeepDays { get; set; } = DefaultKeepDays;
    /// <summary>Mask passwords, secrets, community strings and keys before they reach the file.</summary>
    public bool Redact { get; set; } = true;
    /// <summary>Also keep the raw output, colours and all, in a .vt file next to the text log.</summary>
    public bool KeepRawCopy { get; set; }
    /// <summary>A log over this size is closed and a new part started.</summary>
    public int MaxFileMegabytes { get; set; } = DefaultMaxFileMegabytes;

    public const int DefaultKeepDays = 90;
    public const int DefaultMaxFileMegabytes = 50;

    [JsonIgnore]
    public string EffectiveFolder => string.IsNullOrWhiteSpace(Folder) ? AppPaths.SessionLogDirectory : Environment.ExpandEnvironmentVariables(Folder.Trim());

    public SessionLogSettings Clone() => (SessionLogSettings)MemberwiseClone();
}

public enum SqlAuthentication
{
    /// <summary>Integrated security as the logged on Windows user (Kerberos). For on premises SQL Server.</summary>
    Windows,
    /// <summary>Microsoft Entra, using the signed in account (Active Directory Default). For Azure SQL.</summary>
    Entra,
    /// <summary>Microsoft Entra with an interactive sign in window.</summary>
    EntraInteractive,
}

public sealed class AppSettingsStore
{
    public string Path { get; }

    public AppSettingsStore(string? path = null)
    {
        Path = path ?? AppPaths.SettingsFile;
    }

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(Path)) return new AppSettings();
            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Path), JsonDefaults.Options) ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        var dir = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(Path, JsonSerializer.Serialize(settings, JsonDefaults.Options));
    }
}
