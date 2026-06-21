using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Serilog;

namespace InventorMeta.App;

/// <summary>App-wide logging: Serilog writing to a daily rolling file under %LocalAppData%.
/// Call <see cref="Init"/> once at startup, then log through Serilog's static
/// <c>Log</c> (Log.Information / Log.Warning / Log.Error).</summary>
internal static class AppLog
{
    public static string Directory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ExtrabbitCode.Inventor.MetaReader", "logs");

    public static void Init()
    {
        try { System.IO.Directory.CreateDirectory(Directory); } catch { /* best effort */ }

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.File(
                Path.Combine(Directory, "app-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 10,
                shared: true,   // allow Notepad to read it while we write
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();
    }

    /// <summary>Opens the most recent log file (or the logs folder if none) in the default handler.</summary>
    public static void OpenLatest()
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            FileInfo? latest = new DirectoryInfo(Directory)
                .GetFiles("*.log").OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault();
            Process.Start(new ProcessStartInfo { FileName = latest?.FullName ?? Directory, UseShellExecute = true });
        }
        catch { /* best effort */ }
    }
}
