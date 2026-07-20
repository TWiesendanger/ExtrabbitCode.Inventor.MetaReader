using System;

namespace ExtrabbitCode.Inventor.MetaReader.App;

/// <summary>How the reference graph arranges its nodes.</summary>
internal enum GraphLayout { LeftRight, TopDown, Network }

/// <summary>How the 3D viewer paints bodies: original materials, or a distinct colour per body.</summary>
internal enum ColoringMode { Default, Multicolor }

/// <summary>Which engine turns a model into an SVF viewable: Autodesk Inventor (exact, needs a local
/// installation) or the built-in reverse-engineered converter (fast, best effort). Ask = not chosen
/// yet; the user is asked the first time a viewable has to be generated.</summary>
internal enum SvfEngine { Ask, Inventor, Local }

/// <summary>Persisted settings for the 3D viewer: the shared SVF network store and the Inventor
/// release used to generate viewables.</summary>
internal static class ViewerSettings
{
    private const string NetworkKey = "viewer.networkPath";
    private const string YearKey = "viewer.inventorYear";
    private const string GraphLayoutKey = "viewer.graphLayout";
    private const string HideKey = "viewer.hideInventor";
    private const string SilentKey = "viewer.silentInventor";
    private const string ColoringKey = "viewer.coloringMode";
    private const string EngineKey = "viewer.svfEngine";
    private const string LocalInfoKey = "viewer.localEngineInfoShown";

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

    /// <summary>Default arrangement for the reference graph when a file opens.</summary>
    public static GraphLayout GraphLayout
    {
        get => Enum.TryParse(AppSettings.Get(GraphLayoutKey), out GraphLayout v) ? v : GraphLayout.LeftRight;
        set => AppSettings.Set(GraphLayoutKey, value.ToString());
    }

    /// <summary>Keep Inventor hidden when MetaReader launches it to generate a viewable. A session the
    /// user already had open is never touched. Default on.</summary>
    public static bool HideInventor
    {
        get => AppSettings.GetBool(HideKey, true);
        set => AppSettings.Set(HideKey, value.ToString());
    }

    /// <summary>Run Inventor silently (suppress its dialog prompts) while generating. Default on.</summary>
    public static bool SilentInventor
    {
        get => AppSettings.GetBool(SilentKey, true);
        set => AppSettings.Set(SilentKey, value.ToString());
    }

    /// <summary>The mode the 3D viewer opens in: original materials, or a colour per body. The viewer
    /// toolbar can still toggle it live; this is just the starting state.</summary>
    public static ColoringMode ColoringMode
    {
        get => Enum.TryParse(AppSettings.Get(ColoringKey), out ColoringMode v) ? v : ColoringMode.Default;
        set => AppSettings.Set(ColoringKey, value.ToString());
    }

    /// <summary>Which engine generates SVF viewables. Ask (default) prompts on first use when
    /// Inventor is installed; without Inventor the built-in converter is used regardless.</summary>
    public static SvfEngine Engine
    {
        get => Enum.TryParse(AppSettings.Get(EngineKey), out SvfEngine v) ? v : SvfEngine.Ask;
        set => AppSettings.Set(EngineKey, value.ToString());
    }

    /// <summary>Whether the one-time "best effort viewer" notice (shown when no Inventor is
    /// installed) has already been displayed.</summary>
    public static bool LocalEngineInfoShown
    {
        get => AppSettings.GetBool(LocalInfoKey, false);
        set => AppSettings.Set(LocalInfoKey, value.ToString());
    }
}
