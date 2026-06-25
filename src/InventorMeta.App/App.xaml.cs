using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
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
        // Catch crashes from every source, not just the UI thread: a background-thread throw or a
        // faulted fire-and-forget Task would otherwise terminate the app without being logged.
        UnhandledException += (_, e) => LogCrash("UI thread", e.Exception, e.Message);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            Exception? ex = e.ExceptionObject as Exception;
            LogCrash("background thread", ex, ex?.Message ?? "non-CLR error");
            if (e.IsTerminating) { try { Serilog.Log.CloseAndFlush(); } catch { /* dying anyway */ } }
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            LogCrash("unobserved Task", e.Exception, e.Exception?.Message ?? "");
            e.SetObserved();   // we've logged it; don't let it escalate
        };
        AppLog.Init();
        Analytics.Init();
        Serilog.Log.Information("Inventor MetaReader starting (v{Version})",
            typeof(App).Assembly.GetName().Version?.ToString() ?? "?");

        // Headless docs mode: "--shoot-docs <outDir> [--samples <dir>] [--model <assembly.iam>]"
        // renders one PNG per view (light + dark) and exits, without ever showing the normal app.
        string[] cli = Environment.GetCommandLineArgs();
        int shootIndex = Array.IndexOf(cli, "--shoot-docs");
        if (shootIndex >= 0 && shootIndex + 1 < cli.Length)
        {
            ShootMode = true;
            AppSettings.Ephemeral = true; // clean default settings; never touch the user's file
            int samplesIndex = Array.IndexOf(cli, "--samples");
            string? samples = samplesIndex >= 0 && samplesIndex + 1 < cli.Length
                ? cli[samplesIndex + 1] : null;
            int modelIndex = Array.IndexOf(cli, "--model");
            string? model = modelIndex >= 0 && modelIndex + 1 < cli.Length
                ? cli[modelIndex + 1] : null;
            _ = DocShooter.RunAsync(cli[shootIndex + 1], samples, model);
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

    /// <summary>Logs an unhandled exception from any source (UI thread, background thread, or an
    /// unobserved Task) to the app log, and reports an anonymous crash event.</summary>
    private static void LogCrash(string source, Exception? ex, string message)
    {
        Serilog.Log.Error(ex, "Unhandled exception ({Source}): {Message}", source, message);

        // Anonymous crash signal (opt-in only): just where it happened and the exception type -
        // never the message, stack or any path, per the analytics privacy contract.
        try
        {
            Analytics.CaptureBlocking("app_crashed", new Dictionary<string, object?>
            {
                ["source"] = source,
                ["exception_type"] = ex?.GetType().FullName ?? "unknown",
            });
        }
        catch { /* best-effort */ }
    }

    /// <summary>The window currently hosting the given element, across all (torn-out) windows.</summary>
    public static Window? GetWindowForElement(UIElement element) =>
        element.XamlRoot is { } root
            ? ActiveWindows.FirstOrDefault(w => w.Content?.XamlRoot == root)
            : null;
}
