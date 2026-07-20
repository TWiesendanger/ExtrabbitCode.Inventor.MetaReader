using System;
using System.Collections.Generic;

namespace ExtrabbitCode.Inventor.MetaReader.App.Onboarding;

/// <summary>Persisted state for the in-app tips: a global on/off switch and the set of tips the user
/// has turned off with "Don't show again".</summary>
internal static class TipSettings
{
    private const string EnabledKey = "tips.enabled";
    private const string DismissedKey = "tips.dismissed";

    /// <summary>Whether tips may be shown at all. Default on.</summary>
    public static bool Enabled
    {
        get => !bool.TryParse(AppSettings.Get(EnabledKey), out bool v) || v;
        set => AppSettings.Set(EnabledKey, value.ToString());
    }

    public static bool IsDismissed(string id) => Dismissed().Contains(id);

    /// <summary>Permanently stop showing the given tip ("Don't show again").</summary>
    public static void Dismiss(string id)
    {
        HashSet<string> set = Dismissed();
        if (set.Add(id)) { AppSettings.Set(DismissedKey, string.Join('|', set)); }
    }

    /// <summary>Clear all "Don't show again" choices so dismissed tips can appear again.</summary>
    public static void Reset() => AppSettings.Set(DismissedKey, "");

    private static HashSet<string> Dismissed()
    {
        string? v = AppSettings.Get(DismissedKey);
        return string.IsNullOrEmpty(v)
            ? new(StringComparer.Ordinal)
            : new(v.Split('|', StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal);
    }
}
