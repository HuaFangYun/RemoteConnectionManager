using RemoteConnectionManager.Core.Services;
using RemoteConnectionManager.Core.Storage;
using RemoteConnectionManager.Data.SqlServer;

namespace RemoteConnectionManager.App.Services;

/// <summary>
/// The storage the shell is using right now: the connection tree, the person's own settings, and
/// (for SQL Server) user, group and permission admin. Rebuilt when Settings switches back end.
/// </summary>
public sealed class StorageBackend : IDisposable
{
    public IConnectionStore Store { get; }
    public IUserProfileStore Profile { get; }
    /// <summary>Null for the file back end.</summary>
    public SqlAdminService? Admin { get; }
    /// <summary>Null for the file back end.</summary>
    public SqlConnectionFactory? SqlFactory { get; }

    private StorageBackend(IConnectionStore store, IUserProfileStore profile, SqlAdminService? admin, SqlConnectionFactory? factory)
    {
        Store = store;
        Profile = profile;
        Admin = admin;
        SqlFactory = factory;
    }

    public static StorageBackend Create(AppSettings settings, AppSettingsStore settingsStore, AppLog log)
    {
        if (settings.Storage == StorageKind.SqlServer && settings.Sql.IsConfigured)
        {
            var factory = new SqlConnectionFactory(settings.Sql);
            var store = new SqlConnectionStore(factory, AppPaths.SqlCacheFile(settings.Sql.Server, settings.Sql.Database), log.Warn, settings.Sql.PollInterval);
            var profile = new SqlUserProfileStore(factory, AppPaths.SqlCacheFile(settings.Sql.Server, settings.Sql.Database, "personal"), log.Warn);
            return new StorageBackend(store, profile, new SqlAdminService(factory), factory);
        }
        return new StorageBackend(new FileConnectionStore(settings.EffectiveConnectionsFile), new LocalUserProfileStore(settings, settingsStore), null, null);
    }

    /// <summary>A key that changes when Settings points at different storage.</summary>
    public static string Identity(AppSettings settings) =>
        settings.Storage == StorageKind.SqlServer && settings.Sql.IsConfigured
            ? $"sql|{settings.Sql.Server.Trim()}|{settings.Sql.Database.Trim()}|{settings.Sql.Authentication}|{settings.Sql.TrustServerCertificate}".ToLowerInvariant()
            : $"file|{settings.EffectiveConnectionsFile}".ToLowerInvariant();

    public void Dispose() => Store.Dispose();
}
