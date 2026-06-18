using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.UI.Xaml;

namespace InventorMeta.App;

public partial class App
{
    public static Window MainWindowInstance { get; private set; } = null!;

    /// <summary>Every open top-level window (main + torn-out tab windows).</summary>
    public static List<Window> ActiveWindows { get; } = [];

    public App() { InitializeComponent(); }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        UnhandledException += OnUnhandledException;
        MainWindowInstance = new MainWindow();
        MainWindowInstance.Activate();
    }

    /// <summary>Append any unhandled UI-thread exception to a crash log for diagnosis.</summary>
    private static void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
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
