using System;
using System.Reflection;

namespace InventorMeta.App;

/// <summary>Central app metadata for the title bar and the About dialog.</summary>
internal static class AppInfo
{
    public const string Name = "ExtrabbitCode.Inventor.MetaReader";
    public const string Tagline = "Read .ipt / .iam / .idw /.ipn metadata without Autodesk Inventor";
    public const string Description =
        "Inspect Autodesk Inventor part, assembly and drawing metadata - iProperties, " +
        "references, thumbnails and per-model-state values - straight from the file, " +
        "without Autodesk Inventor installed.";

    public const string GitHubUrl = "https://github.com/TWiesendanger/ExtrabbitCode.Inventor.MetaReader";
    public const string DocsUrl   = "https://github.com/TWiesendanger/ExtrabbitCode.Inventor.MetaReader/blob/master/docs/INVENTOR-FILE-FORMAT.md";

    public static string Version
    {
        get
        {
            Version? v = Assembly.GetExecutingAssembly().GetName().Version;
            return v is null ? "1.0.0.0" : $"{v.Major}.{v.Minor}.{v.Build}.{v.Revision}";
        }
    }

    /// <summary>Newest first.</summary>
    public static readonly (string Version, string[] Notes)[] History =
    [
        ("1.0.0.0", [
            "First release.",
            "Reads document type, all iProperties, custom properties, thumbnail, referenced files and version info from .ipt / .iam / .idw / .ipn.",
            "Per-model-state iProperties with a side-by-side comparison of what differs between states."
        ])
    ];
}