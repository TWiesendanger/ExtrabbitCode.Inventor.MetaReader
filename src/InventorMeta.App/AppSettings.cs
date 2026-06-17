using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace InventorMeta.App
{
    /// <summary>Tiny key=value settings store in %LocalAppData% (theme, window bounds, …).</summary>
    internal static class AppSettings
    {
        private static readonly string FilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ExtrabbitCode.Inventor.MetaReader", "settings.ini");

        private static Dictionary<string, string>? _cache;

        private static Dictionary<string, string> Map()
        {
            if (_cache != null) return _cache;
            _cache = new(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var line in File.ReadAllLines(FilePath))
                {
                    int eq = line.IndexOf('=');
                    if (eq > 0) _cache[line[..eq].Trim()] = line[(eq + 1)..].Trim();
                }
            }
            catch { /* first run / unreadable */ }
            return _cache;
        }

        public static string? Get(string key) => Map().TryGetValue(key, out var v) ? v : null;
        public static int GetInt(string key, int fallback) => int.TryParse(Get(key), out var v) ? v : fallback;

        public static void Set(string key, string value)
        {
            Map()[key] = value;
            Flush();
        }

        public static void SetMany(params (string key, string value)[] pairs)
        {
            foreach (var (k, v) in pairs) Map()[k] = v;
            Flush();
        }

        private static void Flush()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.WriteAllLines(FilePath, Map().Select(kv => $"{kv.Key}={kv.Value}"));
            }
            catch { /* best-effort */ }
        }
    }
}
