using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ExtrabbitCode.Inventor.MetaReader.App;

/// <summary>The sample files shipped with the app. The whole sample folder is copied out of the
/// (read-only, packaged) install folder into a writable user folder the first time it's needed, so
/// the app can open any of them and Inventor can read all their parts when generating a 3D view.</summary>
internal static class SampleFiles
{
    private const string Folder = "SampleBg";
    private const string Assembly = "SampleBg.iam";

    /// <summary>One entry in the Home "Try a sample" gallery: a bundled file and the capability it
    /// shows off.</summary>
    internal sealed record Sample(string FileName, string Title, string Blurb, InventorDocument.DocKind Kind);

    /// <summary>Curated samples, each highlighting a distinct capability, shown on the start page.</summary>
    public static readonly IReadOnlyList<Sample> Gallery =
    [
        new("SampleBg.iam", "Assembly", "Nested subassemblies, the full reference graph and a 3D view.", InventorDocument.DocKind.Assembly),
        new("SamplePart.ipt", "Part with model states", "Three model states, each with its own iProperties, plus a 3D view.", InventorDocument.DocKind.Part),
        new("SampleBg.idw", "Drawing", "A drawing linked back to the model it documents.", InventorDocument.DocKind.Drawing),
        new("iPartSample.ipt", "iPart factory", "A table-driven family of part variants.", InventorDocument.DocKind.Part),
        new("iAssemblyFactory.iam", "iAssembly factory", "A table-driven family of assemblies.", InventorDocument.DocKind.Assembly),
        new("TubeAndPipe.ipt", "Tube & Pipe", "A routed part, categorised from its metadata.", InventorDocument.DocKind.Part),
    ];

    /// <summary>Extracts the bundled sample folder to a writable location on first use and returns it.
    /// Null if the samples aren't bundled.</summary>
    public static string? EnsureSampleFolder()
    {
        try
        {
            string src = Path.Combine(AppContext.BaseDirectory, "Assets", "SampleFiles", Folder);
            if (!File.Exists(Path.Combine(src, Assembly))) { return null; }

            string dst = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ExtrabbitCode.Inventor.MetaReader", "samples", Folder);

            if (!File.Exists(Path.Combine(dst, Assembly))) { CopyDir(src, dst); }
            return Directory.Exists(dst) ? dst : (Directory.Exists(src) ? src : null);
        }
        catch { return null; }
    }

    /// <summary>Resolves a bundled sample to an openable path, extracting the folder on first use.
    /// Null if it isn't available.</summary>
    public static string? Resolve(string fileName)
    {
        string? folder = EnsureSampleFolder();
        if (folder == null) { return null; }
        string path = Path.Combine(folder, fileName);
        return File.Exists(path) ? path : null;
    }

    /// <summary>Path to the sample assembly (used by the welcome tip), extracting on first use.</summary>
    public static string? EnsureSampleAssembly() => Resolve(Assembly);

    /// <summary>The gallery samples that are actually present on disk, resolved to full paths.</summary>
    public static IEnumerable<(Sample Sample, string Path)> AvailableSamples() =>
        Gallery.Select(s => (s, Resolve(s.FileName)))
               .Where(t => t.Item2 != null)
               .Select(t => (t.s, t.Item2!));

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
