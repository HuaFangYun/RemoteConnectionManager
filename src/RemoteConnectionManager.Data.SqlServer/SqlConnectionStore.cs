using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using RemoteConnectionManager.Core.Models;
using RemoteConnectionManager.Core.Services;
using RemoteConnectionManager.Core.Storage;

namespace RemoteConnectionManager.Data.SqlServer;

/// <summary>
/// Keeps the connection tree in SQL Server, one row per folder and entry. The shell still works on
/// a whole document; <see cref="Save"/> compares it with what was last loaded and writes only the
/// rows that changed, in one transaction, each update checking the row version so nobody's change
/// is overwritten. The database decides what the user may see and change.
/// </summary>
public sealed class SqlConnectionStore : IConnectionStore
{
    internal static readonly JsonSerializerOptions Compact = new(JsonDefaults.Options) { WriteIndented = false };

    private readonly SqlConnectionFactory _factory;
    private readonly string? _cachePath;
    private readonly Action<string>? _log;
    private TimeSpan _pollInterval;
    private readonly object _gate = new();

    private Dictionary<Guid, RowState> _folders = new();
    private Dictionary<Guid, RowState> _entries = new();
    private Dictionary<Guid, AccessLevel> _levels = new();
    private string _defaultsJson = "{}";
    private long _stamp;
    private Timer? _poll;
    private int _polling;

    /// <summary>Online; Cached = the tree came from the local cache at load; Lost = loaded from the database, then a check could not reach it.</summary>
    private enum OfflineState { Online, Cached, Lost }
    private volatile OfflineState _offline;
    /// <summary>The read-only reason (if any) from before the connection was lost, put back when it returns.</summary>
    private string? _reasonBeforeLost;
    private DateTime _cacheSavedUtc;

    public StorageKind Kind => StorageKind.SqlServer;
    public string DisplayName => $"SQL: {_factory.DisplayName}";
    public string Location => $"SQL Server {_factory.DisplayName}";
    public bool IsReadOnly => ReadOnlyReason is not null;
    public string? ReadOnlyReason { get; private set; }
    public bool SupportsPermissions => true;
    public string? SignedInAs { get; private set; }
    public bool IsAdmin { get; private set; }
    public int? UserId { get; private set; }

    public event EventHandler? ExternalChange;
    public event EventHandler? StatusChanged;

    /// <summary>How often <see cref="StartWatching"/> checks the database for changes and for the connection.</summary>
    public static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(60);

    /// <param name="cachePath">Where the last loaded tree is kept for when the database cannot be reached. Null for no cache.</param>
    public SqlConnectionStore(SqlConnectionFactory factory, string? cachePath, Action<string>? log = null, TimeSpan? pollInterval = null)
    {
        _factory = factory;
        _cachePath = cachePath;
        _log = log;
        _pollInterval = pollInterval ?? DefaultPollInterval;
    }

    public AccessLevel AccessFor(Guid? folderId)
    {
        if (IsReadOnly) return AccessLevel.View;
        if (folderId is not { } id) return IsAdmin ? AccessLevel.Manage : AccessLevel.None;
        lock (_gate) return _levels.TryGetValue(id, out var level) ? level : AccessLevel.None;
    }

    // ---------- load ----------

    public ConnectionsDocument Load()
    {
        SqlConnection connection;
        try
        {
            connection = _factory.Open();
        }
        catch (SqlStoreUnavailableException ex) when (TryLoadCache(out var cached, ex.Message))
        {
            _log?.Invoke($"Database unreachable, showing the cached copy: {ex.Message}");
            return cached!;
        }

        using (connection)
        {
            return SqlErrors.Run(() =>
            {
                var who = ReadWhoAmI(connection);
                if (who.SchemaVersion < SchemaManager.LatestVersion)
                    throw new InvalidOperationException($"The database is RCM schema version {who.SchemaVersion}; this RCM needs version {SchemaManager.LatestVersion}. Someone with db_owner should open Settings > Storage and choose Create or upgrade.");

                var document = ReadTree(connection);
                _offline = OfflineState.Online;
                _reasonBeforeLost = null;
                ReadOnlyReason = who.SchemaVersion > SchemaManager.LatestVersion
                    ? $"The database was upgraded by a newer RCM (schema {who.SchemaVersion}). Update RCM to make changes."
                    : null;
                WriteCache(document);
                return document;
            });
        }
    }

    private (int SchemaVersion, bool IsAdmin) ReadWhoAmI(SqlConnection connection)
    {
        using var cmd = SqlConnectionFactory.Proc(connection, "rcm.WhoAmI");
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) throw new InvalidOperationException("The database did not register this user.");
        UserId = reader.GetInt32(0);
        SignedInAs = reader.GetString(1);
        IsAdmin = reader.GetBoolean(3);
        return (reader.GetInt32(4), IsAdmin);
    }

    private ConnectionsDocument ReadTree(SqlConnection connection)
    {
        using var cmd = SqlConnectionFactory.Proc(connection, "rcm.GetTree");
        using var reader = cmd.ExecuteReader();

        var document = new ConnectionsDocument();
        reader.Read();
        document.Defaults = JsonSerializer.Deserialize<ConnectionDefaults>(reader.GetString(0), Compact) ?? new ConnectionDefaults();

        var folderRows = new List<(ConnectionFolder Folder, Guid? ParentId, int Sort)>();
        var folderStates = new Dictionary<Guid, RowState>();
        var levels = new Dictionary<Guid, AccessLevel>();
        reader.NextResult();
        while (reader.Read())
        {
            var id = reader.GetGuid(0);
            Guid? parent = reader.IsDBNull(1) ? null : reader.GetGuid(1);
            var body = JsonSerializer.Deserialize<FolderBody>(reader.GetString(4), Compact) ?? new FolderBody();
            var folder = new ConnectionFolder { Id = id, Name = reader.GetString(2), Description = body.Description, Defaults = body.Defaults ?? new ConnectionDefaults() };
            var sort = reader.GetInt32(3);
            folderRows.Add((folder, parent, sort));
            folderStates[id] = new RowState(parent, sort, folder.Name, SerializeFolder(folder), (byte[])reader[6]);
            levels[id] = (AccessLevel)reader.GetByte(9);
        }

        var entryRows = new List<(ConnectionEntry Entry, Guid? FolderId, int Sort)>();
        var entryStates = new Dictionary<Guid, RowState>();
        reader.NextResult();
        while (reader.Read())
        {
            var id = reader.GetGuid(0);
            Guid? folderId = reader.IsDBNull(1) ? null : reader.GetGuid(1);
            ConnectionEntry? entry;
            try
            {
                entry = JsonSerializer.Deserialize<ConnectionEntry>(reader.GetString(5), Compact);
            }
            catch (JsonException ex)
            {
                _log?.Invoke($"Skipped entry {id} ({reader.GetString(3)}): {ex.Message}");
                continue;
            }
            if (entry is null) continue;
            entry.Id = id;
            entry.Name = reader.GetString(3);
            var sort = reader.GetInt32(4);
            entryRows.Add((entry, folderId, sort));
            entryStates[id] = new RowState(folderId, sort, entry.Name, SerializeEntry(entry), (byte[])reader[6]);
        }

        reader.NextResult();
        reader.Read();
        var stamp = reader.GetInt64(0);

        // Build the tree. A folder whose parent the user cannot see is shown at the top level.
        var byId = folderRows.ToDictionary(r => r.Folder.Id, r => r.Folder);
        foreach (var (folder, parentId, _) in folderRows.OrderBy(r => r.Sort).ThenBy(r => r.Folder.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (parentId is { } p && byId.TryGetValue(p, out var parent)) parent.Folders.Add(folder);
            else document.Folders.Add(folder);
        }
        foreach (var (entry, folderId, _) in entryRows.OrderBy(r => r.Sort).ThenBy(r => r.Entry.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (folderId is { } f && byId.TryGetValue(f, out var folder)) folder.Connections.Add(entry);
            else document.Connections.Add(entry);
        }

        lock (_gate)
        {
            _folders = folderStates;
            _entries = entryStates;
            _levels = levels;
            _defaultsJson = JsonSerializer.Serialize(document.Defaults, Compact);
            Interlocked.Exchange(ref _stamp, stamp);
        }
        return document;
    }

    // ---------- save ----------

    public void Save(ConnectionsDocument document)
    {
        if (ReadOnlyReason is { } reason) throw new StorePermissionException(reason);

        Dictionary<Guid, RowState> oldFolders, oldEntries;
        string oldDefaults;
        lock (_gate)
        {
            oldFolders = _folders;
            oldEntries = _entries;
            oldDefaults = _defaultsJson;
        }

        var plan = SavePlan.Build(document, oldFolders, oldEntries);
        var defaultsJson = JsonSerializer.Serialize(document.Defaults, Compact);
        var defaultsChanged = defaultsJson != oldDefaults;
        if (plan.IsEmpty && !defaultsChanged) return;

        using var connection = _factory.Open();
        using var tx = connection.BeginTransaction(IsolationLevel.ReadCommitted);
        var newFolders = new Dictionary<Guid, RowState>(oldFolders);
        var newEntries = new Dictionary<Guid, RowState>(oldEntries);
        long stamp;
        try
        {
            if (defaultsChanged)
            {
                using var cmd = SqlConnectionFactory.Proc(connection, "rcm.SaveDocumentDefaults", tx);
                cmd.Parameters.Add("@Defaults", SqlDbType.NVarChar, -1).Value = defaultsJson;
                cmd.ExecuteNonQuery();
            }
            foreach (var row in plan.FolderUpserts)
            {
                using var cmd = SqlConnectionFactory.Proc(connection, "rcm.SaveFolder", tx);
                cmd.Parameters.Add("@Id", SqlDbType.UniqueIdentifier).Value = row.Id;
                cmd.Parameters.Add("@ParentId", SqlDbType.UniqueIdentifier).Value = (object?)row.State.ParentId ?? DBNull.Value;
                cmd.Parameters.Add("@Name", SqlDbType.NVarChar, 256).Value = Truncate(row.State.Name, 256);
                cmd.Parameters.Add("@SortOrder", SqlDbType.Int).Value = row.State.SortOrder;
                cmd.Parameters.Add("@Body", SqlDbType.NVarChar, -1).Value = row.State.Body;
                cmd.Parameters.Add("@ExpectedVersion", SqlDbType.Binary, 8).Value = (object?)row.ExpectedVersion ?? DBNull.Value;
                var version = (byte[])cmd.ExecuteScalar();
                newFolders[row.Id] = row.State with { RowVersion = version };
            }
            foreach (var row in plan.EntryUpserts)
            {
                using var cmd = SqlConnectionFactory.Proc(connection, "rcm.SaveEntry", tx);
                cmd.Parameters.Add("@Id", SqlDbType.UniqueIdentifier).Value = row.Id;
                cmd.Parameters.Add("@FolderId", SqlDbType.UniqueIdentifier).Value = (object?)row.State.ParentId ?? DBNull.Value;
                cmd.Parameters.Add("@Kind", SqlDbType.NVarChar, 32).Value = row.Kind!;
                cmd.Parameters.Add("@Name", SqlDbType.NVarChar, 256).Value = Truncate(row.State.Name, 256);
                cmd.Parameters.Add("@SortOrder", SqlDbType.Int).Value = row.State.SortOrder;
                cmd.Parameters.Add("@Body", SqlDbType.NVarChar, -1).Value = row.State.Body;
                cmd.Parameters.Add("@ExpectedVersion", SqlDbType.Binary, 8).Value = (object?)row.ExpectedVersion ?? DBNull.Value;
                var version = (byte[])cmd.ExecuteScalar();
                newEntries[row.Id] = row.State with { RowVersion = version };
            }
            foreach (var (id, version) in plan.EntryDeletes)
            {
                using var cmd = SqlConnectionFactory.Proc(connection, "rcm.DeleteEntry", tx);
                cmd.Parameters.Add("@Id", SqlDbType.UniqueIdentifier).Value = id;
                cmd.Parameters.Add("@ExpectedVersion", SqlDbType.Binary, 8).Value = version;
                cmd.ExecuteNonQuery();
                newEntries.Remove(id);
            }
            foreach (var (id, version) in plan.FolderDeletes)
            {
                using var cmd = SqlConnectionFactory.Proc(connection, "rcm.DeleteFolder", tx);
                cmd.Parameters.Add("@Id", SqlDbType.UniqueIdentifier).Value = id;
                cmd.Parameters.Add("@ExpectedVersion", SqlDbType.Binary, 8).Value = version;
                cmd.ExecuteNonQuery();
            }
            // Rows that went with a deleted folder.
            foreach (var id in plan.RemovedFolders) newFolders.Remove(id);
            foreach (var id in plan.RemovedEntries) newEntries.Remove(id);

            using (var cmd = SqlConnectionFactory.Proc(connection, "rcm.GetChangeStamp", tx))
                stamp = Convert.ToInt64(cmd.ExecuteScalar());
            tx.Commit();
        }
        catch (SqlException ex)
        {
            TryRollback(tx);
            throw SqlErrors.Translate(ex);
        }
        catch
        {
            TryRollback(tx);
            throw;
        }

        lock (_gate)
        {
            _folders = newFolders;
            _entries = newEntries;
            _defaultsJson = defaultsJson;
            // New folders take the level of the folder they were made in until the next load says otherwise.
            foreach (var row in plan.FolderUpserts.Where(r => r.ExpectedVersion is null))
                _levels[row.Id] = row.State.ParentId is { } p && _levels.TryGetValue(p, out var l) ? l : AccessLevel.Manage;
            Interlocked.Exchange(ref _stamp, stamp);
        }
        WriteCache(document);
    }

    private static void TryRollback(SqlTransaction tx)
    {
        // XACT_ABORT in the procedures may already have rolled the transaction back.
        try { tx.Rollback(); } catch (InvalidOperationException) { } catch (SqlException) { }
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];

    internal static string SerializeFolder(ConnectionFolder folder) =>
        JsonSerializer.Serialize(new FolderBody { Description = folder.Description, Defaults = folder.Defaults.IsEmpty ? null : folder.Defaults }, Compact);

    internal static string SerializeEntry(ConnectionEntry entry) => JsonSerializer.Serialize(entry, Compact);

    // ---------- change polling ----------

    /// <summary>How often the database is checked. A change takes effect straight away if already watching.</summary>
    public TimeSpan PollInterval
    {
        get => _pollInterval;
        set
        {
            _pollInterval = value;
            _poll?.Change(value, value);
        }
    }

    public void StartWatching()
    {
        _poll?.Dispose();
        _poll = new Timer(_ => Poll(), null, _pollInterval, _pollInterval);
    }

    /// <summary>
    /// Checks the database once. Raises <see cref="StatusChanged"/> when the connection is lost or comes back, and
    /// <see cref="ExternalChange"/> (returning true) when there is something newer to load. Never reloads by itself.
    /// Public so tests can call it.
    /// </summary>
    public bool Poll()
    {
        if (Interlocked.Exchange(ref _polling, 1) == 1) return false;
        try
        {
            long stamp;
            try
            {
                using var connection = _factory.Open();
                using var cmd = SqlConnectionFactory.Proc(connection, "rcm.GetChangeStamp");
                stamp = Convert.ToInt64(cmd.ExecuteScalar());
            }
            catch (SqlStoreUnavailableException ex)
            {
                _log?.Invoke($"Change check failed: {ex.Message}");
                if (_offline == OfflineState.Online)
                {
                    _reasonBeforeLost = ReadOnlyReason;
                    ReadOnlyReason = $"{OfflinePrefix} lost the database at {DateTime.Now:t}. Changes are blocked until it is back. ({ex.Message})";
                    _offline = OfflineState.Lost;
                    StatusChanged?.Invoke(this, EventArgs.Empty);
                }
                return false;
            }

            if (_offline == OfflineState.Lost)
            {
                ReadOnlyReason = _reasonBeforeLost;
                _reasonBeforeLost = null;
                _offline = OfflineState.Online;
                _log?.Invoke("Database reachable again");
                StatusChanged?.Invoke(this, EventArgs.Empty);
            }
            else if (_offline == OfflineState.Cached && !ReadOnlyReason!.Contains("reachable again", StringComparison.Ordinal))
            {
                ReadOnlyReason = $"{OfflinePrefix} showing the copy from {_cacheSavedUtc.ToLocalTime():g}. The database is reachable again; reload to make changes.";
                StatusChanged?.Invoke(this, EventArgs.Empty);
            }

            if (_offline == OfflineState.Cached || stamp != Interlocked.Read(ref _stamp))
            {
                ExternalChange?.Invoke(this, EventArgs.Empty);
                return true;
            }
        }
        catch (Exception ex)
        {
            _log?.Invoke($"Change check failed: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _polling, 0);
        }
        return false;
    }

    // ---------- offline cache ----------

    private const string OfflinePrefix = "Offline:";

    private void WriteCache(ConnectionsDocument document)
    {
        if (_cachePath is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_cachePath)!);
            var cache = new StoreCache { Document = document, SignedInAs = SignedInAs, SavedUtc = DateTime.UtcNow };
            File.WriteAllText(_cachePath, JsonSerializer.Serialize(cache, Compact));
        }
        catch (Exception ex)
        {
            _log?.Invoke($"Could not write the offline cache: {ex.Message}");
        }
    }

    private bool TryLoadCache(out ConnectionsDocument? document, string why)
    {
        document = null;
        if (_cachePath is null || !File.Exists(_cachePath)) return false;
        try
        {
            var cache = JsonSerializer.Deserialize<StoreCache>(File.ReadAllText(_cachePath), Compact);
            if (cache?.Document is null) return false;
            document = cache.Document;
            SignedInAs ??= cache.SignedInAs;
            _cacheSavedUtc = cache.SavedUtc;
            _offline = OfflineState.Cached;
            ReadOnlyReason = $"{OfflinePrefix} the database cannot be reached, so this is the copy from {cache.SavedUtc.ToLocalTime():g}. Changes are blocked until it is back. ({why})";
            return true;
        }
        catch (Exception ex)
        {
            _log?.Invoke($"Could not read the offline cache: {ex.Message}");
            return false;
        }
    }

    public void Dispose()
    {
        _poll?.Dispose();
        _poll = null;
    }

    // ---------- types ----------

    internal sealed record RowState(Guid? ParentId, int SortOrder, string Name, string Body, byte[]? RowVersion);

    private sealed class FolderBody
    {
        public string? Description { get; set; }
        public ConnectionDefaults? Defaults { get; set; }
    }

    private sealed class StoreCache
    {
        public ConnectionsDocument? Document { get; set; }
        public string? SignedInAs { get; set; }
        public DateTime SavedUtc { get; set; }
    }
}
