using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;

namespace ExtrabbitCode.Inventor.MetaReader.App;

/// <summary>Helpers used only by the documentation snapshotter (<see cref="DocShooter"/>) and the
/// demo tour (<see cref="DemoTour"/>).</summary>
public sealed partial class MainWindow
{
    private Border? _shootCaption;

    /// <summary>The document view in the currently selected tab, if any.</summary>
    public DocumentView? CurrentView =>
        (DocTabs.SelectedItem as TabViewItem)?.Content as DocumentView;

    /// <summary>The n-th document tab's header element (0 = Home), as a cursor target.</summary>
    internal TabViewItem? ShootTab(int index) =>
        index >= 0 && index < DocTabs.TabItems.Count ? DocTabs.TabItems[index] as TabViewItem : null;

    /// <summary>Selects the n-th tab (0 = Home), like a click on its header.</summary>
    internal void ShootSelectTabIndex(int index)
    {
        if (ShootTab(index) is { } tab) { DocTabs.SelectedItem = tab; }
    }

    /// <summary>Shows (or, with null/empty, hides) a caption pill at the bottom of the window -
    /// the demo tour labels what each recorded segment demonstrates. Renders above overlays.</summary>
    internal void ShootCaption(string? text)
    {
        if (Content is not Grid root) { return; }
        if (string.IsNullOrEmpty(text))
        {
            if (_shootCaption != null) { root.Children.Remove(_shootCaption); _shootCaption = null; }
            return;
        }
        if (_shootCaption == null)
        {
            _shootCaption = new Border
            {
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0xE0, 0x1B, 0x1B, 0x1B)),
                CornerRadius = new CornerRadius(18),
                Padding = new Thickness(18, 9, 18, 11),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 0, 44),
                IsHitTestVisible = false,
                Child = new TextBlock
                {
                    FontSize = 15,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(Microsoft.UI.Colors.White)
                }
            };
            Grid.SetRowSpan(_shootCaption, Math.Max(1, root.RowDefinitions.Count));
            Grid.SetColumnSpan(_shootCaption, Math.Max(1, root.ColumnDefinitions.Count));
            root.Children.Add(_shootCaption);
        }
        ((TextBlock)_shootCaption.Child!).Text = text;
    }

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
