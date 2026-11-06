using CommunityToolkit.Mvvm.ComponentModel;

namespace RemoteConnectionManager.App.ViewModels;

/// <summary>One tab in the centre strip. The dashboard is pinned; sessions can close.</summary>
public abstract partial class TabViewModelBase : ObservableObject
{
    [ObservableProperty] private string _title = string.Empty;
    [ObservableProperty] private string _glyph = "";
    /// <summary>Theme brush key for the tab glyph, for example Icon.Folder. See the Icon.* tokens in Themes/Rcm.xaml.</summary>
    [ObservableProperty] private string _iconBrushKey = "Icon.Default";
    [ObservableProperty] private bool _isSelected;

    private string? _typeBrushKey;

    /// <summary>Shows the entry's user chosen icon colour ("#RRGGBB"), or puts the type's colour back when null.</summary>
    public void SetIconColour(string? colour)
    {
        _typeBrushKey ??= IconBrushKey;
        IconBrushKey = colour ?? _typeBrushKey;
    }

    public virtual bool CanClose => true;

    /// <summary>The connections file entry this tab was opened from, for the tree's session dots. Null for the dashboard and quick connects.</summary>
    public virtual Guid? EntryId => null;

    /// <summary>True while a session is connecting or connected, or a shell is running.</summary>
    public virtual bool IsLive => false;
}
