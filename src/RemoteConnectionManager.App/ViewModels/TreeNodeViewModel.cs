using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using RemoteConnectionManager.Core.Models;
using RemoteConnectionManager.Core.Services;

namespace RemoteConnectionManager.App.ViewModels;

/// <summary>A folder or entry in the navigation tree. Wraps the Core model, never copies it.</summary>
public sealed partial class TreeNodeViewModel : ObservableObject
{
    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private bool _isVisible = true;
    [ObservableProperty] private int _openSessions;

    public ConnectionFolder? Folder { get; }
    public ConnectionEntry? Entry { get; }
    public TreeNodeViewModel? Parent { get; }
    public ObservableCollection<TreeNodeViewModel> Children { get; } = new();

    public bool IsFolder => Folder is not null;
    public bool HasOpenSession => OpenSessions > 0;

    public string DisplayName => Folder is not null ? Folder.Name : $"{Entry!.Prefix} {Entry.Name}";

    public string Glyph => Folder is not null ? "\uE8D5" : GlyphFor(Entry!.Kind);

    /// <summary>The tree glyph for an entry type. A null kind gives the folder glyph.</summary>
    public static string GlyphFor(ConnectionKind? kind) => kind is null
        ? "\uE8D5"
        : kind switch
        {
            ConnectionKind.Rdp => "",
            ConnectionKind.Terminal => "",
            ConnectionKind.KeyVault => "",
            ConnectionKind.Ssh => "",
            ConnectionKind.Web => "",
            _ => "",
        };

    /// <summary>
    /// The user's icon colour ("#RRGGBB") set here or on the nearest folder above, or null.
    /// Folders show their own colour too, so a red Production folder looks red.
    /// </summary>
    public string? IconColour =>
        IconColours.Normalise(Folder is not null ? Folder.Defaults.IconColour : Entry!.IconColour) ?? Parent?.IconColour;

    /// <summary>Glyph colour: the user's colour if set, otherwise the theme brush key for the type (Icon.* tokens in Themes/Rcm.xaml).</summary>
    public string IconBrushKey => IconColour ?? TypeBrushKey;

    private string TypeBrushKey => TypeBrushKeyFor(Folder is not null ? null : Entry!.Kind);

    /// <summary>The theme brush key for an entry type. A null kind gives the folder's.</summary>
    public static string TypeBrushKeyFor(ConnectionKind? kind) => kind is null
        ? "Icon.Folder"
        : kind switch
        {
            ConnectionKind.Rdp => "Icon.RemoteDesktop",
            ConnectionKind.Terminal => "Icon.Terminal",
            ConnectionKind.KeyVault => "Icon.Vault",
            ConnectionKind.Ssh => "Icon.NetworkDevice",
            ConnectionKind.Web => "Icon.Website",
            _ => "Icon.Default",
        };

    public string Path
    {
        get
        {
            var parts = new List<string>();
            for (var n = Parent; n is not null; n = n.Parent) parts.Insert(0, n.Folder!.Name);
            return string.Join(" / ", parts);
        }
    }

    public TreeNodeViewModel(ConnectionFolder folder, TreeNodeViewModel? parent)
    {
        Folder = folder;
        Parent = parent;
        foreach (var child in folder.Folders) Children.Add(new TreeNodeViewModel(child, this));
        foreach (var entry in folder.Connections) Children.Add(new TreeNodeViewModel(entry, this));
    }

    public TreeNodeViewModel(ConnectionEntry entry, TreeNodeViewModel? parent)
    {
        Entry = entry;
        Parent = parent;
    }

    partial void OnIsExpandedChanged(bool value) => OnPropertyChanged(nameof(Glyph));
    partial void OnOpenSessionsChanged(int value) => OnPropertyChanged(nameof(HasOpenSession));

    public void RefreshDisplay()
    {
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(Glyph));
    }

    /// <summary>Hides nodes that do not match. Folders stay visible when any descendant matches and expand to show it.</summary>
    public bool ApplyFilter(string filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
        {
            IsVisible = true;
            foreach (var child in Children) child.ApplyFilter(filter);
            return true;
        }

        if (Entry is not null)
        {
            IsVisible = ConnectionSearch.Matches(Entry, filter);
            return IsVisible;
        }

        var any = false;
        foreach (var child in Children)
            any |= child.ApplyFilter(filter);
        var selfMatch = Folder!.Name.Contains(filter, StringComparison.OrdinalIgnoreCase);
        if (selfMatch && !any)
            foreach (var child in Children) child.IsVisible = true;
        IsVisible = any || selfMatch;
        if (any) IsExpanded = true;
        return IsVisible;
    }

    public IEnumerable<TreeNodeViewModel> Descendants()
    {
        foreach (var child in Children)
        {
            yield return child;
            foreach (var grand in child.Descendants()) yield return grand;
        }
    }
}
