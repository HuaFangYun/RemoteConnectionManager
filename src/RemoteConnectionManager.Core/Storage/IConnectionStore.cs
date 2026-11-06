using RemoteConnectionManager.Core.Models;

namespace RemoteConnectionManager.Core.Storage;

/// <summary>Where the connection tree lives.</summary>
public enum StorageKind
{
    File,
    SqlServer,
}

/// <summary>What the signed in user may do with a folder and everything in it. Ordered: higher includes lower.</summary>
public enum AccessLevel
{
    None = 0,
    View = 1,
    Edit = 2,
    Manage = 3,
}

/// <summary>
/// Loads and saves the connection tree. The shell works on a whole <see cref="ConnectionsDocument"/>;
/// a store that keeps rows (SQL Server) works out what changed itself.
/// </summary>
public interface IConnectionStore : IDisposable
{
    StorageKind Kind { get; }

    /// <summary>Short text for the status bar, for example "connections.json" or "SQL: sql01/RCM".</summary>
    string DisplayName { get; }

    /// <summary>Full location for a tooltip: a file path or server and database.</summary>
    string Location { get; }

    /// <summary>True when edits cannot be saved, for example the database is unreachable and a cached copy is shown.</summary>
    bool IsReadOnly { get; }

    /// <summary>Why the store is read only, or null.</summary>
    string? ReadOnlyReason { get; }

    /// <summary>True when folders carry group permissions (SQL Server).</summary>
    bool SupportsPermissions { get; }

    /// <summary>The identity the store knows the user by (SQL login), or null for a file.</summary>
    string? SignedInAs { get; }

    /// <summary>True when the user may manage users, groups and top level folders.</summary>
    bool IsAdmin { get; }

    /// <summary>
    /// Raised off the UI thread when there is something newer to load: someone else changed the tree, or a
    /// cached copy is shown and the real one can now be loaded. The store does not reload by itself.
    /// </summary>
    event EventHandler? ExternalChange;

    /// <summary>Raised off the UI thread when <see cref="IsReadOnly"/> or <see cref="ReadOnlyReason"/> changes without a load, for example the database was lost or came back.</summary>
    event EventHandler? StatusChanged;

    ConnectionsDocument Load();

    /// <summary>Saves the document. Throws <see cref="StoreConflictException"/> or <see cref="StorePermissionException"/>; nothing is half saved.</summary>
    void Save(ConnectionsDocument document);

    void StartWatching();

    /// <summary>The user's level on a folder (null means the top level), as of the last load.</summary>
    AccessLevel AccessFor(Guid? folderId);
}

/// <summary>Someone else changed or deleted an item since it was loaded. The save was rolled back.</summary>
public sealed class StoreConflictException(string message) : Exception(message);

/// <summary>The user is not allowed to make the change. The save was rolled back.</summary>
public sealed class StorePermissionException(string message) : Exception(message);
