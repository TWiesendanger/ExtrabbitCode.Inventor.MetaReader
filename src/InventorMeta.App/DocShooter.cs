using System;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace ExtrabbitCode.Inventor.MetaReader.App;

/// <summary>
/// Headless documentation snapshotter. Run with
/// <c>InventorMeta.App.exe --shoot-docs &lt;outDir&gt; --samples &lt;sampleFilesDir&gt;</c>:
/// it opens a curated set of sample files, walks the tabs, and writes one PNG per view in
/// light and dark themes (<c>&lt;slug&gt;-light.png</c> / <c>&lt;slug&gt;-dark.png</c>) so each
/// screenshot can sit next to its section in the docs. Generated locally; the PNGs are committed.
/// </summary>
internal static class DocShooter
{
    public static async Task RunAsync(string outDir, string? samplesDir)
    {
        try
        {
            Directory.CreateDirectory(outDir);

            // The File Structure tab is hidden by default; show it so it can be captured (and so
            // all four tabs appear in the strip). Ephemeral settings keep this in memory only.
            HideStore.Set(HideStore.TabKey("File Structure"), hidden: false);

            string? part = Sample(samplesDir, "SampleBg", "SamplePart.ipt"); // thumbnail + 3 model states
            string? asm = Sample(samplesDir, "SampleBg", "SampleBg.iam");    // resolvable reference graph

            // An assembly that uses iParts (factory + members), for the iPart-marking shot. Lives in
            // the (git-ignored) jet-engine sample; the shot is skipped if it isn't extracted locally.
            string? ipartAsm = samplesDir is null ? null : Sample(
                Path.Combine(samplesDir, "Jet_Engine_Model", "Jet Engine Model"), "Workspace", "Mid Compression Assembly.iam");

            // One persistent window for the whole run: a WinUI app exits when its last window
            // closes, so we never close it mid-run - we just reset its tabs between shots.
            MainWindow w = new();
            w.Activate();
            w.ShootResize(1280, 840);
            await Task.Delay(600);

            foreach (ElementTheme theme in new[] { ElementTheme.Light, ElementTheme.Dark })
            {
                ThemeManager.Apply(w, theme);
                await Task.Delay(300);

                // 1. The empty drop-zone welcome screen (no file open).
                w.ShootCloseAllTabs();
                w.SetStatus("Ready. Open or drop an Inventor file to begin.");
                await Task.Delay(400);
                await Capture(w, "app__welcome", theme, outDir);

                // 2-4. A part: the overview (key iProperties + thumbnail), model states, raw structure.
                if (part != null)
                {
                    w.ShootCloseAllTabs();
                    w.ShootOpen(part);
                    await Task.Delay(800);
                    DocumentView? dv = w.CurrentView;
                    await Capture(w, "app__overview", theme, outDir);

                    dv?.ShootSelectTab("Model States");
                    await Task.Delay(600);
                    await Capture(w, "app__model-states", theme, outDir);

                    dv?.ShootSelectTab("File Structure");
                    await Task.Delay(600);
                    await Capture(w, "app__file-structure", theme, outDir);
                }

                // 5. An assembly: the interactive reference graph (builds asynchronously).
                if (asm != null)
                {
                    w.ShootCloseAllTabs();
                    w.ShootOpen(asm);
                    await Task.Delay(800);
                    w.CurrentView?.ShootSelectTab("References");
                    await Task.Delay(2200);
                    await Capture(w, "app__references", theme, outDir);
                }

                // 6. iParts: factory + members highlighted in the reference graph. Shot in a
                // taller window so the graph fits at a readable size, then restored.
                if (ipartAsm != null)
                {
                    w.ShootCloseAllTabs();
                    w.ShootResize(1480, 960);
                    w.ShootOpen(ipartAsm);
                    await Task.Delay(800);
                    w.CurrentView?.ShootSelectTab("References");
                    await Task.Delay(2600);
                    await Capture(w, "app__iparts", theme, outDir);
                    w.ShootResize(1280, 840);
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

    private static async Task Capture(Window w, string slug, ElementTheme theme, string outDir)
    {
        RenderTargetBitmap rtb = new();
        await rtb.RenderAsync(w.Content);
        IBuffer pixels = await rtb.GetPixelsAsync();

        string suffix = theme == ElementTheme.Light ? "light" : "dark";
        string path = Path.Combine(outDir, $"{slug}-{suffix}.png");

        InMemoryRandomAccessStream ras = new();
        BitmapEncoder encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, ras);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
            (uint)rtb.PixelWidth, (uint)rtb.PixelHeight, 96, 96, pixels.ToArray());
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
