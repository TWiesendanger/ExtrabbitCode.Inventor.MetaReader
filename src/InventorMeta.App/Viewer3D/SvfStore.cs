using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace ExtrabbitCode.Inventor.MetaReader.App;

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

    /// <summary>Redline markup for a viewable is persisted next to it in the cache entry.</summary>
    public const string RedlineLayersFile = "redline-layers.json";

    public string EntryDir(string key) => Path.Combine(PrimaryRoot, key);

    /// <summary>Path to the redline markup file for a cache entry.</summary>
    public string RedlineLayersPath(string key) => Path.Combine(EntryDir(key), RedlineLayersFile);

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

    /// <summary>The raw SVF the built-in (best effort) converter writes for a key - such entries have
    /// output\0.svf but no bubble.json manifest; null if not cached anywhere.</summary>
    public string? FindLocalSvf(string key)
    {
        foreach (string root in Roots())
        {
            string svf = Path.Combine(root, key, "output", "0.svf");
            if (File.Exists(svf)) { return svf; }
        }

        return null;
    }

    public bool Has(string key) => FindBubble(key) != null || FindLocalSvf(key) != null;

    /// <summary>Deletes the LOCAL cache only - never the shared network store (that would affect
    /// every user). Returns the number of bytes freed.</summary>
    public long ClearLocal()
    {
        long freed = DirSize(LocalRoot);
        try
        {
            if (Directory.Exists(LocalRoot)) { Directory.Delete(LocalRoot, recursive: true); }
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Clearing the local SVF cache at {Dir} failed", LocalRoot);
            return 0;
        }

        Serilog.Log.Information("Local SVF cache cleared ({Freed:N0} B at {Dir})", freed, LocalRoot);
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
