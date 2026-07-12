using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ExtrabbitCode.Inventor.MetaReader.App;

/// <summary>The sample files shipped with the app. Sample folders are copied out of the (read-only,
/// packaged) install folder into a writable user folder when first needed, so the app can open any
/// of them and Inventor can read all their parts when generating a 3D view.</summary>
internal static class SampleFiles
{
    private const string InventorFolder = "SampleBg";
    private const string StepFolder = "SampleSteps";
    private const string Assembly = "SampleBg.iam";

    /// <summary>One entry in the Home "Try a sample" gallery: a bundled file and the capability it
    /// shows off.</summary>
    internal sealed record Sample(string FileName, string Title, string Blurb, InventorDocument.DocKind Kind,
        string Folder = InventorFolder);

    /// <summary>Curated samples, each highlighting a distinct capability, shown on the start page.</summary>
    public static readonly IReadOnlyList<Sample> Gallery =
    [
        new("SampleBg.iam", "Assembly", "Nested subassemblies, the full reference graph and a 3D view.", InventorDocument.DocKind.Assembly),
        new("SamplePart.ipt", "Part with model states", "Three model states, each with its own iProperties, plus a 3D view.", InventorDocument.DocKind.Part),
        new("SampleBg.idw", "Drawing", "A drawing linked back to the model it documents.", InventorDocument.DocKind.Drawing),
        new("iPartSample.ipt", "iPart factory", "A table-driven family of part variants.", InventorDocument.DocKind.Part),
        new("iAssemblyFactory.iam", "iAssembly factory", "A table-driven family of assemblies.", InventorDocument.DocKind.Assembly),
        new("TubeAndPipe.ipt", "Tube & Pipe", "A routed part, categorised from its metadata.", InventorDocument.DocKind.Part),
        new("Line Guide Drive Shaft.ipt", "Real-world part", "A steel part with material, mass and volume, and version history reaching back to Inventor 11 (2006).", InventorDocument.DocKind.Part),
        new("_Fishing Reel Assembly.iam", "Real-world assembly", "An Autodesk fishing-reel demo with nested subassemblies and 144 saved versions, from Inventor 11 (2006) to 2019.", InventorDocument.DocKind.Assembly),
        new("Line Guide Drive Shaft_203.stp", "STEP (AP203)", "A neutral CAD part in the classic CONFIG_CONTROL_DESIGN schema, with a 3D view.", InventorDocument.DocKind.Step, StepFolder),
        new("Line Guide Drive Shaft_214.step", "STEP (AP214)", "The same part as AUTOMOTIVE_DESIGN (AP214), using the .step extension.", InventorDocument.DocKind.Step, StepFolder),
        new("Line Guide Drive Shaft_242.stp", "STEP (AP242)", "The same part again in the modern AP242 schema.", InventorDocument.DocKind.Step, StepFolder),
    ];

    /// <summary>Extracts the bundled Inventor sample folder to a writable location and returns it.
    /// Null if the samples aren't bundled.</summary>
    public static string? EnsureSampleFolder() => EnsureFolder(InventorFolder);

    /// <summary>Resolves a bundled sample to an openable path, extracting its folder as needed.
    /// Null if it isn't available.</summary>
    public static string? Resolve(string fileName, string folder = InventorFolder)
    {
        string? root = EnsureFolder(folder);
        if (root == null) { return null; }
        string path = Path.Combine(root, fileName);
        return File.Exists(path) ? path : null;
    }

    /// <summary>Path to the sample assembly (used by the welcome tip), extracting on first use.</summary>
    public static string? EnsureSampleAssembly() => Resolve(Assembly);

    /// <summary>The gallery samples that are actually present on disk, resolved to full paths.</summary>
    public static IEnumerable<(Sample Sample, string Path)> AvailableSamples() =>
        Gallery.Select(s => (s, Resolve(s.FileName, s.Folder)))
               .Where(t => t.Item2 != null)
               .Select(t => (t.s, t.Item2!));

    /// <summary>Extracts one bundled sample folder to the writable samples location and returns it.
    /// Files a previous app version already extracted are kept; ones added since (new samples in an
    /// update) are copied in. Null if the folder isn't bundled.</summary>
    private static string? EnsureFolder(string folder)
    {
        try
        {
            string src = Path.Combine(AppContext.BaseDirectory, "Assets", "SampleFiles", folder);
            if (!Directory.Exists(src)) { return null; }

            string dst = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ExtrabbitCode.Inventor.MetaReader", "samples", folder);

            CopyMissing(src, dst);
            return Directory.Exists(dst) ? dst : src;
        }
        catch { return null; }
    }

    /// <summary>Copies files that don't exist at the destination yet; existing ones are left alone
    /// (Inventor may have touched them when generating a viewable). Cheap when nothing is missing.</summary>
    private static void CopyMissing(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (string f in Directory.EnumerateFiles(src))
        {
            string target = Path.Combine(dst, Path.GetFileName(f));
            if (!File.Exists(target)) { File.Copy(f, target); }
        }
        foreach (string d in Directory.EnumerateDirectories(src))
        {
            CopyMissing(d, Path.Combine(dst, Path.GetFileName(d)));
        }
    }
}
