using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace InventorMeta.App;

/// <summary>
/// On-disk cache of generated SVF viewables. A model maps to an entry keyed by the SHA-256 of its
/// bytes, so the same model yields the same key on any machine - which lets a shared network store
/// dedupe viewables across users/PCs. Each entry lives under &lt;root&gt;\&lt;key&gt;\output\
/// (bubble.json + the SVF resources, the layout the LMV viewer loads).
/// </summary>
public sealed class SvfStore
{
    /// <summary>Per-machine cache under %LocalAppData%.</summary>
    public string LocalRoot { get; }

    /// <summary>Optional shared store (a network path); when set, it's searched and written first.</summary>
    public string? NetworkRoot { get; }

    public SvfStore(string? networkRoot)
    {
        LocalRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ExtrabbitCode.Inventor.MetaReader", "svf-cache");
        NetworkRoot = string.IsNullOrWhiteSpace(networkRoot) ? null : networkRoot.Trim();
    }

    /// <summary>Where freshly generated viewables are written (the shared store if configured).</summary>
    public string PrimaryRoot => NetworkRoot ?? LocalRoot;

    /// <summary>SHA-256 of the file's bytes as lowercase hex - the cache key for that exact model.</summary>
    public static string ComputeKey(string filePath)
    {
        using FileStream fs = File.OpenRead(filePath);
        return Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
    }

    public string EntryDir(string key) => Path.Combine(PrimaryRoot, key);

    /// <summary>The output folder a generator should write into for <paramref name="key"/>.</summary>
    public string OutputDir(string key) => Path.Combine(EntryDir(key), "output");

    /// <summary>The viewer manifest for a key (at the entry root, alongside the output\ folder),
    /// searching the network store then the local one; null if not cached anywhere.</summary>
    public string? FindBubble(string key)
    {
        foreach (string root in Roots())
        {
            string bubble = Path.Combine(root, key, "bubble.json");
            if (File.Exists(bubble)) { return bubble; }
        }

        return null;
    }

    public bool Has(string key) => FindBubble(key) != null;

    /// <summary>Deletes the LOCAL cache only - never the shared network store (that would affect
    /// every user). Returns the number of bytes freed.</summary>
    public long ClearLocal()
    {
        long freed = DirSize(LocalRoot);
        try
        {
            if (Directory.Exists(LocalRoot)) { Directory.Delete(LocalRoot, recursive: true); }
        }
        catch { return 0; }

        return freed;
    }

    public long LocalSizeBytes() => DirSize(LocalRoot);

    private IEnumerable<string> Roots()
    {
        if (NetworkRoot != null) { yield return NetworkRoot; }
        yield return LocalRoot;
    }

    private static long DirSize(string dir)
    {
        try
        {
            return Directory.Exists(dir)
                ? new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length)
                : 0;
        }
        catch { return 0; }
    }
}
