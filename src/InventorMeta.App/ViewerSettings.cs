namespace InventorMeta.App;

/// <summary>Persisted settings for the 3D viewer: the shared SVF network store and the Inventor
/// release used to generate viewables.</summary>
internal static class ViewerSettings
{
    private const string NetworkKey = "viewer.networkPath";
    private const string YearKey = "viewer.inventorYear";

    /// <summary>Optional shared cache path; null/empty means local-only.</summary>
    public static string? NetworkPath
    {
        get
        {
            string? v = AppSettings.Get(NetworkKey);
            return string.IsNullOrWhiteSpace(v) ? null : v;
        }
        set => AppSettings.Set(NetworkKey, value ?? "");
    }

    /// <summary>The Inventor release year selected for generation (0 = ask each time).</summary>
    public static int InventorYear
    {
        get => AppSettings.GetInt(YearKey, 0);
        set => AppSettings.Set(YearKey, value.ToString());
    }
}
