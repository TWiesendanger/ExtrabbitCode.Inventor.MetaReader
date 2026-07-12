using System;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace ExtrabbitCode.Inventor.MetaReader.App;

/// <summary>
/// Headless documentation snapshotter. Run with
/// <c>InventorMeta.App.exe --shoot-docs &lt;outDir&gt; --samples &lt;sampleFilesDir&gt; [--model &lt;assembly.iam&gt;]</c>:
/// <c>--model</c> picks the showcase assembly - overview, sidebar, file structure, the recent list
/// and the reference-graph shots (defaults to the bundled SampleBg).
/// it opens a curated set of sample files, walks the tabs, and writes one PNG per view in
/// light and dark themes (<c>&lt;slug&gt;-light.png</c> / <c>&lt;slug&gt;-dark.png</c>) so each
/// screenshot can sit next to its section in the docs. Generated locally; the PNGs are committed.
/// </summary>
internal static class DocShooter
{
    public static async Task RunAsync(string outDir, string? samplesDir, string? modelPath = null)
    {
        try
        {
            Directory.CreateDirectory(outDir);

            // The File Structure tab is hidden by default; show it so it can be captured (and so
            // all four tabs appear in the strip). Ephemeral settings keep this in memory only.
            HideStore.Set(HideStore.TabKey("File Structure"), hidden: false);

            string? part = Sample(samplesDir, "SampleBg", "SamplePart.ipt"); // thumbnail + 3 model states
            string? tnp = Sample(samplesDir, "SampleBg", "TubeAndPipe.ipt"); // Content Center pipe -> "Piping" badge
            string? sampleAsm = Sample(samplesDir, "SampleBg", "SampleBg.iam"); // for the recent list

            // The reference graph uses the --model assembly when given (and present), else SampleBg.
            string? asm = modelPath != null && File.Exists(modelPath) ? modelPath : sampleAsm;

            // An assembly that uses iParts (factory + members), for the iPart-marking shot. Lives in
            // the (git-ignored) jet-engine sample; the shot is skipped if it isn't extracted locally.
            string? ipartAsm = samplesDir is null ? null : Sample(
                Path.Combine(samplesDir, "Jet_Engine_Model", "Jet Engine Model"), "Workspace", "Mid Compression Assembly.iam");

            // One persistent window for the whole run: a WinUI app exits when its last window
            // closes, so we never close it mid-run - we just reset its tabs between shots.
            MainWindow w = new();
            w.Activate();
            w.ShootResize(1840, 1180);
            await Task.Delay(600);

            foreach (ElementTheme theme in new[] { ElementTheme.Light, ElementTheme.Dark })
            {
                ThemeManager.Apply(w, theme);
                await Task.Delay(300);

                // 1. The Home screen: welcome card + a seeded recent list, so it shows the same in
                // both themes (otherwise it's empty in the first theme and populated in the second).
                RecentFiles.Clear();
                foreach (string? recent in new[] { tnp, asm, part })
                {
                    if (recent != null) { RecentFiles.Add(recent); }
                }
                w.ShootCloseAllTabs();
                w.SetStatus("Ready. Open or drop an Inventor file to begin.");
                await Task.Delay(400);
                await Capture(w, "app__welcome", theme, outDir);

                // 2. The reference model (the bundled assembly, or whatever --model gives): the overview
                // and a close-up of just the sidebar card.
                if (asm != null)
                {
                    w.ShootCloseAllTabs();
                    w.ShootOpen(asm);
                    await Task.Delay(1000);
                    DocumentView? adv = w.CurrentView;
                    await Capture(w, "app__overview", theme, outDir);
                    if (adv?.ShootElement("SidebarCard") is { } sidebar)
                    {
                        await CaptureRegion(w, sidebar, "app__sidebar", theme, outDir);
                    }

                    // the raw file structure, shot on the same assembly (any document has one)
                    adv?.ShootSelectTab("File Structure");
                    await Task.Delay(600);
                    if (adv?.ShootElement("DetailTabs") is { } structurePanel)
                    {
                        await CaptureRegion(w, structurePanel, "app__file-structure-panel", theme, outDir);
                    }
                }

                // 3. A part with model states: the states diff as just the detail-tabs panel.
                if (part != null)
                {
                    w.ShootCloseAllTabs();
                    w.ShootOpen(part);
                    await Task.Delay(800);
                    DocumentView? dv = w.CurrentView;

                    dv?.ShootSelectTab("Model States");
                    await Task.Delay(600);
                    if (dv?.ShootElement("DetailTabs") is { } statesPanel)
                    {
                        await CaptureRegion(w, statesPanel, "app__model-states-panel", theme, outDir);
                    }
                }

                // 4b. The colour-coded document-category badge (a Content Center pipe -> "Piping"),
                // shown as a close-up of just the sidebar card where the badge sits.
                if (tnp != null)
                {
                    w.ShootCloseAllTabs();
                    w.ShootOpen(tnp);
                    await Task.Delay(800);
                    if (w.CurrentView?.ShootElement("SidebarCard") is { } catSidebar)
                    {
                        await CaptureRegion(w, catSidebar, "app__category-sidebar", theme, outDir);
                    }
                }

                // 5. The reference graph, expanded and fitted, in each of the three layouts.
                if (asm != null)
                {
                    w.ShootCloseAllTabs();
                    w.ShootOpen(asm);
                    await Task.Delay(1200);
                    DocumentView? rv = w.CurrentView;
                    rv?.ShootSelectTab("References");
                    await Task.Delay(3000);            // large assembly: let the graph build
                    if (rv != null)
                    {
                        rv.ShootExpandGraph();
                        await Task.Delay(2500);
                        foreach ((GraphLayout layout, string slug) in new[]
                        {
                            (GraphLayout.LeftRight, "app__references-leftright"),
                            (GraphLayout.TopDown, "app__references-topdown"),
                            (GraphLayout.Network, "app__references-network"),
                        })
                        {
                            rv.ShootSetGraphLayout(layout);
                            await Task.Delay(layout == GraphLayout.Network ? 4500 : 2500); // settle / physics
                            rv.ShootFitGraph();
                            await Task.Delay(1400);     // fit animation
                            await CaptureWithGraph(w, rv, slug, theme, outDir);
                        }
                    }
                }

                // 6. iParts: factory + members highlighted in the reference graph. Shot in a
                // taller window so the graph fits at a readable size, then restored.
                if (ipartAsm != null)
                {
                    w.ShootCloseAllTabs();
                    w.ShootResize(1900, 1240);
                    w.ShootOpen(ipartAsm);
                    await Task.Delay(800);
                    w.CurrentView?.ShootSelectTab("References");
                    await Task.Delay(2600);
                    if (w.CurrentView is { } ipartsView)
                    {
                        await CaptureWithGraph(w, ipartsView, "app__iparts", theme, outDir);
                    }
                    w.ShootResize(1840, 1180);
                    await Task.Delay(300);
                }
            }
        }
        catch (Exception ex)
        {
            try { File.AppendAllText(Path.Combine(outDir, "shoot-docs.log"), ex + "\n"); }
            catch { /* best-effort */ }
        }
        finally
        {
            Application.Current.Exit();
        }
    }

    /// <summary>Captures the whole window.</summary>
    private static Task Capture(Window w, string slug, ElementTheme theme, string outDir) =>
        CaptureElement(w.Content, slug, theme, outDir);

    /// <summary>Captures a single element (a region of the window) rather than the whole window -
    /// RenderTargetBitmap can render any element in the visual tree at its own size.</summary>
    private static async Task CaptureElement(UIElement element, string slug, ElementTheme theme, string outDir)
    {
        if (element == null) { return; }
        RenderTargetBitmap rtb = new();
        await rtb.RenderAsync(element);
        byte[] pixels = (await rtb.GetPixelsAsync()).ToArray();
        await WritePng(pixels, rtb.PixelWidth, rtb.PixelHeight, slug, theme, outDir);
    }

    /// <summary>Captures the window and paints the reference-graph WebView2 into it: RenderTargetBitmap
    /// can't see WebView2 content, so we grab it via CapturePreviewAsync and composite it at its bounds.</summary>
    private static async Task CaptureWithGraph(MainWindow w, DocumentView dv, string slug, ElementTheme theme, string outDir)
    {
        FrameworkElement content = (FrameworkElement)w.Content;
        RenderTargetBitmap rtb = new();
        await rtb.RenderAsync(content);
        int W = rtb.PixelWidth, H = rtb.PixelHeight;
        byte[] pixels = (await rtb.GetPixelsAsync()).ToArray();

        if (content.ActualWidth > 0 && await dv.ShootGraphImageAsync(content) is { } graph)
        {
            double sx = W / content.ActualWidth, sy = H / content.ActualHeight;
            int dx = (int)Math.Round(graph.x * sx), dy = (int)Math.Round(graph.y * sy);
            int dw = (int)Math.Round(graph.width * sx), dh = (int)Math.Round(graph.height * sy);
            if (dw > 0 && dh > 0)
            {
                using InMemoryRandomAccessStream gs = new();
                await gs.WriteAsync(graph.png.AsBuffer());
                gs.Seek(0);
                BitmapDecoder dec = await BitmapDecoder.CreateAsync(gs);
                BitmapTransform fit = new() { ScaledWidth = (uint)dw, ScaledHeight = (uint)dh, InterpolationMode = BitmapInterpolationMode.Fant };
                byte[] gpix = (await dec.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, fit,
                    ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage)).DetachPixelData();

                for (int y = 0; y < dh; y++)
                {
                    int ty = dy + y;
                    if (ty < 0 || ty >= H) { continue; }
                    for (int x = 0; x < dw; x++)
                    {
                        int tx = dx + x;
                        if (tx < 0 || tx >= W) { continue; }
                        int di = (ty * W + tx) * 4, si = (y * dw + x) * 4;
                        pixels[di] = gpix[si]; pixels[di + 1] = gpix[si + 1];
                        pixels[di + 2] = gpix[si + 2]; pixels[di + 3] = gpix[si + 3];
                    }
                }
            }
        }

        await WritePng(pixels, W, H, slug, theme, outDir);
    }

    /// <summary>Captures a region of the window by cropping a full-window render. RenderTargetBitmap on
    /// an isolated sub-element mis-resolves theme brushes (card backgrounds come out light in dark mode);
    /// the whole window themes correctly, so we render it and crop to the element's bounds.</summary>
    private static async Task CaptureRegion(MainWindow w, FrameworkElement element, string slug, ElementTheme theme, string outDir)
    {
        FrameworkElement content = (FrameworkElement)w.Content;
        RenderTargetBitmap rtb = new();
        await rtb.RenderAsync(content);
        int W = rtb.PixelWidth, H = rtb.PixelHeight;
        byte[] pixels = (await rtb.GetPixelsAsync()).ToArray();
        if (content.ActualWidth <= 0 || element.ActualWidth <= 0) { return; }

        double sx = W / content.ActualWidth, sy = H / content.ActualHeight;
        Rect b = element.TransformToVisual(content).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
        int rx = Math.Clamp((int)Math.Round(b.X * sx), 0, W);
        int ry = Math.Clamp((int)Math.Round(b.Y * sy), 0, H);
        int rw = Math.Clamp((int)Math.Round(b.Width * sx), 0, W - rx);
        int rh = Math.Clamp((int)Math.Round(b.Height * sy), 0, H - ry);
        if (rw <= 0 || rh <= 0) { return; }

        byte[] crop = new byte[rw * rh * 4];
        for (int y = 0; y < rh; y++)
        {
            Array.Copy(pixels, ((ry + y) * W + rx) * 4, crop, y * rw * 4, rw * 4);
        }
        await WritePng(crop, rw, rh, slug, theme, outDir);
    }

    private static async Task WritePng(byte[] bgra, int width, int height, string slug, ElementTheme theme, string outDir)
    {
        string suffix = theme == ElementTheme.Light ? "light" : "dark";
        string path = Path.Combine(outDir, $"{slug}-{suffix}.png");

        InMemoryRandomAccessStream ras = new();
        BitmapEncoder encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, ras);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
            (uint)width, (uint)height, 96, 96, bgra);
        await encoder.FlushAsync();

        ras.Seek(0);
        using FileStream fs = new(path, FileMode.Create, FileAccess.Write);
        await ras.AsStreamForRead().CopyToAsync(fs);
    }

    private static string? Sample(string? dir, string sub, string name)
    {
        if (dir == null)
        {
            return null;
        }

        string path = Path.Combine(dir, sub, name);
        return File.Exists(path) ? path : null;
    }
}
