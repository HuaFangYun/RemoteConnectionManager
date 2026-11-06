using System.Text.Json;
using RemoteConnectionManager.Core.Models;
using RemoteConnectionManager.Core.Services;
using RemoteConnectionManager.Core.Storage;
using Xunit;

namespace RemoteConnectionManager.Core.Tests;

public class FileConnectionStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rcm-tests-" + Guid.NewGuid().ToString("N"));

    public FileConnectionStoreTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void RoundTrip_PreservesTreeAndTypes()
    {
        var path = Path.Combine(_dir, "connections.json");
        var store = new FileConnectionStore(path);
        var doc = new ConnectionsDocument
        {
            Defaults = new ConnectionDefaults { KeyboardPassThrough = true },
            Folders =
            {
                new ConnectionFolder
                {
                    Name = "Client A",
                    Defaults = new ConnectionDefaults { Domain = "clienta.local" },
                    Connections =
                    {
                        new RdpConnection { Name = "DC01", Host = "dc01.clienta.local", AdminSession = true, Tags = { "dc" } },
                        new TerminalConnection { Name = "Az", Shell = TerminalShell.Pwsh, StartScript = "Connect-AzAccount" },
                        new KeyVaultConnection { Name = "Vault", VaultUri = "https://kv-a.vault.azure.net" },
                    },
                    Folders = { new ConnectionFolder { Name = "Nested", Connections = { new RdpConnection { Name = "APP01", Host = "app01", Port = 3390 } } } },
                },
            },
            Connections = { new RdpConnection { Name = "Loose", Host = "loose" } },
        };

        store.Save(doc);
        var loaded = store.Load();

        Assert.Equal(1, loaded.Version);
        Assert.True(loaded.Defaults.KeyboardPassThrough);
        var folder = Assert.Single(loaded.Folders);
        Assert.Equal("clienta.local", folder.Defaults.Domain);
        Assert.Equal(3, folder.Connections.Count);
        Assert.IsType<RdpConnection>(folder.Connections[0]);
        Assert.IsType<TerminalConnection>(folder.Connections[1]);
        Assert.IsType<KeyVaultConnection>(folder.Connections[2]);
        Assert.True(((RdpConnection)folder.Connections[0]).AdminSession);
        Assert.Equal(TerminalShell.Pwsh, ((TerminalConnection)folder.Connections[1]).Shell);
        Assert.Equal(3390, ((RdpConnection)folder.Folders[0].Connections[0]).Port);
        Assert.Equal(5, loaded.EntryCount);
        Assert.Equal(doc.Folders[0].Connections[0].Id, folder.Connections[0].Id);
    }

    [Fact]
    public void Load_WhenMissing_CreatesStarterFile()
    {
        var path = Path.Combine(_dir, "sub", "connections.json");
        var store = new FileConnectionStore(path);
        var doc = store.Load();
        Assert.True(File.Exists(path));
        Assert.NotEmpty(doc.Folders);
    }

    [Fact]
    public void Json_UsesCamelCaseAndTypeDiscriminator()
    {
        var doc = new ConnectionsDocument { Connections = { new RdpConnection { Name = "X", Host = "x" } } };
        var json = JsonSerializer.Serialize(doc, JsonDefaults.Options);
        Assert.Contains("\"type\": \"rdp\"", json);
        Assert.Contains("\"host\": \"x\"", json);
        Assert.DoesNotContain("password", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Json_DoesNotWriteComputedProperties()
    {
        var doc = new ConnectionsDocument
        {
            Folders = { new ConnectionFolder { Name = "F", Connections = { new RdpConnection { Name = "X", Host = "x" } } } },
        };
        var json = JsonSerializer.Serialize(doc, JsonDefaults.Options);
        Assert.DoesNotContain("isEmpty", json);
        Assert.DoesNotContain("entryCount", json);
        Assert.DoesNotContain("\"kind\"", json);
        Assert.DoesNotContain("prefix", json);

        var settingsJson = JsonSerializer.Serialize(new AppSettings(), JsonDefaults.Options);
        Assert.DoesNotContain("effectiveConnectionsFile", settingsJson);
    }

    [Fact]
    public void Json_AcceptsHandEditedFileWithDiscriminatorLast()
    {
        const string json = """
            {
              "version": 1,
              "connections": [
                { "name": "Edited", "host": "edited.local", "type": "rdp" }
              ]
            }
            """;
        var doc = JsonSerializer.Deserialize<ConnectionsDocument>(json, JsonDefaults.Options)!;
        var rdp = Assert.IsType<RdpConnection>(Assert.Single(doc.Connections));
        Assert.Equal("edited.local", rdp.Host);
    }

    [Fact]
    public void Load_RejectsNewerVersion()
    {
        var path = Path.Combine(_dir, "connections.json");
        File.WriteAllText(path, """{ "version": 99 }""");
        Assert.Throws<InvalidDataException>(() => new FileConnectionStore(path).Load());
    }
}
