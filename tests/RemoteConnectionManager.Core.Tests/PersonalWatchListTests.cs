using RemoteConnectionManager.Core.Models;
using RemoteConnectionManager.Core.Services;
using RemoteConnectionManager.Core.Storage;
using Xunit;

namespace RemoteConnectionManager.Core.Tests;

public class PersonalWatchListTests
{
    private static (ConnectionsDocument Doc, ConnectionFolder Folder, RdpConnection Entry) Tree()
    {
        var entry = new RdpConnection { Name = "web01", Host = "web01" };
        var folder = new ConnectionFolder { Name = "Web", Defaults = new ConnectionDefaults { WatchedServices = ["W3SVC", "WAS"] }, Connections = { entry } };
        return (new ConnectionsDocument { Folders = { folder } }, folder, entry);
    }

    [Fact]
    public void NoPersonalSettings_GivesSharedList()
    {
        var (doc, _, entry) = Tree();
        Assert.Equal(["W3SVC", "WAS"], SettingsResolver.Resolve(doc, entry).WatchedServices);
    }

    [Fact]
    public void PersonalPatterns_AreAdded_FromEntryAndFolders()
    {
        var (doc, folder, entry) = Tree();
        var personal = new Dictionary<Guid, PersonalEntrySettings>
        {
            [entry.Id] = new() { WatchedServices = ["Spooler", "w3svc"] },
            [folder.Id] = new() { WatchedServices = ["MSSQL*"] },
        };
        var result = SettingsResolver.Resolve(doc, entry, id => personal.GetValueOrDefault(id)).WatchedServices;
        Assert.Equal(["W3SVC", "WAS", "Spooler", "MSSQL*"], result);
    }

    [Fact]
    public void Replace_DropsSharedList()
    {
        var (doc, folder, entry) = Tree();
        var personal = new Dictionary<Guid, PersonalEntrySettings>
        {
            [folder.Id] = new() { WatchedServices = ["Spooler"], ReplaceSharedWatchList = true },
        };
        Assert.Equal(["Spooler"], SettingsResolver.Resolve(doc, entry, id => personal.GetValueOrDefault(id)).WatchedServices);
    }

    [Fact]
    public void LocalProfile_KeepsSettingsInSettingsFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"rcm-settings-{Guid.NewGuid():N}.json");
        try
        {
            var store = new AppSettingsStore(path);
            var settings = new AppSettings();
            var profile = new LocalUserProfileStore(settings, store);
            var id = Guid.NewGuid();
            profile.SaveEntrySettings(id, new PersonalEntrySettings { WatchedServices = ["Spooler"] });

            var reloaded = store.Load();
            Assert.Equal(["Spooler"], reloaded.PersonalEntrySettings[id].WatchedServices!);

            profile.SaveEntrySettings(id, new PersonalEntrySettings());
            Assert.Empty(store.Load().PersonalEntrySettings);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
