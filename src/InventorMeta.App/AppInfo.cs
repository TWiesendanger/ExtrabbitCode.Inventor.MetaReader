using System;
using System.Reflection;

namespace ExtrabbitCode.Inventor.MetaReader.App;

/// <summary>Central app metadata for the title bar and the About dialog.</summary>
internal static class AppInfo
{
    public const string Name = "ExtrabbitCode.Inventor.MetaReader";
    public const string Tagline = "Read .ipt / .iam / .idw / .ipn / .stp metadata without Autodesk Inventor";
    public const string Description =
        "Inspect Autodesk Inventor and STEP metadata - iProperties, " +
        "references, thumbnails, per-model-state values and neutral CAD headers - straight from the file, " +
        "without Autodesk Inventor installed.";

    public const string ExtrabbitUrl = "https://extrabbitcode.com";
    public const string PrivacyUrl = "https://metareader.extrabbitcode.com/docs/privacy";
    public const string GitHubUrl = "https://github.com/TWiesendanger/ExtrabbitCode.Inventor.MetaReader";
    public const string DocsUrl   = "https://metareader.extrabbitcode.com/";

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
        ("1.2.0.0", [
            "STEP file support (.stp/.step) - header metadata and the 3D model, in the app and the CLI.",
            "Redlining: draw on the 3D model - freehand, shapes, text and an eraser, organized in layers with per-layer camera poses, saved with the cached viewable.",
            "3D paint: strokes that stick to the model surface and stay put while you orbit.",
            "Built-in 3D engine: best-effort viewables for parts and assemblies without Inventor installed.",
            "Body coloring in the 3D viewer - a colour per body, one click (or key) to toggle.",
            "Rebindable keyboard shortcuts with a viewer shortcuts window.",
            "A richer sample gallery on the start page, including STEP samples."
        ]),
        ("1.1.0.0", [
            "Interactive reference graph with three layouts (Left-Right, Top-Bottom and an organic Network), node thumbnails, expand/collapse, fit and fullscreen.",
            "Home tab with a recent-files list, always one click away.",
            "Colour-coded document categories - Content Center, Piping, Frame Generator, Weldment, Sheet Metal, iPart/iAssembly and more.",
            "A bundled sample assembly to open straight from the welcome screen.",
            "3D generation now runs Inventor hidden and silent by default.",
            "In-app tips, close-other / close-all tabs, and a diagnostics log in Settings."
        ]),
        ("1.0.0.0", [
            "First release.",
            "Reads document type, all iProperties, custom properties, thumbnail, referenced files and version info from .ipt / .iam / .idw / .ipn.",
            "Per-model-state iProperties with a side-by-side comparison of what differs between states."
        ])
    ];
}
