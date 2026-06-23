namespace ExtrabbitCode.Inventor.MetaReader;

/// <summary>
/// Path helpers for the paths stored <em>inside</em> Inventor files. Inventor always records
/// references with Windows-style backslash separators, regardless of the host OS. System.IO.Path
/// only treats the current platform's separator as a divider, so on Linux/macOS it returns the
/// whole string for such a path (no '/' to split on). These helpers split on both separators so
/// file-name extraction works the same on every OS - which the cross-platform core relies on.
/// </summary>
public static class InventorPath
{
    private static readonly char[] Separators = ['/', '\\'];

    /// <summary>The final segment (file name) of a stored Inventor path, treating both '/' and
    /// '\' as separators on every OS. Returns the input unchanged when it has no separator.</summary>
    public static string GetFileName(string? path)
    {
        if (string.IsNullOrEmpty(path)) { return ""; }
        int i = path.LastIndexOfAny(Separators);
        return i >= 0 ? path[(i + 1)..] : path;
    }
}
