using Microsoft.Data.SqlClient;
using RemoteConnectionManager.Core.Models;
using RemoteConnectionManager.Core.Storage;
using RemoteConnectionManager.Data.SqlServer;
using Xunit;

namespace RemoteConnectionManager.Data.SqlServer.Tests;

[Collection(LocalDbCollection.Name)]
public sealed class SqlStoreTests
{
    private readonly LocalDbFixture _db;
    private readonly SqlAdminService _admin;

    public SqlStoreTests(LocalDbFixture db)
    {
        Assert.True(db.Available, db.SkipReason);
        _db = db;
        _admin = new SqlAdminService(db.Admin());
    }

    private SqlConnectionStore AdminStore() => new(_db.Admin(), cachePath: null);

    private SqlConnectionStore StoreAs(string user, string? cachePath = null)
    {
        var factory = _db.As(user);
        return new SqlConnectionStore(factory, cachePath);
    }

    /// <summary>Registers the users (by login) and puts them in a new group. Returns the group id.</summary>
    private int Group(string name, params string[] users)
    {
        var ids = users.Select(u =>
        {
            _db.As(u);
            var login = _db.LoginName(u);
            return _admin.ListUsers().FirstOrDefault(x => x.Login == login)?.Id ?? _admin.SaveUser(null, login, u, false);
        }).ToList();
        var group = _admin.SaveGroup(null, $"{name}_{_db.Suffix}", null);
        _admin.SetGroupMembers(group, ids);
        return group;
    }

    private void Grant(Guid folder, params (int Group, AccessLevel Level)[] permissions) =>
        _admin.SetFolderPermissions(folder, false, permissions.Select(p => new FolderPermission(p.Group, "", p.Level)));

    /// <summary>Adds a top level folder holding one RDP entry, as the admin.</summary>
    private (ConnectionFolder Folder, RdpConnection Entry) AddFolder(string name)
    {
        var store = AdminStore();
        var doc = store.Load();
        var entry = new RdpConnection { Name = name + "-host", Host = name + ".example.local" };
        var folder = new ConnectionFolder { Name = name, Connections = { entry } };
        doc.Folders.Add(folder);
        store.Save(doc);
        return (folder, entry);
    }

    [Fact]
    public void Schema_IsCurrent_AndCreatorIsAdmin()
    {
        var status = new SchemaManager(_db.Admin()).GetStatus();
        Assert.True(status.IsCurrent);
        Assert.True(status.IsDbOwner);
        var store = AdminStore();
        store.Load();
        Assert.True(store.IsAdmin);
        Assert.Equal(AccessLevel.Manage, store.AccessFor(null));
    }

    [Fact]
    public void AdminRoundTrip_KeepsTreeOrderAndFields()
    {
        var store = AdminStore();
        var doc = store.Load();
        var folder = new ConnectionFolder
        {
            Name = "RoundTrip",
            Description = "desc",
            Defaults = new ConnectionDefaults { Domain = "corp.local", WatchedServices = ["W3SVC"] },
            Folders = { new ConnectionFolder { Name = "Inner", Connections = { new TerminalConnection { Name = "pwsh", Shell = TerminalShell.Pwsh, StartScript = "Get-Date" } } } },
            Connections =
            {
                new RdpConnection { Name = "b-second", Host = "b", Port = 3390 },
                new KeyVaultConnection { Name = "a-first-by-name-but-second-by-order", VaultUri = "https://kv.vault.azure.net" },
            },
        };
        doc.Folders.Add(folder);
        store.Save(doc);

        var reloaded = AdminStore().Load();
        var back = Assert.Single(reloaded.Folders, f => f.Id == folder.Id);
        Assert.Equal("desc", back.Description);
        Assert.Equal("corp.local", back.Defaults.Domain);
        Assert.Equal(["W3SVC"], back.Defaults.WatchedServices!);
        Assert.Equal(["b-second", "a-first-by-name-but-second-by-order"], back.Connections.Select(c => c.Name));
        Assert.Equal(3390, Assert.IsType<RdpConnection>(back.Connections[0]).Port);
        var inner = Assert.Single(back.Folders);
        Assert.Equal("Get-Date", Assert.IsType<TerminalConnection>(Assert.Single(inner.Connections)).StartScript);
    }

    [Fact]
    public void SshTelnetAndWebEntries_RoundTrip()
    {
        var store = AdminStore();
        var doc = store.Load();
        var folder = new ConnectionFolder
        {
            Name = "Network gear",
            Defaults = new ConnectionDefaults
            {
                SshUserName = "netadmin",
                SshCredential = new CredentialReference { Source = CredentialSource.KeyVault, VaultUri = "https://kv.vault.azure.net/", SecretName = "switch" },
                SessionLogging = true,
            },
            Connections =
            {
                new SshConnection { Name = "core-sw", Host = "10.22.22.1", LegacyAlgorithms = true },
                new SshConnection { Name = "old-sw", Host = "10.22.22.2", Protocol = TerminalProtocol.Telnet, Port = 2323, SessionLogging = false },
                new WebConnection { Name = "fw", Url = "https://fw01/", OpenIn = WebOpenMode.Tab },
            },
        };
        doc.Folders.Add(folder);
        store.Save(doc);

        var back = Assert.Single(AdminStore().Load().Folders, f => f.Id == folder.Id);
        Assert.Equal("netadmin", back.Defaults.SshUserName);
        Assert.Equal("switch", back.Defaults.SshCredential!.SecretName);
        Assert.True(back.Defaults.SessionLogging);
        var core = Assert.IsType<SshConnection>(back.Connections[0]);
        Assert.True(core.LegacyAlgorithms);
        var old = Assert.IsType<SshConnection>(back.Connections[1]);
        Assert.Equal(TerminalProtocol.Telnet, old.Protocol);
        Assert.Equal(2323, old.Port);
        Assert.False(old.SessionLogging);
        Assert.Equal(WebOpenMode.Tab, Assert.IsType<WebConnection>(back.Connections[2]).OpenIn);
        Assert.Equal(1, _db.Scalar<int>($"SELECT COUNT(*) FROM rcm.Entries WHERE Kind = N'ssh' AND Id = '{core.Id}'"));
        Assert.Equal(1, _db.Scalar<int>($"SELECT COUNT(*) FROM rcm.Entries WHERE Kind = N'web' AND FolderId = '{folder.Id}'"));
    }

    [Fact]
    public void Save_WithNoChanges_WritesNothing()
    {
        AddFolder("NoChange");
        var store = AdminStore();
        var doc = store.Load();
        var before = _db.Scalar<long>("SELECT ISNULL(MAX(Id), 0) FROM rcm.ChangeLog");
        store.Save(doc);
        Assert.Equal(before, _db.Scalar<long>("SELECT ISNULL(MAX(Id), 0) FROM rcm.ChangeLog"));
    }

    [Fact]
    public void ViewGroup_SeesOnlyItsFolder_AndCannotChangeIt()
    {
        var (visible, entry) = AddFolder("Visible");
        AddFolder("Hidden");
        var viewers = Group("viewers", "alice");
        Grant(visible.Id, (viewers, AccessLevel.View));

        var alice = StoreAs("alice");
        var doc = alice.Load();
        var folder = Assert.Single(doc.Folders);
        Assert.Equal(visible.Id, folder.Id);
        Assert.Equal(entry.Id, Assert.Single(folder.Connections).Id);
        Assert.Empty(doc.Connections);
        Assert.False(alice.IsAdmin);
        Assert.Equal(AccessLevel.View, alice.AccessFor(visible.Id));
        Assert.Equal(AccessLevel.None, alice.AccessFor(null));

        ((RdpConnection)folder.Connections[0]).Host = "changed";
        Assert.Throws<StorePermissionException>(() => alice.Save(doc));
    }

    [Fact]
    public void EditGroup_CanChangeEntries_ButNotAddTopLevelFolders()
    {
        var (folder, _) = AddFolder("Editable");
        var editors = Group("editors", "bob");
        Grant(folder.Id, (editors, AccessLevel.Edit));

        var bob = StoreAs("bob");
        var doc = bob.Load();
        var mine = Assert.Single(doc.Folders);
        ((RdpConnection)mine.Connections[0]).Host = "bob.example.local";
        mine.Connections.Add(new RdpConnection { Name = "new", Host = "new" });
        mine.Folders.Add(new ConnectionFolder { Name = "Sub" });
        bob.Save(doc);

        var check = StoreAs("bob").Load();
        var f = Assert.Single(check.Folders);
        Assert.Equal("bob.example.local", ((RdpConnection)f.Connections[0]).Host);
        Assert.Equal(2, f.Connections.Count);
        Assert.Single(f.Folders);
        Assert.Equal(_db.LoginName("bob"), _db.Scalar<string>($"SELECT ModifiedBy FROM rcm.Entries WHERE Id = '{f.Connections[0].Id}'"));

        doc = bob.Load();
        doc.Folders.Add(new ConnectionFolder { Name = "Top" });
        Assert.Throws<StorePermissionException>(() => bob.Save(doc));
    }

    [Fact]
    public void SecondWriter_GetsConflict_InsteadOfOverwriting()
    {
        var (folder, entry) = AddFolder("Conflict");
        var editors = Group("conflict", "carol", "dave");
        Grant(folder.Id, (editors, AccessLevel.Edit));

        var carol = StoreAs("carol");
        var dave = StoreAs("dave");
        var carolDoc = carol.Load();
        var daveDoc = dave.Load();

        ((RdpConnection)carolDoc.FindEntry(entry.Id)!).Host = "carol";
        carol.Save(carolDoc);

        ((RdpConnection)daveDoc.FindEntry(entry.Id)!).Host = "dave";
        var ex = Assert.Throws<StoreConflictException>(() => dave.Save(daveDoc));
        Assert.Contains(_db.LoginName("carol"), ex.Message);

        Assert.Equal("carol", ((RdpConnection)StoreAs("carol").Load().FindEntry(entry.Id)!).Host);
    }

    [Fact]
    public void FailedSave_RollsBackEverything()
    {
        var (folder, entry) = AddFolder("Rollback");
        var other = AddFolder("RollbackOther");
        var editors = Group("rollback", "erin", "frank");
        Grant(folder.Id, (editors, AccessLevel.Edit));
        Grant(other.Folder.Id, (editors, AccessLevel.Edit));

        var erin = StoreAs("erin");
        var erinDoc = erin.Load();
        var frank = StoreAs("frank");
        var frankDoc = frank.Load();
        ((RdpConnection)frankDoc.FindEntry(other.Entry.Id)!).Host = "frank";
        frank.Save(frankDoc);

        // Erin changes both; the second is a conflict, so the first must not stick either.
        ((RdpConnection)erinDoc.FindEntry(entry.Id)!).Host = "erin";
        ((RdpConnection)erinDoc.FindEntry(other.Entry.Id)!).Host = "erin";
        Assert.Throws<StoreConflictException>(() => erin.Save(erinDoc));

        var check = AdminStore().Load();
        Assert.Equal("Rollback.example.local", ((RdpConnection)check.FindEntry(entry.Id)!).Host);
    }

    [Fact]
    public void BreakInheritance_HidesChildFolder()
    {
        var store = AdminStore();
        var doc = store.Load();
        var secret = new ConnectionFolder { Name = "Secret" };
        var parent = new ConnectionFolder { Name = "Parent", Folders = { secret, new ConnectionFolder { Name = "Open" } } };
        doc.Folders.Add(parent);
        store.Save(doc);
        var group = Group("inherit", "gina");
        Grant(parent.Id, (group, AccessLevel.View));
        _admin.SetFolderPermissions(secret.Id, breakInheritance: true, []);

        var gina = StoreAs("gina").Load();
        var p = Assert.Single(gina.Folders);
        Assert.Equal(["Open"], p.Folders.Select(f => f.Name));

        var perms = _admin.GetFolderPermissions(secret.Id);
        Assert.True(perms.BreakInheritance);
        Assert.Empty(perms.Inherited);
        var openPerms = _admin.GetFolderPermissions(p.Folders[0].Id);
        Assert.Equal(AccessLevel.View, Assert.Single(openPerms.Inherited).Level);
        Assert.Equal("Parent", openPerms.Inherited[0].FromFolder);
    }

    [Fact]
    public void FolderWithHiddenParent_ShowsAtTopLevel_AndKeepsItsParentOnSave()
    {
        var store = AdminStore();
        var doc = store.Load();
        var child = new ConnectionFolder { Name = "OnlyChild", Connections = { new RdpConnection { Name = "c1", Host = "c1" } } };
        var parent = new ConnectionFolder { Name = "HiddenParent", Folders = { child } };
        doc.Folders.Add(parent);
        store.Save(doc);
        var group = Group("childonly", "hank");
        Grant(child.Id, (group, AccessLevel.Edit));

        var hank = StoreAs("hank");
        var hankDoc = hank.Load();
        var top = Assert.Single(hankDoc.Folders);
        Assert.Equal(child.Id, top.Id);
        top.Name = "OnlyChild renamed";
        hank.Save(hankDoc);

        Assert.Equal(parent.Id, _db.Scalar<Guid>($"SELECT ParentId FROM rcm.Folders WHERE Id = '{child.Id}'"));
    }

    [Fact]
    public void DeleteFolder_RefusedWhenItHoldsAFolderTheUserCannotChange()
    {
        var store = AdminStore();
        var doc = store.Load();
        var locked = new ConnectionFolder { Name = "Locked" };
        var parent = new ConnectionFolder { Name = "DeleteMe", Folders = { locked } };
        doc.Folders.Add(parent);
        store.Save(doc);
        var group = Group("deleters", "ivy");
        Grant(parent.Id, (group, AccessLevel.Edit));
        _admin.SetFolderPermissions(locked.Id, breakInheritance: true, []);

        // Ivy can edit DeleteMe but not delete it from the top level (only admins change the top level),
        // so give her a parent she can edit.
        doc = store.Load();
        var holder = new ConnectionFolder { Name = "Holder" };
        doc.Folders.Add(holder);
        store.Save(doc);
        doc = store.Load();
        var moved = doc.FindFolder(parent.Id)!;
        doc.Remove(moved);
        doc.FindFolder(holder.Id)!.Folders.Add(moved);
        store.Save(doc);
        Grant(holder.Id, (group, AccessLevel.Edit));

        var ivy = StoreAs("ivy");
        var ivyDoc = ivy.Load();
        var target = ivyDoc.FindFolder(parent.Id)!;
        ivyDoc.Remove(target);
        var ex = Assert.Throws<StorePermissionException>(() => ivy.Save(ivyDoc));
        Assert.Contains("not allowed", ex.Message);
        Assert.Equal(1, _db.Scalar<int>($"SELECT COUNT(*) FROM rcm.Folders WHERE Id = '{locked.Id}'"));
    }

    [Fact]
    public void DeleteFolder_RemovesEverythingInside()
    {
        var store = AdminStore();
        var doc = store.Load();
        var inner = new ConnectionFolder { Name = "Inner", Connections = { new RdpConnection { Name = "i", Host = "i" } } };
        var outer = new ConnectionFolder { Name = "Outer", Folders = { inner }, Connections = { new RdpConnection { Name = "o", Host = "o" } } };
        doc.Folders.Add(outer);
        store.Save(doc);

        doc = store.Load();
        doc.Remove(doc.FindFolder(outer.Id)!);
        store.Save(doc);

        Assert.Equal(0, _db.Scalar<int>($"SELECT COUNT(*) FROM rcm.Folders WHERE Id IN ('{outer.Id}', '{inner.Id}')"));
        Assert.Equal(0, _db.Scalar<int>($"SELECT COUNT(*) FROM rcm.Entries WHERE FolderId IN ('{outer.Id}', '{inner.Id}')"));
    }

    [Fact]
    public void RcmUser_CannotReadTablesDirectly()
    {
        var factory = _db.As("jack");
        using var connection = factory.Open();
        using var cmd = new SqlCommand("SELECT COUNT(*) FROM rcm.Entries", connection);
        var ex = Assert.Throws<SqlException>(() => cmd.ExecuteScalar());
        Assert.Equal(229, ex.Number);
    }

    [Fact]
    public void NonAdmin_CannotManageGroups()
    {
        StoreAs("kim").Load();
        var kimAdmin = new SqlAdminService(_db.As("kim"));
        Assert.Throws<StorePermissionException>(() => kimAdmin.SaveGroup(null, "nope", null));
        Assert.Throws<StorePermissionException>(() => kimAdmin.ListUsers());
        Assert.NotNull(kimAdmin.ListGroups());
    }

    [Fact]
    public void Poll_SeesOtherPeoplesChanges()
    {
        var (folder, entry) = AddFolder("Poll");
        var group = Group("poll", "liam");
        Grant(folder.Id, (group, AccessLevel.View));
        var liam = StoreAs("liam");
        liam.Load();
        Assert.False(liam.Poll());

        var admin = AdminStore();
        var doc = admin.Load();
        ((RdpConnection)doc.FindEntry(entry.Id)!).Host = "polled";
        admin.Save(doc);

        var raised = false;
        liam.ExternalChange += (_, _) => raised = true;
        Assert.True(liam.Poll());
        Assert.True(raised);
        Assert.Equal("polled", ((RdpConnection)liam.Load().FindEntry(entry.Id)!).Host);
        Assert.False(liam.Poll());
    }

    [Fact]
    public void OwnSave_DoesNotLookLikeSomeoneElsesChange()
    {
        var (folder, entry) = AddFolder("OwnSave");
        var group = Group("ownsave", "mia");
        Grant(folder.Id, (group, AccessLevel.Edit));
        var mia = StoreAs("mia");
        var doc = mia.Load();
        ((RdpConnection)doc.FindEntry(entry.Id)!).Host = "mine";
        mia.Save(doc);
        Assert.False(mia.Poll());
    }

    [Fact]
    public void PersonalSettings_AreKeptPerUser()
    {
        var (folder, entry) = AddFolder("Personal");
        var group = Group("personal", "nora", "omar");
        Grant(folder.Id, (group, AccessLevel.View));
        StoreAs("nora").Load();
        StoreAs("omar").Load();

        var nora = new SqlUserProfileStore(_db.As("nora"), cachePath: null);
        nora.SaveEntrySettings(entry.Id, new PersonalEntrySettings { WatchedServices = ["Spooler"], ReplaceSharedWatchList = true });
        var omar = new SqlUserProfileStore(_db.As("omar"), cachePath: null);

        var noraSettings = nora.LoadEntrySettings();
        Assert.Equal(["Spooler"], noraSettings[entry.Id].WatchedServices!);
        Assert.True(noraSettings[entry.Id].ReplaceSharedWatchList);
        Assert.Empty(omar.LoadEntrySettings());

        nora.SaveEntrySettings(entry.Id, null);
        Assert.Empty(new SqlUserProfileStore(_db.As("nora"), null).LoadEntrySettings());
    }

    [Fact]
    public void Preferences_RoundTrip()
    {
        StoreAs("pat").Load();
        var settings = new Core.Services.AppSettings { NavigationWidth = 412, NavigationVisible = false, DisabledPlugins = ["onepassword"], ExpandedFolders = [Guid.NewGuid()] };
        new SqlUserProfileStore(_db.As("pat"), null).SavePreferences(settings);

        var fresh = new Core.Services.AppSettings();
        new SqlUserProfileStore(_db.As("pat"), null).ApplyPreferences(fresh);
        Assert.Equal(412, fresh.NavigationWidth);
        Assert.False(fresh.NavigationVisible);
        Assert.Equal(["onepassword"], fresh.DisabledPlugins);
        Assert.Equal(settings.ExpandedFolders, fresh.ExpandedFolders);
    }

    [Fact]
    public void UnreachableDatabase_FallsBackToCache_ReadOnly()
    {
        var cache = Path.Combine(Path.GetTempPath(), $"rcm-cache-{Guid.NewGuid():N}.json");
        try
        {
            var (folder, _) = AddFolder("Cached");
            var group = Group("cached", "quinn");
            Grant(folder.Id, (group, AccessLevel.Edit));
            StoreAs("quinn", cache).Load();

            var dead = new SqlConnectionFactory(
                new SqlConnectionStringBuilder { DataSource = @"(localdb)\NoSuchInstance_" + _db.Suffix, InitialCatalog = "x", IntegratedSecurity = true, ConnectTimeout = 2 }.ConnectionString,
                "dead");
            var offline = new SqlConnectionStore(dead, cache);
            var doc = offline.Load();
            Assert.Equal(folder.Id, Assert.Single(doc.Folders).Id);
            Assert.True(offline.IsReadOnly);
            Assert.Equal(AccessLevel.View, offline.AccessFor(folder.Id));
            Assert.Throws<StorePermissionException>(() => offline.Save(doc));
        }
        finally
        {
            File.Delete(cache);
        }
    }

    [Fact]
    public void NotInRole_GetsAClearMessage()
    {
        var factory = _db.As("rita", inRole: false);
        var status = new SchemaManager(factory).GetStatus();
        Assert.False(status.InRcmRole);
        Assert.Null(status.Version);
        var ex = Assert.ThrowsAny<Exception>(() => new SqlConnectionStore(factory, null).Load());
        Assert.Contains("rcm_user", ex.Message);
    }

    [Fact]
    public void AdminFlag_ControlsAccessToAdminProcedures()
    {
        // The developer is db_owner, so the guard does not apply to them; check the rule through a fresh admin login.
        StoreAs("sam").Load();
        var samId = _admin.ListUsers().Single(u => u.Login == _db.LoginName("sam")).Id;
        _admin.SaveUser(samId, _db.LoginName("sam"), "Sam", isAdmin: true);
        var sam = new SqlAdminService(_db.As("sam"));
        Assert.NotEmpty(sam.ListUsers());

        // Other admins exist (the developer), so Sam may drop his own flag.
        sam.SaveUser(samId, _db.LoginName("sam"), "Sam", isAdmin: false);
        Assert.Throws<StorePermissionException>(() => sam.ListUsers());
    }
}
