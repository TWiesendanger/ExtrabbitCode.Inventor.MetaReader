using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using Windows.Foundation;
using Windows.Storage.Streams;

namespace ExtrabbitCode.Inventor.MetaReader.App;

/// <summary>Helpers used only by the documentation snapshotter (<see cref="DocShooter"/>).</summary>
public sealed partial class DocumentView
{
    /// <summary>Selects a detail tab by its header prefix (e.g. "Model States" matches
    /// "Model States (3)"). No-op if the tab is hidden or absent.</summary>
    public void ShootSelectTab(string headerPrefix)
    {
        TabViewItem? tab = DetailTabs.TabItems.OfType<TabViewItem>().FirstOrDefault(t =>
            t.Visibility == Visibility.Visible &&
            t.Header is string h && h.StartsWith(headerPrefix, StringComparison.Ordinal));

        if (tab != null)
        {
            DetailTabs.SelectedItem = tab;
        }
    }

    /// <summary>Resolves a named element inside this view's XAML namescope, for capturing just that
    /// region (e.g. the sidebar card or a single detail panel).</summary>
    public FrameworkElement? ShootElement(string name) => FindName(name) as FrameworkElement;

    /// <summary>Opens the category badge's hover legend (demo tour). The tour's cursor is painted
    /// into the window and never moves the OS pointer, so it can't raise a real hover tooltip -
    /// this pops the same legend directly.</summary>
    internal void ShootShowCategoryLegend()
    {
        if (_categoryTip != null) { _categoryTip.IsOpen = true; }
    }

    /// <summary>Closes the category badge legend opened by <see cref="ShootShowCategoryLegend"/>.</summary>
    internal void ShootHideCategoryLegend()
    {
        if (_categoryTip != null) { _categoryTip.IsOpen = false; }
    }

    /// <summary>The header element of a detail tab, so the demo tour can move the cursor onto it
    /// before switching (snapshotter).</summary>
    internal FrameworkElement? ShootTabHeader(string headerPrefix) =>
        DetailTabs.TabItems.OfType<TabViewItem>().FirstOrDefault(t =>
            t.Visibility == Visibility.Visible &&
            t.Header is string h && h.StartsWith(headerPrefix, StringComparison.Ordinal));

    /// <summary>The layout dropdown of the reference graph, as a cursor target (snapshotter).</summary>
    internal FrameworkElement? ShootGraphLayoutPick() => _graphLayoutPick;

    /// <summary>The 3D viewer WebView2's top-left corner relative to the window content, in DIPs -
    /// the demo tour maps page coordinates to screen coordinates through this (snapshotter).</summary>
    internal Point? ShootViewer3DOrigin(UIElement relativeTo) =>
        _viewer3dWeb is { ActualWidth: > 0 }
            ? _viewer3dWeb.TransformToVisual(relativeTo).TransformPoint(new Point(0, 0))
            : null;

    /// <summary>The reference graph WebView2's top-left corner relative to the window content.</summary>
    internal Point? ShootGraphOrigin(UIElement relativeTo) =>
        _graphWeb is { ActualWidth: > 0 }
            ? _graphWeb.TransformToVisual(relativeTo).TransformPoint(new Point(0, 0))
            : null;

    /// <summary>Runs JavaScript in the reference graph page (demo tour).</summary>
    internal async Task<string> ShootGraphScriptAsync(string js)
    {
        if (_graphWeb?.CoreWebView2 is null) { return "null"; }
        try { return await _graphWeb.CoreWebView2.ExecuteScriptAsync(js); }
        catch (Exception) { return "null"; }
    }

    /// <summary>Captures the reference-graph WebView2 (which RenderTargetBitmap renders blank) as a PNG,
    /// with its bounds inside <paramref name="relativeTo"/>, so the shooter can paint it into the shot.</summary>
    public async Task<(byte[] png, double x, double y, double width, double height)?> ShootGraphImageAsync(UIElement relativeTo)
    {
        if (_graphWeb?.CoreWebView2 is null || _graphWeb.ActualWidth <= 0 || _graphWeb.ActualHeight <= 0)
        {
            return null;
        }

        InMemoryRandomAccessStream stream = new();
        await _graphWeb.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, stream);
        stream.Seek(0);
        using MemoryStream ms = new();
        await stream.AsStreamForRead().CopyToAsync(ms);

        Point topLeft = _graphWeb.TransformToVisual(relativeTo).TransformPoint(new Point(0, 0));
        return (ms.ToArray(), topLeft.X, topLeft.Y, _graphWeb.ActualWidth, _graphWeb.ActualHeight);
    }

    /// <summary>Switches the reference graph to the given layout (snapshotter). Goes through the
    /// layout dropdown when it exists, so the visible label changes along with the layout.</summary>
    internal void ShootSetGraphLayout(GraphLayout layout)
    {
        if (_graphWeb is null || _graphRoot is null) { return; }
        if (_graphLayoutPick is { } pick && pick.SelectedIndex != (int)layout)
        {
            pick.SelectedIndex = (int)layout;   // SelectionChanged applies the layout and resends the graph
            return;
        }
        _graphLayout = layout;
        SendGraph(_graphWeb, _graphRoot);
    }

    /// <summary>Expands every node in the reference graph (snapshotter).</summary>
    public void ShootExpandGraph()
    {
        if (_graphWeb is null || _graphRoot is null) { return; }
        SetAllExpanded(_graphRoot, true);
        SendGraph(_graphWeb, _graphRoot);
    }

    /// <summary>Fits the reference graph to the view (snapshotter).</summary>
    public void ShootFitGraph()
    {
        if (_graphWeb is not null) { Post(_graphWeb, "{\"cmd\":\"fit\"}"); }
    }

    /// <summary>Rides the graph camera into a couple of nodes and back out (demo tour, ~5.5s).</summary>
    internal void ShootGraphShowcase()
    {
        if (_graphWeb is not null) { Post(_graphWeb, "{\"cmd\":\"showcase\"}"); }
    }

    /// <summary>The graph's node-thumbnails toggle, as a cursor target (demo tour).</summary>
    internal FrameworkElement? ShootGraphThumbsToggle() => _graphThumbsToggle;

    /// <summary>Turns node thumbnails on in the reference graph (demo tour).</summary>
    internal void ShootShowGraphThumbs()
    {
        if (_graphThumbsToggle is { } toggle && _graphWeb is not null && _graphRoot is not null)
        {
            toggle.IsChecked = true;
            _showThumbs = true;
            SendGraph(_graphWeb, _graphRoot);
        }
    }

    /// <summary>Smooth-scrolls the ScrollViewer that hosts a named detail panel (demo tour).</summary>
    internal void ShootScrollPanel(string panelName, double toOffset)
    {
        if (FindName(panelName) is FrameworkElement panel && panel.Parent is ScrollViewer sv)
        {
            sv.ChangeView(null, toOffset, null);
        }
    }

    /// <summary>Collapses or expands every property group (demo tour).</summary>
    internal void ShootSetPropsExpanded(bool expanded) => SetAllExpanded(expanded);

    /// <summary>A property group's header (the chevron's parent), as a cursor target.</summary>
    internal FrameworkElement? ShootPropGroupHeader(int index) =>
        index >= 0 && index < _collapsibles.Count ? _collapsibles[index].chevron.Parent as FrameworkElement : null;

    /// <summary>Expands a single property group (demo tour).</summary>
    internal void ShootExpandPropGroup(int index)
    {
        if (index >= 0 && index < _collapsibles.Count)
        {
            _collapsibles[index].body.Visibility = Visibility.Visible;
            _collapsibles[index].chevron.Glyph = G(ChevronUp);
        }
    }

    // ---- 3D viewer (snapshotter): the overlay opens only through private event handlers, so the
    // shooter gets its own entry points. The WebView2 is captured like the graph's - via
    // CapturePreviewAsync - because RenderTargetBitmap renders WebView2 content blank. ----

    /// <summary>Opens the 3D viewer overlay (snapshotter). Poll the page via
    /// <see cref="ShootViewer3DScriptAsync"/> to learn when the model is loaded.</summary>
    internal void ShootOpen3D() => _ = OpenViewer3DAsync();

    /// <summary>Closes the 3D viewer overlay if it is open (snapshotter).</summary>
    internal void ShootClose3D() => _viewer3dClose?.Invoke();

    /// <summary>Runs JavaScript in the open 3D viewer page; returns the JSON-encoded result, or
    /// "null" when the viewer isn't open (snapshotter).</summary>
    internal async Task<string> ShootViewer3DScriptAsync(string js)
    {
        if (_viewer3dWeb?.CoreWebView2 is null) { return "null"; }
        try { return await _viewer3dWeb.CoreWebView2.ExecuteScriptAsync(js); }
        catch (Exception) { return "null"; }
    }

    /// <summary>Captures the 3D viewer WebView2 as a PNG with its bounds inside
    /// <paramref name="relativeTo"/>, so the shooter can paint it into the shot (snapshotter).</summary>
    public async Task<(byte[] png, double x, double y, double width, double height)?> ShootViewer3DImageAsync(UIElement relativeTo)
    {
        if (_viewer3dWeb?.CoreWebView2 is null || _viewer3dWeb.ActualWidth <= 0 || _viewer3dWeb.ActualHeight <= 0)
        {
            return null;
        }

        InMemoryRandomAccessStream stream = new();
        await _viewer3dWeb.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, stream);
        stream.Seek(0);
        using MemoryStream ms = new();
        await stream.AsStreamForRead().CopyToAsync(ms);

        Point topLeft = _viewer3dWeb.TransformToVisual(relativeTo).TransformPoint(new Point(0, 0));
        return (ms.ToArray(), topLeft.X, topLeft.Y, _viewer3dWeb.ActualWidth, _viewer3dWeb.ActualHeight);
    }
}
