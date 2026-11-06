using RemoteConnectionManager.Core.Models;

namespace RemoteConnectionManager.Core.Storage;

/// <summary>Copies one document into another, for moving a connections file into the database.</summary>
public static class DocumentMerge
{
    /// <summary>
    /// Adds the source's folders and top level entries to the target. Ids are kept (so personal
    /// settings still match) unless the target already has the same id, in which case the copy gets
    /// a new one. Document defaults are taken from the source when the target has none.
    /// </summary>
    /// <returns>Folders and entries added.</returns>
    public static (int Folders, int Entries) AppendInto(ConnectionsDocument target, ConnectionsDocument source)
    {
        var used = new HashSet<Guid>(target.AllFolders().Select(f => f.Id).Concat(target.AllEntries().Select(e => e.Entry.Id)));
        var folders = 0;
        var entries = 0;

        void ClaimFolder(ConnectionFolder folder)
        {
            if (!used.Add(folder.Id)) { folder.Id = Guid.NewGuid(); used.Add(folder.Id); }
            folders++;
            foreach (var entry in folder.Connections) ClaimEntry(entry);
            foreach (var child in folder.Folders) ClaimFolder(child);
        }

        void ClaimEntry(ConnectionEntry entry)
        {
            if (!used.Add(entry.Id)) { entry.Id = Guid.NewGuid(); used.Add(entry.Id); }
            entries++;
        }

        foreach (var folder in source.Folders)
        {
            ClaimFolder(folder);
            target.Folders.Add(folder);
        }
        foreach (var entry in source.Connections)
        {
            ClaimEntry(entry);
            target.Connections.Add(entry);
        }
        if (target.Defaults.IsEmpty && !source.Defaults.IsEmpty) target.Defaults = source.Defaults.Clone();
        return (folders, entries);
    }
}
