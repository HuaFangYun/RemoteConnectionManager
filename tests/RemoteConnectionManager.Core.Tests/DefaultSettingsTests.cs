using RemoteConnectionManager.Core.Services;
using RemoteConnectionManager.Core.Storage;
using Xunit;

namespace RemoteConnectionManager.Core.Tests;

/// <summary>Machine wide default settings in ProgramData, copied to a new user's settings file.</summary>
public sealed class DefaultSettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"rcm-defaults-{Guid.NewGuid():N}");
    private string UserFile => Path.Combine(_dir, "user", "settings.json");
    private string DefaultsFile => Path.Combine(_dir, "default-settings.json");

    public DefaultSettingsTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private const string SqlDefaults = """
        {
          // Comments are fine: admins hand edit this file.
          "storage": "sqlServer",
          "sql": { "server": "sql01.corp.local", "database": "RCM", "authentication": "windows" },
          "disabledPlugins": [ "onepassword" ],
        }
        """;

    [Fact]
    public void NewUser_GetsDefaults_AndTheyAreCopied()
    {
        File.WriteAllText(DefaultsFile, SqlDefaults);

        var settings = new AppSettingsStore(UserFile, DefaultsFile).Load();

        Assert.Equal(StorageKind.SqlServer, settings.Storage);
        Assert.Equal("sql01.corp.local", settings.Sql.Server);
        Assert.Equal("RCM", settings.Sql.Database);
        Assert.Equal(["onepassword"], settings.DisabledPlugins);
        Assert.True(File.Exists(UserFile));
    }

    [Fact]
    public void ExistingUser_IgnoresDefaults()
    {
        File.WriteAllText(DefaultsFile, SqlDefaults);
        var store = new AppSettingsStore(UserFile, DefaultsFile);
        store.Save(new AppSettings { NavigationWidth = 420 });

        var settings = store.Load();

        Assert.Equal(StorageKind.File, settings.Storage);
        Assert.Equal(420, settings.NavigationWidth);
    }

    [Fact]
    public void ChangedDefaults_DoNotOverwriteAUserWhoAlreadyStarted()
    {
        File.WriteAllText(DefaultsFile, SqlDefaults);
        var store = new AppSettingsStore(UserFile, DefaultsFile);
        store.Load();

        File.WriteAllText(DefaultsFile, """{ "sql": { "server": "sql02" } }""");

        Assert.Equal("sql01.corp.local", store.Load().Sql.Server);
    }

    [Fact]
    public void BrokenDefaults_AreIgnored_AndNotCopied()
    {
        File.WriteAllText(DefaultsFile, "{ this is not json");

        var settings = new AppSettingsStore(UserFile, DefaultsFile).Load();

        Assert.Equal(StorageKind.File, settings.Storage);
        Assert.False(File.Exists(UserFile));
    }

    [Fact]
    public void NoDefaultsFile_GivesBuiltInSettings()
    {
        var settings = new AppSettingsStore(UserFile, DefaultsFile).Load();

        Assert.Equal(StorageKind.File, settings.Storage);
        Assert.False(File.Exists(UserFile));
    }

    [Fact]
    public void ExplicitUserPath_NeverReadsMachineDefaults()
    {
        Assert.Null(new AppSettingsStore(UserFile).DefaultsPath);
        Assert.Equal(AppPaths.MachineDefaultSettingsFile, new AppSettingsStore().DefaultsPath);
    }

    [Fact]
    public void ConnectionsFilePath_ExpandsEnvironmentVariables()
    {
        var settings = new AppSettings { ConnectionsFilePath = @"%USERPROFILE%\Team\connections.json" };

        Assert.Equal(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Team", "connections.json"),
            settings.EffectiveConnectionsFile);
    }
}
