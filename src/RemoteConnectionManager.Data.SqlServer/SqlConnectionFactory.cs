using Microsoft.Data.SqlClient;
using RemoteConnectionManager.Core.Services;
using RemoteConnectionManager.Core.Storage;

namespace RemoteConnectionManager.Data.SqlServer;

/// <summary>Opens connections to the shared database. Windows or Microsoft Entra sign in only, never a password.</summary>
public sealed class SqlConnectionFactory
{
    private readonly string _connectionString;
    private readonly Action<SqlConnection>? _afterOpen;

    /// <summary>"server/database", for the status bar.</summary>
    public string DisplayName { get; }

    public SqlConnectionFactory(SqlStorageSettings settings)
        : this(BuildConnectionString(settings), $"{settings.Server.Trim()}/{settings.Database.Trim()}")
    {
    }

    /// <param name="afterOpen">Runs on every new connection. Tests use it to impersonate a login.</param>
    public SqlConnectionFactory(string connectionString, string displayName, Action<SqlConnection>? afterOpen = null)
    {
        _connectionString = connectionString;
        DisplayName = displayName;
        _afterOpen = afterOpen;
    }

    public static string BuildConnectionString(SqlStorageSettings settings)
    {
        if (!settings.IsConfigured) throw new InvalidOperationException("Enter the SQL Server name and database in Settings.");
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = settings.Server.Trim(),
            InitialCatalog = settings.Database.Trim(),
            ApplicationName = "RemoteConnectionManager",
            ConnectTimeout = 10,
            CommandTimeout = 30,
            Encrypt = SqlConnectionEncryptOption.Mandatory,
            TrustServerCertificate = settings.TrustServerCertificate,
        };
        switch (settings.Authentication)
        {
            case SqlAuthentication.Windows:
                builder.IntegratedSecurity = true;
                break;
            case SqlAuthentication.Entra:
                builder.Authentication = SqlAuthenticationMethod.ActiveDirectoryDefault;
                break;
            case SqlAuthentication.EntraInteractive:
                builder.Authentication = SqlAuthenticationMethod.ActiveDirectoryInteractive;
                break;
        }
        return builder.ConnectionString;
    }

    /// <summary>Opens a connection. Failures to reach or sign in to the server surface as <see cref="SqlStoreUnavailableException"/>.</summary>
    public SqlConnection Open()
    {
        var connection = new SqlConnection(_connectionString);
        try
        {
            connection.Open();
        }
        catch (Exception ex) when (ex is SqlException or InvalidOperationException or TimeoutException)
        {
            connection.Dispose();
            throw new SqlStoreUnavailableException($"Could not connect to {DisplayName}: {ex.Message}", ex);
        }
        _afterOpen?.Invoke(connection);
        return connection;
    }

    internal static SqlCommand Proc(SqlConnection connection, string name, SqlTransaction? transaction = null) =>
        new(name, connection, transaction) { CommandType = System.Data.CommandType.StoredProcedure };
}

/// <summary>The database could not be reached or would not let the user in.</summary>
public sealed class SqlStoreUnavailableException(string message, Exception inner) : Exception(message, inner);

internal static class SqlErrors
{
    public const int Conflict = 50001;
    public const int Denied = 50003;
    public const int Invalid = 50004;
    public const int MissingProcedure = 2812;
    public const int PermissionDenied = 229;

    /// <summary>Turns RCM's own error numbers into the store exceptions the shell understands.</summary>
    public static Exception Translate(SqlException ex) => ex.Number switch
    {
        Conflict => new StoreConflictException(ex.Message),
        Denied => new StorePermissionException(ex.Message),
        Invalid => new InvalidOperationException(ex.Message, ex),
        PermissionDenied => new StorePermissionException("You are not in the rcm_user role on this database. Ask the database admin to add you."),
        MissingProcedure => new InvalidOperationException("This database does not have the RCM tables yet, or you are not in its rcm_user role. Someone with db_owner should open Settings > Storage and choose Create or upgrade.", ex),
        _ => ex,
    };

    public static T Run<T>(Func<T> action)
    {
        try { return action(); }
        catch (SqlException ex) { throw Translate(ex); }
    }

    public static void Run(Action action)
    {
        try { action(); }
        catch (SqlException ex) { throw Translate(ex); }
    }
}
