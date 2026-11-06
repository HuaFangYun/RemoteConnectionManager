using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using RemoteConnectionManager.App.Services;
using RemoteConnectionManager.App.ViewModels;

namespace RemoteConnectionManager.App.Views;

/// <summary>
/// Hosts one RDP session. The same view is used in the tab and in a detached / full screen window.
/// Exactly one view owns the session's control at a time: the tab's view while the session is in
/// its tab, the window's view (<see cref="IsWindowHost"/>) otherwise. The view that stops owning it
/// lets go first, so the control is parked by WinForms (handle kept, session alive) and never has
/// two parents.
/// </summary>
public partial class RdpSessionView : UserControl
{
    public static readonly DependencyProperty IsWindowHostProperty = DependencyProperty.Register(
        nameof(IsWindowHost), typeof(bool), typeof(RdpSessionView),
        new PropertyMetadata(false, (d, _) => ((RdpSessionView)d).UpdateOwnership()));

    private RdpSessionTabViewModel? _vm;
    private bool _released;

    /// <summary>True for the view inside a detached or full screen session window.</summary>
    public bool IsWindowHost
    {
        get => (bool)GetValue(IsWindowHostProperty);
        set => SetValue(IsWindowHostProperty, value);
    }

    public RdpSessionView()
    {
        InitializeComponent();
    }

    /// <summary>Lets go of the control for good. The window calls this before it closes.</summary>
    public void Release()
    {
        _released = true;
        Host.Child = null;
    }

    private bool ShouldOwn => _vm is not null && !_released && _vm.IsDetached == IsWindowHost;

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_vm is not null) _vm.PropertyChanged -= OnVmPropertyChanged;
        _vm = e.NewValue as RdpSessionTabViewModel;
        if (_vm is not null) _vm.PropertyChanged += OnVmPropertyChanged;
        UpdateOwnership();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Connect once layout has given the host its real size, so DesktopWidth/Height match the tab.
        Dispatcher.BeginInvoke(() =>
        {
            UpdateLayout();
            if (ShouldOwn) _vm?.ConnectIfIdle();
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(RdpSessionTabViewModel.Placement):
                UpdateOwnership();
                break;
            case nameof(RdpSessionTabViewModel.ShowOverlay):
            case nameof(RdpSessionTabViewModel.State):
                UpdateSurface();
                break;
            case nameof(RdpSessionTabViewModel.IsSelected):
                UpdateSurface();
                if (_vm?.IsSelected == true && ShouldOwn)
                    Dispatcher.BeginInvoke(() => _vm.Control.FocusSession(), System.Windows.Threading.DispatcherPriority.Input);
                break;
        }
    }

    private void UpdateOwnership()
    {
        if (ShouldOwn)
        {
            if (!ReferenceEquals(Host.Child, _vm!.Control)) Host.Child = _vm.Control;
        }
        else if (Host.Child is not null)
        {
            Host.Child = null;
        }
        UpdateSurface();
    }

    private void UpdateSurface()
    {
        var placeholder = _vm is { IsDetached: true } && !IsWindowHost;
        var overlay = !placeholder && _vm is { ShowOverlay: true };
        DetachedPanel.Visibility = placeholder ? Visibility.Visible : Visibility.Collapsed;
        OverlayPanel.Visibility = overlay ? Visibility.Visible : Visibility.Collapsed;
        Host.Visibility = !placeholder && !overlay ? Visibility.Visible : Visibility.Collapsed;

        var fullScreen = IsWindowHost && _vm is { IsFullScreen: true };
        Toolbar.Visibility = fullScreen ? Visibility.Collapsed : Visibility.Visible;
        HostBorder.BorderThickness = new Thickness(fullScreen ? 0 : 2);
        HostBorder.BorderBrush = _vm is { IsConnected: true } && !fullScreen && !placeholder
            ? (System.Windows.Media.Brush)FindResource("Chrome.Accent")
            : System.Windows.Media.Brushes.Transparent;

        DetachButton.Visibility = _vm is { IsDetached: true } ? Visibility.Collapsed : Visibility.Visible;
        DockButton.Visibility = _vm is { IsDetached: true } ? Visibility.Visible : Visibility.Collapsed;
    }

    private void FullScreen_Click(object sender, RoutedEventArgs e)
    {
        if (_vm is null || sender is not Button button) return;
        var menu = BuildMonitorMenu(_vm);
        menu.PlacementTarget = button;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    /// <summary>One item per display, current one first in the list order Windows numbers them.</summary>
    public static ContextMenu BuildMonitorMenu(RdpSessionTabViewModel vm)
    {
        var menu = new ContextMenu();
        foreach (var monitor in vm.AvailableMonitors)
        {
            menu.Items.Add(new MenuItem
            {
                Header = $"Full screen on {monitor.Label}",
                Command = vm.FullScreenCommand,
                CommandParameter = monitor,
                IsChecked = vm.IsFullScreen && vm.FullScreenMonitor?.DeviceName == monitor.DeviceName,
            });
        }
        if (vm.IsFullScreen)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(new MenuItem { Header = "Leave full screen", Command = vm.ExitFullScreenCommand });
        }
        return menu;
    }
}
