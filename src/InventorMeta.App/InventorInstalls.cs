using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace ExtrabbitCode.Inventor.MetaReader.App;

/// <summary>An installed Autodesk Inventor release - the year and the Inventor.exe that drives it
/// over COM. Every release ships the SVF/collaboration translator add-in used to generate viewables.</summary>
public sealed record InventorInstall(int Year, string ExePath)
{
    public string DisplayName => $"Inventor {Year}";
}

/// <summary>Discovers the Inventor releases installed on this machine.</summary>
public static partial class InventorInstalls
{
    /// <summary>Installed releases, newest first. Empty when Inventor isn't installed (viewing
    /// cached SVFs still works; generating new ones does not).</summary>
    public static IReadOnlyList<InventorInstall> Detect()
    {
        Dictionary<int, InventorInstall> byYear = [];

        foreach (Environment.SpecialFolder sf in new[]
                 { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
        {
            string adsk = Path.Combine(Environment.GetFolderPath(sf), "Autodesk");
            foreach (string dir in SafeDirs(adsk, "Inventor 20*"))
            {
                Match m = YearInName().Match(Path.GetFileName(dir));
                if (!m.Success || !int.TryParse(m.Value, out int year)) { continue; }

                string exe = Path.Combine(dir, "Bin", "Inventor.exe");
                if (File.Exists(exe)) { byYear[year] = new InventorInstall(year, exe); }
            }
        }

        return byYear.Values.OrderByDescending(i => i.Year).ToList();
    }

    private static IEnumerable<string> SafeDirs(string root, string pattern)
    {
        try { return Directory.Exists(root) ? Directory.GetDirectories(root, pattern) : []; }
        catch { return []; }
    }

    [GeneratedRegex(@"20\d{2}")]
    private static partial Regex YearInName();
}
