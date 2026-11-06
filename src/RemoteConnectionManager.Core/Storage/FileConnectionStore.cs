using System.Text.Json;
using RemoteConnectionManager.Core.Models;
using RemoteConnectionManager.Core.Services;

namespace RemoteConnectionManager.Core.Storage;

/// <summary>
/// Loads and saves connections.json. Saves are atomic (write temp, replace). A file watcher
/// raises <see cref="ExternalChange"/> when something other than this store edits the file.
/// </summary>
public sealed class FileConnectionStore : IConnectionStore
{
    private readonly object _gate = new();
    private FileSystemWatcher? _watcher;
    private DateTime _lastOwnWriteUtc;
    private System.Threading.Timer? _debounce;

    public string Path { get; private set; }

    /// <summary>Raised off the UI thread after the file changes on disk by another process or editor.</summary>
    public event EventHandler? ExternalChange;

    /// <summary>A file is never read only, so this is never raised.</summary>
    public event EventHandler? StatusChanged { add { } remove { } }

    public FileConnectionStore(string path)
    {
        Path = path;
    }

    public void SetPath(string path)
    {
        if (string.Equals(path, Path, StringComparison.OrdinalIgnoreCase)) return;
        Path = path;
        StartWatching();
    }

    public bool Exists => File.Exists(Path);

    public StorageKind Kind => StorageKind.File;
    public string DisplayName => System.IO.Path.GetFileName(Path);
    public string Location => Path;
    public bool IsReadOnly => false;
    public string? ReadOnlyReason => null;
    public bool SupportsPermissions => false;
    public string? SignedInAs => null;
    public bool IsAdmin => true;

    /// <summary>A file has no permissions of its own: whoever can write the file can change everything.</summary>
    public AccessLevel AccessFor(Guid? folderId) => AccessLevel.Manage;

    public ConnectionsDocument Load()
    {
        if (!File.Exists(Path))
        {
            var fresh = CreateStarterDocument();
            Save(fresh);
            return fresh;
        }

        var json = File.ReadAllText(Path);
        var doc = JsonSerializer.Deserialize<ConnectionsDocument>(json, JsonDefaults.Options)
                  ?? throw new InvalidDataException($"{Path} is empty or not a connections document.");
        if (doc.Version > ConnectionsDocument.CurrentVersion)
            throw new InvalidDataException($"{Path} is version {doc.Version}; this build understands up to {ConnectionsDocument.CurrentVersion}.");
        return doc;
    }

    public void Save(ConnectionsDocument document)
    {
        lock (_gate)
        {
            var dir = System.IO.Path.GetDirectoryName(Path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(document, JsonDefaults.Options);
            var temp = Path + ".tmp";
            File.WriteAllText(temp, json);
            if (File.Exists(Path))
                File.Replace(temp, Path, Path + ".bak", ignoreMetadataErrors: true);
            else
                File.Move(temp, Path);
            _lastOwnWriteUtc = DateTime.UtcNow;
        }
    }

    public void StartWatching()
    {
        _watcher?.Dispose();
        _watcher = null;
        var dir = System.IO.Path.GetDirectoryName(Path);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;

        _watcher = new FileSystemWatcher(dir, System.IO.Path.GetFileName(Path))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
        };
        _watcher.Changed += OnFileEvent;
        _watcher.Created += OnFileEvent;
        _watcher.Renamed += OnFileEvent;
        _watcher.EnableRaisingEvents = true;
    }

    private void OnFileEvent(object sender, FileSystemEventArgs e)
    {
        // Ignore the echo of our own save (File.Replace fires a couple of events).
        if ((DateTime.UtcNow - _lastOwnWriteUtc) < TimeSpan.FromSeconds(2)) return;
        _debounce?.Dispose();
        _debounce = new System.Threading.Timer(_ => ExternalChange?.Invoke(this, EventArgs.Empty), null, 500, Timeout.Infinite);
    }

    public static ConnectionsDocument CreateStarterDocument()
    {
        return new ConnectionsDocument
        {
            Folders =
            {
                new ConnectionFolder
                {
                    Name = "Examples",
                    Description = "Delete this folder once you have your own entries.",
                    Defaults = new ConnectionDefaults { KeyboardPassThrough = true },
                    Connections =
                    {
                        new RdpConnection { Name = "Example server", Host = "server01.example.local", Description = "Edit me: a plain RDP entry that connects as the logged on user." },
                        new TerminalConnection { Name = "Azure PowerShell", Shell = TerminalShell.Pwsh, StartScript = "Import-Module Az.Accounts; Connect-AzAccount", Description = "Terminals arrive in phase two." },
                    },
                },
            },
        };
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _debounce?.Dispose();
    }
}
