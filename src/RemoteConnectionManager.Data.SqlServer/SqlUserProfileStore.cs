using System.Data;
using System.Text.Json;
using RemoteConnectionManager.Core.Models;
using RemoteConnectionManager.Core.Services;
using RemoteConnectionManager.Core.Storage;

namespace RemoteConnectionManager.Data.SqlServer;

/// <summary>
/// Keeps the person's preferences and personal entry settings in rcm.UserPreferences and
/// rcm.UserEntrySettings, so they follow them to any PC. When the database cannot be reached the
/// last copy (cached locally) is used and changes are kept in memory only.
/// </summary>
public sealed class SqlUserProfileStore(SqlConnectionFactory factory, string? cachePath, Action<string>? log = null) : IUserProfileStore
{
    internal const string ExpandedFoldersKey = "expandedFolders";
    internal const string DisabledPluginsKey = "disabledPlugins";
    internal const string NavigationWidthKey = "navigationWidth";
    internal const string NavigationVisibleKey = "navigationVisible";

    private readonly Dictionary<string, string> _saved = new(StringComparer.Ordinal);
    private Dictionary<Guid, PersonalEntrySettings> _entrySettings = new();
    private bool _entrySettingsLoaded;

    public void ApplyPreferences(AppSettings settings)
    {
        try
        {
            using var connection = factory.Open();
            using var cmd = SqlConnectionFactory.Proc(connection, "rcm.GetMyPreferences");
            using var reader = cmd.ExecuteReader();
            _saved.Clear();
            while (reader.Read()) _saved[reader.GetString(0)] = reader.GetString(1);
        }
        catch (Exception ex)
        {
            log?.Invoke($"Preferences not loaded from the database: {ex.Message}");
            return;
        }

        T? Get<T>(string key) => _saved.TryGetValue(key, out var json) ? JsonSerializer.Deserialize<T>(json, SqlConnectionStore.Compact) : default;
        try
        {
            if (Get<List<Guid>>(ExpandedFoldersKey) is { } expanded) settings.ExpandedFolders = expanded;
            if (Get<List<string>>(DisabledPluginsKey) is { } disabled) settings.DisabledPlugins = disabled;
            if (Get<double?>(NavigationWidthKey) is { } width) settings.NavigationWidth = width;
            if (Get<bool?>(NavigationVisibleKey) is { } visible) settings.NavigationVisible = visible;
        }
        catch (JsonException ex)
        {
            log?.Invoke($"A stored preference could not be read: {ex.Message}");
        }
    }

    public void SavePreferences(AppSettings settings)
    {
        var wanted = new Dictionary<string, string>
        {
            [ExpandedFoldersKey] = JsonSerializer.Serialize(settings.ExpandedFolders, SqlConnectionStore.Compact),
            [DisabledPluginsKey] = JsonSerializer.Serialize(settings.DisabledPlugins, SqlConnectionStore.Compact),
            [NavigationWidthKey] = JsonSerializer.Serialize(Math.Round(settings.NavigationWidth), SqlConnectionStore.Compact),
            [NavigationVisibleKey] = JsonSerializer.Serialize(settings.NavigationVisible, SqlConnectionStore.Compact),
        };
        var changed = wanted.Where(kv => !_saved.TryGetValue(kv.Key, out var old) || old != kv.Value).ToList();
        if (changed.Count == 0) return;
        try
        {
            using var connection = factory.Open();
            foreach (var (key, value) in changed)
            {
                using var cmd = SqlConnectionFactory.Proc(connection, "rcm.SetMyPreference");
                cmd.Parameters.Add("@Key", SqlDbType.NVarChar, 128).Value = key;
                cmd.Parameters.Add("@Value", SqlDbType.NVarChar, -1).Value = value;
                cmd.ExecuteNonQuery();
                _saved[key] = value;
            }
        }
        catch (Exception ex)
        {
            log?.Invoke($"Preferences not saved to the database: {ex.Message}");
        }
    }

    public IReadOnlyDictionary<Guid, PersonalEntrySettings> LoadEntrySettings()
    {
        try
        {
            var result = new Dictionary<Guid, PersonalEntrySettings>();
            using var connection = factory.Open();
            using var cmd = SqlConnectionFactory.Proc(connection, "rcm.GetMyEntrySettings");
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                try
                {
                    if (JsonSerializer.Deserialize<PersonalEntrySettings>(reader.GetString(1), SqlConnectionStore.Compact) is { } s)
                        result[reader.GetGuid(0)] = s;
                }
                catch (JsonException) { }
            }
            _entrySettings = result;
            _entrySettingsLoaded = true;
            WriteCache();
        }
        catch (Exception ex)
        {
            log?.Invoke($"Personal settings not loaded from the database: {ex.Message}");
            if (!_entrySettingsLoaded) ReadCache();
        }
        return new Dictionary<Guid, PersonalEntrySettings>(_entrySettings);
    }

    public void SaveEntrySettings(Guid targetId, PersonalEntrySettings? value)
    {
        var remove = value is null || value.IsEmpty;
        SqlErrors.Run(() =>
        {
            using var connection = factory.Open();
            using var cmd = SqlConnectionFactory.Proc(connection, "rcm.SetMyEntrySetting");
            cmd.Parameters.Add("@TargetId", SqlDbType.UniqueIdentifier).Value = targetId;
            cmd.Parameters.Add("@Body", SqlDbType.NVarChar, -1).Value = remove ? DBNull.Value : JsonSerializer.Serialize(value, SqlConnectionStore.Compact);
            cmd.ExecuteNonQuery();
        });
        if (remove) _entrySettings.Remove(targetId); else _entrySettings[targetId] = value!;
        WriteCache();
    }

    private void WriteCache()
    {
        if (cachePath is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
            File.WriteAllText(cachePath, JsonSerializer.Serialize(_entrySettings, SqlConnectionStore.Compact));
        }
        catch (Exception ex)
        {
            log?.Invoke($"Could not cache personal settings: {ex.Message}");
        }
    }

    private void ReadCache()
    {
        if (cachePath is null || !File.Exists(cachePath)) return;
        try
        {
            _entrySettings = JsonSerializer.Deserialize<Dictionary<Guid, PersonalEntrySettings>>(File.ReadAllText(cachePath), SqlConnectionStore.Compact) ?? new();
        }
        catch (Exception ex)
        {
            log?.Invoke($"Could not read cached personal settings: {ex.Message}");
        }
    }
}
