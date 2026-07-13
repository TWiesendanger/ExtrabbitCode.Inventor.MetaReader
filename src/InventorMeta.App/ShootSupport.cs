using System.IO;

namespace ExtrabbitCode.Inventor.MetaReader.App;

/// <summary>Helpers shared by the two dev-tooling drivers - the documentation snapshotter
/// (<see cref="DocShooter"/>) and the demo-tour recorder (<see cref="DemoTour"/>).</summary>
internal static class ShootSupport
{
    /// <summary>Resolves a bundled sample under a subfolder to a full path; null if the samples
    /// directory is unset or the file is absent.</summary>
    public static string? Sample(string? dir, string sub, string name)
    {
        if (dir == null) { return null; }
        string path = Path.Combine(dir, sub, name);
        return File.Exists(path) ? path : null;
    }

    /// <summary>Deletes any saved redline markup for a model so a fresh shoot/tour starts clean.
    /// A no-op when the model has no cache entry yet.</summary>
    public static void ClearRedlineMarkup(string modelPath)
    {
        try
        {
            SvfStore store = new(ViewerSettings.NetworkPath);
            string marks = store.RedlineLayersPath(SvfStore.ComputeKey(modelPath));
            if (File.Exists(marks)) { File.Delete(marks); }
        }
        catch { /* no cache entry yet - nothing to clear */ }
    }

    /// <summary>Page probe returning "1" once the LMV model has loaded and the Extrabbit toolbar
    /// group exists - polled before capturing the 3D viewer.</summary>
    public const string ViewerReadyJs =
        "(function(){try{return (window.NOP_VIEWER && NOP_VIEWER.model && NOP_VIEWER.model.isLoadDone() && document.getElementById('extrabbit-group')) ? '1' : '0';}catch(e){return '0';}})()";
}
