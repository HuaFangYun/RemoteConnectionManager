using Microsoft.Data.SqlClient;
using RemoteConnectionManager.Data.SqlServer;
using Xunit;

namespace RemoteConnectionManager.Data.SqlServer.Tests;

/// <summary>
/// A throwaway database on LocalDB with the RCM schema, created as the developer (sysadmin, so
/// db_owner and RCM admin). Test users are SQL logins that the tests impersonate with
/// EXECUTE AS LOGIN, so SUSER_SNAME() inside the procedures is the test user.
/// </summary>
public sealed class LocalDbFixture : IDisposable
{
    public const string Server = @"(localdb)\MSSQLLocalDB";

    public string Database { get; } = "RcmTest_" + Guid.NewGuid().ToString("N")[..12];
    public string Suffix { get; } = Guid.NewGuid().ToString("N")[..6];
    public bool Available { get; }
    public string? SkipReason { get; }

    private readonly List<string> _logins = new();

    public LocalDbFixture()
    {
        try
        {
            using (var master = new SqlConnection(ConnectionString("master")))
            {
                master.Open();
                Exec(master, $"CREATE DATABASE [{Database}]");
            }
            new SchemaManager(Admin()).Upgrade();
            Available = true;
        }
        catch (Exception ex)
        {
            SkipReason = $"LocalDB not available: {ex.Message}";
        }
    }

    public static string ConnectionString(string database) => new SqlConnectionStringBuilder
    {
        DataSource = Server,
        InitialCatalog = database,
        IntegratedSecurity = true,
        TrustServerCertificate = true,
        Pooling = false,
        ConnectTimeout = 30,
    }.ConnectionString;

    /// <summary>The developer: db_owner, so an RCM admin.</summary>
    public SqlConnectionFactory Admin() => new(ConnectionString(Database), "localdb/" + Database);

    /// <summary>Creates (once) a login in rcm_user and returns a factory whose connections run as it.</summary>
    public SqlConnectionFactory As(string name, bool inRole = true)
    {
        var login = LoginName(name);
        if (!_logins.Contains(login))
        {
            using var connection = new SqlConnection(ConnectionString(Database));
            connection.Open();
            Exec(connection, $"CREATE LOGIN [{login}] WITH PASSWORD = N'{Guid.NewGuid():N}Aa1!', CHECK_POLICY = OFF");
            Exec(connection, $"CREATE USER [{login}] FOR LOGIN [{login}]");
            if (inRole) Exec(connection, $"ALTER ROLE rcm_user ADD MEMBER [{login}]");
            _logins.Add(login);
        }
        return new SqlConnectionFactory(ConnectionString(Database), "localdb/" + Database,
            c => Exec(c, $"EXECUTE AS LOGIN = N'{login}'"));
    }

    public string LoginName(string name) => $"rcm_t_{name}_{Suffix}";

    public void Sql(string sql)
    {
        using var connection = new SqlConnection(ConnectionString(Database));
        connection.Open();
        Exec(connection, sql);
    }

    public T Scalar<T>(string sql)
    {
        using var connection = new SqlConnection(ConnectionString(Database));
        connection.Open();
        using var cmd = new SqlCommand(sql, connection);
        return (T)cmd.ExecuteScalar();
    }

    private static void Exec(SqlConnection connection, string sql)
    {
        using var cmd = new SqlCommand(sql, connection);
        cmd.ExecuteNonQuery();
    }

    public void Dispose()
    {
        if (!Available) return;
        try
        {
            SqlConnection.ClearAllPools();
            using var master = new SqlConnection(ConnectionString("master"));
            master.Open();
            Exec(master, $"ALTER DATABASE [{Database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{Database}]");
            foreach (var login in _logins) Exec(master, $"DROP LOGIN [{login}]");
        }
        catch
        {
            // Leave it; a stray test database on LocalDB is harmless.
        }
    }
}

[CollectionDefinition(Name)]
public sealed class LocalDbCollection : ICollectionFixture<LocalDbFixture>
{
    public const string Name = "LocalDB";
}
