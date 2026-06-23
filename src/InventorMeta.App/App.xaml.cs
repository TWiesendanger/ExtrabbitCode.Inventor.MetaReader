using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.UI.Xaml;

namespace ExtrabbitCode.Inventor.MetaReader.App;

public partial class App
{
    public static Window MainWindowInstance { get; private set; } = null!;

    /// <summary>Every open top-level window (main + torn-out tab windows).</summary>
    public static List<Window> ActiveWindows { get; } = [];

    /// <summary>True while the documentation snapshotter is running; suppresses persisting
    /// window bounds so generating screenshots never overwrites the user's saved layout.</summary>
    public static bool ShootMode { get; private set; }

    public App() { InitializeComponent(); }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        UnhandledException += OnUnhandledException;
        AppLog.Init();
        Serilog.Log.Information("Inventor MetaReader starting (v{Version})",
            typeof(App).Assembly.GetName().Version?.ToString() ?? "?");

        // Headless docs mode: "--shoot-docs <outDir> [--samples <dir>]" renders one PNG per view
        // (light + dark) and exits, without ever showing the normal app.
        string[] cli = Environment.GetCommandLineArgs();
        int shootIndex = Array.IndexOf(cli, "--shoot-docs");
        if (shootIndex >= 0 && shootIndex + 1 < cli.Length)
        {
            ShootMode = true;
            AppSettings.Ephemeral = true; // clean default settings; never touch the user's file
            int samplesIndex = Array.IndexOf(cli, "--samples");
            string? samples = samplesIndex >= 0 && samplesIndex + 1 < cli.Length
                ? cli[samplesIndex + 1] : null;
            _ = DocShooter.RunAsync(cli[shootIndex + 1], samples);
            return;
        }

        // Headless 3D test: "--gen-svf <file> [--inv-year N]" detects Inventor, computes the cache
        // key, generates the SVF into the store, prints the result to the console, and exits.
        if (Array.IndexOf(cli, "--gen-svf") >= 0)
        {
            SvfTestRunner.Run(cli);
            return;
        }

        MainWindowInstance = new MainWindow();
        MainWindowInstance.Activate();
    }

    /// <summary>Log any unhandled UI-thread exception (also to a crash.log for the launcher to check).</summary>
    private static void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        Serilog.Log.Error(e.Exception, "Unhandled UI-thread exception: {Message}", e.Message);
        try
        {
            string path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ExtrabbitCode.Inventor.MetaReader", "crash.log");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllText(path, $"=== {DateTime.Now:O} ===\n{e.Message}\n{e.Exception}\n\n");
        }
        catch { /* best-effort */ }
    }

    /// <summary>The window currently hosting the given element, across all (torn-out) windows.</summary>
    public static Window? GetWindowForElement(UIElement element) =>
        element.XamlRoot is { } root
            ? ActiveWindows.FirstOrDefault(w => w.Content?.XamlRoot == root)
            : null;
}
