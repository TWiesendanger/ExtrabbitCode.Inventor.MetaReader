using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;

namespace ExtrabbitCode.Inventor.MetaReader.App;

/// <summary>Helpers used only by the documentation snapshotter (<see cref="DocShooter"/>).</summary>
public sealed partial class MainWindow
{
    /// <summary>The document view in the currently selected tab, if any.</summary>
    public DocumentView? CurrentView =>
        (DocTabs.SelectedItem as TabViewItem)?.Content as DocumentView;

    /// <summary>Resolves a window-level named element, for capturing just that region.</summary>
    public FrameworkElement? ShootElement(string name) =>
        (Content as FrameworkElement)?.FindName(name) as FrameworkElement;

    /// <summary>Opens a file in a new tab (snapshotter entry point).</summary>
    public void ShootOpen(string path) => OpenFile(path);

    /// <summary>Resizes the window to a fixed size for deterministic screenshots.</summary>
    public void ShootResize(int width, int height) =>
        _appWindow?.Resize(new SizeInt32(width, height));

    /// <summary>Moves the window to a fixed position (the demo-tour recorder captures a fixed
    /// screen region, so the window must sit at known physical coordinates).</summary>
    public void ShootMove(int x, int y) =>
        _appWindow?.Move(new PointInt32(x, y));

    /// <summary>The window's position and size in physical pixels, for the recorder's crop.</summary>
    public (int X, int Y, int W, int H)? ShootWindowRect() =>
        _appWindow is { } a ? (a.Position.X, a.Position.Y, a.Size.Width, a.Size.Height) : null;

    /// <summary>Keeps the window above everything else - the demo-tour recorder captures a screen
    /// region, so another window drifting over the app would end up in the video.</summary>
    public void ShootTopmost()
    {
        if (_appWindow?.Presenter is Microsoft.UI.Windowing.OverlappedPresenter p)
        {
            p.IsAlwaysOnTop = true;
        }
    }

    /// <summary>Closes the document tabs and shows the Home tab (snapshotter reset).</summary>
    public void ShootCloseAllTabs()
    {
        CloseAllTabs();                  // removes doc tabs but keeps the pinned Home tab
        DocTabs.SelectedItem = HomeTab;  // show the Home / welcome view
    }
}
