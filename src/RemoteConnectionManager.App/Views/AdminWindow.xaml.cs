using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using RemoteConnectionManager.Data.SqlServer;

namespace RemoteConnectionManager.App.Views;

/// <summary>One RCM user in the admin window.</summary>
public sealed partial class UserRow(RcmUser user) : ObservableObject
{
    public RcmUser User { get; } = user;
    public int Id => User.Id;
    public string Login => User.Login;
    public string? Groups => User.Groups;
    public string Title => string.IsNullOrWhiteSpace(User.DisplayName) ? User.Login : $"{User.DisplayName}  ({User.Login})";
    public string LastSeenText => User.LastSeenUtc is { } t ? t.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "not yet";

    [ObservableProperty] private bool _isAdmin = user.IsAdmin;
    [ObservableProperty] private bool _isMember;
    [ObservableProperty] private bool _canTickMember;
}

/// <summary>RCM users, groups and memberships. Every change is saved at once; the database checks the caller is an RCM admin.</summary>
public partial class AdminWindow : Window
{
    private readonly SqlAdminService _admin;
    private readonly string? _signedInAs;
    private List<UserRow> _users = new();
    /// <summary>Set while the window itself changes row values, so only the user's ticks are saved.</summary>
    private bool _loading;
    /// <summary>The user whose login and display name are in the boxes below the list; null when the boxes add a new user.</summary>
    private int? _editingId;
    private readonly string _addHelp;

    public AdminWindow(SqlAdminService admin, string? signedInAs)
    {
        _admin = admin;
        _signedInAs = signedInAs;
        InitializeComponent();
        _addHelp = UserHelpText.Text;
        Loaded += (_, _) => Reload();
    }

    private RcmGroup? SelectedGroup => GroupList.SelectedItem as RcmGroup;

    private void Reload(int? selectGroupId = null)
    {
        Try("Loading", () =>
        {
            var keep = selectGroupId ?? SelectedGroup?.Id;
            var groups = _admin.ListGroups();
            GroupList.ItemsSource = groups;
            GroupList.SelectedItem = groups.FirstOrDefault(g => g.Id == keep) ?? groups.FirstOrDefault();
            foreach (var old in _users) old.PropertyChanged -= Row_PropertyChanged;
            _users = _admin.ListUsers().Select(u => new UserRow(u)).ToList();
            foreach (var row in _users) row.PropertyChanged += Row_PropertyChanged;
            UserList.ItemsSource = _users;
            LoadMembers();
        });
    }

    private void LoadMembers()
    {
        var group = SelectedGroup;
        GroupNameBox.Text = group?.Name ?? string.Empty;
        GroupDescriptionBox.Text = group?.Description ?? string.Empty;
        UsersTitle.Text = group is null ? "Users" : $"Users (tick to put them in {group.Name})";
        var members = group is null ? new HashSet<int>() : _admin.GetGroupMembers(group.Id).ToHashSet();
        _loading = true;
        try
        {
            foreach (var row in _users)
            {
                row.IsMember = members.Contains(row.Id);
                row.CanTickMember = group is not null;
            }
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>A tick from the mouse, keyboard or UI Automation. Saved after the binding update finishes.</summary>
    private void Row_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_loading || sender is not UserRow row) return;
        if (e.PropertyName == nameof(UserRow.IsMember)) Dispatcher.BeginInvoke(() => SaveMembership(row));
        else if (e.PropertyName == nameof(UserRow.IsAdmin)) Dispatcher.BeginInvoke(() => SaveAdmin(row));
    }

    private void GroupList_SelectionChanged(object sender, SelectionChangedEventArgs e) => Try("Loading members", LoadMembers);

    private void NewGroup_Click(object sender, RoutedEventArgs e)
    {
        GroupList.SelectedItem = null;
        GroupNameBox.Text = string.Empty;
        GroupDescriptionBox.Text = string.Empty;
        GroupNameBox.Focus();
        StatusText.Text = "Type a name and press Save group.";
    }

    private void SaveGroup_Click(object sender, RoutedEventArgs e) => Try("Saving the group", () =>
    {
        var id = _admin.SaveGroup(SelectedGroup?.Id, GroupNameBox.Text, GroupDescriptionBox.Text);
        StatusText.Text = $"Saved group {GroupNameBox.Text.Trim()}.";
        Reload(id);
    });

    private void DeleteGroup_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedGroup is not { } group) return;
        if (MessageBox.Show(this, $"Delete group '{group.Name}'? Its members lose the folder access it gave them.", "Delete group",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        Try("Deleting the group", () =>
        {
            _admin.DeleteGroup(group.Id);
            StatusText.Text = $"Deleted group {group.Name}.";
            Reload();
        });
    }

    private void SaveMembership(UserRow row)
    {
        if (SelectedGroup is not { } group) return;
        Try("Saving members", () =>
        {
            _admin.SetGroupMembers(group.Id, _users.Where(u => u.IsMember).Select(u => u.Id));
            StatusText.Text = $"{(row.IsMember ? "Added" : "Removed")} {row.Login} {(row.IsMember ? "to" : "from")} {group.Name}.";
            Reload(group.Id);
        });
    }

    private void SaveAdmin(UserRow row)
    {
        var makeAdmin = row.IsAdmin;
        if (!makeAdmin && string.Equals(row.Login, _signedInAs, StringComparison.OrdinalIgnoreCase) &&
            MessageBox.Show(this, "Remove your own admin rights? You will not be able to open this window again unless another admin (or db_owner) gives them back.",
                "Users and groups", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            Reload();
            return;
        }
        Try("Saving the user", () =>
        {
            _admin.SaveUser(row.Id, row.Login, row.User.DisplayName, makeAdmin);
            StatusText.Text = $"{row.Login} is {(makeAdmin ? "now" : "no longer")} an RCM admin.";
            Reload();
        });
    }

    private void EditUser_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is UserRow row) BeginEdit(row);
    }

    /// <summary>Double-clicking a row edits it, unless the click landed on one of its check boxes or buttons.</summary>
    private void UserList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source) return;
        for (var d = source; d is not null && d != UserList; d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
            if (d is System.Windows.Controls.Primitives.ButtonBase) return;
        if (ItemsControl.ContainerFromElement(UserList, source) is ListBoxItem { DataContext: UserRow row }) BeginEdit(row);
    }

    private void BeginEdit(UserRow row)
    {
        _editingId = row.Id;
        NewLoginBox.Text = row.Login;
        NewDisplayNameBox.Text = row.User.DisplayName ?? string.Empty;
        SaveUserButton.Content = "Save user";
        CancelEditButton.Visibility = Visibility.Visible;
        UserHelpText.Text = $"Editing {row.Login}. Change the display name or login and press Save user, or Cancel (Esc) to stop.";
        NewDisplayNameBox.Focus();
        NewDisplayNameBox.SelectAll();
    }

    private void EndEdit()
    {
        _editingId = null;
        NewLoginBox.Text = string.Empty;
        NewDisplayNameBox.Text = string.Empty;
        SaveUserButton.Content = "Add user";
        CancelEditButton.Visibility = Visibility.Collapsed;
        UserHelpText.Text = _addHelp;
    }

    private void CancelEdit_Click(object sender, RoutedEventArgs e) => EndEdit();

    /// <summary>Esc cancels an edit instead of closing the window.</summary>
    private void UserBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || _editingId is null) return;
        EndEdit();
        e.Handled = true;
    }

    private void SaveUser_Click(object sender, RoutedEventArgs e)
    {
        var login = NewLoginBox.Text.Trim();
        var displayName = NewDisplayNameBox.Text.Trim();
        if (login.Length == 0)
        {
            StatusText.Text = "Enter a login first.";
            return;
        }
        if (_editingId is not { } id)
        {
            Try("Adding the user", () =>
            {
                _admin.SaveUser(null, login, displayName, isAdmin: false);
                EndEdit();
                StatusText.Text = $"Added {login}.";
                Reload();
            });
            return;
        }

        if (_users.FirstOrDefault(u => u.Id == id) is not { } row)
        {
            EndEdit();
            StatusText.Text = "That user no longer exists.";
            Reload();
            return;
        }
        if (!string.Equals(row.Login, login, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(row.Login, _signedInAs, StringComparison.OrdinalIgnoreCase) &&
            MessageBox.Show(this, $"Change your own login to {login}? RCM matches you by login, so unless this is exactly how SQL Server sees you, you lose your admin rights and groups.",
                "Users and groups", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        Try("Saving the user", () =>
        {
            _admin.SaveUser(id, login, displayName, row.IsAdmin);
            EndEdit();
            StatusText.Text = $"Saved {login}.";
            Reload();
        });
    }

    private void DeleteUser_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not UserRow row) return;
        if (MessageBox.Show(this, $"Remove {row.Login} from RCM? Their groups and personal settings go too. If they still have database access they are added back (with no groups) next time they open RCM.",
                "Remove user", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        Try("Removing the user", () =>
        {
            _admin.DeleteUser(row.Id);
            if (_editingId == row.Id) EndEdit();
            StatusText.Text = $"Removed {row.Login}.";
            Reload();
        });
    }

    private void Changes_Expanded(object sender, RoutedEventArgs e) => Try("Loading changes", () => ChangeList.ItemsSource = _admin.GetChangeLog());

    private void Try(string what, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"{what} failed.";
            MessageBox.Show(this, ex.Message, "Users and groups", MessageBoxButton.OK, MessageBoxImage.Error);
            if (!what.StartsWith("Loading", StringComparison.Ordinal)) Dispatcher.BeginInvoke(() => Reload());
        }
    }
}
