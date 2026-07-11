using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.Web.WebView2.Core;

namespace ExtrabbitCode.Inventor.MetaReader.App;

/// <summary>
/// The reference graph, rendered with the bundled vis-network library inside a WebView2: interactive
/// (drag nodes, the rest reacts; pan, zoom), with the toolbar dropdown switching between hierarchical
/// left-right / top-down layouts and the organic physics network. Double-click a node to open it.
/// </summary>
public sealed partial class DocumentView
{
    private const string GraphHost = "inventormeta.refgraph";
    // Stable ids per node so positions survive rebuilds (expand/collapse); reset per graph.
    private readonly Dictionary<RefNode, int> _stableIds = new();
    private int _nextStableId;
    private readonly Dictionary<int, RefNode> _nodeById = new();   // current visible id -> node, for clicks

    // The live graph, kept so we can re-theme it when the app switches light/dark.
    private WebView2? _graphWeb;
    private RefNode? _graphRoot;
    private Border? _graphChrome;

    /// <summary>Re-themes the reference graph - page palette, WebView2 background and the toolbar
    /// chrome - when the app toggles light/dark. The WebView2 doesn't follow the XAML theme itself.</summary>
    private void OnActualThemeChanged(FrameworkElement sender, object args)
    {
        if (_graphWeb is null || _graphRoot is null) { return; }
        try
        {
            byte g = (byte)(ActualTheme != ElementTheme.Light ? 0x1f : 0xf6);
            _graphWeb.DefaultBackgroundColor = Windows.UI.Color.FromArgb(255, g, g, g);
            if (_graphChrome != null)
            {
                _graphChrome.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0xE6, g, g, g));
            }
            SendGraph(_graphWeb, _graphRoot);   // pushes the new palette (incl. canvas background)
        }
        catch (Exception ex) { Serilog.Log.Debug(ex, "Re-theming the reference graph failed"); }
    }

    private const string GraphSettingsKey = "graph.physics";
    // Tunable graph options exposed in the in-graph cogwheel panel; persisted as a JSON blob.
    private static readonly Dictionary<string, double> GraphDefaults = new()
    {
        ["fadeMs"] = 420, ["gravitationalConstant"] = -9000, ["centralGravity"] = 0.35,
        ["springLength"] = 140, ["springConstant"] = 0.05, ["damping"] = 0.6, ["avoidOverlap"] = 0.6
    };

    private static Dictionary<string, double> LoadGraphSettings()
    {
        Dictionary<string, double> s = new(GraphDefaults);
        string? saved = AppSettings.Get(GraphSettingsKey);
        if (!string.IsNullOrEmpty(saved))
        {
            try
            {
                Dictionary<string, double>? d = JsonSerializer.Deserialize<Dictionary<string, double>>(saved);
                if (d != null) { foreach (KeyValuePair<string, double> kv in d) { s[kv.Key] = kv.Value; } }
            }
            catch { /* fall back to defaults */ }
        }
        return s;
    }

    private UIElement RenderRefGraphWeb(RefNode root, Border host)
    {
        _graphLayout = ViewerSettings.GraphLayout;
        _stableIds.Clear(); _nextStableId = 0;
        SetAllExpanded(root, false);   // start collapsed to just the root

        // Opaque background: a transparent WebView2 surface makes pointer/hover input flaky in WinUI.
        byte g = (byte)(ActualTheme != ElementTheme.Light ? 0x1f : 0xf6);
        WebView2 web = new() { DefaultBackgroundColor = Windows.UI.Color.FromArgb(255, g, g, g) };
        Grid viewport = new() { Background = new SolidColorBrush(Colors.Transparent) };
        viewport.Children.Add(web);   // the WebView2 fills the cell; the toolbar floats over its top-right corner

        // --- toolbar (floats over the graph's top-right corner, so it costs no extra row) ---
        StackPanel toolbar = new()
        {
            Orientation = Orientation.Horizontal, Spacing = 4,
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center
        };
        static Button IconBtn(int glyph, string tip)
        {
            Button b = new() { Content = new FontIcon { Glyph = G(glyph), FontSize = 14 }, Padding = new Thickness(8, 5, 8, 5), MinWidth = 0 };
            ToolTipService.SetToolTip(b, tip);
            return b;
        }

        ComboBox layoutPick = new()
        {
            MinWidth = 0, VerticalAlignment = VerticalAlignment.Center,
            Items = { "Left → Right", "Top → Bottom", "Network" }, SelectedIndex = (int)_graphLayout
        };
        ToolTipService.SetToolTip(layoutPick, "Graph layout");
        layoutPick.SelectionChanged += (_, _) =>
        {
            _graphLayout = (GraphLayout)Math.Max(0, layoutPick.SelectedIndex);
            ViewerSettings.GraphLayout = _graphLayout;
            SendGraph(web, root);
        };

        Button expandAll = IconBtn(0xE710, "Expand all");
        expandAll.Click += (_, _) => { SetAllExpanded(root, true); SendGraph(web, root); };
        Button collapseAll = IconBtn(0xE738, "Collapse all");
        collapseAll.Click += (_, _) => { SetAllExpanded(root, false); SendGraph(web, root); };

        Button fit = IconBtn(0xE9A6, "Fit to view");
        fit.Click += (_, _) => Post(web, "{\"cmd\":\"fit\"}");

        Button cog = IconBtn(0xE713, "Graph options");
        cog.Click += (_, _) => Post(web, "{\"cmd\":\"toggleSettings\"}");

        ToggleButton thumbs = new()
        {
            Content = new FontIcon { Glyph = G(0xE8B9), FontSize = 14 },
            Padding = new Thickness(8, 5, 8, 5), MinWidth = 0, IsChecked = _showThumbs
        };
        ToolTipService.SetToolTip(thumbs, "Show thumbnails");
        thumbs.Click += (_, _) => { _showThumbs = thumbs.IsChecked == true; SendGraph(web, root); };

        // Fullscreen: pop the viewport (graph + toolbar) into a window-filling overlay + OS fullscreen.
        FontIcon fullIcon = new() { Glyph = G(0xE740), FontSize = 14 };
        Button full = new() { Content = fullIcon, Padding = new Thickness(8, 5, 8, 5), MinWidth = 0 };
        ToolTipService.SetToolTip(full, "Fullscreen");
        Grid? overlay = null;
        void SetFullscreen(bool on)
        {
            if (HostWindow is not MainWindow win || on == _graphFs) { return; }
            if (on)
            {
                host.Child = null;
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
                host.Child = viewport;
                overlay = null;
                _graphFs = false;
            }
            fullIcon.Glyph = G(_graphFs ? 0xE73F : 0xE740);
            ToolTipService.SetToolTip(full, _graphFs ? "Exit fullscreen" : "Fullscreen");
            full.Focus(FocusState.Programmatic);
        }
        full.Click += (_, _) => SetFullscreen(!_graphFs);
        _exitGraphFullscreen = () => SetFullscreen(false);
        Microsoft.UI.Xaml.Input.KeyboardAccelerator esc = new() { Key = Windows.System.VirtualKey.Escape };
        esc.Invoked += (_, e) => { if (_graphFs) { SetFullscreen(false); e.Handled = true; } };
        full.KeyboardAccelerators.Add(esc);

        toolbar.Children.Add(layoutPick);
        toolbar.Children.Add(expandAll);
        toolbar.Children.Add(collapseAll);
        toolbar.Children.Add(thumbs);
        toolbar.Children.Add(fit);
        toolbar.Children.Add(cog);
        toolbar.Children.Add(full);

        // Floating chrome so the controls stay legible over whatever the graph draws beneath them.
        Border toolbarChrome = new()
        {
            Child = toolbar,
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 8, 12, 0), Padding = new Thickness(4), CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0xE6, g, g, g)),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(0x28, 0x80, 0x80, 0x80))
        };
        viewport.Children.Add(toolbarChrome);

        _graphWeb = web;
        _graphRoot = root;
        _graphChrome = toolbarChrome;

        _ = InitGraphAsync(web, root);
        return viewport;
    }

    private async Task InitGraphAsync(WebView2 web, RefNode root)
    {
        try
        {
            await web.EnsureCoreWebView2Async();
            // map the whole Assets folder so the page can load the bundled vis library and the type icons
            string dir = Path.Combine(AppContext.BaseDirectory, "Assets");
            web.CoreWebView2.SetVirtualHostNameToFolderMapping(GraphHost, dir, CoreWebView2HostResourceAccessKind.Allow);
            web.CoreWebView2.WebMessageReceived += (_, a) => OnGraphMessage(web, root, a);
            web.CoreWebView2.Navigate($"https://{GraphHost}/vis/graph.html");
        }
        catch (Exception ex) { Serilog.Log.Error(ex, "Reference graph WebView2 init failed"); }
    }

    private void OnGraphMessage(WebView2 web, RefNode root, CoreWebView2WebMessageReceivedEventArgs a)
    {
        try
        {
            using JsonDocument jd = JsonDocument.Parse(a.TryGetWebMessageAsString());
            string? type = jd.RootElement.GetProperty("type").GetString();
            if (type == "ready") { SendGraph(web, root); return; }
            if (type == "settings")
            {
                if (jd.RootElement.TryGetProperty("values", out JsonElement vals)) { AppSettings.Set(GraphSettingsKey, vals.GetRawText()); }
                return;
            }
            if (!jd.RootElement.TryGetProperty("id", out JsonElement idEl)
                || !idEl.TryGetInt32(out int id) || !_nodeById.TryGetValue(id, out RefNode? n)) { return; }

            if (type == "toggle" && n.Children.Count > 0)
            {
                n.Expanded = !n.Expanded;
                SendGraph(web, root);
            }
            else if (type == "open" && n.Resolved && n.Depth > 0 && !n.Cyclic && !n.IsLinkedFile)
            {
                OpenReference(n.Path);
            }
            else if (type == "reveal" && n.Resolved)
            {
                RevealInExplorer(n.Path);
            }
        }
        catch { /* ignore malformed messages */ }
    }

    /// <summary>Expand the whole tree (nodes that have children) or collapse to just the root node.</summary>
    private static void SetAllExpanded(RefNode root, bool expand)
    {
        void Walk(RefNode n) { n.Expanded = expand && n.Children.Count > 0; foreach (RefNode c in n.Children) { Walk(c); } }
        Walk(root);
    }

    private static void Post(WebView2 web, string json)
    {
        try { web.CoreWebView2?.PostWebMessageAsJson(json); } catch { /* not ready */ }
    }

    private void SendGraph(WebView2 web, RefNode root)
    {
        if (web.CoreWebView2 != null) { Post(web, BuildGraphJson(root)); }
    }

    /// <summary>Flattens the reference tree into vis-network nodes/edges (+ a theme palette) and
    /// returns the JSON the page consumes. Each tree node becomes one graph node keyed by its index.</summary>
    private string BuildGraphJson(RefNode root)
    {
        List<RefNode> visible = [];
        _nodeById.Clear();
        int Id(RefNode n) => _stableIds.TryGetValue(n, out int v) ? v : (_stableIds[n] = _nextStableId++);
        void Walk(RefNode n) { _nodeById[Id(n)] = n; visible.Add(n); if (n.Expanded) { foreach (RefNode c in n.Children) { Walk(c); } } }
        Walk(root);

        // Tidy tree positions computed here (one row per leaf, parents centred over their children) so
        // the LR / TopDown layouts are correct and stable on expand. Network mode lets vis place nodes.
        bool isNet = _graphLayout == GraphLayout.Network;
        bool topDown = _graphLayout == GraphLayout.TopDown;
        Dictionary<RefNode, double> row = new();
        int leaf = 0;
        void AssignRow(RefNode n)
        {
            if (!n.Expanded || n.Children.Count == 0) { row[n] = leaf++; return; }
            foreach (RefNode c in n.Children) { AssignRow(c); }
            row[n] = (row[n.Children[0]] + row[n.Children[^1]]) / 2.0;
        }
        if (!isNet) { AssignRow(root); }

        bool dark = ActualTheme != ElementTheme.Light;
        string nodeBg = dark ? "#2b2b2b" : "#ffffff", nodeBorder = dark ? "#5a5a5a" : "#c4c4c4";
        string fontCol = dark ? "#e8e8e8" : "#1c1c1c", subDim = dark ? "#9a9a9a" : "#6a6a6a";
        string hiBg = dark ? "#373737" : "#eef3fb";
        string badgeBg = dark ? "#3a3a3a" : "#eef1f5", badgeBorder = dark ? "#707070" : "#b9c0c8";
        string badgeHi = dark ? "#4a4a4a" : "#dce8f8", canvasBg = dark ? "#1f1f1f" : "#f6f6f6";
        string edge = dark ? "#7c7c7c" : "#b3b3b3", edgeHi = dark ? "#b4b4b4" : "#6f6f6f";
        const string accent = "#4f9cff", red = "#d16060", gold = "#d69e2e";

        List<object> nodes = [];
        List<object> edges = [];
        foreach (RefNode n in visible)
        {
            int i = Id(n);
            bool isFactory = n.IPart == IPartRole.Factory, isMember = n.IPart == IPartRole.Member;
            string ext = Path.GetExtension(n.Name).TrimStart('.').ToLowerInvariant();
            string sub = n.Cyclic ? "↻ already shown"
                : !n.Resolved ? (n.IsLinkedFile ? "linked - not found" : "not found")
                : n.ReadError ? "unreadable"
                : n.IsLinkedFile ? "linked " + ext
                : isFactory ? "iPart factory"
                : isMember ? "iPart member"
                : KindLabel(n.Kind);
            string kind = n.IsLinkedFile ? "other" : n.Kind switch
            {
                InventorDocument.DocKind.Part => "part",
                InventorDocument.DocKind.Assembly => "assembly",
                InventorDocument.DocKind.Drawing => "drawing",
                InventorDocument.DocKind.Presentation => "presentation",
                InventorDocument.DocKind.Step => "other",
                _ => "other"
            };

            Dictionary<string, object?> node = new()
            {
                ["id"] = i, ["name"] = n.Name, ["sub"] = sub, ["title"] = n.Path, ["kind"] = kind,
                ["border"] = n.Depth == 0 ? accent : !n.Resolved ? red : isFactory ? gold : nodeBorder,
                ["bw"] = n.Depth == 0 || isFactory || !n.Resolved ? 2 : 1,
                ["subcolor"] = !n.Resolved ? red : isFactory || isMember ? gold : null,
                ["subbold"] = !n.Resolved || isFactory || isMember,
                ["haschildren"] = n.Children.Count > 0, ["expanded"] = n.Expanded, ["count"] = n.Children.Count,
                ["canopen"] = n.Resolved && n.Depth > 0 && !n.Cyclic && !n.IsLinkedFile,
                ["canreveal"] = n.Resolved
            };
            if (_showThumbs && n.Thumbnail is { Length: > 0 })
            {
                node["image"] = $"data:image/{(n.ThumbnailExt == "bmp" ? "bmp" : "png")};base64,{Convert.ToBase64String(n.Thumbnail)}";
            }
            if (!isNet)
            {
                node["x"] = topDown ? row[n] * 250 : n.Depth * 300;
                node["y"] = topDown ? n.Depth * 132 : row[n] * 84;
            }
            else if (n.Depth == 0)   // pin the root so the physics graph doesn't drift around
            {
                node["x"] = 0; node["y"] = 0; node["fixed"] = new { x = true, y = true };
            }
            nodes.Add(node);
            if (n.Expanded) { foreach (RefNode c in n.Children) { edges.Add(new { from = i, to = Id(c) }); } }
        }

        Dictionary<string, object?> payload = new()
        {
            ["cmd"] = "build",
            ["layout"] = _graphLayout switch { GraphLayout.TopDown => "UD", GraphLayout.Network => "network", _ => "LR" },
            ["palette"] = new { nodeBg, hiBg, font = fontCol, sub = subDim, badgeBg, badgeBorder, badgeHi, badgeHiBorder = accent, bg = canvasBg, edge, edgeHi },
            ["settings"] = LoadGraphSettings(),
            ["settingsDefaults"] = GraphDefaults,
            ["nodes"] = nodes,
            ["edges"] = edges
        };
        return JsonSerializer.Serialize(payload);
    }
}
