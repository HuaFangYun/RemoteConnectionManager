using RemoteConnectionManager.Core.Models;
using RemoteConnectionManager.Core.Storage;
using Xunit;

namespace RemoteConnectionManager.Data.SqlServer.Tests;

[Collection(LocalDbCollection.Name)]
public sealed class CopyIntoDatabaseTests(LocalDbFixture db)
{
    [Fact]
    public void FileCopiedIn_KeepsIds_AndASecondCopyGetsNewOnes()
    {
        Assert.True(db.Available, db.SkipReason);
        var path = Path.Combine(Path.GetTempPath(), $"rcm-copy-{Guid.NewGuid():N}.json");
        try
        {
            var entry = new RdpConnection { Name = "copied", Host = "copied.local" };
            var folder = new ConnectionFolder { Name = "FromFile", Folders = { new ConnectionFolder { Name = "Nested", Connections = { entry } } } };
            using (var file = new FileConnectionStore(path))
                file.Save(new ConnectionsDocument { Folders = { folder } });

            for (var i = 0; i < 2; i++)
            {
                using var store = new SqlConnectionStore(db.Admin(), null);
                var target = store.Load();
                var source = new FileConnectionStore(path).Load();
                var (folders, entries) = DocumentMerge.AppendInto(target, source);
                Assert.Equal((2, 1), (folders, entries));
                store.Save(target);
            }

            var all = new SqlConnectionStore(db.Admin(), null).Load();
            Assert.NotNull(all.FindEntry(entry.Id));
            Assert.Equal(2, all.AllEntries().Count(e => e.Entry.Name == "copied"));
            Assert.Equal(2, all.Folders.Count(f => f.Name == "FromFile"));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
