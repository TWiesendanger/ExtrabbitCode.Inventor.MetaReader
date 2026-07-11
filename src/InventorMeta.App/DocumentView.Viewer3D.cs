using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.Web.WebView2.Core;

namespace ExtrabbitCode.Inventor.MetaReader.App;

/// <summary>
/// The 3D viewer: clicking the thumbnail (or the command-bar 3D button) opens a window-filling
/// overlay that shows the model in the Autodesk LMV viewer. The SVF viewable is read from the cache
/// store; on a miss it's generated with Inventor first. Click outside the panel (or Esc) to close.
/// </summary>
public sealed partial class DocumentView
{
    private const string ViewerHost = "inventormeta.viewer";

    /// <summary>The Autodesk (LMV) viewer release the app loads from the CDN. Pinned (issue #29) so an
    /// Autodesk-pushed update can't change behaviour under us - bump deliberately and retest.</summary>
    private const string LmvViewerVersion = "7.122";

    private bool _viewerOpen;

    private void OnOpen3D(object sender, RoutedEventArgs e) => _ = OpenViewer3DAsync();

    private void OnThumbTapped(object sender, TappedRoutedEventArgs e)
    {
        if (Document?.Kind is InventorDocument.DocKind.Part or InventorDocument.DocKind.Assembly)
        {
            _ = OpenViewer3DAsync();
        }
    }

    private async Task OpenViewer3DAsync()
    {
        if (_viewerOpen || Document == null || HostWindow is not MainWindow win || !File.Exists(FilePath))
        {
            return;
        }

        _viewerOpen = true;
        string coloring = ViewerSettings.ColoringMode == ColoringMode.Multicolor ? "multicolor" : "default";
        Analytics.Capture("viewer_3d_opened", new System.Collections.Generic.Dictionary<string, object?>
        {
            ["doc_kind"] = Document.Kind.ToString(),
            ["coloring"] = coloring
        });
        ViewerLog.Clear();   // fresh log per open

        // ---- overlay: dim backdrop (click-out closes) + centred panel with the WebView2 + status ----
        WebView2 web = new();

        ProgressRing ring = new() { IsActive = true, Width = 36, Height = 36 };
        TextBlock statusText = new()
        {
            Text = "Preparing…", FontSize = 14, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.White), MaxWidth = 460
        };
        Border statusHost = new()
        {
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0xE6, 0x1B, 0x1B, 0x1B)),
            Child = new StackPanel
            {
                Spacing = 16, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                Children = { ring, statusText }
            }
        };

        Button close = new()
        {
            Content = new FontIcon { Glyph = G(0xE711), FontSize = 14 },
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 10, 10, 0), Width = 36, Height = 36, Padding = new Thickness(0),
            CornerRadius = new CornerRadius(18)
        };
        ToolTipService.SetToolTip(close, "Close (Esc)");

        Grid panelGrid = new();
        panelGrid.Children.Add(web);
        panelGrid.Children.Add(statusHost);
        panelGrid.Children.Add(close);

        Border panel = new()
        {
            Margin = new Thickness(48), CornerRadius = new CornerRadius(10), Child = panelGrid,
            Background = (Brush)Application.Current.Resources["SolidBackgroundFillColorBaseBrush"],
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultSolidBrush"],
            BorderThickness = new Thickness(1)
        };
        panel.Tapped += (_, e) => e.Handled = true;   // clicks inside the panel don't close

        Grid root = new() { Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        root.Children.Add(panel);

        void Close()
        {
            if (!_viewerOpen) { return; }
            _viewerOpen = false;
            try { web.Close(); } catch { /* disposing */ }
            win.HideOverlay();
        }

        root.Tapped += (_, _) => Close();
        close.Click += (_, _) => Close();
        KeyboardAccelerator esc = new() { Key = Windows.System.VirtualKey.Escape };
        esc.Invoked += (_, e) => { e.Handled = true; Close(); };
        close.KeyboardAccelerators.Add(esc);

        win.ShowOverlay(root, dimmed: true);
        close.Focus(FocusState.Programmatic);

        // ---- ensure an SVF exists (generate on a miss), then load it into the viewer ----
        try
        {
            SvfStore store = new(ViewerSettings.NetworkPath);
            statusText.Text = "Checking cache…";
            string key = await Task.Run(() => SvfStore.ComputeKey(FilePath));
            bool cached = store.Has(key);
            Serilog.Log.Information("3D view for {File} (cached={Cached})", Path.GetFileName(FilePath), cached);

            if (!cached)
            {
                InventorInstall? inv = await ResolveInventorAsync(win);
                if (inv == null)
                {
                    ring.IsActive = false;
                    statusText.Text = InventorInstalls.Detect().Count == 0
                        ? "Inventor isn't installed, so a 3D viewable can't be generated.\nOpen this model on a machine with Inventor, or point at a shared store that already has it."
                        : "No Inventor version selected.";
                    return;
                }

                statusText.Text = $"Generating the 3D view with {inv.DisplayName}…\nThis opens the model in Inventor and can take a while.";
                Serilog.Log.Information("Generating SVF with {Version} for {File}", inv.DisplayName, Path.GetFileName(FilePath));
                SvfGenerator.Result res = await GenerateOnStaThread(inv, FilePath, store.EntryDir(key));
                if (!res.Ok)
                {
                    Serilog.Log.Error("SVF generation failed for {File}: {Error}", FilePath, res.Error);
                    ring.IsActive = false;
                    statusText.Text = "Couldn't generate the 3D view:\n" + res.Error;
                    return;
                }
                Serilog.Log.Information("SVF generated for {File}", Path.GetFileName(FilePath));
            }

            string? bubble = store.FindBubble(key);
            if (bubble == null)
            {
                ring.IsActive = false;
                statusText.Text = "The 3D view was generated but its manifest is missing.";
                return;
            }

            statusText.Text = "Loading viewer…";
            await web.EnsureCoreWebView2Async();
            if (!_viewerOpen) { return; }   // user closed while we were generating

            // bubble = <entry>\bubble.json (copied to the root by the generator). Map the host to
            // <entry> and serve the page from the host ROOT, loading "./bubble.json" - so the LMV
            // viewer sets $file$ = <entry> and "$file$/output/..." resolves to <entry>\output\...
            // (same-origin too, so no CORS on the SVF/worker fetches).
            string entryDir = Path.GetDirectoryName(bubble)!;
            web.CoreWebView2.SetVirtualHostNameToFolderMapping(ViewerHost, entryDir, CoreWebView2HostResourceAccessKind.Allow);
            try { File.WriteAllText(Path.Combine(entryDir, "viewer.html"), ViewerHtml.Replace("{LMV_VERSION}", LmvViewerVersion)); } catch { /* read-only store */ }

            // diagnostics: log failed resource fetches and JS messages to %TEMP%\invmeta-viewer.log
            web.CoreWebView2.WebResourceResponseReceived += (_, a) =>
            {
                try { int s = a.Response.StatusCode; if (s is 0 or >= 400) { ViewerLog.Write($"HTTP {s}  {a.Request.Uri}"); } }
                catch { /* ignore */ }
            };
            web.CoreWebView2.WebMessageReceived += (_, a) =>
            {
                try { ViewerLog.Write("js: " + a.TryGetWebMessageAsString()); } catch { /* ignore */ }
            };
            web.CoreWebView2.NavigationCompleted += (_, _) => statusHost.Visibility = Visibility.Collapsed;
            web.CoreWebView2.Navigate($"https://{ViewerHost}/viewer.html?coloring={coloring}");
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "3D view error for {File}", FilePath);
            ring.IsActive = false;
            statusText.Text = "3D view error:\n" + ex.Message;
        }
    }

    /// <summary>Resolves which Inventor release to generate with: the saved choice, the only one
    /// installed, or a prompt (remembered). Null if none installed or the user cancels.</summary>
    private static async Task<InventorInstall?> ResolveInventorAsync(MainWindow win)
    {
        var installs = InventorInstalls.Detect();
        if (installs.Count == 0) { return null; }

        InventorInstall? saved = installs.FirstOrDefault(i => i.Year == ViewerSettings.InventorYear);
        if (saved != null) { return saved; }
        if (installs.Count == 1) { ViewerSettings.InventorYear = installs[0].Year; return installs[0]; }

        ComboBox combo = new() { ItemsSource = installs.Select(i => i.DisplayName).ToList(), SelectedIndex = 0 };
        ContentDialog dlg = new()
        {
            Title = "Choose Inventor version",
            Content = new StackPanel
            {
                Spacing = 10,
                Children =
                {
                    new TextBlock { Text = "Which installed Inventor should generate the 3D viewable?", TextWrapping = TextWrapping.Wrap },
                    combo
                }
            },
            PrimaryButtonText = "Use", CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = win.Content.XamlRoot
        };

        if (await dlg.ShowAsync() != ContentDialogResult.Primary) { return null; }
        InventorInstall pick = installs[combo.SelectedIndex];
        ViewerSettings.InventorYear = pick.Year;
        return pick;
    }

    /// <summary>Runs the (blocking, COM) generation on a dedicated STA thread, off the UI thread.</summary>
    private static Task<SvfGenerator.Result> GenerateOnStaThread(InventorInstall inv, string file, string baseDir)
    {
        bool hide = ViewerSettings.HideInventor;
        bool silent = ViewerSettings.SilentInventor;
        TaskCompletionSource<SvfGenerator.Result> tcs = new();
        Thread t = new(() =>
        {
            try { tcs.SetResult(SvfGenerator.Generate(inv, file, baseDir, hide, silent,
                log: m => Serilog.Log.Information("SVF gen: {Step}", m))); }
            catch (Exception ex) { tcs.SetResult(new SvfGenerator.Result(false, null, ex.Message)); }
        }) { IsBackground = true };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        return tcs.Task;
    }

    // The LMV viewer page (scripts from the Autodesk CDN), loading the cached bubble.json over the
    // mapped virtual host. env:"Local" => no cloud auth, reads the local SVF directly.
    private const string ViewerHtml = """
<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="UTF-8"/>
<meta name="viewport" content="width=device-width, initial-scale=1.0"/>
<link rel="stylesheet" href="https://developer.api.autodesk.com/modelderivative/v2/viewers/{LMV_VERSION}/style.min.css" type="text/css"/>
<script src="https://developer.api.autodesk.com/modelderivative/v2/viewers/{LMV_VERSION}/viewer3D.min.js"></script>
<style>
  html,body,#viewer{margin:0;height:100%;width:100%;overflow:hidden;background:#2b2b2b;}
  /* palette glyph for the coloring toolbar button (LMV would otherwise show an empty icon) */
  #extrabbit-coloring-btn .adsk-button-icon:before{content:"\1F3A8";font-size:20px;line-height:1;}
</style>
</head>
<body>
<div id="viewer"></div>
<script>
  function report(m){ try{ window.chrome.webview.postMessage(String(m)); }catch(e){} console.log(m); }
  window.addEventListener("error", e => report("error: " + e.message));
  window.addEventListener("unhandledrejection", e => report("reject: " + (e.reason && e.reason.message || e.reason)));

  // --- Coloring: give every body its own colour so parts of an assembly are easy to tell apart. ---
  // Golden-angle hue spacing keeps neighbouring bodies separated; soft, CAD-ish saturation (low S,
  // high V) keeps the palette muted rather than neon.
  function hsvToRgb(h, s, v){
    let r = v, g = v, b = v;
    if (s > 0){
      const hh = h * 6, i = Math.floor(hh) % 6, f = hh - Math.floor(hh);
      const p = v * (1 - s), q = v * (1 - s * f), t = v * (1 - s * (1 - f));
      if (i === 0){ r = v; g = t; b = p; }
      else if (i === 1){ r = q; g = v; b = p; }
      else if (i === 2){ r = p; g = v; b = t; }
      else if (i === 3){ r = p; g = q; b = v; }
      else if (i === 4){ r = t; g = p; b = v; }
      else { r = v; g = p; b = q; }
    }
    return [r, g, b];
  }
  function bodyColor(i){
    const rgb = hsvToRgb((i * 0.6180339887) % 1, 0.56, 0.80);   // golden-ratio hue, soft CAD saturation
    // w < 1 blends the tint with the surface's own shading so it stays muted, not neon - theming
    // paints over an already-lit surface, so a full-intensity tint reads much brighter than a baked colour.
    return new THREE.Vector4(rgb[0], rgb[1], rgb[2], 0.86);
  }

  class ColoringExtension extends Autodesk.Viewing.Extension {
    load(){
      this._on = false;
      this._maybeInitial = this._maybeInitial.bind(this);
      this.viewer.addEventListener(Autodesk.Viewing.GEOMETRY_LOADED_EVENT, this._maybeInitial);
      if (this.viewer.toolbar){ this.onToolbarCreated(this.viewer.toolbar); }
      else { this._onTb = () => this.onToolbarCreated(this.viewer.toolbar); this.viewer.addEventListener(Autodesk.Viewing.TOOLBAR_CREATED_EVENT, this._onTb); }
      return true;
    }
    unload(){
      this.viewer.removeEventListener(Autodesk.Viewing.GEOMETRY_LOADED_EVENT, this._maybeInitial);
      if (this._onTb){ this.viewer.removeEventListener(Autodesk.Viewing.TOOLBAR_CREATED_EVENT, this._onTb); }
      if (this._group && this.viewer.toolbar){ this.viewer.toolbar.removeControl(this._group); }
      this._group = this._button = null;
      return true;
    }
    onToolbarCreated(toolbar){
      if (this._group) { return; }                              // guard against a double call
      this._button = new Autodesk.Viewing.UI.Button("extrabbit-coloring-btn");
      this._button.setToolTip("Body coloring — give every body its own colour");
      this._button.onClick = () => this.setEnabled(!this._on);
      this._group = new Autodesk.Viewing.UI.ControlGroup("extrabbit-coloring-group");
      this._group.addControl(this._button);
      toolbar.addControl(this._group);
      this._sync();
      this._maybeInitial();
    }
    _maybeInitial(){                                            // apply the app's chosen starting mode, once the model is ready
      if (this.options && this.options.initial && !this._on && this._hasTree()){ this.setEnabled(true); }
    }
    _hasTree(){ const m = this.viewer.model; return !!(m && m.getInstanceTree && m.getInstanceTree()); }
    setEnabled(on){
      this._on = on;
      if (on){ this._applyColors(); } else { this.viewer.clearThemingColors(this.viewer.model); }
      this._sync();
      report("coloring " + (on ? "on" : "off"));
    }
    _sync(){
      if (!this._button) { return; }
      const S = Autodesk.Viewing.UI.Button.State;
      this._button.setState(this._on ? S.ACTIVE : S.INACTIVE);
    }
    _applyColors(){
      const model = this.viewer.model;
      const tree = model && model.getInstanceTree && model.getInstanceTree();
      if (!tree){ return; }
      const leaves = [];
      tree.enumNodeChildren(tree.getRootId(), (dbId) => {
        if (tree.getChildCount(dbId) === 0){ leaves.push(dbId); }   // a leaf node is one body
      }, true);
      leaves.forEach((dbId, i) => this.viewer.setThemingColor(dbId, bodyColor(i), model, true));
      report("colored " + leaves.length + " bodies");
    }
  }
  Autodesk.Viewing.theExtensionManager.registerExtension("Extrabbit.Coloring", ColoringExtension);

  const wantMulticolor = new URLSearchParams(location.search).get("coloring") === "multicolor";

  const viewer = new Autodesk.Viewing.GuiViewer3D(document.getElementById("viewer"));
  Autodesk.Viewing.Initializer({ env: "Local", useADP: false }, () => {
    report("initialized");
    viewer.start();
    viewer.loadExtension("Extrabbit.Coloring", { initial: wantMulticolor }).then(
      () => report("coloring extension loaded"),
      (err) => report("coloring extension failed: " + err));
    viewer.addEventListener(Autodesk.Viewing.GEOMETRY_LOADED_EVENT, () => {
      const m = viewer.model;
      const box = m && m.getBoundingBox && m.getBoundingBox();
      if (box) {
        viewer.navigation.fitBounds(true, box);   // immediate, non-animated fit
        let c;
        try { c = box.getCenter ? box.getCenter(new THREE.Vector3()) : box.center(); } catch (e) { report("center err: " + e); }
        if (c) {
          report("center " + c.x.toFixed(1) + "," + c.y.toFixed(1) + "," + c.z.toFixed(1));
          try { viewer.navigation.setPivotPoint(c); } catch (e) { report("setPivotPoint: " + e); }  // orbit around the model, not origin
        }
      } else {
        viewer.fitToView();
      }
      // lock the fitted view in as the home view (correct method lives on autocam)
      try { viewer.autocam.setHomeViewFrom(viewer.getCamera()); report("home set"); }
      catch (e) { report("autocam.setHomeViewFrom: " + e); }
    });
    Autodesk.Viewing.Document.load(
      "./bubble.json",
      (doc) => {
        report("bubble loaded");
        const node = doc.getRoot().getDefaultGeometry();
        report("geometry node: " + (node ? "found" : "MISSING"));
        // createWireframe makes LMV derive edges client-side (mesh boundaries + hard angles) - our
        // SVF has no edge data, so without it model.hasEdges stays false and LMV hides the "Display
        // edges" setting, meaning it could never be turned on. With it, the setting appears in the
        // viewer's Appearance settings and the chosen state persists in localStorage.
        viewer.loadDocumentNode(doc, node, { createWireframe: true }).then(
          () => {
            // edges on by default for an Inventor-like shaded-with-edges look; the setting still
            // toggles it, but each opened model starts with edges shown
            try { viewer.setDisplayEdges(true); } catch (e) { report("setDisplayEdges: " + e); }
            report("model loaded");
          },
          (err) => report("loadDocumentNode failed: " + JSON.stringify(err)));
      },
      (code, msg) => report("Document.load failed: code=" + code + " msg=" + msg));
  });
</script>
</body>
</html>
""";
}
