using RemoteConnectionManager.Core.Models;
using RemoteConnectionManager.Core.Services;

namespace RemoteConnectionManager.Core.Storage;

/// <summary>
/// Things that belong to the person rather than the shared tree: preferences that follow them
/// between PCs, and personal settings on entries and folders (their own watched services).
/// </summary>
public interface IUserProfileStore
{
    /// <summary>Copies the stored preferences onto the settings (expanded folders, plugins switched off, and so on).</summary>
    void ApplyPreferences(AppSettings settings);

    /// <summary>Stores the preferences from the settings.</summary>
    void SavePreferences(AppSettings settings);

    IReadOnlyDictionary<Guid, PersonalEntrySettings> LoadEntrySettings();

    /// <summary>Stores or (with null or an empty value) removes the personal settings for an entry or folder.</summary>
    void SaveEntrySettings(Guid targetId, PersonalEntrySettings? value);
}

/// <summary>Keeps the profile in the local settings.json. Used with the file back end.</summary>
public sealed class LocalUserProfileStore(AppSettings settings, AppSettingsStore settingsStore) : IUserProfileStore
{
    public void ApplyPreferences(AppSettings target) { }

    public void SavePreferences(AppSettings source) { }

    public IReadOnlyDictionary<Guid, PersonalEntrySettings> LoadEntrySettings() =>
        new Dictionary<Guid, PersonalEntrySettings>(settings.PersonalEntrySettings);

    public void SaveEntrySettings(Guid targetId, PersonalEntrySettings? value)
    {
        if (value is null || value.IsEmpty) settings.PersonalEntrySettings.Remove(targetId);
        else settings.PersonalEntrySettings[targetId] = value;
        settingsStore.Save(settings);
    }
}
