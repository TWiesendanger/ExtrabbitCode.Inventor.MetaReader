using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
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
    private const string ViewerAssetsHost = "inventormeta.viewerassets";
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
            // The page's scripts/styles are served straight from the install's Assets\viewer folder
            // over a second host, so they are always current; only the small entry page itself is
            // written into the cache entry (the page origin must match the model data for LMV's
            // same-origin fetches of bubble.json / SVF resources).
            string assetsDir = Path.Combine(AppContext.BaseDirectory, "Assets", "viewer");
            web.CoreWebView2.SetVirtualHostNameToFolderMapping(ViewerAssetsHost, assetsDir, CoreWebView2HostResourceAccessKind.Allow);
            try
            {
                string page = File.ReadAllText(Path.Combine(assetsDir, "viewer.html"))
                    .Replace("{LMV_VERSION}", LmvViewerVersion)
                    .Replace("{ASSETS}", $"https://{ViewerAssetsHost}");
                File.WriteAllText(Path.Combine(entryDir, "viewer.html"), page);
            }
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
            web.CoreWebView2.WebMessageReceived += (sender, a) =>
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
                    if (msg.StartsWith("redline-save:", StringComparison.Ordinal))
                    {
                        // layer history lives next to the viewable in the cache entry
                        try { File.WriteAllText(Path.Combine(entryDir, "redline-layers.json"), msg["redline-save:".Length..]); }
                        catch (Exception ex) { Serilog.Log.Warning("Couldn't save redline layers in {Dir}: {Error}", entryDir, ex.Message); }
                        return;
                    }
                    if (msg.StartsWith("redline-shot:", StringComparison.Ordinal))
                    {
                        _ = SaveRedlineShotAsync(win, msg["redline-shot:".Length..]);
                        return;
                    }
                    ViewerLog.Write("js: " + msg);
                }
                catch { /* ignore */ }
            };
            web.CoreWebView2.NavigationCompleted += (_, _) => statusHost.Visibility = Visibility.Collapsed;
            string query = $"?coloring={coloring}&file={Uri.EscapeDataString(Document?.FileName ?? "")}"
                + (bestEffort ? "&src=svf&engine=local" : "");
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

    /// <summary>Handles a redline-layer screenshot posted by the viewer page: payload is JSON
    /// {"name": layer name, "mode": "save"|"copy", "data": PNG data URL}. Save shows a file dialog
    /// owned by the viewer's window (cancelling is fine); copy puts the PNG on the clipboard.</summary>
    private async Task SaveRedlineShotAsync(MainWindow win, string payload)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(payload);
            string name = doc.RootElement.GetProperty("name").GetString() ?? "Layer";
            string mode = doc.RootElement.TryGetProperty("mode", out var m) ? m.GetString() ?? "save" : "save";
            string dataUrl = doc.RootElement.GetProperty("data").GetString() ?? "";
            const string prefix = "data:image/png;base64,";
            if (!dataUrl.StartsWith(prefix, StringComparison.Ordinal)) { return; }
            byte[] png = Convert.FromBase64String(dataUrl[prefix.Length..]);

            if (mode == "copy")
            {
                var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
                await stream.WriteAsync(png.AsBuffer());
                stream.Seek(0);
                var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
                package.SetBitmap(Windows.Storage.Streams.RandomAccessStreamReference.CreateFromStream(stream));
                Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
                win.ShowToast($"Screenshot of “{name}” copied to the clipboard");
                Serilog.Log.Information("Redline screenshot copied to clipboard ({Layer})", name);
                return;
            }

            var picker = new Windows.Storage.Pickers.FileSavePicker
            {
                SuggestedFileName = $"{Path.GetFileNameWithoutExtension(Document?.FileName ?? "model")} - {name}",
            };
            picker.FileTypeChoices.Add("PNG image", [".png"]);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(win));
            Windows.Storage.StorageFile? file = await picker.PickSaveFileAsync();
            if (file == null) { return; }
            await Windows.Storage.FileIO.WriteBytesAsync(file, png);
            win.ShowToast($"Screenshot saved: {file.Name}");
            Serilog.Log.Information("Redline screenshot saved to {File}", file.Path);
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning("Redline screenshot export failed: {Error}", ex.Message);
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

}
