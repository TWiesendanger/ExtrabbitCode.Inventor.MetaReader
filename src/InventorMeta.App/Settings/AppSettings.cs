using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace ExtrabbitCode.Inventor.MetaReader.App.Settings;

/// <summary>Tiny key/value settings store persisted as JSON in %LocalAppData% (theme, window
/// bounds, sidebar layout, 3D-viewer options, …).</summary>
internal static class AppSettings
{
    private static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ExtrabbitCode.Inventor.MetaReader");
    private static readonly string FilePath = Path.Combine(Dir, "settings.json");
    private static readonly string LegacyIniPath = Path.Combine(Dir, "settings.ini");
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private static Dictionary<string, string>? _cache;
    private static bool _wasFreshInstall;

    /// <summary>When true (documentation snapshotter), settings live only in memory: the file is
    /// neither read nor written, so screenshots use clean defaults and the run never disturbs the
    /// user's saved layout. Set before any settings access.</summary>
    internal static bool Ephemeral { get; set; }

    /// <summary>True when no settings file existed at first load - the app has never run on this
    /// machine before. Captured the first time the store is touched (before any first-run write
    /// creates the file), so it stays valid for the whole session regardless of what gets written
    /// afterwards. Lets first-launch UI tell a fresh install from an update.</summary>
    public static bool IsFirstRun { get { Map(); return _wasFreshInstall; } }

    private static Dictionary<string, string> Map()
    {
        if (_cache != null)
        {
            return _cache;
        }

        _cache = new(StringComparer.OrdinalIgnoreCase);
        if (Ephemeral)
        {
            return _cache;
        }

        // captured before the try loads anything and before any Set() can Flush a file into place
        _wasFreshInstall = !File.Exists(FilePath) && !File.Exists(LegacyIniPath);

        try
        {
            if (File.Exists(FilePath))
            {
                Dictionary<string, string>? loaded =
                    JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(FilePath));
                if (loaded != null)
                {
                    foreach (KeyValuePair<string, string> kv in loaded) { _cache[kv.Key] = kv.Value; }
                }
            }
            else if (File.Exists(LegacyIniPath))
            {
                // one-time migration from the old key=value .ini, then drop it
                foreach (string line in File.ReadAllLines(LegacyIniPath))
                {
                    int eq = line.IndexOf('=');
                    if (eq > 0) { _cache[line[..eq].Trim()] = line[(eq + 1)..].Trim(); }
                }
                Flush();
                try { File.Delete(LegacyIniPath); } catch { /* leave it */ }
            }
        }
        catch (Exception ex)
        {
            // first run has no file; anything else here means settings silently reset - log it
            if (File.Exists(FilePath) || File.Exists(LegacyIniPath))
            {
                Serilog.Log.Warning(ex, "Settings could not be read from {File} - continuing with defaults", FilePath);
            }
        }
        return _cache;
    }

    public static string? Get(string key) => Map().TryGetValue(key, out string? v) ? v : null;
    public static int GetInt(string key, int fallback) => int.TryParse(Get(key), out int v) ? v : fallback;
    public static bool GetBool(string key, bool fallback) => bool.TryParse(Get(key), out bool v) ? v : fallback;

    public static void Set(string key, string value)
    {
        Map()[key] = value;
        Flush();
    }

    public static void SetMany(params (string key, string value)[] pairs)
    {
        foreach ((string k, string v) in pairs)
        {
            Map()[k] = v;
        }

        Flush();
    }

    private static void Flush()
    {
        if (Ephemeral)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(Map(), JsonOpts));
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Settings could not be saved to {File}", FilePath);
        }
    }
}
