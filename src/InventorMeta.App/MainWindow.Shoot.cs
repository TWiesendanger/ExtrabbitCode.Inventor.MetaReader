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

    /// <summary>Closes the document tabs and shows the Home tab (snapshotter reset).</summary>
    public void ShootCloseAllTabs()
    {
        CloseAllTabs();                  // removes doc tabs but keeps the pinned Home tab
        DocTabs.SelectedItem = HomeTab;  // show the Home / welcome view
    }
}
