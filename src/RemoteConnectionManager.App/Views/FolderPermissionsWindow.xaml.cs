using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using RemoteConnectionManager.Core.Models;
using RemoteConnectionManager.Core.Storage;
using RemoteConnectionManager.Data.SqlServer;

namespace RemoteConnectionManager.App.Views;

/// <summary>One group's level on the folder being edited.</summary>
public sealed partial class PermissionRow(int groupId, string groupName, AccessLevel level) : ObservableObject
{
    public static IReadOnlyList<AccessLevel> Levels { get; } = [AccessLevel.View, AccessLevel.Edit, AccessLevel.Manage];

    public int GroupId { get; } = groupId;
    public string GroupName { get; } = groupName;
    [ObservableProperty] private AccessLevel _level = level;
}

/// <summary>Which RCM groups can see, edit or manage a folder. The database checks the user has Manage.</summary>
public partial class FolderPermissionsWindow : Window
{
    private readonly SqlAdminService _admin;
    private readonly ConnectionFolder _folder;
    private readonly ObservableCollection<PermissionRow> _own = new();
    private FolderPermissionSet? _loaded;

    public FolderPermissionsWindow(SqlAdminService admin, ConnectionFolder folder)
    {
        _admin = admin;
        _folder = folder;
        InitializeComponent();
        TitleText.Text = $"Permissions for '{folder.Name}'";
        OwnList.ItemsSource = _own;
        _own.CollectionChanged += (_, _) => UpdateEmptyTexts();
        AddLevelBox.SelectedItem = AccessLevel.View;
        Loaded += (_, _) => LoadPermissions();
    }

    private void LoadPermissions()
    {
        try
        {
            _loaded = _admin.GetFolderPermissions(_folder.Id);
            var groups = _admin.ListGroups();
            GroupBox.ItemsSource = groups;
            if (groups.Count > 0) GroupBox.SelectedIndex = 0;
            NoGroupsText.Visibility = groups.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            BreakBox.IsChecked = _loaded.BreakInheritance;
            InheritedList.ItemsSource = _loaded.Inherited;
            foreach (var p in _loaded.Own) _own.Add(new PermissionRow(p.GroupId, p.GroupName, p.Level));
            UpdateEmptyTexts();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Folder permissions", MessageBoxButton.OK, MessageBoxImage.Error);
            DialogResult = false;
        }
    }

    private void UpdateEmptyTexts()
    {
        var breaks = BreakBox.IsChecked == true;
        InheritedPanel.Visibility = breaks ? Visibility.Collapsed : Visibility.Visible;
        NoInheritedText.Visibility = _loaded is { Inherited.Count: > 0 } ? Visibility.Collapsed : Visibility.Visible;
        NoOwnText.Visibility = _own.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        InheritedBorder.Visibility = _loaded is { Inherited.Count: > 0 } ? Visibility.Visible : Visibility.Collapsed;
        OwnBorder.Visibility = _own.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        WarningText.Text = breaks && _own.Count == 0
            ? "With inheritance off and no permissions set, only RCM admins will see this folder."
            : string.Empty;
    }

    private void Break_Changed(object sender, RoutedEventArgs e) => UpdateEmptyTexts();

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        if (GroupBox.SelectedItem is not RcmGroup group || AddLevelBox.SelectedItem is not AccessLevel level) return;
        var existing = _own.FirstOrDefault(r => r.GroupId == group.Id);
        if (existing is not null) existing.Level = level;
        else _own.Add(new PermissionRow(group.Id, group.Name, level));
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is PermissionRow row) _own.Remove(row);
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _admin.SetFolderPermissions(_folder.Id, BreakBox.IsChecked == true,
                _own.Select(r => new FolderPermission(r.GroupId, r.GroupName, r.Level)));
            DialogResult = true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Folder permissions", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
