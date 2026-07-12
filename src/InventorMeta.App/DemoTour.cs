using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;

namespace ExtrabbitCode.Inventor.MetaReader.App;

/// <summary>
/// Scripted feature tour for recording promotional videos and documentation GIFs. Run with
/// <c>InventorMeta.App.exe --demo-tour &lt;outDir&gt; [--samples &lt;dir&gt;] [--model &lt;assembly.iam&gt;]</c>:
/// the window opens VISIBLY at a fixed position/size (written to <c>window-rect.json</c> so an
/// external recorder knows the region to capture), waits for the recorder to create
/// <c>record-ready.flag</c> in the out folder, then walks the app's features at a human pace -
/// once per theme - and writes <c>chapters.json</c> with the start/end of every segment so the
/// recording can be cut into per-feature clips afterwards.
/// </summary>
internal static class DemoTour
{
    private sealed record Chapter(string Name, string Theme, long StartMs, long EndMs);

    public static async Task RunAsync(string outDir, string? samplesDir, string? modelPath)
    {
        List<Chapter> chapters = [];
        Stopwatch clock = new();
        try
        {
            Directory.CreateDirectory(outDir);
            HideStore.Set(HideStore.TabKey("File Structure"), hidden: false);
            TipSettings.Enabled = false;                 // no teaching tips popping into the recording

            string? part = Sample(samplesDir, "SampleBg", "SamplePart.ipt");
            string? tnp = Sample(samplesDir, "SampleBg", "TubeAndPipe.ipt");
            string? sampleAsm = Sample(samplesDir, "SampleBg", "SampleBg.iam");
            string? asm = modelPath != null && File.Exists(modelPath) ? modelPath : sampleAsm;

            // start with a clean model: saved markup from earlier sessions would otherwise appear
            // on the 3D view before the redlining chapter introduces it
            if (asm != null)
            {
                try
                {
                    SvfStore store = new(ViewerSettings.NetworkPath);
                    string marks = Path.Combine(store.EntryDir(SvfStore.ComputeKey(asm)), "redline-layers.json");
                    if (File.Exists(marks)) { File.Delete(marks); }
                }
                catch { /* no cache entry yet */ }
            }

            MainWindow w = new();
            w.Activate();
            w.ShootMove(8, 8);
            w.ShootResize(1600, 1000);
            w.ShootTopmost();
            await Task.Delay(600);

            if (w.ShootWindowRect() is { } r)
            {
                File.WriteAllText(Path.Combine(outDir, "window-rect.json"),
                    $"{{\"x\":{r.X},\"y\":{r.Y},\"w\":{r.W},\"h\":{r.H}}}");
            }

            // wait for the external recorder to say it is rolling
            string flag = Path.Combine(outDir, "record-ready.flag");
            for (int i = 0; i < 240 && !File.Exists(flag); i++) { await Task.Delay(500); }

            clock.Start();
            long tourStartEpoch = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            foreach (ElementTheme theme in new[] { ElementTheme.Light, ElementTheme.Dark })
            {
                string t = theme == ElementTheme.Light ? "light" : "dark";
                ThemeManager.Apply(w, theme);
                await Task.Delay(600);

                // -- home: the welcome screen with recents and the sample gallery --------------
                long c0 = clock.ElapsedMilliseconds;
                RecentFiles.Clear();
                foreach (string? recent in new[] { tnp, asm, part })
                {
                    if (recent != null) { RecentFiles.Add(recent); }
                }
                w.ShootCloseAllTabs();
                w.SetStatus("Ready. Open or drop an Inventor file to begin.");
                await Task.Delay(3600);
                chapters.Add(new("home", t, c0, clock.ElapsedMilliseconds));

                // -- open the showcase assembly: thumbnail, iProperties, categories ------------
                if (asm != null)
                {
                    c0 = clock.ElapsedMilliseconds;
                    w.ShootOpen(asm);
                    await Task.Delay(5200);
                    chapters.Add(new("overview", t, c0, clock.ElapsedMilliseconds));

                    // -- the interactive reference graph -------------------------------------
                    c0 = clock.ElapsedMilliseconds;
                    DocumentView? dv = w.CurrentView;
                    dv?.ShootSelectTab("References");
                    await Task.Delay(3000);
                    dv?.ShootExpandGraph();
                    await Task.Delay(2600);
                    dv?.ShootSetGraphLayout(GraphLayout.TopDown);
                    await Task.Delay(2600);
                    dv?.ShootSetGraphLayout(GraphLayout.Network);
                    await Task.Delay(4200);
                    dv?.ShootFitGraph();
                    await Task.Delay(2000);
                    chapters.Add(new("references", t, c0, clock.ElapsedMilliseconds));
                }

                // -- model states side by side --------------------------------------------------
                if (part != null)
                {
                    c0 = clock.ElapsedMilliseconds;
                    w.ShootCloseAllTabs();
                    w.ShootOpen(part);
                    await Task.Delay(1400);
                    w.CurrentView?.ShootSelectTab("Model States");
                    await Task.Delay(4200);
                    chapters.Add(new("model-states", t, c0, clock.ElapsedMilliseconds));
                }

                // -- the 3D viewer: orbit, body coloring, redlining, shortcuts ------------------
                if (asm != null)
                {
                    w.ShootCloseAllTabs();
                    w.ShootOpen(asm);
                    await Task.Delay(1200);
                    if (w.CurrentView is { } v3)
                    {
                        v3.ShootOpen3D();
                        bool ready = false;
                        for (int i = 0; i < 240 && !ready; i++)
                        {
                            await Task.Delay(500);
                            ready = await v3.ShootViewer3DScriptAsync(ViewerReadyJs) == "\"1\"";
                        }
                        if (ready)
                        {
                            await Task.Delay(1500);

                            c0 = clock.ElapsedMilliseconds;
                            await v3.ShootViewer3DScriptAsync(OrbitJs(5200));
                            await Task.Delay(5600);
                            chapters.Add(new("viewer-orbit", t, c0, clock.ElapsedMilliseconds));

                            c0 = clock.ElapsedMilliseconds;
                            await v3.ShootViewer3DScriptAsync(
                                "NOP_VIEWER.getExtension('Extrabbit.Coloring').setEnabled(true)");
                            await Task.Delay(3200);
                            chapters.Add(new("coloring", t, c0, clock.ElapsedMilliseconds));
                            await v3.ShootViewer3DScriptAsync(
                                "NOP_VIEWER.getExtension('Extrabbit.Coloring').setEnabled(false)");
                            await Task.Delay(700);

                            c0 = clock.ElapsedMilliseconds;
                            await v3.ShootViewer3DScriptAsync(RedlineSlowJs);
                            await Task.Delay(11500);         // the script draws for ~9.5s
                            chapters.Add(new("redlining", t, c0, clock.ElapsedMilliseconds));

                            c0 = clock.ElapsedMilliseconds;
                            await v3.ShootViewer3DScriptAsync(
                                "NOP_VIEWER.getExtension('Extrabbit.Hotkeys')._toggle()");
                            await Task.Delay(3400);
                            await v3.ShootViewer3DScriptAsync(
                                "NOP_VIEWER.getExtension('Extrabbit.Hotkeys')._toggle()");
                            await Task.Delay(600);
                            chapters.Add(new("hotkeys", t, c0, clock.ElapsedMilliseconds));
                        }
                        v3.ShootClose3D();
                        await Task.Delay(600);
                    }
                }

                // -- back home for a clean outro -----------------------------------------------
                c0 = clock.ElapsedMilliseconds;
                w.ShootCloseAllTabs();
                await Task.Delay(2200);
                chapters.Add(new("outro", t, c0, clock.ElapsedMilliseconds));
            }

            WriteChapters(outDir, tourStartEpoch, chapters);
        }
        catch (Exception ex)
        {
            try { File.AppendAllText(Path.Combine(outDir, "demo-tour.log"), ex + "\n"); }
            catch { /* best-effort */ }
        }
        finally
        {
            try { File.WriteAllText(Path.Combine(outDir, "tour-done.flag"), "done"); }
            catch { /* best-effort */ }
            Application.Current.Exit();
        }
    }

    private static void WriteChapters(string outDir, long tourStartEpoch, List<Chapter> chapters)
    {
        StringBuilder sb = new();
        sb.Append($"{{\"tourStartEpochMs\":{tourStartEpoch},\"chapters\":[");
        for (int i = 0; i < chapters.Count; i++)
        {
            Chapter c = chapters[i];
            if (i > 0) { sb.Append(','); }
            sb.Append($"{{\"name\":\"{c.Name}\",\"theme\":\"{c.Theme}\",\"startMs\":{c.StartMs},\"endMs\":{c.EndMs}}}");
        }
        sb.Append("]}");
        File.WriteAllText(Path.Combine(outDir, "chapters.json"), sb.ToString());
    }

    private const string ViewerReadyJs =
        "(function(){try{return (window.NOP_VIEWER && NOP_VIEWER.model && NOP_VIEWER.model.isLoadDone() && document.getElementById('extrabbit-group')) ? '1' : '0';}catch(e){return '0';}})()";

    /// <summary>Smooth orbit around the model for <paramref name="durationMs"/>, via rAF.</summary>
    private static string OrbitJs(int durationMs) =>
        $$"""
        (function () {
          try {
            const vv = window.NOP_VIEWER, nav = vv.navigation;
            const t0 = performance.now(), dur = {{durationMs}};
            const u = nav.getCameraUpVector();
            const axis = new THREE.Vector3(u.x, u.y, u.z).normalize();
            let last = t0;
            (function step() {
              const now = performance.now();
              if (now - t0 >= dur) { return; }
              const dt = now - last; last = now;
              const pos = nav.getPosition(), tgt = nav.getTarget();
              const v = new THREE.Vector3(pos.x - tgt.x, pos.y - tgt.y, pos.z - tgt.z);
              v.applyAxisAngle(axis, 0.00038 * dt);
              nav.setPosition(new THREE.Vector3(tgt.x + v.x, tgt.y + v.y, tgt.z + v.z));
              requestAnimationFrame(step);
            })();
            return "orbiting";
          } catch (e) { return "err:" + e.message; }
        })()
        """;

    /// <summary>Draws redline markup at a human pace: activates the mode, then a 3D paint stroke
    /// along the surface, a circle and an arrow, with real delays between the pointer moves so the
    /// recording shows the strokes appearing.</summary>
    private const string RedlineSlowJs =
        """
        (async function () {
          try {
            const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
            const vv = window.NOP_VIEWER;
            const ext = vv.getExtension("Extrabbit.Redline");
            if (!ext) { return "no-ext"; }
            ext.setActive(true);
            while (ext._layers.length) { ext._deleteLayer(ext._layers[0].id); }
            const layer = ext._ensureLayer();
            layer.name = "Review notes";
            ext._syncBrowser();
            await sleep(900);
            const svg = document.getElementById("redline2d");
            const W = vv.canvas.clientWidth, H = vv.canvas.clientHeight;
            const fire = (t, x, y, e) => svg.dispatchEvent(new PointerEvent(t,
              Object.assign({ clientX: x, clientY: y, button: 0, buttons: 1, pointerId: 1, bubbles: true }, e || {})));
            const row = (yF) => {
              const y = H * yF, xs = [];
              for (let i = 0; i <= 80; i++) {
                const x = W * 0.2 + i * (W * 0.6 / 80);
                if (vv.impl.hitTest(x, y, false)) { xs.push(x); }
              }
              return { y, xs };
            };
            let r = row(0.52);
            if (r.xs.length < 8) { r = row(0.6); }
            if (r.xs.length >= 8) {
              ext._width = 2;
              ext._selectTool("paint3d");
              fire("pointerdown", r.xs[0], r.y);
              for (let i = 0; i < r.xs.length; i++) {
                fire("pointermove", r.xs[i], r.y + Math.sin(i / 3) * 10);
                await sleep(38);
              }
              fire("pointerup", r.xs[r.xs.length - 1], r.y, { buttons: 0 });
            }
            await sleep(700);
            const mid = row(0.4);
            const cx = mid.xs.length ? mid.xs[Math.floor(mid.xs.length / 2)] : W * 0.55;
            const cy = mid.y;
            ext._selectTool("circle");
            fire("pointerdown", cx - 45, cy - 32);
            for (let i = 1; i <= 14; i++) {
              fire("pointermove", cx - 45 + i * (90 / 14), cy - 32 + i * (64 / 14));
              await sleep(45);
            }
            fire("pointerup", cx + 45, cy + 32, { buttons: 0 });
            await sleep(700);
            const ax0 = Math.min(cx + 170, W - 30), ay0 = cy - 110;
            ext._selectTool("arrow");
            fire("pointerdown", ax0, ay0);
            for (let i = 1; i <= 12; i++) {
              fire("pointermove", ax0 + i * ((cx + 52 - ax0) / 12), ay0 + i * ((cy - 26 - ay0) / 12));
              await sleep(45);
            }
            fire("pointerup", cx + 52, cy - 26, { buttons: 0 });
            ext._selectTool("free");
            return "ok";
          } catch (e) { return "err:" + e.message; }
        })()
        """;

    private static string? Sample(string? dir, string sub, string name)
    {
        if (dir == null) { return null; }
        string path = Path.Combine(dir, sub, name);
        return File.Exists(path) ? path : null;
    }
}
