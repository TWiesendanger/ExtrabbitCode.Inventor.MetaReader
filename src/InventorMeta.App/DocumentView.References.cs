using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace ExtrabbitCode.Inventor.MetaReader.App;

/// <summary>The References tab: the inline canvas node graph (layout, pan/zoom, node boxes)
/// and reference opening.</summary>
public sealed partial class DocumentView
{
    // ---- references node graph -----------------------------------------------------
    private int _refGen;

    private void PopulateReferences(InventorDocument doc)
    {
        RefsRoot.RowDefinitions.Clear();
        RefsRoot.Children.Clear();

        // version/provenance details - version history first, then schema/template
        string[] detailOrder = ["Current Version", "Previous Version", "Next Version", "Last update with", "Last saved by"];
        List<(string, string)> provItems = [];
        foreach (string k in detailOrder)
        {
            if (doc.VersionInfo.TryGetValue(k, out string? v)) { provItems.Add((k, v)); }
        }
        foreach (KeyValuePair<string, string> kv in doc.VersionInfo)
        {
            if (!detailOrder.Contains(kv.Key)) { provItems.Add((kv.Key, kv.Value)); }
        }
        StackPanel details = KeyValueSection("Version", provItems);

        // Graph fills the top (star); the short version panel docks below it (auto), never scrolling.
        RefsRoot.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        RefsRoot.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        Border graphHost = new();
        Grid.SetRow(graphHost, 0);
        RefsRoot.Children.Add(graphHost);

        Border prov = new()
        {
            BorderThickness = new Thickness(0, 1, 0, 0),
            BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(40, 128, 128, 128)),
            Padding = new Thickness(14, 10, 14, 12),
            Child = details
        };
        Grid.SetRow(prov, 1);
        RefsRoot.Children.Add(prov);

        if (doc.References.Count == 0)
        {
            graphHost.Child = GraphInfo("No referenced files.");
            return;
        }

        int gen = ++_refGen;
        _ = BuildAndShowGraphAsync(doc, graphHost, gen);
    }

    private async Task BuildAndShowGraphAsync(InventorDocument doc, Border host, int gen)
    {
        if (doc.References.Count == 0)
        {
            host.Child = GraphInfo("No referenced files.");
            return;
        }

        host.Child = GraphInfo("Building reference graph…");

        RefNode? root = null;
        try { root = await Task.Run(() => ReferenceGraph.Build(doc)); }
        catch { /* fall through to error message */ }

        if (gen != _refGen)
        {
            return; // a newer load superseded this build
        }

        host.Child = root != null ? RenderRefGraphWeb(root, host) : GraphInfo("Couldn't build the reference graph.");
    }

    private static TextBlock GraphInfo(string text) => new()
    {
        Text = text, Opacity = 0.6, Margin = new Thickness(20),
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
    };

    private bool _graphFs;
    private bool _showThumbs;
    private GraphLayout _graphLayout;

    // Set while the reference graph is mounted: exits its fullscreen overlay. Lets the
    // node "open" actions drop out of fullscreen before switching to the opened tab.
    private Action? _exitGraphFullscreen;

    /// <summary>Opens a referenced document in a tab. If the reference graph is currently
    /// fullscreen, leave fullscreen first so the newly opened (and selected) tab is visible.</summary>
    private void OpenReference(string path)
    {
        Analytics.Capture("reference_opened");
        if (_graphFs) { _exitGraphFullscreen?.Invoke(); }
        HostWindow?.OpenDocument(path);
    }

    private UIElement RenderRefGraph(RefNode root, Border host)
    {
        // start with only level 1 visible: root expanded, everything deeper collapsed
        ForEachNode(root, n => n.Expanded = n.Depth == 0);

        CompositeTransform xf = new();
        Canvas canvas = new()
        {
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            RenderTransform = xf,
            // pin to the viewport's top-left so the pan/zoom transform's origin is (0,0)
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top
        };

        // Pan/zoom via a render transform on a clipped viewport. We avoid ScrollViewer: its
        // ZoomMode + a Canvas hits a WinUI "Layout cycle detected" crash, and it also eats
        // the left-drag we want for panning.
        Grid viewport = new() { Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        bool fitted = false;
        viewport.SizeChanged += (_, e) =>
        {
            viewport.Clip = new RectangleGeometry { Rect = new Windows.Foundation.Rect(0, 0, e.NewSize.Width, e.NewSize.Height) };
            if (!fitted && e.NewSize.Width > 0 && e.NewSize.Height > 0)
            {
                fitted = true;
                ZoomToFit(viewport, canvas, xf);
            }
        };
        viewport.Children.Add(canvas);

        LayoutAndDraw(canvas, root);
        WirePanZoom(viewport, canvas, xf);

        // floating toolbar (top-right)
        StackPanel toolbar = new()
        {
            Orientation = Orientation.Horizontal, Spacing = 4,
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 8, 14, 0)
        };
        static Button IconBtn(int glyph, string tip)
        {
            Button b = new() { Content = new FontIcon { Glyph = G(glyph), FontSize = 14 },
                Padding = new Thickness(8, 5, 8, 5), MinWidth = 0 };
            ToolTipService.SetToolTip(b, tip);
            return b;
        }

        Button fit = IconBtn(0xE9A6, "Fit to view");
        fit.Click += (_, _) => ZoomToFit(viewport, canvas, xf);
        Button expandAll = IconBtn(0xE710, "Expand all");
        expandAll.Click += (_, _) => { ForEachNode(root, n => n.Expanded = n.Children.Count > 0); LayoutAndDraw(canvas, root); };
        Button collapseAll = IconBtn(0xE738, "Collapse all");
        collapseAll.Click += (_, _) => { ForEachNode(root, n => n.Expanded = n.Depth == 0); LayoutAndDraw(canvas, root); };

        // thumbnails toggle: redraw the graph with each node showing its document's preview
        ToggleButton thumbs = new()
        {
            Content = new FontIcon { Glyph = G(0xE8B9), FontSize = 14 },
            Padding = new Thickness(8, 5, 8, 5), MinWidth = 0, IsChecked = _showThumbs
        };
        ToolTipService.SetToolTip(thumbs, "Show thumbnails");
        thumbs.Click += (_, _) =>
        {
            _showThumbs = thumbs.IsChecked == true;
            LayoutAndDraw(canvas, root);
            // re-fit after the relayout settles
            DispatcherQueue.TryEnqueue(() => ZoomToFit(viewport, canvas, xf));
        };

        // Fullscreen: pop the whole viewport (graph + this toolbar) into a window-filling overlay
        // and switch the OS window to true fullscreen; the button toggles back, as does Esc.
        FontIcon fullIcon = new() { Glyph = G(0xE740), FontSize = 14 };
        Button full = new() { Content = fullIcon, Padding = new Thickness(8, 5, 8, 5), MinWidth = 0 };
        ToolTipService.SetToolTip(full, "Fullscreen");
        Grid? overlay = null;
        void SetFullscreen(bool on)
        {
            MainWindow? win = HostWindow;
            if (win is null || on == _graphFs) { return; }

            if (on)
            {
                host.Child = null;                       // detach from the References tab
                overlay = new Grid();
                overlay.Children.Add(viewport);
                win.ShowOverlay(overlay);
                win.AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
                _graphFs = true;
                Analytics.Capture("references_fullscreen");
            }
            else
            {
                win.AppWindow.SetPresenter(AppWindowPresenterKind.Default);
                overlay?.Children.Remove(viewport);
                win.HideOverlay();
                host.Child = viewport;                   // reattach to the tab
                overlay = null;
                _graphFs = false;
            }

            fullIcon.Glyph = G(_graphFs ? 0xE73F : 0xE740);   // BackToWindow / FullScreen
            ToolTipService.SetToolTip(full, _graphFs ? "Exit fullscreen" : "Fullscreen");
            fitted = false;                              // re-fit once the new size settles
            full.Focus(FocusState.Programmatic);
        }
        full.Click += (_, _) => SetFullscreen(!_graphFs);
        _exitGraphFullscreen = () => SetFullscreen(false);
        KeyboardAccelerator esc = new() { Key = Windows.System.VirtualKey.Escape };
        esc.Invoked += (_, e) => { if (_graphFs) { SetFullscreen(false); e.Handled = true; } };
        full.KeyboardAccelerators.Add(esc);

        toolbar.Children.Add(fit);
        toolbar.Children.Add(expandAll);
        toolbar.Children.Add(collapseAll);
        toolbar.Children.Add(thumbs);
        toolbar.Children.Add(full);
        viewport.Children.Add(toolbar);

        // Discoverability tip: point at the thumbnails toggle (unless already on).
        if (!_showThumbs)
        {
            TipService.Show(viewport, thumbs, new Tip
            {
                Id = "refs.thumbnails",
                Title = "Did you know?",
                Message = "You can show each referenced file's preview thumbnail right in the graph.",
                ActionText = "Show thumbnails",
                Action = () =>
                {
                    thumbs.IsChecked = true;
                    _showThumbs = true;
                    LayoutAndDraw(canvas, root);
                    ZoomToFit(viewport, canvas, xf);
                }
            });
        }

        return viewport;
    }

    /// <summary>Scales and centres the graph so all of it fits the viewport (zoom extents).</summary>
    private static void ZoomToFit(Grid viewport, Canvas canvas, CompositeTransform xf)
    {
        double vw = viewport.ActualWidth, vh = viewport.ActualHeight, cw = canvas.Width, ch = canvas.Height;
        if (vw <= 0 || vh <= 0 || cw <= 0 || ch <= 0) { return; }

        const double margin = 28;
        double scale = Math.Clamp(Math.Min((vw - margin) / cw, (vh - margin) / ch), 0.2, 1.0);
        xf.ScaleX = xf.ScaleY = scale;
        xf.TranslateX = (vw - cw * scale) / 2;
        xf.TranslateY = (vh - ch * scale) / 2;
    }

    private static void ForEachNode(RefNode n, Action<RefNode> action)
    {
        action(n);
        foreach (RefNode c in n.Children) { ForEachNode(c, action); }
    }

    /// <summary>Lays out the visible (expanded) part of the tree and (re)draws the canvas.</summary>
    private void LayoutAndDraw(Canvas canvas, RefNode root)
    {
        const double colStep = 300, pad = 16;
        // bigger nodes when thumbnails are shown so each preview has room
        double nodeW = _showThumbs ? 252 : 224;
        double nodeH = _showThumbs ? 104 : 56;
        double rowStep = _showThumbs ? 120 : 74;

        // tidy left-to-right layout over visible nodes; a collapsed node counts as a leaf
        int leaf = 0, maxDepth = 0;
        void Assign(RefNode n)
        {
            maxDepth = Math.Max(maxDepth, n.Depth);
            if (!n.Expanded || n.Children.Count == 0) { n.Row = leaf++; return; }
            foreach (RefNode c in n.Children) { Assign(c); }
            n.Row = (n.Children[0].Row + n.Children[^1].Row) / 2.0;
        }
        Assign(root);

        List<RefNode> vis = [];
        void Collect(RefNode n) { vis.Add(n); if (n.Expanded) { foreach (RefNode c in n.Children) { Collect(c); } } }
        Collect(root);

        double X(RefNode n) => pad + n.Depth * colStep;
        double Y(RefNode n) => pad + n.Row * rowStep;

        canvas.Children.Clear();
        canvas.Width = pad * 2 + maxDepth * colStep + nodeW;
        canvas.Height = pad * 2 + Math.Max(0, leaf - 1) * rowStep + nodeH;

        Brush link = new SolidColorBrush(Windows.UI.Color.FromArgb(150, 130, 130, 130));
        foreach (RefNode n in vis)
        {
            if (!n.Expanded) { continue; }
            foreach (RefNode c in n.Children)
            {
                double sx = X(n) + nodeW, sy = Y(n) + nodeH / 2, ex = X(c), ey = Y(c) + nodeH / 2;
                double dx = (ex - sx) * 0.5;
                PathFigure fig = new() { StartPoint = new Windows.Foundation.Point(sx, sy) };
                fig.Segments.Add(new BezierSegment
                {
                    Point1 = new Windows.Foundation.Point(sx + dx, sy),
                    Point2 = new Windows.Foundation.Point(ex - dx, ey),
                    Point3 = new Windows.Foundation.Point(ex, ey)
                });
                PathGeometry geo = new();
                geo.Figures.Add(fig);
                canvas.Children.Add(new Microsoft.UI.Xaml.Shapes.Path { Data = geo, Stroke = link, StrokeThickness = 1.5 });
            }
        }

        foreach (RefNode n in vis)
        {
            Border box = RefNodeBox(n, nodeW, nodeH, canvas, root);
            Canvas.SetLeft(box, X(n));
            Canvas.SetTop(box, Y(n));
            canvas.Children.Add(box);
        }
    }

    private Border RefNodeBox(RefNode n, double w, double h, Canvas canvas, RefNode root)
    {
        Grid g = new() { ColumnSpacing = 8, Padding = new Thickness(10, 6, 8, 6) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        ImageSource? thumb = _showThumbs ? BitmapFromBytes(n.Thumbnail) : null;
        FrameworkElement iconEl;
        if (thumb != null)
        {
            iconEl = new Border
            {
                Width = h - 20, Height = h - 20, CornerRadius = new CornerRadius(4),
                VerticalAlignment = VerticalAlignment.Center,
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(28, 128, 128, 128)),
                Child = new Image { Source = thumb, Stretch = Stretch.Uniform, Margin = new Thickness(2) }
            };
        }
        else if (n.IsLinkedFile)
        {
            iconEl = new FontIcon { Glyph = G(IsImageFile(n.Name) ? 0xE8B9 : 0xE7C3),
                FontSize = _showThumbs ? 40 : 22, Width = _showThumbs ? 56 : 30, Opacity = 0.85,
                VerticalAlignment = VerticalAlignment.Center };
        }
        else
        {
            double s = _showThumbs ? 48 : 30;
            iconEl = new Image { Width = s, Height = s, Source = AppIcons.Bitmap(n.Kind),
                VerticalAlignment = VerticalAlignment.Center };
        }

        StackPanel text = new() { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = n.Name, FontWeight = FontWeights.SemiBold, FontSize = 13,
            TextTrimming = TextTrimming.CharacterEllipsis });
        // iPart classification (done in the core graph): model-state parts carry the same marker
        // but aren't iParts, so they classify as neither.
        bool isMember = n.IPart == IPartRole.Member;
        bool isFactory = n.IPart == IPartRole.Factory;

        string sub = n.Cyclic ? "↻ already shown above"
            : !n.Resolved ? (n.IsLinkedFile ? "linked file - not found" : "file not found")
            : n.ReadError ? "unreadable"
            : n.IsLinkedFile ? "linked " + Path.GetExtension(n.Name).TrimStart('.').ToLowerInvariant()
            : isFactory ? "iPart factory"
            : isMember ? "iPart member"
            : KindLabel(n.Kind);
        TextBlock subtitle = new() { Text = sub, FontSize = 11, Opacity = 0.6, TextTrimming = TextTrimming.CharacterEllipsis };
        if (!n.Resolved)
        {
            subtitle.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 209, 96, 96));
            subtitle.Opacity = 0.95;
        }
        else if (isFactory || isMember)
        {
            subtitle.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 214, 158, 46)); // gold
            subtitle.Opacity = 1.0;
            subtitle.FontWeight = FontWeights.SemiBold;
        }
        text.Children.Add(subtitle);
        Grid.SetColumn(text, 1);
        g.Children.Add(iconEl);
        g.Children.Add(text);

        // +/- toggle for nodes that have children
        if (n.Children.Count > 0)
        {
            Button toggle = new()
            {
                Width = 24, Height = 24, MinWidth = 0, Padding = new Thickness(0),
                CornerRadius = new CornerRadius(12), VerticalAlignment = VerticalAlignment.Center,
                Content = new FontIcon { Glyph = G(n.Expanded ? 0xE738 : 0xE710), FontSize = 11 }
            };
            ToolTipService.SetToolTip(toggle, n.Expanded ? "Collapse" : $"Expand ({n.Children.Count})");
            toggle.Click += (_, _) => { n.Expanded = !n.Expanded; LayoutAndDraw(canvas, root); };
            Grid.SetColumn(toggle, 2);
            g.Children.Add(toggle);
        }

        Border box = new() { Style = (Style)Resources["DataCard"], Width = w, Height = h, Child = g };
        if (n.Depth == 0)
        {
            box.BorderBrush = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];
            box.BorderThickness = new Thickness(2);
        }
        else if (isFactory)
        {
            box.BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 214, 158, 46)); // gold
            box.BorderThickness = new Thickness(2);
        }
        ToolTipService.SetToolTip(box, n.Path);

        bool canOpen = n.Resolved && n.Depth > 0 && !n.Cyclic && !n.IsLinkedFile;
        if (canOpen)
        {
            box.Tapped += (_, _) => OpenReference(n.Path);
            ToolTipService.SetToolTip(box, n.Path + "\nClick to open · right-click for more");
        }

        if (n.Resolved)
        {
            box.ContextRequested += (_, e) =>
            {
                MenuFlyout menu = new();
                if (canOpen)
                {
                    MenuFlyoutItem open = new() { Text = "Open in a tab", Icon = new FontIcon { Glyph = G(0xE8E5) } };
                    open.Click += (_, _) => OpenReference(n.Path);
                    menu.Items.Add(open);
                }
                MenuFlyoutItem reveal = new() { Text = "Show in Explorer", Icon = new FontIcon { Glyph = G(0xEC50) } };
                reveal.Click += (_, _) => RevealInExplorer(n.Path);
                menu.Items.Add(reveal);
                if (e.TryGetPosition(box, out Windows.Foundation.Point p)) { menu.ShowAt(box, p); }
                else { menu.ShowAt(box); }
                e.Handled = true;
            };
        }
        return box;
    }

    private static bool IsImageFile(string name) =>
        Path.GetExtension(name).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".tif" or ".tiff" or ".gif";

    /// <summary>Decodes raw image bytes (a document's cached preview) into a usable image source.</summary>
    private static ImageSource? BitmapFromBytes(byte[]? bytes)
    {
        if (bytes is not { Length: > 0 }) { return null; }
        try
        {
            BitmapImage bmp = new();
            using MemoryStream ms = new(bytes);
            bmp.SetSource(ms.AsRandomAccessStream());
            return bmp;
        }
        catch { return null; }
    }

    /// <summary>Left-drag on empty space (or middle-drag anywhere) pans; wheel zooms at the cursor.</summary>
    private static void WirePanZoom(Grid viewport, Canvas canvas, CompositeTransform xf)
    {
        bool panning = false;
        Windows.Foundation.Point start = default;
        double tx0 = 0, ty0 = 0;

        viewport.PointerPressed += (_, e) =>
        {
            Microsoft.UI.Input.PointerPoint pp = e.GetCurrentPoint(viewport);
            bool onBackground = ReferenceEquals(e.OriginalSource, canvas) || ReferenceEquals(e.OriginalSource, viewport);
            if (!pp.Properties.IsMiddleButtonPressed && !(pp.Properties.IsLeftButtonPressed && onBackground))
            {
                return;
            }
            panning = true; start = pp.Position; tx0 = xf.TranslateX; ty0 = xf.TranslateY;
            viewport.CapturePointer(e.Pointer);
        };
        viewport.PointerMoved += (_, e) =>
        {
            if (!panning) { return; }
            Windows.Foundation.Point p = e.GetCurrentPoint(viewport).Position;
            xf.TranslateX = tx0 + (p.X - start.X);
            xf.TranslateY = ty0 + (p.Y - start.Y);
        };
        void End(object _, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            if (panning) { panning = false; viewport.ReleasePointerCapture(e.Pointer); }
        }
        viewport.PointerReleased += End;
        viewport.PointerCanceled += End;
        viewport.PointerCaptureLost += (_, _) => panning = false;

        viewport.PointerWheelChanged += (_, e) =>
        {
            Microsoft.UI.Input.PointerPoint pp = e.GetCurrentPoint(viewport);
            double factor = pp.Properties.MouseWheelDelta > 0 ? 1.12 : 1 / 1.12;
            double scale = Math.Clamp(xf.ScaleX * factor, 0.2, 3.0);
            factor = scale / xf.ScaleX;
            Windows.Foundation.Point c = pp.Position;
            xf.TranslateX = c.X - (c.X - xf.TranslateX) * factor;
            xf.TranslateY = c.Y - (c.Y - xf.TranslateY) * factor;
            xf.ScaleX = xf.ScaleY = scale;
            e.Handled = true;
        };
    }

    private static string KindLabel(InventorDocument.DocKind k) => k switch
    {
        InventorDocument.DocKind.Part => "Part",
        InventorDocument.DocKind.Assembly => "Assembly",
        InventorDocument.DocKind.Drawing => "Drawing",
        InventorDocument.DocKind.Presentation => "Presentation",
        InventorDocument.DocKind.Step => "STEP",
        _ => "Document"
    };
}
