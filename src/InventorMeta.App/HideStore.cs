using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.Messaging;

namespace ExtrabbitCode.Inventor.MetaReader.App;

/// <summary>Broadcast when any hide/show state changes; views rebuild in response.</summary>
internal sealed record HideChangedMessage(string Key);

/// <summary>
/// Tracks which tabs / property sets / individual properties are hidden.
/// Defaults (File Structure tab, internal property sets) apply as rules; the user's
/// explicit hide/show choices override them and persist via <see cref="AppSettings"/>.
/// Keys: "tab:&lt;name&gt;", "set:&lt;set&gt;", "prop:&lt;set&gt;#&lt;pid&gt;".
/// </summary>
internal static class HideStore
{
    private static readonly HashSet<string> On  = Parse("hide.on");   // forced hidden
    private static readonly HashSet<string> Off = Parse("hide.off");  // forced shown (overrides a default)

    private static HashSet<string> Parse(string key) =>
        new((AppSettings.Get(key) ?? "").Split('|', StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal);

    public static string TabKey(string name) => "tab:" + name;
    public static string SetKey(string set) => "set:" + set;
    public static string PropKey(string set, uint pid) => $"prop:{set}#{pid}";

    public static bool IsHidden(string key) =>
        Off.Contains(key) ? false
        : On.Contains(key) ? true
        : IsDefaultHidden(key);

    private static bool IsDefaultHidden(string key) =>
        key == "tab:File Structure" ||
        (key.StartsWith("set:", StringComparison.Ordinal) && key.Contains("(internal)"));

    public static void Set(string key, bool hidden)
    {
        On.Remove(key);
        Off.Remove(key);
        if (hidden != IsDefaultHidden(key))
        {
            (hidden ? On : Off).Add(key);
        }

        AppSettings.SetMany(("hide.on", string.Join('|', On)), ("hide.off", string.Join('|', Off)));
        WeakReferenceMessenger.Default.Send(new HideChangedMessage(key));
    }
}
