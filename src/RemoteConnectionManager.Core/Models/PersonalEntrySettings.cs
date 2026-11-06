using System.Text.Json.Serialization;

namespace RemoteConnectionManager.Core.Models;

/// <summary>
/// One person's own settings on an entry or folder, kept apart from the shared tree so
/// colleagues are not affected. Folder settings apply to everything beneath, like shared defaults.
/// </summary>
public sealed class PersonalEntrySettings
{
    /// <summary>Extra service patterns this person wants Check services to list.</summary>
    public List<string>? WatchedServices { get; set; }

    /// <summary>True to ignore the shared watch list and use only personal patterns.</summary>
    public bool ReplaceSharedWatchList { get; set; }

    [JsonIgnore]
    public bool IsEmpty => (WatchedServices is null || WatchedServices.Count == 0) && !ReplaceSharedWatchList;
}
