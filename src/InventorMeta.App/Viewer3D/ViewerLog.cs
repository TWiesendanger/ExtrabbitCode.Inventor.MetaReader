using Serilog;

namespace ExtrabbitCode.Inventor.MetaReader.App.Viewer3D;

/// <summary>Routes 3D-viewer diagnostics (JS messages and failed fetches) into the app log at Debug
/// level. A thin shim over Serilog so the viewer code stays unchanged.</summary>
internal static class ViewerLog
{
    public static void Write(string message) => Log.Debug("viewer: {Message}", message);

    public static void Clear() { /* nothing to clear with the rolling app log */ }
}
