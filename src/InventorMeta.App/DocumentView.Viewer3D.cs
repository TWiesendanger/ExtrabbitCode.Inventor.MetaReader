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
    private const string StepViewerHost = "inventormeta.stepviewer";
    private const string StepModelHost = "inventormeta.stepmodel";

    /// <summary>The Autodesk (LMV) viewer release the app loads from the CDN. Pinned (issue #29) so an
    /// Autodesk-pushed update can't change behaviour under us - bump deliberately and retest.</summary>
    private const string LmvViewerVersion = "7.122";

    private bool _viewerOpen;

    private void OnOpen3D(object sender, RoutedEventArgs e) => _ = OpenViewer3DAsync();

    private void OnThumbTapped(object sender, TappedRoutedEventArgs e)
    {
        if (Document?.Kind is InventorDocument.DocKind.Part or InventorDocument.DocKind.Assembly or InventorDocument.DocKind.Step)
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

        if (Document.IsStep)
        {
            await OpenStepViewer3DAsync(win);
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
                SvfEngine? engine = await ResolveEngineAsync(win);
                if (engine == null)
                {
                    ring.IsActive = false;
                    statusText.Text = "No 3D engine selected.";
                    return;
                }

                string engineInfo;
                if (engine == SvfEngine.Local)
                {
                    string converterVersion =
                        typeof(ExtrabbitCode.Inventor.SvfConverter.InventorSvfConverter)
                            .Assembly.GetName().Version?.ToString(3) ?? "unknown";
                    engineInfo = $"Built-in converter (ExtrabbitCode.Inventor.SvfConverter {converterVersion}, best effort)";

                    statusText.Text = "Converting with the built-in engine (best effort)…";
                    Serilog.Log.Information("Converting SVF locally (best effort) for {File}", Path.GetFileName(FilePath));
                    string file = FilePath;
                    string outDir = store.OutputDir(key);
                    string status = await Task.Run(() =>
                        ExtrabbitCode.Inventor.SvfConverter.InventorSvfConverter.ConvertFile(file, outDir));
                    Serilog.Log.Information("Local SVF conversion for {File}: {Status}", Path.GetFileName(FilePath), status);
                    if (status.Contains("0v/0t"))
                    {
                        ring.IsActive = false;
                        statusText.Text = "The built-in converter found no displayable geometry cached in this file.\nSave the model once in Inventor (to refresh its cached mesh) and try again.";
                        return;
                    }
                }
                else
                {
                    InventorInstall? inv = await ResolveInventorAsync(win);
                    if (inv == null)
                    {
                        ring.IsActive = false;
                        statusText.Text = "No Inventor version selected.";
                        return;
                    }
                    engineInfo = $"{inv.DisplayName} (SVF translator add-in, exact)";

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

                // Provenance marker: which model this entry came from and which engine made it -
                // makes cache entries (SHA-named folders) identifiable when browsing the store.
                try
                {
                    File.WriteAllText(Path.Combine(store.EntryDir(key), "source.txt"),
                        $"Source:    {Path.GetFileName(FilePath)}{Environment.NewLine}" +
                        $"Path:      {FilePath}{Environment.NewLine}" +
                        $"Engine:    {engineInfo}{Environment.NewLine}" +
                        $"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}{Environment.NewLine}" +
                        $"App:       Inventor MetaReader {AppInfo.Version}{Environment.NewLine}");
                }
                catch { /* best effort */ }
            }

            // An Inventor-generated entry has a bubble.json manifest at its root; a built-in-converter
            // entry has the raw SVF at output\0.svf. The layout doubles as the "which engine made
            // this?" marker, so cached best-effort entries keep their warning badge.
            string? bubble = store.FindBubble(key);
            string? localSvf = bubble == null ? store.FindLocalSvf(key) : null;
            if (bubble == null && localSvf == null)
            {
                ring.IsActive = false;
                statusText.Text = "The 3D view was generated but its manifest is missing.";
                return;
            }
            bool bestEffort = bubble == null;

            statusText.Text = "Loading viewer…";
            await web.EnsureCoreWebView2Async();
            if (!_viewerOpen) { return; }   // user closed while we were generating

            // bubble = <entry>\bubble.json (copied to the root by the generator). Map the host to
            // <entry> and serve the page from the host ROOT, loading "./bubble.json" - so the LMV
            // viewer sets $file$ = <entry> and "$file$/output/..." resolves to <entry>\output\...
            // (same-origin too, so no CORS on the SVF/worker fetches). For a best-effort entry the
            // page loads the raw SVF from ./output/0.svf instead.
            string entryDir = bestEffort
                ? Path.GetDirectoryName(Path.GetDirectoryName(localSvf!))!
                : Path.GetDirectoryName(bubble!)!;
            web.CoreWebView2.SetVirtualHostNameToFolderMapping(ViewerHost, entryDir, CoreWebView2HostResourceAccessKind.Allow);
            try { File.WriteAllText(Path.Combine(entryDir, "viewer.html"), ViewerHtml.Replace("{LMV_VERSION}", LmvViewerVersion)); }
            catch (Exception ex)
            {
                // A read-only shared store serves whatever viewer.html a previous writer left (or
                // none) - the viewer may be stale or fail to load. Surface it instead of hiding it.
                Serilog.Log.Warning("Couldn't refresh viewer.html in {Dir}: {Error}", entryDir, ex.Message);
            }

            // diagnostics: log failed resource fetches and JS messages to %TEMP%\invmeta-viewer.log
            web.CoreWebView2.WebResourceResponseReceived += (_, a) =>
            {
                try { int s = a.Response.StatusCode; if (s is 0 or >= 400) { ViewerLog.Write($"HTTP {s}  {a.Request.Uri}"); } }
                catch { /* ignore */ }
            };
            web.CoreWebView2.WebMessageReceived += (_, a) =>
            {
                try
                {
                    string msg = a.TryGetWebMessageAsString();
                    if (msg == "report-issue")   // the best-effort badge's "Report this model" link
                    {
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = EngineDialogs.ReportUri(Document?.FileName).AbsoluteUri,
                            UseShellExecute = true
                        });
                        return;
                    }
                    ViewerLog.Write("js: " + msg);
                }
                catch { /* ignore */ }
            };
            web.CoreWebView2.NavigationCompleted += (_, _) => statusHost.Visibility = Visibility.Collapsed;
            string query = $"?coloring={coloring}" + (bestEffort ? "&src=svf&engine=local" : "");
            web.CoreWebView2.Navigate($"https://{ViewerHost}/viewer.html{query}");
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "3D view error for {File}", FilePath);
            ring.IsActive = false;
            statusText.Text = "3D view error:\n" + ex.Message;
        }
    }

    private async Task OpenStepViewer3DAsync(MainWindow win)
    {
        _viewerOpen = true;
        Analytics.Capture("viewer_3d_opened", new System.Collections.Generic.Dictionary<string, object?>
        {
            ["doc_kind"] = "Step",
            ["converter"] = "occt-import-js",
            ["viewer"] = "Autodesk.glTF"
        });
        ViewerLog.Clear();

        WebView2 web = new();
        ProgressRing ring = new() { IsActive = true, Width = 36, Height = 36 };
        TextBlock statusText = new()
        {
            Text = "Preparing STEP conversion...", FontSize = 14, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.White), MaxWidth = 520
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
        panel.Tapped += (_, e) => e.Handled = true;

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

        try
        {
            string assetsDir = Path.Combine(AppContext.BaseDirectory, "Assets", "stepviewer");
            string viewerPage = Path.Combine(assetsDir, "step-viewer.html");
            if (!File.Exists(viewerPage))
            {
                ring.IsActive = false;
                statusText.Text = "The bundled STEP viewer assets are missing.";
                return;
            }

            statusText.Text = "Preparing local model cache...";
            string key = await Task.Run(() => SvfStore.ComputeKey(FilePath));
            string modelDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ExtrabbitCode.Inventor.MetaReader", "step-cache", key);
            Directory.CreateDirectory(modelDir);
            string modelPath = Path.Combine(modelDir, "model.stp");
            if (!File.Exists(modelPath))
            {
                File.Copy(FilePath, modelPath);
            }

            await web.EnsureCoreWebView2Async();
            if (!_viewerOpen) { return; }

            web.CoreWebView2.SetVirtualHostNameToFolderMapping(
                StepViewerHost, assetsDir, CoreWebView2HostResourceAccessKind.Allow);
            web.CoreWebView2.SetVirtualHostNameToFolderMapping(
                StepModelHost, modelDir, CoreWebView2HostResourceAccessKind.Allow);

            web.CoreWebView2.WebResourceResponseReceived += (_, a) =>
            {
                try { int s = a.Response.StatusCode; if (s is 0 or >= 400) { ViewerLog.Write($"HTTP {s}  {a.Request.Uri}"); } }
                catch { /* ignore */ }
            };
            web.CoreWebView2.WebMessageReceived += (_, a) =>
            {
                try
                {
                    string msg = a.TryGetWebMessageAsString();
                    ViewerLog.Write("step js: " + msg);
                    if (msg.StartsWith("status:", StringComparison.Ordinal))
                    {
                        statusText.Text = msg["status:".Length..];
                    }
                    else if (msg.StartsWith("loaded:", StringComparison.Ordinal))
                    {
                        statusHost.Visibility = Visibility.Collapsed;
                        StatusSink?.Invoke(msg["loaded:".Length..]);
                    }
                    else if (msg.StartsWith("error:", StringComparison.Ordinal))
                    {
                        ring.IsActive = false;
                        statusText.Text = "STEP 3D view error:\n" + msg["error:".Length..];
                    }
                }
                catch { /* ignore malformed viewer messages */ }
            };

            string modelUrl = $"https://{StepModelHost}/model.stp";
            string nav = $"https://{StepViewerHost}/step-viewer.html?model={Uri.EscapeDataString(modelUrl)}&name={Uri.EscapeDataString(Document?.FileName ?? "model.stp")}";
            web.CoreWebView2.Navigate(nav);
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "STEP 3D view error for {File}", FilePath);
            ring.IsActive = false;
            statusText.Text = "STEP 3D view error:\n" + ex.Message;
        }
    }

    /// <summary>Which engine to generate with. Without Inventor it's always the built-in converter,
    /// behind a one-time best-effort notice; with Inventor the saved choice applies, asking (two-card
    /// chooser, persisted) on first use. Null when the user backs out of the chooser.</summary>
    private static async Task<SvfEngine?> ResolveEngineAsync(MainWindow win)
    {
        if (InventorInstalls.Detect().Count == 0)
        {
            if (!ViewerSettings.LocalEngineInfoShown)
            {
                await EngineDialogs.ShowBestEffortInfoAsync(win.Content.XamlRoot);
                ViewerSettings.LocalEngineInfoShown = true;
            }
            return SvfEngine.Local;
        }

        if (ViewerSettings.Engine != SvfEngine.Ask) { return ViewerSettings.Engine; }

        SvfEngine? choice = await EngineDialogs.ShowChooserAsync(win.Content.XamlRoot);
        if (choice != null) { ViewerSettings.Engine = choice.Value; }
        return choice;
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
  /* best-effort warning badge (shown when the model came from the built-in converter) */
  #warn{position:fixed;top:10px;left:10px;z-index:20;display:none;max-width:360px;
    background:rgba(146,64,14,.92);color:#fff;font:12px/1.45 "Segoe UI",system-ui,sans-serif;
    padding:7px 11px;border-radius:6px;box-shadow:0 2px 10px rgba(0,0,0,.35);}
  #warn a{color:#fff;font-weight:600;}
  /* redlining: pencil toolbar glyph, screen-space drawing surface, tool palette, text entry.
     The three elements are re-parented into LMV's own container (a stacking context) at load, so
     the z-indexes here are relative to LMV's UI: the drawing surface sits ABOVE the canvas but
     BELOW the toolbar (50) and panels (20) - the viewer stays operable while marking up. The
     palette (60) floats above everything of LMV's; top:48px keeps it clear of the best-effort
     badge, which lives at body level and always paints above the container. */
  /* \FE0F forces emoji presentation - the bare pencil is a text-style glyph and renders as a
     near-invisible dash in the viewer's font stack */
  #extrabbit-redline-btn .adsk-button-icon:before{content:"\270F\FE0F";font-size:18px;line-height:1;}
  #redline2d{position:absolute;inset:0;z-index:10;pointer-events:none;touch-action:none;}
  #redline2d.drawing{cursor:crosshair;}
  #redlinePanel{position:absolute;top:48px;left:50%;transform:translateX(-50%);z-index:60;display:none;
    gap:5px;align-items:center;background:rgba(28,28,28,.92);padding:6px 10px;border-radius:8px;
    box-shadow:0 2px 10px rgba(0,0,0,.4);font:13px "Segoe UI",system-ui,sans-serif;color:#eee;}
  #redlinePanel .rl-tool{width:30px;height:30px;border:1px solid transparent;border-radius:6px;background:transparent;
    color:#eee;font-size:16px;cursor:pointer;display:flex;align-items:center;justify-content:center;}
  #redlinePanel .rl-tool:hover{background:rgba(255,255,255,.12);}
  #redlinePanel .rl-tool.sel{border-color:#4da3ff;background:rgba(77,163,255,.18);}
  #redlinePanel .rl-swatch{width:18px;height:18px;border-radius:50%;border:2px solid transparent;cursor:pointer;padding:0;}
  #redlinePanel .rl-swatch.sel{border-color:#fff;}
  #redlinePanel .rl-sep{width:1px;height:22px;background:rgba(255,255,255,.25);}
  #redlineText{position:absolute;z-index:61;display:none;background:transparent;border:1px dashed;outline:none;
    font:18px "Segoe UI",system-ui,sans-serif;padding:2px 4px;min-width:80px;}
</style>
</head>
<body>
<div id="viewer"></div>
<div id="warn">&#9888; Best-effort view &mdash; positions or rotations may be off.
  <a href="#" id="warnReport">Report this model</a></div>
<svg id="redline2d" xmlns="http://www.w3.org/2000/svg" width="100%" height="100%"></svg>
<div id="redlinePanel"></div>
<input id="redlineText" type="text" spellcheck="false"/>
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
      // Coloring can run before the model is complete (the toolbar exists while fragments are
      // still streaming in, and the object tree often arrives after the geometry - always for the
      // built-in converter's raw SVF loads). So on BOTH "now it's complete" signals, re-apply if
      // already on (an early pass only caught the bodies loaded so far) or apply the initial mode.
      this._refresh = () => {
        if (this._on){ this._applyColors(); return; }
        this._maybeInitial();                                   // may switch coloring on
        if (!this._on && this.options && this.options.bakedPalette){ this._applyNeutral(); }
      };
      this.viewer.addEventListener(Autodesk.Viewing.GEOMETRY_LOADED_EVENT, this._refresh);
      this.viewer.addEventListener(Autodesk.Viewing.OBJECT_TREE_CREATED_EVENT, this._refresh);
      if (this.viewer.toolbar){ this.onToolbarCreated(this.viewer.toolbar); }
      else { this._onTb = () => this.onToolbarCreated(this.viewer.toolbar); this.viewer.addEventListener(Autodesk.Viewing.TOOLBAR_CREATED_EVENT, this._onTb); }
      return true;
    }
    unload(){
      if (this._refresh){
        this.viewer.removeEventListener(Autodesk.Viewing.GEOMETRY_LOADED_EVENT, this._refresh);
        this.viewer.removeEventListener(Autodesk.Viewing.OBJECT_TREE_CREATED_EVENT, this._refresh);
      }
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
      if (this.options && this.options.initial && !this._on && this.viewer.model){ this.setEnabled(true); }
    }
    setEnabled(on){
      this._on = on;
      // The built-in converter BAKES a palette colour per component into the SVF materials, so just
      // clearing the theming would still show a multicoloured model. For those, "off" means a
      // neutral grey theme over every body - the converter carries no real appearances to reveal.
      if (on){ this._applyColors(); }
      else if (this.options && this.options.bakedPalette){ this._applyNeutral(); }
      else { this.viewer.clearThemingColors(this.viewer.model); }
      this._sync();
      report("coloring " + (on ? "on" : "off"));
    }
    _sync(){
      if (!this._button) { return; }
      const S = Autodesk.Viewing.UI.Button.State;
      this._button.setState(this._on ? S.ACTIVE : S.INACTIVE);
    }
    // Every body's dbId: the UNION of instance-tree leaves and the dbIds the fragments actually
    // reference. Both sources are needed - a raw-SVF load may never grow a tree, and the built-in
    // converter's single-body parts link their fragment to the ROOT node (dbId 1) while the tree's
    // leaf is dbId 2, so theming only the leaves would leave the mesh untouched. The fragment
    // lookup tries the API first, then the raw fragId2dbId array leaner model representations use.
    _collectIds(model){
      const seen = new Set();
      const tree = model.getInstanceTree && model.getInstanceTree();
      if (tree){
        tree.enumNodeChildren(tree.getRootId(), (dbId) => {
          if (tree.getChildCount(dbId) === 0){ seen.add(dbId); }   // a leaf node is one body
        }, true);
      }
      try {
        const fl = model.getFragmentList();
        const map = fl && fl.fragments && fl.fragments.fragId2dbId;
        const n = (fl && fl.getCount) ? fl.getCount() : (map ? map.length : 0);
        for (let f = 0; f < n; f++) {
          const d = (fl && fl.getDbIds) ? fl.getDbIds(f) : (map ? map[f] : 0);
          (Array.isArray(d) ? d : [d]).forEach(x => { if (x > 0) seen.add(x); });
        }
      } catch (e) { report("fragment dbId scan: " + e); }
      const ids = [...seen].sort((a, b) => a - b);
      ids._fromTree = !!tree;
      return ids;
    }
    _applyColors(){
      const model = this.viewer.model;
      if (!model){ return; }
      const ids = this._collectIds(model);
      try { this.viewer.clearThemingColors(model); } catch (e) { /* fresh model */ }
      ids.forEach((dbId, i) => this.viewer.setThemingColor(dbId, bodyColor(i), model, true));
      report("colored " + ids.length + " bodies" + (ids._fromTree ? "" : " (from fragments)"));
    }
    _applyNeutral(){
      const model = this.viewer.model;
      if (!model){ return; }
      const ids = this._collectIds(model);
      const grey = new THREE.Vector4(0.72, 0.72, 0.72, 1.0);
      try { this.viewer.clearThemingColors(model); } catch (e) { /* fresh model */ }
      ids.forEach((dbId) => this.viewer.setThemingColor(dbId, grey, model, true));
      report("neutralized " + ids.length + " bodies");
    }
  }
  Autodesk.Viewing.theExtensionManager.registerExtension("Extrabbit.Coloring", ColoringExtension);

  // --- Redlining: draw markup over the model. 2D tools (freehand, rectangle, circle, arrow, text)
  // draw in screen space on an SVG overlay; the experimental 3D pen raycasts each sample onto the
  // model surface and draws a world-space line in an LMV overlay scene, so it sticks to the model
  // and rotates with it. Default colour red; markup is per-session (not persisted).
  const RL_NS = "http://www.w3.org/2000/svg";
  const RL_COLORS = ["#e53935", "#fb8c00", "#fdd835", "#43a047", "#1e88e5", "#ffffff", "#000000"];
  const RL_TOOLS = [
    ["free",    "✎",  "Freehand"],
    ["rect",    "▭",  "Rectangle"],
    ["circle",  "◯",  "Circle"],
    ["arrow",   "↗",  "Arrow"],
    ["text",    "T",       "Text"],
    ["paint3d", "\u{1F58C}", "Paint on the model (experimental) - strokes stick to the surface"],
  ];

  class RedlineExtension extends Autodesk.Viewing.Extension {
    load(){
      this._active = false;
      this._tool = "free";
      this._color = RL_COLORS[0];
      this._undo = [];                                   // drawn items, newest last
      this._scene = "extrabbit-redline3d";
      this._ac = new AbortController();                  // one handle tears down every DOM listener
      this._svg = document.getElementById("redline2d");
      this._panel = document.getElementById("redlinePanel");
      this._input = document.getElementById("redlineText");
      // Re-parent into LMV's container (a stacking context capped at z-index 1): as body-level
      // siblings the overlay would paint above the WHOLE viewer UI and block the toolbar/viewcube
      // while drawing. Inside the context the z-indexes above layer it under LMV's controls.
      const host = (this.viewer.canvas && this.viewer.canvas.closest(".adsk-viewing-viewer")) || document.body;
      host.appendChild(this._svg); host.appendChild(this._panel); host.appendChild(this._input);
      this._buildPanel();
      this._bindDraw();
      this._bindText();
      window.addEventListener("keydown", (e) => {
        if (!this._active){ return; }
        if (e.key === "Escape"){ this.setActive(false); }
        else if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === "z"){ e.preventDefault(); this.undo(); }
      }, { signal: this._ac.signal });
      if (this.viewer.toolbar){ this.onToolbarCreated(this.viewer.toolbar); }
      else { this._onTb = () => this.onToolbarCreated(this.viewer.toolbar); this.viewer.addEventListener(Autodesk.Viewing.TOOLBAR_CREATED_EVENT, this._onTb); }
      return true;
    }
    unload(){
      this.setActive(false);                             // release the overlay + hide the palette
      this._ac.abort();                                  // svg / input / window listeners
      if (this._onTb){ this.viewer.removeEventListener(Autodesk.Viewing.TOOLBAR_CREATED_EVENT, this._onTb); }
      if (this._group && this.viewer.toolbar){ this.viewer.toolbar.removeControl(this._group); }
      this._group = this._button = null;
      this.clear();
      try { if (this.viewer.impl.removeOverlayScene){ this.viewer.impl.removeOverlayScene(this._scene); } } catch (e) { /* never created */ }
      this._panel.replaceChildren();                     // a later load() rebuilds it fresh
      return true;
    }
    onToolbarCreated(toolbar){
      if (this._button){ return; }
      this._button = new Autodesk.Viewing.UI.Button("extrabbit-redline-btn");
      this._button.setToolTip("Redline — draw on the model");
      this._button.onClick = () => this.setActive(!this._active);
      this._group = new Autodesk.Viewing.UI.ControlGroup("extrabbit-redline-group");
      this._group.addControl(this._button);
      toolbar.addControl(this._group);
    }
    setActive(on){
      this._active = on;
      this._svg.style.pointerEvents = on ? "auto" : "none";
      this._svg.classList.toggle("drawing", on);
      this._panel.style.display = on ? "flex" : "none";
      if (!on){ this._finishStroke(); this._commitText(); }
      if (this._button){
        const S = Autodesk.Viewing.UI.Button.State;
        this._button.setState(on ? S.ACTIVE : S.INACTIVE);
      }
      report("redline " + (on ? "on" : "off"));
    }
    // ---------- palette ----------
    _buildPanel(){
      this._panel.replaceChildren();                     // idempotent across load cycles
      const btn = (title, label, cls, onclick) => {
        const b = document.createElement("button");
        b.className = cls; b.title = title; b.textContent = label; b.addEventListener("click", onclick);
        this._panel.appendChild(b);
        return b;
      };
      this._toolBtns = {};
      for (const [id, glyph, title] of RL_TOOLS){
        this._toolBtns[id] = btn(title, glyph, "rl-tool", () => this._selectTool(id));
      }
      this._panel.appendChild(Object.assign(document.createElement("div"), { className: "rl-sep" }));
      this._swatches = RL_COLORS.map(c => {
        const s = btn("Colour " + c, "", "rl-swatch", () => this._selectColor(c));
        s.style.background = c;
        return s;
      });
      this._panel.appendChild(Object.assign(document.createElement("div"), { className: "rl-sep" }));
      btn("Undo (Ctrl+Z)", "↩", "rl-tool", () => this.undo());
      btn("Clear all markup", "\u{1F5D1}", "rl-tool", () => this.clear());
      btn("Close (Esc)", "✕", "rl-tool", () => this.setActive(false));
      this._selectTool("free");
      this._selectColor(RL_COLORS[0]);
    }
    _selectTool(id){
      this._tool = id;
      for (const k in this._toolBtns){ this._toolBtns[k].classList.toggle("sel", k === id); }
    }
    _selectColor(c){
      this._color = c;
      this._swatches.forEach(s => s.classList.toggle("sel", s.style.background === c || rlHex(s.style.background) === c));
    }
    // ---------- drawing ----------
    // One stroke at a time: tool and pointerId are LATCHED at pointerdown (this._strokeTool /
    // this._pointerId), so a mid-stroke tool switch or a second finger can't corrupt the stroke
    // state (orphaned SVG elements, TypeErrors in the 3D branch). Non-left buttons and the wheel
    // are forwarded to the LMV canvas so the camera stays navigable while marking up.
    _bindDraw(){
      const svg = this._svg;
      const sig = { signal: this._ac.signal };
      const pos = (e) => { const r = svg.getBoundingClientRect(); return { x: e.clientX - r.left, y: e.clientY - r.top }; };
      svg.addEventListener("pointerdown", (e) => {
        if (!this._active){ return; }
        if (e.button !== 0){ this._forwardNav(e); return; }
        if (this._start){ return; }                      // a second pointer doesn't start a stroke
        this._commitText();
        const p = pos(e);
        if (this._tool === "text"){ this._beginText(p); return; }
        try { svg.setPointerCapture(e.pointerId); } catch (err) { /* capture is best-effort */ }
        this._start = p;
        this._strokeTool = this._tool;
        this._pointerId = e.pointerId;
        this._moved = false;
        if (this._strokeTool === "paint3d"){ this._pts3d = []; this._line3d = null; this._add3D(p); }
        else { this._el = this._begin2D(p); svg.appendChild(this._el); }
        e.preventDefault();
      }, sig);
      svg.addEventListener("pointermove", (e) => {
        if (!this._active || !this._start || e.pointerId !== this._pointerId){ return; }
        if ((e.buttons & 1) === 0){ this._finishStroke(); return; }   // lost pointerup (focus steal etc.)
        const p = pos(e);
        if (Math.abs(p.x - this._start.x) > 2 || Math.abs(p.y - this._start.y) > 2){ this._moved = true; }
        if (this._strokeTool === "paint3d"){ this._add3D(p); }
        else if (this._el){ this._update2D(p); }
      }, sig);
      const finish = (e) => {
        if (!this._start || (e && e.pointerId !== this._pointerId)){ return; }
        this._finishStroke();
      };
      svg.addEventListener("pointerup", finish, sig);
      svg.addEventListener("pointercancel", finish, sig);
      // wheel-zoom keeps working over the drawing surface (LMV binds the legacy "mousewheel"
      // event on its canvas, not the standard "wheel")
      svg.addEventListener("wheel", (e) => {
        if (!this._active){ return; }
        e.preventDefault();
        try { this.viewer.canvas.dispatchEvent(new WheelEvent("mousewheel", e)); } catch (err) { /* no canvas */ }
      }, { passive: false, signal: this._ac.signal });
    }
    _finishStroke(){
      if (!this._start){ return; }
      if (this._strokeTool === "paint3d"){ this._commit3D(); }
      else if (this._el){
        if (this._moved){ this._undo.push({ kind: "2d", el: this._el }); }
        else { this._el.remove(); }                      // a bare click draws nothing visible
        this._el = null;
      }
      this._start = null;
      this._pointerId = undefined;
    }
    // Middle/right drags are navigation: replay the down on the LMV canvas and drop the overlay's
    // pointer-events until the drag ends, so the rest of the gesture reaches the viewer natively.
    _forwardNav(e){
      const svg = this._svg, canvas = this.viewer.canvas;
      if (!canvas){ return; }
      svg.style.pointerEvents = "none";
      try {
        canvas.dispatchEvent(new PointerEvent("pointerdown", e));
        canvas.dispatchEvent(new MouseEvent("mousedown", e));
      } catch (err) { /* forwarding is best-effort */ }
      const restore = () => {
        window.removeEventListener("pointerup", restore, true);
        window.removeEventListener("pointercancel", restore, true);
        if (this._active){ svg.style.pointerEvents = "auto"; }
      };
      window.addEventListener("pointerup", restore, { capture: true, signal: this._ac.signal });
      window.addEventListener("pointercancel", restore, { capture: true, signal: this._ac.signal });
    }
    _shape(tag){
      const el = document.createElementNS(RL_NS, tag);
      el.setAttribute("stroke", this._color);
      el.setAttribute("stroke-width", "3");
      el.setAttribute("fill", "none");
      el.setAttribute("stroke-linecap", "round");
      el.setAttribute("stroke-linejoin", "round");
      return el;
    }
    _begin2D(p){
      const t = this._tool;
      if (t === "rect"){ const el = this._shape("rect"); el.setAttribute("x", p.x); el.setAttribute("y", p.y); return el; }
      if (t === "circle"){ const el = this._shape("ellipse"); el.setAttribute("cx", p.x); el.setAttribute("cy", p.y); return el; }
      const el = this._shape("path");                    // freehand + arrow are paths
      el.setAttribute("d", `M ${p.x} ${p.y}`);
      return el;
    }
    _update2D(p){
      const el = this._el, s = this._start, t = this._tool;
      if (t === "free"){ el.setAttribute("d", el.getAttribute("d") + ` L ${p.x} ${p.y}`); }
      else if (t === "rect"){
        el.setAttribute("x", Math.min(s.x, p.x)); el.setAttribute("y", Math.min(s.y, p.y));
        el.setAttribute("width", Math.abs(p.x - s.x)); el.setAttribute("height", Math.abs(p.y - s.y));
      }
      else if (t === "circle"){
        el.setAttribute("cx", (s.x + p.x) / 2); el.setAttribute("cy", (s.y + p.y) / 2);
        el.setAttribute("rx", Math.abs(p.x - s.x) / 2); el.setAttribute("ry", Math.abs(p.y - s.y) / 2);
      }
      else if (t === "arrow"){
        const a = Math.atan2(p.y - s.y, p.x - s.x), h = 14;
        const h1x = p.x - h * Math.cos(a - 0.45), h1y = p.y - h * Math.sin(a - 0.45);
        const h2x = p.x - h * Math.cos(a + 0.45), h2y = p.y - h * Math.sin(a + 0.45);
        el.setAttribute("d", `M ${s.x} ${s.y} L ${p.x} ${p.y} M ${h1x} ${h1y} L ${p.x} ${p.y} L ${h2x} ${h2y}`);
      }
    }
    // ---------- text ----------
    _bindText(){
      const inp = this._input, sig = { signal: this._ac.signal };
      inp.addEventListener("keydown", (e) => {
        if (e.key === "Enter" && !e.isComposing){ this._commitText(); }   // IME Enter confirms the composition, not the annotation
        else if (e.key === "Escape"){ this._cancelText(); }
        e.stopPropagation();                             // typing must not trigger the mode's shortcuts
      }, sig);
      inp.addEventListener("blur", () => this._commitText(), sig);
    }
    _beginText(p){
      const inp = this._input;
      inp.style.display = "block";
      inp.style.left = p.x + "px"; inp.style.top = (p.y - 14) + "px";
      inp.style.color = this._color; inp.style.borderColor = this._color;
      inp.value = "";
      this._textPos = p;
      setTimeout(() => inp.focus(), 0);
    }
    _cancelText(){
      this._textPos = null;                              // discard: hide without committing
      this._input.style.display = "none";
      this._input.value = "";
    }
    _commitText(){
      const inp = this._input;
      if (inp.style.display === "none" || !this._textPos){ return; }
      const value = inp.value.trim();
      inp.style.display = "none";
      if (value){
        const el = document.createElementNS(RL_NS, "text");
        el.setAttribute("x", this._textPos.x); el.setAttribute("y", this._textPos.y + 4);
        el.setAttribute("fill", this._color);
        el.setAttribute("style", 'font:18px "Segoe UI",system-ui,sans-serif;');
        el.textContent = value;
        this._svg.appendChild(el);
        this._undo.push({ kind: "2d", el });
      }
      this._textPos = null;
    }
    // ---------- 3D pen (experimental) ----------
    _add3D(p){
      const hit = this.viewer.impl.hitTest(p.x, p.y, false);
      if (!hit || !hit.intersectPoint){ return; }
      // lift the point slightly toward the camera so the stroke doesn't z-fight the surface
      const q = hit.intersectPoint.clone();
      const toCam = this.viewer.impl.camera.position.clone().sub(q).normalize();
      const size = this.viewer.model ? this.viewer.model.getBoundingBox().getSize(new THREE.Vector3()).length() : 100;
      q.add(toCam.multiplyScalar(size * 0.004));
      this._pts3d.push(q);
      if (this._pts3d.length >= 2){ this._refresh3D(); }
    }
    _makeLine(points){
      const arr = new Float32Array(points.length * 3);
      points.forEach((q, i) => { arr[i * 3] = q.x; arr[i * 3 + 1] = q.y; arr[i * 3 + 2] = q.z; });
      const geom = new THREE.BufferGeometry();
      const attr = new THREE.BufferAttribute(arr, 3);
      if (geom.setAttribute){ geom.setAttribute("position", attr); } else { geom.addAttribute("position", attr); }
      const mat = new THREE.LineBasicMaterial({ color: new THREE.Color(this._color), depthTest: true, depthWrite: false });
      return new THREE.Line(geom, mat);
    }
    _ensureScene(){
      const impl = this.viewer.impl;
      if (!impl.overlayScenes || !impl.overlayScenes[this._scene]){ impl.createOverlayScene(this._scene); }
    }
    _refresh3D(){
      const impl = this.viewer.impl;
      this._ensureScene();
      // the true flag disposes the replaced geometry - LMV's GL buffers are pool-allocated and only
      // freed via dispose(), so rebuilding per sample would otherwise leak GPU memory per move
      if (this._line3d){ impl.removeOverlay(this._scene, this._line3d, true); this._line3d.material.dispose(); }
      this._line3d = this._makeLine(this._pts3d);
      impl.addOverlay(this._scene, this._line3d);
      impl.invalidate(false, false, true);
    }
    _commit3D(){
      if (this._line3d){ this._undo.push({ kind: "3d", obj: this._line3d }); }
      else { report("3d stroke discarded (no surface under the cursor)"); }
      this._pts3d = null; this._line3d = null;
    }
    // ---------- undo / clear ----------
    undo(){
      const item = this._undo.pop();
      if (!item){ return; }
      if (item.kind === "2d"){ item.el.remove(); }
      else {
        this.viewer.impl.removeOverlay(this._scene, item.obj, true);   // true = dispose geometry
        item.obj.material.dispose();
        this.viewer.impl.invalidate(false, false, true);
      }
    }
    clear(){ while (this._undo.length){ this.undo(); } }
  }
  function rlHex(rgb){                                   // "rgb(r, g, b)" -> "#rrggbb" for swatch compare
    const m = /rgb\((\d+),\s*(\d+),\s*(\d+)\)/.exec(rgb);
    return m ? "#" + [m[1], m[2], m[3]].map(n => (+n).toString(16).padStart(2, "0")).join("") : rgb;
  }
  Autodesk.Viewing.theExtensionManager.registerExtension("Extrabbit.Redline", RedlineExtension);

  const params = new URLSearchParams(location.search);
  const wantMulticolor = params.get("coloring") === "multicolor";
  const srcSvf = params.get("src") === "svf";           // raw SVF from the built-in converter
  const bestEffort = params.get("engine") === "local";  // show the warning badge
  if (bestEffort) {
    document.getElementById("warn").style.display = "block";
    document.getElementById("warnReport").addEventListener("click", (e) => {
      e.preventDefault();
      try { window.chrome.webview.postMessage("report-issue"); } catch (err) { /* no host */ }
    });
  }

  const viewer = new Autodesk.Viewing.GuiViewer3D(document.getElementById("viewer"));
  Autodesk.Viewing.Initializer({ env: "Local", useADP: false }, () => {
    report("initialized");
    viewer.start();
    // No fullscreen button: the viewer already fills a window overlay, and browser fullscreen inside
    // the WebView fights with the overlay's Esc-to-close. The button is core GuiViewer3D UI (not the
    // FullScreen extension's), so remove the control - deferred with setTimeout, because touching the
    // toolbar DURING the TOOLBAR_CREATED dispatch breaks the viewer's own follow-up setup (NavTools,
    // measure/section/explode) and strips the whole toolbar.
    viewer.addEventListener(Autodesk.Viewing.TOOLBAR_CREATED_EVENT, () => setTimeout(() => {
      try {
        const st = viewer.getToolbar() && viewer.getToolbar().getControl("settingsTools");
        if (st && st.removeControl("toolbar-fullscreenTool")) { report("fullscreen button removed"); }
      } catch (e) { report("fullscreen removal: " + e); }
    }, 0));
    viewer.loadExtension("Extrabbit.Coloring", { initial: wantMulticolor, bakedPalette: srcSvf }).then(
      () => report("coloring extension loaded"),
      (err) => report("coloring extension failed: " + err));
    viewer.loadExtension("Extrabbit.Redline").then(
      () => report("redline extension loaded"),
      (err) => report("redline extension failed: " + err));
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
      // Edges on by default, HERE and not in the load callbacks: the viewer applies its persisted
      // preference profile after the model root loads, so an earlier setDisplayEdges(true) gets
      // overridden by a stored "off" - by GEOMETRY_LOADED the profile has been applied and this
      // call has the last word. The user can still toggle edges off for the session.
      try { viewer.setDisplayEdges(true); report("edges on"); }
      catch (e) { report("setDisplayEdges: " + e); }
      // Direct-SVF loads never get GuiViewer3D's default tools: its internal model-added UI
      // bootstrap throws on manifest-less models (killing the loadModel success chain too), so
      // NavTools/measure/explode/... stay unloaded. Bring the standard set up ourselves - HERE,
      // because GEOMETRY_LOADED demonstrably still fires; loadExtension is idempotent.
      if (srcSvf) {
        ["Autodesk.DefaultTools.NavTools", "Autodesk.ViewCubeUi", "Autodesk.ModelStructure",
         "Autodesk.PropertiesManager", "Autodesk.Measure", "Autodesk.Section", "Autodesk.Explode"]
          .forEach(id => viewer.loadExtension(id, viewer.config)
            .then(() => report(id + " loaded"), (e) => report(id + " failed: " + e)));
      }
    });
    if (srcSvf) {
      // A built-in-converter entry: no bubble manifest, load the raw SVF package directly.
      viewer.loadModel("./output/0.svf", { createWireframe: true },
        () => report("model loaded (raw svf)"),
        (err) => report("loadModel failed: " + JSON.stringify(err)));
      return;
    }
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
          () => report("model loaded"),
          (err) => report("loadDocumentNode failed: " + JSON.stringify(err)));
      },
      (code, msg) => report("Document.load failed: code=" + code + " msg=" + msg));
  });
</script>
</body>
</html>
""";
}
