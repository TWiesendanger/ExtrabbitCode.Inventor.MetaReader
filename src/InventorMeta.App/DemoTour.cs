using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Windows.Foundation;

namespace ExtrabbitCode.Inventor.MetaReader.App;

/// <summary>
/// Scripted feature tour for recording promotional videos and documentation GIFs. Run with
/// <c>InventorMeta.App.exe --demo-tour &lt;outDir&gt; [--samples &lt;dir&gt;] [--model &lt;assembly.iam&gt;]</c>:
/// the window opens VISIBLY, always-on-top, at a fixed position/size (written to
/// <c>window-rect.json</c> so an external recorder knows the region to capture), waits for the
/// recorder to create <c>record-ready.flag</c>, then walks the app's features at a human pace -
/// once per theme - and writes <c>chapters.json</c> with the start/end of every segment.
///
/// The REAL mouse cursor is choreographed along the way (the recorder picks it up automatically):
/// it glides onto each control before the action fires, and traces the redline strokes as they are
/// drawn, so the footage reads as a person using the app. Don't touch the mouse while it runs.
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

            Cursor cursor = new(w);
            cursor.ParkOffWindow();                      // start outside the frame, like a real session

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

            const string t = "light";                     // one pass; light matches the docs
            ThemeManager.Apply(w, ElementTheme.Light);
            await Task.Delay(600);

            // -- home: the welcome screen with recents and the sample gallery ------------------
            long c0 = clock.ElapsedMilliseconds;
            w.ShootCaption("Read Inventor & STEP files — no Inventor install needed");
            RecentFiles.Clear();
            foreach (string? recent in new[] { tnp, asm, part })
            {
                if (recent != null) { RecentFiles.Add(recent); }
            }
            w.ShootCloseAllTabs();
            w.SetStatus("Ready. Open or drop an Inventor file to begin.");
            await Task.Delay(3600);
            chapters.Add(new("home", t, c0, clock.ElapsedMilliseconds));

            // -- open the showcase assembly: thumbnail, iProperties, categories ----------------
            if (asm != null)
            {
                c0 = clock.ElapsedMilliseconds;
                w.ShootCaption("Every iProperty, straight from the file");
                w.ShootOpen(asm);
                await Task.Delay(5200);
                chapters.Add(new("overview", t, c0, clock.ElapsedMilliseconds));

                // -- the interactive reference graph ------------------------------------------
                c0 = clock.ElapsedMilliseconds;
                w.ShootCaption("The reference graph — explore, zoom, rearrange");
                DocumentView? dv = w.CurrentView;
                if (dv?.ShootTabHeader("References") is { } refsTab)
                {
                    await cursor.MoveToElementAsync(refsTab, 700);
                }
                dv?.ShootSelectTab("References");
                await Task.Delay(3000);
                dv?.ShootExpandGraph();
                await Task.Delay(2400);
                dv?.ShootGraphShowcase();                 // camera ride into a few nodes and back
                await Task.Delay(6000);
                FrameworkElement? layoutPick = dv?.ShootGraphLayoutPick();
                if (layoutPick != null) { await cursor.MoveToElementAsync(layoutPick, 650); }
                dv?.ShootSetGraphLayout(GraphLayout.Network);
                await Task.Delay(4200);
                dv?.ShootFitGraph();
                await Task.Delay(1800);
                chapters.Add(new("references", t, c0, clock.ElapsedMilliseconds));
            }

            // -- multi-tabbing: several documents side by side, hop between them ---------------
            if (part != null && tnp != null)
            {
                c0 = clock.ElapsedMilliseconds;
                w.ShootCaption("Each file in its own tab");
                w.ShootOpen(part);
                await Task.Delay(1400);
                w.ShootOpen(tnp);
                await Task.Delay(1800);
                foreach (int tab in new[] { 1, 3, 2 })    // reel -> pipe -> part, cursor first
                {
                    if (w.ShootTab(tab) is { } header) { await cursor.MoveToElementAsync(header, 650); }
                    w.ShootSelectTabIndex(tab);
                    await Task.Delay(1500);
                }
                chapters.Add(new("tabs", t, c0, clock.ElapsedMilliseconds));

                // -- model states side by side (the part tab is already selected) --------------
                c0 = clock.ElapsedMilliseconds;
                w.ShootCaption("Model states, compared side by side");
                if (w.CurrentView?.ShootTabHeader("Model States") is { } statesTab)
                {
                    await cursor.MoveToElementAsync(statesTab, 700);
                }
                w.CurrentView?.ShootSelectTab("Model States");
                await Task.Delay(4200);
                chapters.Add(new("model-states", t, c0, clock.ElapsedMilliseconds));
            }

            // -- the 3D viewer: orbit, body coloring, redlining, shortcuts ---------------------
            if (asm != null)
            {
                // back to the assembly's tab, then into the 3D view via its preview
                if (w.ShootTab(1) is { } asmTab) { await cursor.MoveToElementAsync(asmTab, 650); }
                w.ShootSelectTabIndex(1);
                await Task.Delay(900);
                if (w.CurrentView is { } v3)
                {
                    w.ShootCaption("Interactive 3D — generated without Inventor");
                    if (v3.ShootElement("SidebarCard") is { } card)
                    {
                        await cursor.MoveToElementAsync(card, 700, yFraction: 0.18);
                    }
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
                        await v3.ShootViewer3DScriptAsync(DefineFireJs);   // page-side event helper
                        ViewerCursor vc = new(cursor, w, v3);

                        c0 = clock.ElapsedMilliseconds;
                        await v3.ShootViewer3DScriptAsync(OrbitJs(5200));
                        await Task.Delay(5600);
                        chapters.Add(new("viewer-orbit", t, c0, clock.ElapsedMilliseconds));

                        c0 = clock.ElapsedMilliseconds;
                        w.ShootCaption("One click gives every body its own colour");
                        await vc.MoveToDomAsync("#extrabbit-coloring-btn", 700);
                        await v3.ShootViewer3DScriptAsync(
                            "NOP_VIEWER.getExtension('Extrabbit.Coloring').setEnabled(true)");
                        await Task.Delay(3200);
                        chapters.Add(new("coloring", t, c0, clock.ElapsedMilliseconds));
                        await v3.ShootViewer3DScriptAsync(
                            "NOP_VIEWER.getExtension('Extrabbit.Coloring').setEnabled(false)");
                        await Task.Delay(700);

                        c0 = clock.ElapsedMilliseconds;
                        w.ShootCaption("Redlining — draw and paint directly on the model");
                        await RunRedlineAsync(v3, vc);
                        chapters.Add(new("redlining", t, c0, clock.ElapsedMilliseconds));

                        c0 = clock.ElapsedMilliseconds;
                        w.ShootCaption("Every shortcut rebindable");
                        await vc.MoveToDomAsync("#extrabbit-hotkeys-btn", 700);
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

            // -- back home for a clean outro ----------------------------------------------------
            c0 = clock.ElapsedMilliseconds;
            w.ShootCaption("Inventor MetaReader — free on the Microsoft Store");
            w.ShootCloseAllTabs();
            cursor.ParkOffWindow();
            await Task.Delay(2600);
            w.ShootCaption(null);
            chapters.Add(new("outro", t, c0, clock.ElapsedMilliseconds));

            cursor.Restore();
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

    /// <summary>Redlining with the cursor doing the drawing: activates the mode from its toolbar
    /// button, then draws a 3D paint stroke, a circle and an arrow - each pointer event fired in
    /// the page while the real cursor sits exactly on that spot.</summary>
    private static async Task RunRedlineAsync(DocumentView v3, ViewerCursor vc)
    {
        await vc.MoveToDomAsync("#extrabbit-redline-btn", 700);
        await v3.ShootViewer3DScriptAsync(RedlinePrepJs);
        await Task.Delay(900);

        // the page plans the stroke paths (hit-testing needs the live model); C# walks them
        string planJson = await v3.ShootViewer3DScriptAsync(RedlinePlanJs);
        RedlinePlan? plan = ParsePlan(planJson);
        if (plan == null) { return; }

        if (plan.Paint.Count >= 8)
        {
            await vc.MoveToDomAsync("#redlinePanel .rl-tool[title^=\"Paint\"]", 650);
            await v3.ShootViewer3DScriptAsync("NOP_VIEWER.getExtension('Extrabbit.Redline')._selectTool('paint3d')");
            await Task.Delay(350);
            await vc.TraceStrokeAsync(plan.Paint, stepMs: 36);
        }
        await Task.Delay(600);

        await vc.MoveToDomAsync("#redlinePanel .rl-style", 650);   // the shapes dropdown trigger
        await v3.ShootViewer3DScriptAsync("NOP_VIEWER.getExtension('Extrabbit.Redline')._selectTool('circle')");
        await Task.Delay(350);
        await vc.TraceStrokeAsync(plan.Circle, stepMs: 42);
        await Task.Delay(500);

        await v3.ShootViewer3DScriptAsync("NOP_VIEWER.getExtension('Extrabbit.Redline')._selectTool('arrow')");
        await vc.TraceStrokeAsync(plan.Arrow, stepMs: 42);
        await v3.ShootViewer3DScriptAsync("NOP_VIEWER.getExtension('Extrabbit.Redline')._selectTool('free')");
        await Task.Delay(900);
    }

    private sealed record RedlinePlan(List<Point> Paint, List<Point> Circle, List<Point> Arrow);

    private static RedlinePlan? ParsePlan(string json)
    {
        try
        {
            // ExecuteScriptAsync returns a JSON-encoded string: unwrap, then parse the object
            string? inner = JsonSerializer.Deserialize<string>(json);
            if (inner == null || inner.StartsWith("err", StringComparison.Ordinal)) { return null; }
            using JsonDocument doc = JsonDocument.Parse(inner);
            static List<Point> Pts(JsonElement e)
            {
                List<Point> list = [];
                foreach (JsonElement p in e.EnumerateArray())
                {
                    list.Add(new Point(p[0].GetDouble(), p[1].GetDouble()));
                }
                return list;
            }
            return new RedlinePlan(
                Pts(doc.RootElement.GetProperty("paint")),
                Pts(doc.RootElement.GetProperty("circle")),
                Pts(doc.RootElement.GetProperty("arrow")));
        }
        catch { return null; }
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

    // ---------- page scripts ----------

    private const string ViewerReadyJs =
        "(function(){try{return (window.NOP_VIEWER && NOP_VIEWER.model && NOP_VIEWER.model.isLoadDone() && document.getElementById('extrabbit-group')) ? '1' : '0';}catch(e){return '0';}})()";

    /// <summary>Installs a tiny page-global so per-step pointer events are cheap to fire.</summary>
    private const string DefineFireJs =
        """
        (function () {
          const svg = document.getElementById("redline2d");
          window.__demoFire = (t, x, y, up) => svg.dispatchEvent(new PointerEvent(t,
            { clientX: x, clientY: y, button: 0, buttons: up ? 0 : 1, pointerId: 1, bubbles: true }));
          return "ok";
        })()
        """;

    /// <summary>Activates redlining and clears restored layers, leaving a named fresh one.</summary>
    private const string RedlinePrepJs =
        """
        (function () {
          try {
            const ext = NOP_VIEWER.getExtension("Extrabbit.Redline");
            ext.setActive(true);
            while (ext._layers.length) { ext._deleteLayer(ext._layers[0].id); }
            ext._ensureLayer().name = "Review notes";
            ext._syncBrowser();
            ext._width = 2;
            return "ok";
          } catch (e) { return "err:" + e.message; }
        })()
        """;

    /// <summary>Plans the demo strokes in page coordinates: a wavy run along the model surface
    /// (hit-tested), a circle around a feature and an arrow pointing at it.</summary>
    private const string RedlinePlanJs =
        """
        (function () {
          try {
            const vv = window.NOP_VIEWER;
            const W = vv.canvas.clientWidth, H = vv.canvas.clientHeight;
            const row = (yF) => {
              const y = H * yF, xs = [];
              for (let i = 0; i <= 60; i++) {
                const x = W * 0.2 + i * (W * 0.6 / 60);
                if (vv.impl.hitTest(x, y, false)) { xs.push(x); }
              }
              return { y, xs };
            };
            let r = row(0.52);
            if (r.xs.length < 8) { r = row(0.6); }
            const paint = r.xs.map((x, i) => [x, r.y + Math.sin(i / 3) * 10]);
            const mid = row(0.4);
            const cx = mid.xs.length ? mid.xs[Math.floor(mid.xs.length / 2)] : W * 0.55;
            const cy = mid.y;
            const circle = [];
            for (let i = 0; i <= 14; i++) {
              circle.push([cx - 45 + i * (90 / 14), cy - 32 + i * (64 / 14)]);
            }
            const ax = Math.min(cx + 170, W - 30), ay = cy - 110;
            const arrow = [];
            for (let i = 0; i <= 12; i++) {
              arrow.push([ax + i * ((cx + 52 - ax) / 12), ay + i * ((cy - 26 - ay) / 12)]);
            }
            return JSON.stringify({ paint, circle, arrow });
          } catch (e) { return "err:" + e.message; }
        })()
        """;

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

    private static string? Sample(string? dir, string sub, string name)
    {
        if (dir == null) { return null; }
        string path = Path.Combine(dir, sub, name);
        return File.Exists(path) ? path : null;
    }

    // ---------- the choreographed cursor ----------

    /// <summary>Moves the REAL mouse cursor (SetCursorPos) so the recording shows a person at
    /// work: eased glides onto controls, and stroke tracing for the redline drawing. Coordinates
    /// go from XAML DIPs to physical pixels through the window's rasterization scale.</summary>
    private sealed class Cursor
    {
        private readonly MainWindow _w;
        private readonly IntPtr _hwnd;
        private POINT _saved;

        public Cursor(MainWindow w)
        {
            _w = w;
            _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(w);
            GetCursorPos(out _saved);
        }

        public void ParkOffWindow()
        {
            if (_w.ShootWindowRect() is { } r) { SetCursorPos(r.X + r.W + 60, r.Y + r.H / 2); }
        }

        public void Restore() => SetCursorPos(_saved.X, _saved.Y);

        /// <summary>Screen position of a point given in window-content DIPs.</summary>
        public (int X, int Y) ToScreen(Point dips)
        {
            double scale = _w.Content.XamlRoot?.RasterizationScale ?? 1.0;
            POINT origin = default;
            ClientToScreen(_hwnd, ref origin);
            return (origin.X + (int)Math.Round(dips.X * scale), origin.Y + (int)Math.Round(dips.Y * scale));
        }

        public async Task MoveToElementAsync(FrameworkElement element, int durationMs, double yFraction = 0.5)
        {
            if (element.ActualWidth <= 0) { return; }
            Point center = element.TransformToVisual(_w.Content)
                .TransformPoint(new Point(element.ActualWidth / 2, element.ActualHeight * yFraction));
            (int x, int y) = ToScreen(center);
            await GlideToAsync(x, y, durationMs);
            await Task.Delay(180);                       // dwell so the hover state reads on camera
        }

        /// <summary>Eased (smooth-step) glide from wherever the cursor is to the target.</summary>
        public async Task GlideToAsync(int tx, int ty, int durationMs)
        {
            GetCursorPos(out POINT from);
            int steps = Math.Max(2, durationMs / 12);
            for (int i = 1; i <= steps; i++)
            {
                double k = (double)i / steps;
                double e = k * k * (3 - 2 * k);
                SetCursorPos((int)Math.Round(from.X + (tx - from.X) * e),
                             (int)Math.Round(from.Y + (ty - from.Y) * e));
                await Task.Delay(12);
            }
        }

        [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
        [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr hWnd, ref POINT p);

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X; public int Y; }
    }

    /// <summary>Cursor choreography inside the 3D viewer page: maps page (CSS px) coordinates to
    /// the screen via the WebView2's position, hovers DOM elements, and traces redline strokes -
    /// each pointer event fires while the real cursor sits on exactly that point.</summary>
    private sealed class ViewerCursor(Cursor cursor, MainWindow w, DocumentView view)
    {
        public async Task MoveToDomAsync(string selector, int durationMs)
        {
            string json = await view.ShootViewer3DScriptAsync(
                $"(function(){{const e=document.querySelector('{selector}');if(!e)return '';const r=e.getBoundingClientRect();return JSON.stringify([r.x+r.width/2,r.y+r.height/2]);}})()");
            if (PagePoint(json) is not { } p) { return; }
            (int x, int y) = PageToScreen(p);
            await cursor.GlideToAsync(x, y, durationMs);
            await Task.Delay(220);
        }

        /// <summary>Walks a planned stroke: pointerdown at the first point, a move per step with
        /// the cursor riding along, pointerup at the last.</summary>
        public async Task TraceStrokeAsync(List<Point> path, int stepMs)
        {
            if (path.Count < 2) { return; }
            (int sx, int sy) = PageToScreen(path[0]);
            await cursor.GlideToAsync(sx, sy, 500);
            await Task.Delay(150);
            await Fire("pointerdown", path[0]);
            for (int i = 1; i < path.Count; i++)
            {
                (int x, int y) = PageToScreen(path[i]);
                _ = SetCursor(x, y);
                await Fire("pointermove", path[i]);
                await Task.Delay(stepMs);
            }
            await Fire("pointerup", path[^1], up: true);
        }

        private Task<string> Fire(string type, Point p, bool up = false) =>
            view.ShootViewer3DScriptAsync(
                $"__demoFire('{type}',{p.X.ToString(System.Globalization.CultureInfo.InvariantCulture)},{p.Y.ToString(System.Globalization.CultureInfo.InvariantCulture)},{(up ? "true" : "false")})");

        private (int X, int Y) PageToScreen(Point pagePx)
        {
            // WebView2 scales its content with the monitor DPI, so page CSS px equal DIPs here
            Point origin = view.ShootViewer3DOrigin(w.Content) ?? new Point(0, 0);
            return cursor.ToScreen(new Point(origin.X + pagePx.X, origin.Y + pagePx.Y));
        }

        private static Point? PagePoint(string scriptResult)
        {
            try
            {
                string? inner = JsonSerializer.Deserialize<string>(scriptResult);
                if (string.IsNullOrEmpty(inner)) { return null; }
                double[]? xy = JsonSerializer.Deserialize<double[]>(inner);
                return xy is { Length: 2 } ? new Point(xy[0], xy[1]) : null;
            }
            catch { return null; }
        }

        private static bool SetCursor(int x, int y) => SetCursorPosNative(x, y);

        [DllImport("user32.dll", EntryPoint = "SetCursorPos")]
        private static extern bool SetCursorPosNative(int x, int y);
    }
}
