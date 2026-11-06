using RemoteConnectionManager.Core.Models;
using RemoteConnectionManager.Core.Services;

namespace RemoteConnectionManager.App.Services;

/// <summary>Windows the view models need to open. Implemented by the App so view models stay testable.</summary>
public interface IDialogService
{
    /// <summary>Edits the entry in place. Returns false when the user cancels (the entry is left untouched).</summary>
    /// <param name="knownVaults">Vault addresses offered when choosing a Key Vault credential.</param>
    /// <param name="inheritedColour">The icon colour the folders above pass down, so the editor can preview it.</param>
    bool EditEntry(ConnectionEntry entry, bool isNew, IReadOnlyList<string> knownVaults, string? inheritedColour = null);
    bool EditFolder(ConnectionFolder folder, bool isNew, IReadOnlyList<string> knownVaults, string? inheritedColour = null);
    bool EditSettings(AppSettings settings);
    /// <summary>The user's own watched services for an entry or folder. Null when cancelled.</summary>
    PersonalEntrySettings? EditPersonalWatchList(string targetName, IReadOnlyList<string> sharedList, PersonalEntrySettings current, bool followsUser);
    /// <summary>Folder permissions (SQL Server). True when something was saved.</summary>
    bool EditFolderPermissions(Data.SqlServer.SqlAdminService admin, ConnectionFolder folder);
    /// <summary>Users and groups (SQL Server, RCM admins).</summary>
    void ShowAdmin(Data.SqlServer.SqlAdminService admin, string? signedInAs);
    string? PickOpenFile(string title, string filter);
    string? PickSaveFile(string title, string filter, string fileName);
    /// <summary>Shows the vault item editor. True when the user saved and the view model validated.</summary>
    bool EditVaultItem(ViewModels.VaultItemEditorViewModel editor);
    bool Confirm(string message, string title);
    void Info(string message, string title);
    void Error(string message, string title);
}
