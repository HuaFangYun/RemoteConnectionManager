using RemoteConnectionManager.Core.Models;
using RowState = RemoteConnectionManager.Data.SqlServer.SqlConnectionStore.RowState;

namespace RemoteConnectionManager.Data.SqlServer;

/// <summary>
/// Works out which rows a save has to write by comparing the document with the rows last loaded.
/// Pure, so it is tested without a database.
/// </summary>
internal sealed class SavePlan
{
    public sealed record Upsert(Guid Id, RowState State, byte[]? ExpectedVersion, string? Kind);

    /// <summary>Parents before children, so a new folder exists before anything is put in it.</summary>
    public List<Upsert> FolderUpserts { get; } = new();
    public List<Upsert> EntryUpserts { get; } = new();
    /// <summary>Entries removed from folders that still exist.</summary>
    public List<(Guid Id, byte[] Version)> EntryDeletes { get; } = new();
    /// <summary>The top most removed folders; the database removes what is inside them.</summary>
    public List<(Guid Id, byte[] Version)> FolderDeletes { get; } = new();
    public HashSet<Guid> RemovedFolders { get; } = new();
    public HashSet<Guid> RemovedEntries { get; } = new();

    public bool IsEmpty => FolderUpserts.Count == 0 && EntryUpserts.Count == 0 && EntryDeletes.Count == 0 && FolderDeletes.Count == 0;

    public static SavePlan Build(ConnectionsDocument document, IReadOnlyDictionary<Guid, RowState> oldFolders, IReadOnlyDictionary<Guid, RowState> oldEntries)
    {
        var plan = new SavePlan();
        var seenFolders = new HashSet<Guid>();
        var seenEntries = new HashSet<Guid>();
        var folderRows = new List<(int Depth, Upsert Row)>();

        void WalkEntries(List<ConnectionEntry> entries, Guid? folderId)
        {
            var orders = AssignOrders(entries.Select(e => oldEntries.TryGetValue(e.Id, out var o) ? o.SortOrder : (int?)null).ToList());
            for (var i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                if (!seenEntries.Add(entry.Id)) continue;
                var state = new RowState(folderId, orders[i], entry.Name, SqlConnectionStore.SerializeEntry(entry), null);
                oldEntries.TryGetValue(entry.Id, out var old);
                if (old is not null && SameContent(old, state)) continue;
                plan.EntryUpserts.Add(new Upsert(entry.Id, state, old?.RowVersion, KindName(entry.Kind)));
            }
        }

        void WalkFolders(List<ConnectionFolder> folders, Guid? parentId, int depth)
        {
            var orders = AssignOrders(folders.Select(f => oldFolders.TryGetValue(f.Id, out var o) ? o.SortOrder : (int?)null).ToList());
            for (var i = 0; i < folders.Count; i++)
            {
                var folder = folders[i];
                if (!seenFolders.Add(folder.Id)) continue;
                oldFolders.TryGetValue(folder.Id, out var old);

                // A folder whose parent this user cannot see is shown at the top level. Keep its real parent.
                var parent = parentId;
                if (parent is null && old?.ParentId is { } hidden && !oldFolders.ContainsKey(hidden)) parent = hidden;

                var state = new RowState(parent, orders[i], folder.Name, SqlConnectionStore.SerializeFolder(folder), null);
                if (old is null || !SameContent(old, state))
                    folderRows.Add((depth, new Upsert(folder.Id, state, old?.RowVersion, null)));

                WalkFolders(folder.Folders, folder.Id, depth + 1);
                WalkEntries(folder.Connections, folder.Id);
            }
        }

        WalkFolders(document.Folders, null, 0);
        WalkEntries(document.Connections, null);
        plan.FolderUpserts.AddRange(folderRows.OrderBy(r => r.Depth).Select(r => r.Row));

        foreach (var (id, _) in oldFolders)
            if (!seenFolders.Contains(id)) plan.RemovedFolders.Add(id);
        foreach (var id in plan.RemovedFolders)
        {
            var old = oldFolders[id];
            if (old.ParentId is { } p && plan.RemovedFolders.Contains(p)) continue;
            plan.FolderDeletes.Add((id, old.RowVersion!));
        }

        // Everything inside a removed folder goes with it, including entries this user never saw moved.
        var underRemoved = new HashSet<Guid>(plan.RemovedFolders);
        foreach (var (id, old) in oldEntries)
        {
            if (seenEntries.Contains(id)) continue;
            plan.RemovedEntries.Add(id);
            if (old.ParentId is { } f && underRemoved.Contains(f)) continue;
            plan.EntryDeletes.Add((id, old.RowVersion!));
        }
        return plan;
    }

    /// <summary>
    /// Keeps each item's stored order where it still fits the list, and gives new or moved items the
    /// next number, so adding or removing one item does not rewrite all its neighbours.
    /// </summary>
    internal static int[] AssignOrders(IReadOnlyList<int?> existing)
    {
        var result = new int[existing.Count];
        int? previous = null;
        for (var i = 0; i < existing.Count; i++)
        {
            var value = existing[i] is { } v && (previous is null || v > previous) ? v : (previous ?? -1) + 1;
            result[i] = value;
            previous = value;
        }
        return result;
    }

    private static bool SameContent(RowState a, RowState b) =>
        a.ParentId == b.ParentId && a.SortOrder == b.SortOrder && a.Name == b.Name && a.Body == b.Body;

    internal static string KindName(ConnectionKind kind) => kind switch
    {
        ConnectionKind.Rdp => "rdp",
        ConnectionKind.Terminal => "terminal",
        ConnectionKind.KeyVault => "keyvault",
        ConnectionKind.Ssh => "ssh",
        ConnectionKind.Web => "web",
        _ => kind.ToString().ToLowerInvariant(),
    };
}
