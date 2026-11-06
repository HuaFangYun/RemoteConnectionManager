using System.Windows;
using RemoteConnectionManager.Core.Models;
using RemoteConnectionManager.Plugins;

namespace RemoteConnectionManager.App.Views;

/// <summary>Edits one person's own watched services on an entry or folder. Colleagues are not affected.</summary>
public partial class PersonalWatchListWindow : Window
{
    public PersonalEntrySettings? Result { get; private set; }

    public PersonalWatchListWindow(string targetName, IReadOnlyList<string> sharedList, PersonalEntrySettings current, bool followsUser)
    {
        InitializeComponent();
        TitleText.Text = targetName;
        IntroText.Text = followsUser
            ? "Services only you want Check services to list here. Saved against your login in the shared database, so they follow you to any PC and nobody else sees them."
            : "Services only you want Check services to list here. Saved in your settings on this PC, not in the connections file.";
        SharedText.Text = sharedList.Count == 0 ? "(none)" : string.Join(Environment.NewLine, sharedList);
        MineBox.Text = string.Join(Environment.NewLine, current.WatchedServices ?? []);
        ReplaceBox.IsChecked = current.ReplaceSharedWatchList;
        Loaded += (_, _) => MineBox.Focus();
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        Result = new PersonalEntrySettings();
        DialogResult = true;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var list = ServiceFilter.ParseList(MineBox.Text);
        Result = new PersonalEntrySettings
        {
            WatchedServices = list.Count == 0 ? null : list.ToList(),
            ReplaceSharedWatchList = ReplaceBox.IsChecked == true,
        };
        DialogResult = true;
    }
}
