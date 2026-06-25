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

    /// <summary>Switches the reference graph to the given layout (snapshotter).</summary>
    internal void ShootSetGraphLayout(GraphLayout layout)
    {
        if (_graphWeb is null || _graphRoot is null) { return; }
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
}
