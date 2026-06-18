using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.Messaging;

namespace InventorMeta.App;

/// <summary>Broadcast when the left sidebar layout (properties / thumbnail) changes.</summary>
internal sealed record SidebarConfigChangedMessage;

/// <summary>
/// User-configurable layout of the left info sidebar: which properties are shown,
/// in what order, and whether the thumbnail is visible. Persists via <see cref="AppSettings"/>.
/// Property keys are "&lt;setId-guid&gt;#&lt;pid&gt;" so a choice is stable across files;
/// keys whose property is absent from the current file are kept but simply not rendered.
/// </summary>
internal static class SidebarConfig
{
    private const string DesignTracking = "32853f0f-3444-11d1-9e93-0060b03c1ca6";
    private const string SummaryInfo    = "f29f85e0-4ff9-1068-ab91-08002b27b3d9";

    /// <summary>Defaults mirror the core document Summary (see InventorDocument.BuildSummaryInto).</summary>
    private static readonly string[] DefaultKeys =
    [
        K(DesignTracking, 5),  K(DesignTracking, 29), K(DesignTracking, 41),
        K(DesignTracking, 42), K(DesignTracking, 43), K(DesignTracking, 7),
        K(DesignTracking, 20), K(DesignTracking, 55), K(DesignTracking, 30),
        K(DesignTracking, 36), K(DesignTracking, 40), K(DesignTracking, 4),
        K(DesignTracking, 58), K(DesignTracking, 60), K(DesignTracking, 59),
        K(DesignTracking, 61), K(DesignTracking, 67),
        K(SummaryInfo, 2),     K(SummaryInfo, 9),
    ];

    /// <summary>Stable key for a property set/pid pair.</summary>
    public static string K(Guid setId, uint pid) => setId.ToString().ToLowerInvariant() + "#" + pid;
    private static string K(string setId, uint pid) => setId + "#" + pid;

    public static bool ShowThumbnail
    {
        get => (AppSettings.Get("sidebar.thumb") ?? "1") != "0";
        set { AppSettings.Set("sidebar.thumb", value ? "1" : "0"); Changed(); }
    }

    /// <summary>The configured property keys in display order (defaults when never customised).</summary>
    public static List<string> Keys =>
        AppSettings.Get("sidebar.keys") is { } raw
            ? [.. raw.Split('|', StringSplitOptions.RemoveEmptyEntries)]
            : [.. DefaultKeys];

    public static void SetKeys(IEnumerable<string> keys)
    {
        AppSettings.Set("sidebar.keys", string.Join('|', keys));
        Changed();
    }

    public static void ResetDefaults()
    {
        AppSettings.SetMany(("sidebar.keys", string.Join('|', DefaultKeys)), ("sidebar.thumb", "1"));
        Changed();
    }

    private static void Changed() =>
        WeakReferenceMessenger.Default.Send(new SidebarConfigChangedMessage());
}
