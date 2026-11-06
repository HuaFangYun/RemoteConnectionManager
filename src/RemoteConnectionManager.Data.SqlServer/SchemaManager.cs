using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace RemoteConnectionManager.Data.SqlServer;

/// <summary>What the database looks like to the current user.</summary>
/// <param name="Version">Schema version, or null when the RCM tables are missing or hidden from this user.</param>
/// <param name="IsDbOwner">True when the user may create or upgrade the schema.</param>
/// <param name="InRcmRole">True when the user is in rcm_user (or db_owner), so RCM will work for them.</param>
public sealed record SchemaStatus(string Login, int? Version, bool IsDbOwner, bool InRcmRole)
{
    public bool IsCurrent => Version == SchemaManager.LatestVersion;
    public bool NeedsUpgrade => Version is null || Version < SchemaManager.LatestVersion;
    public bool IsNewerThanApp => Version > SchemaManager.LatestVersion;

    public string Describe() => Version switch
    {
        null when IsDbOwner => $"Signed in as {Login}. The RCM tables are not there yet; you are db_owner, so you can create them.",
        null when !InRcmRole => $"Signed in as {Login}, but you are not in the rcm_user role (or the RCM tables have not been created).",
        null => $"Signed in as {Login}. The RCM tables have not been created yet; someone with db_owner needs to create them.",
        var v when v > SchemaManager.LatestVersion => $"Signed in as {Login}. The database is schema version {v}, newer than this RCM (version {SchemaManager.LatestVersion}). Update RCM.",
        var v when v < SchemaManager.LatestVersion && IsDbOwner => $"Signed in as {Login}. Schema version {v}; this RCM needs {SchemaManager.LatestVersion}. You are db_owner, so you can upgrade it.",
        var v when v < SchemaManager.LatestVersion => $"Signed in as {Login}. Schema version {v}; this RCM needs {SchemaManager.LatestVersion}. Someone with db_owner needs to upgrade it.",
        _ => $"Signed in as {Login}. Schema version {Version}, ready.",
    };
}

/// <summary>Creates and upgrades the rcm schema from the numbered scripts embedded in this assembly.</summary>
public sealed class SchemaManager(SqlConnectionFactory factory)
{
    private static readonly Regex GoLine = new(@"^\s*GO\s*;?\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ScriptName = new(@"Schema\.(\d{3})_[^.]+\.sql$", RegexOptions.Compiled);

    public static IReadOnlyList<(int Version, string Name)> Scripts { get; } = typeof(SchemaManager).Assembly
        .GetManifestResourceNames()
        .Select(n => (Match: ScriptName.Match(n), Name: n))
        .Where(x => x.Match.Success)
        .Select(x => (int.Parse(x.Match.Groups[1].Value), x.Name))
        .OrderBy(x => x.Item1)
        .ToList();

    public static int LatestVersion => Scripts.Count == 0 ? 0 : Scripts[^1].Version;

    public SchemaStatus GetStatus()
    {
        using var connection = factory.Open();
        string login;
        bool owner, inRole;
        using (var cmd = new SqlCommand("SELECT SUSER_SNAME(), ISNULL(IS_MEMBER(N'db_owner'), 0), ISNULL(IS_MEMBER(N'rcm_user'), 0)", connection))
        using (var reader = cmd.ExecuteReader())
        {
            reader.Read();
            login = reader.GetString(0);
            owner = reader.GetInt32(1) == 1;
            inRole = owner || reader.GetInt32(2) == 1;
        }

        int? version = null;
        try
        {
            using var cmd = SqlConnectionFactory.Proc(connection, "rcm.GetSchemaVersion");
            version = Convert.ToInt32(cmd.ExecuteScalar());
        }
        catch (SqlException ex) when (ex.Number is SqlErrors.MissingProcedure or SqlErrors.PermissionDenied)
        {
        }
        return new SchemaStatus(login, version, owner, inRole);
    }

    /// <summary>Applies every script newer than the database. The person who first creates the schema becomes an RCM admin.</summary>
    /// <returns>The versions applied.</returns>
    public IReadOnlyList<int> Upgrade()
    {
        var status = GetStatus();
        if (!status.IsDbOwner) throw new InvalidOperationException("Creating or upgrading the RCM tables needs db_owner on the database.");
        if (status.IsNewerThanApp) throw new InvalidOperationException(status.Describe());

        var current = status.Version ?? 0;
        var applied = new List<int>();
        using var connection = factory.Open();
        foreach (var (version, name) in Scripts.Where(s => s.Version > current))
        {
            using var tx = connection.BeginTransaction();
            foreach (var batch in SplitBatches(ReadScript(name)))
            {
                using var cmd = new SqlCommand(batch, connection, tx) { CommandTimeout = 120 };
                cmd.ExecuteNonQuery();
            }
            using (var record = new SqlCommand("INSERT rcm.SchemaVersion (Version) VALUES (@v)", connection, tx))
            {
                record.Parameters.AddWithValue("@v", version);
                record.ExecuteNonQuery();
            }
            tx.Commit();
            applied.Add(version);
        }

        if (current == 0)
        {
            using var who = SqlConnectionFactory.Proc(connection, "rcm.WhoAmI");
            who.ExecuteNonQuery();
            using var admin = new SqlCommand("UPDATE rcm.Users SET IsAdmin = 1 WHERE Login = SUSER_SNAME()", connection);
            admin.ExecuteNonQuery();
        }
        return applied;
    }

    internal static string ReadScript(string resourceName)
    {
        using var stream = typeof(SchemaManager).Assembly.GetManifestResourceStream(resourceName)
                           ?? throw new InvalidOperationException($"Missing embedded script {resourceName}.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    internal static IEnumerable<string> SplitBatches(string script) =>
        GoLine.Split(script).Select(b => b.Trim()).Where(b => b.Length > 0 && !IsOnlyComments(b));

    private static bool IsOnlyComments(string batch) =>
        batch.Split('\n').All(line => line.Trim().Length == 0 || line.TrimStart().StartsWith("--", StringComparison.Ordinal));
}
