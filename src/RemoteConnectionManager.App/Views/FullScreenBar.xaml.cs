using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using RemoteConnectionManager.App.Services;
using RemoteConnectionManager.App.ViewModels;

namespace RemoteConnectionManager.App.Views;

/// <summary>The drop down bar at the top of a full screen session, like mstsc's connection bar.</summary>
public partial class FullScreenBar : Window
{
    private readonly RdpSessionTabViewModel _vm;
    private bool _menuOpen;

    public MonitorChoice? Monitor { get; set; }

    /// <summary>True while the pointer is on the bar or one of its menus is open, so it stays put.</summary>
    public bool IsBusy => _menuOpen || IsMouseOver;

    public double HeightInPixels => ActualHeight * (PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice.M22 ?? 1);

    public FullScreenBar(RdpSessionTabViewModel vm)
    {
        _vm = vm;
        DataContext = vm;
        InitializeComponent();
    }

    public void ShowAtTop()
    {
        if (Monitor is not { } monitor) return;
        Show();
        UpdateLayout();
        var source = PresentationSource.FromVisual(this) ?? HwndSource.FromHwnd(new WindowInteropHelper(this).EnsureHandle());
        var m = source?.CompositionTarget?.TransformFromDevice ?? System.Windows.Media.Matrix.Identity;
        var topLeft = m.Transform(new System.Windows.Point(monitor.Left, monitor.Top));
        var size = m.Transform(new System.Windows.Point(monitor.Width, monitor.Height));
        Left = topLeft.X + (size.X - ActualWidth) / 2;
        Top = topLeft.Y;
    }

    private void Displays_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;
        var menu = RdpSessionView.BuildMonitorMenu(_vm);
        menu.PlacementTarget = button;
        menu.Placement = PlacementMode.Bottom;
        menu.Opened += (_, _) => _menuOpen = true;
        menu.Closed += (_, _) => _menuOpen = false;
        menu.IsOpen = true;
    }
}
