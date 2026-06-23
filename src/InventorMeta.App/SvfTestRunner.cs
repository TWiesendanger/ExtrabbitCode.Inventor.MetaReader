using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace ExtrabbitCode.Inventor.MetaReader.App;

/// <summary>
/// Headless diagnostic entry point for the 3D generation chain - no UI yet. Run:
///   InventorMeta.App.exe --gen-svf "C:\path\model.ipt" [--inv-year 2026]
/// It detects Inventor, computes the cache key, generates the SVF into the store, and prints the
/// result (to the parent console and to %TEMP%\invmeta-gensvf.log). Temporary until the viewer UI
/// wires this in.
/// </summary>
internal static class SvfTestRunner
{
    private static readonly string LogPath = Path.Combine(Path.GetTempPath(), "invmeta-gensvf.log");

    public static void Run(string[] cli)
    {
        try { File.Delete(LogPath); } catch { /* fresh log per run */ }
        AttachConsole(AttachParentProcess);
        try { RunCore(cli); }
        catch (Exception ex) { Log("ERROR: " + ex); }
        Environment.Exit(0);
    }

    private static void RunCore(string[] cli)
    {
        Log($"=== SVF generation test ===");

        int gi = Array.IndexOf(cli, "--gen-svf");
        string? file = gi + 1 < cli.Length ? cli[gi + 1] : null;
        if (string.IsNullOrWhiteSpace(file) || !File.Exists(file))
        {
            Log($"Usage: InventorMeta.App.exe --gen-svf \"<file.ipt|iam>\" [--inv-year N]");
            Log(file == null ? "(no file given)" : $"File not found: {file}");
            return;
        }

        Log($"File: {file}");

        var installs = InventorInstalls.Detect();
        Log("Installed Inventor: " + (installs.Count == 0 ? "(none)" : string.Join(", ", installs.Select(i => i.Year))));

        var store = new SvfStore(ViewerSettings.NetworkPath);
        Log("Hashing file…");
        string key = SvfStore.ComputeKey(file);
        Log($"Cache key (SHA-256): {key}");
        Log($"Store root: {store.PrimaryRoot}");
        string baseDir = store.EntryDir(key);
        Log($"Entry dir: {baseDir}");
        Log($"Expected manifest: {Path.Combine(store.OutputDir(key), "bubble.json")}");
        Log($"Already cached: {store.Has(key)}");

        if (installs.Count == 0)
        {
            Log("No Inventor installed - detection + cache verified, but can't generate here.");
            return;
        }

        int yi = Array.IndexOf(cli, "--inv-year");
        int wantYear = yi >= 0 && yi + 1 < cli.Length && int.TryParse(cli[yi + 1], out int y) ? y : installs[0].Year;
        InventorInstall inv = installs.FirstOrDefault(i => i.Year == wantYear) ?? installs[0];
        Log($"Using {inv.DisplayName}  ({inv.ExePath})");

        if (store.Has(key))
        {
            Log("Skipping generation (already cached).");
            return;
        }

        Log("Generating - this opens the model in Inventor and can take a while…");
        var sw = Stopwatch.StartNew();
        SvfGenerator.Result res = SvfGenerator.Generate(inv, file, baseDir, Log);
        sw.Stop();
        Log(res.Ok
            ? $"OK in {sw.Elapsed.TotalSeconds:0}s -> {res.BubblePath}"
            : $"FAILED in {sw.Elapsed.TotalSeconds:0}s: {res.Error}");
        Log($"store.Has(key) now: {store.Has(key)}");   // the viewer will use this lookup

        // Show what actually landed (and where), to pin down the output structure.
        string entry = store.EntryDir(key);
        Log($"--- files under {entry} ---");
        if (Directory.Exists(entry))
        {
            string[] files = [.. Directory.EnumerateFiles(entry, "*", SearchOption.AllDirectories)];
            if (files.Length == 0) { Log("  (nothing was written)"); }
            foreach (string f in files.Take(80)) { Log("  " + Path.GetRelativePath(entry, f)); }
        }
        else { Log("  (entry dir does not exist)"); }
    }

    private static void Log(string message)
    {
        Console.WriteLine(message);
        try { File.AppendAllText(LogPath, message + Environment.NewLine); } catch { /* best effort */ }
    }

    private const int AttachParentProcess = -1;

    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int dwProcessId);
}
