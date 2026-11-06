using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using RemoteConnectionManager.Core.Storage;

namespace RemoteConnectionManager.Data.SqlServer;

public sealed record RcmUser(int Id, string Login, string? DisplayName, bool IsAdmin, DateTime? LastSeenUtc, string? Groups);

public sealed record RcmGroup(int Id, string Name, string? Description, int MemberCount);

/// <param name="FromFolder">For an inherited permission, the folder it is set on.</param>
public sealed record FolderPermission(int GroupId, string GroupName, AccessLevel Level, string? FromFolder = null);

public sealed record FolderPermissionSet(bool BreakInheritance, IReadOnlyList<FolderPermission> Own, IReadOnlyList<FolderPermission> Inherited);

public sealed record ChangeLogRow(long Id, DateTime ChangedUtc, string ChangedBy, string Action, string TargetKind, string? TargetName);

/// <summary>Users, groups and folder permissions. The database checks that the caller is an RCM admin (or has Manage on the folder).</summary>
public sealed class SqlAdminService(SqlConnectionFactory factory)
{
    public IReadOnlyList<RcmUser> ListUsers() => Query("rcm.ListUsers", null, r => new RcmUser(
        r.GetInt32(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.GetBoolean(3),
        r.IsDBNull(4) ? null : r.GetDateTime(4), r.IsDBNull(5) ? null : r.GetString(5)));

    /// <returns>The user's id.</returns>
    public int SaveUser(int? id, string login, string? displayName, bool isAdmin) => Scalar<int>("rcm.SaveUser", p =>
    {
        p.Add("@Id", SqlDbType.Int).Value = (object?)id ?? DBNull.Value;
        p.Add("@Login", SqlDbType.NVarChar, 256).Value = login;
        p.Add("@DisplayName", SqlDbType.NVarChar, 256).Value = (object?)displayName ?? DBNull.Value;
        p.Add("@IsAdmin", SqlDbType.Bit).Value = isAdmin;
    });

    public void DeleteUser(int id) => Execute("rcm.DeleteUser", p => p.Add("@Id", SqlDbType.Int).Value = id);

    public IReadOnlyList<RcmGroup> ListGroups() => Query("rcm.ListGroups", null, r => new RcmGroup(
        r.GetInt32(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.GetInt32(3)));

    /// <returns>The group's id.</returns>
    public int SaveGroup(int? id, string name, string? description) => Scalar<int>("rcm.SaveGroup", p =>
    {
        p.Add("@Id", SqlDbType.Int).Value = (object?)id ?? DBNull.Value;
        p.Add("@Name", SqlDbType.NVarChar, 128).Value = name;
        p.Add("@Description", SqlDbType.NVarChar, 1000).Value = (object?)description ?? DBNull.Value;
    });

    public void DeleteGroup(int id) => Execute("rcm.DeleteGroup", p => p.Add("@Id", SqlDbType.Int).Value = id);

    public IReadOnlyList<int> GetGroupMembers(int groupId) =>
        Query("rcm.GetGroupMembers", p => p.Add("@GroupId", SqlDbType.Int).Value = groupId, r => r.GetInt32(0));

    public void SetGroupMembers(int groupId, IEnumerable<int> userIds) => Execute("rcm.SetGroupMembers", p =>
    {
        p.Add("@GroupId", SqlDbType.Int).Value = groupId;
        p.Add("@UserIds", SqlDbType.NVarChar, -1).Value = JsonSerializer.Serialize(userIds.Distinct().ToArray());
    });

    public FolderPermissionSet GetFolderPermissions(Guid folderId) => SqlErrors.Run(() =>
    {
        using var connection = factory.Open();
        using var cmd = SqlConnectionFactory.Proc(connection, "rcm.GetFolderPermissions");
        cmd.Parameters.Add("@FolderId", SqlDbType.UniqueIdentifier).Value = folderId;
        using var reader = cmd.ExecuteReader();
        reader.Read();
        var breakInheritance = !reader.IsDBNull(0) && reader.GetBoolean(0);
        var own = new List<FolderPermission>();
        reader.NextResult();
        while (reader.Read()) own.Add(new FolderPermission(reader.GetInt32(0), reader.GetString(1), (AccessLevel)reader.GetByte(2)));
        var inherited = new List<FolderPermission>();
        reader.NextResult();
        while (reader.Read()) inherited.Add(new FolderPermission(reader.GetInt32(0), reader.GetString(1), (AccessLevel)reader.GetByte(2), reader.GetString(3)));
        return new FolderPermissionSet(breakInheritance, own, inherited);
    });

    public void SetFolderPermissions(Guid folderId, bool breakInheritance, IEnumerable<FolderPermission> permissions) => Execute("rcm.SetFolderPermissions", p =>
    {
        p.Add("@FolderId", SqlDbType.UniqueIdentifier).Value = folderId;
        p.Add("@BreakInheritance", SqlDbType.Bit).Value = breakInheritance;
        p.Add("@Permissions", SqlDbType.NVarChar, -1).Value = JsonSerializer.Serialize(
            permissions.Where(x => x.Level != AccessLevel.None).Select(x => new { groupId = x.GroupId, level = (int)x.Level }));
    });

    public IReadOnlyList<ChangeLogRow> GetChangeLog(int top = 200) => Query("rcm.GetChangeLog", p => p.Add("@Top", SqlDbType.Int).Value = top,
        r => new ChangeLogRow(r.GetInt64(0), r.GetDateTime(1), r.GetString(2), r.GetString(3), r.GetString(4), r.IsDBNull(6) ? null : r.GetString(6)));

    // ---------- plumbing ----------

    private List<T> Query<T>(string proc, Action<SqlParameterCollection>? parameters, Func<SqlDataReader, T> map) => SqlErrors.Run(() =>
    {
        using var connection = factory.Open();
        using var cmd = SqlConnectionFactory.Proc(connection, proc);
        parameters?.Invoke(cmd.Parameters);
        using var reader = cmd.ExecuteReader();
        var list = new List<T>();
        while (reader.Read()) list.Add(map(reader));
        return list;
    });

    private T Scalar<T>(string proc, Action<SqlParameterCollection> parameters) => SqlErrors.Run(() =>
    {
        using var connection = factory.Open();
        using var cmd = SqlConnectionFactory.Proc(connection, proc);
        parameters(cmd.Parameters);
        return (T)Convert.ChangeType(cmd.ExecuteScalar(), typeof(T));
    });

    private void Execute(string proc, Action<SqlParameterCollection> parameters) => SqlErrors.Run(() =>
    {
        using var connection = factory.Open();
        using var cmd = SqlConnectionFactory.Proc(connection, proc);
        parameters(cmd.Parameters);
        cmd.ExecuteNonQuery();
    });
}
