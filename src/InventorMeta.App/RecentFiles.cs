using System;
using System.Collections.Generic;
using System.Linq;

namespace ExtrabbitCode.Inventor.MetaReader.App;

/// <summary>The most-recently-opened files, newest first, persisted as a capped list. Paths are joined
/// by '|', which is illegal in Windows paths and so is a safe separator.</summary>
internal static class RecentFiles
{
    private const string Key = "recent.files";
    private const string CollapsedKey = "recent.collapsed";
    private const int Max = 6;

    /// <summary>Whether the recent-files card is shown as a thin collapsed bar.</summary>
    public static bool Collapsed
    {
        get => bool.TryParse(AppSettings.Get(CollapsedKey), out bool v) && v;
        set => AppSettings.Set(CollapsedKey, value.ToString());
    }

    public static List<string> Get()
    {
        string? v = AppSettings.Get(Key);
        return string.IsNullOrEmpty(v)
            ? []
            : [.. v.Split('|', StringSplitOptions.RemoveEmptyEntries)];
    }

    /// <summary>Records a freshly opened file, moving it to the top and trimming the tail.</summary>
    public static void Add(string path)
    {
        List<string> list = Get();
        list.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        list.Insert(0, path);
        if (list.Count > Max) { list = [.. list.Take(Max)]; }
        Save(list);
    }

    public static void Remove(string path)
    {
        List<string> list = Get();
        if (list.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase)) > 0) { Save(list); }
    }

    public static void Clear() => AppSettings.Set(Key, "");

    private static void Save(List<string> list) => AppSettings.Set(Key, string.Join('|', list));
}
