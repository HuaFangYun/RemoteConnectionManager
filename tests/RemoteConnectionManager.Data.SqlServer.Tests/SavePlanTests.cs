using RemoteConnectionManager.Core.Models;
using RemoteConnectionManager.Data.SqlServer;
using Xunit;
using RowState = RemoteConnectionManager.Data.SqlServer.SqlConnectionStore.RowState;

namespace RemoteConnectionManager.Data.SqlServer.Tests;

public sealed class SavePlanTests
{
    private static readonly byte[] V = [0, 0, 0, 0, 0, 0, 0, 1];

    [Theory]
    [InlineData(new[] { 0, 1, 2 }, new[] { 0, 1, 2 })]
    [InlineData(new[] { 0, 2 }, new[] { 0, 2 })]               // a gap from a delete is left alone
    [InlineData(new[] { 0, 1, -1 }, new[] { 0, 1, 2 })]        // -1 = new item at the end
    [InlineData(new[] { 2, 1, 0 }, new[] { 2, 3, 4 })]         // reordered: renumbered after the first
    [InlineData(new[] { -1, 0, 1 }, new[] { 0, 1, 2 })]
    public void AssignOrders_KeepsStoredOrdersWhereTheyFit(int[] stored, int[] expected)
    {
        var existing = stored.Select(s => s < 0 ? (int?)null : s).ToList();
        Assert.Equal(expected, SavePlan.AssignOrders(existing));
    }

    private static (Dictionary<Guid, RowState> Folders, Dictionary<Guid, RowState> Entries) Snapshot(ConnectionsDocument doc)
    {
        var plan = SavePlan.Build(doc, new Dictionary<Guid, RowState>(), new Dictionary<Guid, RowState>());
        return (plan.FolderUpserts.ToDictionary(u => u.Id, u => u.State with { RowVersion = V }),
                plan.EntryUpserts.ToDictionary(u => u.Id, u => u.State with { RowVersion = V }));
    }

    [Fact]
    public void NewDocument_InsertsParentsBeforeChildren()
    {
        var inner = new ConnectionFolder { Name = "inner" };
        var doc = new ConnectionsDocument { Folders = { new ConnectionFolder { Name = "outer", Folders = { inner } } } };
        var plan = SavePlan.Build(doc, new Dictionary<Guid, RowState>(), new Dictionary<Guid, RowState>());
        Assert.Equal(["outer", "inner"], plan.FolderUpserts.Select(u => u.State.Name));
        Assert.All(plan.FolderUpserts, u => Assert.Null(u.ExpectedVersion));
    }

    [Fact]
    public void Unchanged_IsEmpty_AndOneEdit_IsOneRow()
    {
        var entry = new RdpConnection { Name = "a", Host = "a" };
        var doc = new ConnectionsDocument { Folders = { new ConnectionFolder { Name = "f", Connections = { entry, new RdpConnection { Name = "b", Host = "b" } } } } };
        var (folders, entries) = Snapshot(doc);

        Assert.True(SavePlan.Build(doc, folders, entries).IsEmpty);

        entry.Host = "changed";
        var plan = SavePlan.Build(doc, folders, entries);
        var row = Assert.Single(plan.EntryUpserts);
        Assert.Equal(entry.Id, row.Id);
        Assert.Same(V, row.ExpectedVersion);
        Assert.Equal("rdp", row.Kind);
        Assert.Empty(plan.FolderUpserts);
    }

    [Fact]
    public void DeletingAnItem_DoesNotRewriteItsNeighbours()
    {
        var first = new RdpConnection { Name = "1", Host = "1" };
        var doc = new ConnectionsDocument { Connections = { first, new RdpConnection { Name = "2", Host = "2" }, new RdpConnection { Name = "3", Host = "3" } } };
        var (folders, entries) = Snapshot(doc);
        doc.Connections.Remove(first);

        var plan = SavePlan.Build(doc, folders, entries);
        Assert.Empty(plan.EntryUpserts);
        Assert.Equal(first.Id, Assert.Single(plan.EntryDeletes).Id);
    }

    [Fact]
    public void DeletingAFolder_DeletesOnlyTheTopOne()
    {
        var inner = new ConnectionFolder { Name = "inner", Connections = { new RdpConnection { Name = "x", Host = "x" } } };
        var outer = new ConnectionFolder { Name = "outer", Folders = { inner }, Connections = { new RdpConnection { Name = "y", Host = "y" } } };
        var keep = new RdpConnection { Name = "keep", Host = "keep" };
        outer.Connections.Add(keep);
        var doc = new ConnectionsDocument { Folders = { outer } };
        var (folders, entries) = Snapshot(doc);

        // Move one entry out, then delete the folder.
        outer.Connections.Remove(keep);
        doc.Connections.Add(keep);
        doc.Folders.Remove(outer);

        var plan = SavePlan.Build(doc, folders, entries);
        Assert.Equal(outer.Id, Assert.Single(plan.FolderDeletes).Id);
        Assert.Empty(plan.EntryDeletes);
        Assert.Equal(keep.Id, Assert.Single(plan.EntryUpserts).Id);
        Assert.Null(plan.EntryUpserts[0].State.ParentId);
        Assert.Equal(2, plan.RemovedFolders.Count);
        Assert.Equal(2, plan.RemovedEntries.Count);
    }
}
