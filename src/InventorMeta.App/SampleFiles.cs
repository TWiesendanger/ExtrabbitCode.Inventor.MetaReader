using System;
using System.IO;

namespace ExtrabbitCode.Inventor.MetaReader.App;

/// <summary>The sample assembly shipped with the app. It is copied out of the (read-only, packaged)
/// install folder into a writable user folder the first time it's needed, so the app can open it and
/// Inventor can read all its parts when generating a 3D view.</summary>
internal static class SampleFiles
{
    private const string Folder = "SampleBg";
    private const string Assembly = "SampleBg.iam";

    /// <summary>Path to the sample assembly, extracting it on first use. Null if it isn't bundled.</summary>
    public static string? EnsureSampleAssembly()
    {
        try
        {
            string src = Path.Combine(AppContext.BaseDirectory, "Assets", "SampleFiles", Folder);
            if (!File.Exists(Path.Combine(src, Assembly))) { return null; }

            string dst = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ExtrabbitCode.Inventor.MetaReader", "samples", Folder);
            string dstAssembly = Path.Combine(dst, Assembly);

            if (!File.Exists(dstAssembly)) { CopyDir(src, dst); }
            return File.Exists(dstAssembly) ? dstAssembly : Path.Combine(src, Assembly);
        }
        catch { return null; }
    }

    private static void CopyDir(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (string f in Directory.EnumerateFiles(src))
        {
            File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), overwrite: true);
        }
        foreach (string d in Directory.EnumerateDirectories(src))
        {
            CopyDir(d, Path.Combine(dst, Path.GetFileName(d)));
        }
    }
}
