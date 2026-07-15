using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace ExtrabbitCode.Inventor.MetaReader;

/// <summary>
/// Detects and repairs the classic "Error in reading RSe segment - the number of objects found
/// in the segment does not match the segment summary" damage that makes Inventor refuse to open
/// a file MetaReader reads without complaint.
///
/// Every RSeStorage segment stores its payload as blocks in the B&lt;id&gt; stream, an index over
/// those blocks in the M&lt;id&gt; stream, and a block-count summary in its /RSeStorage/RSeSegInfo
/// entry. On load, Inventor counts the blocks and hard-aborts the open when the RSeSegInfo
/// summary disagrees - a stale summary is all that is wrong with such files (observed after
/// external tooling rewrote the segment streams without refreshing the registry). The repair is
/// a same-size in-place patch of the summary values; no model data is touched, deleted or
/// recompressed, and freshly saved files satisfy summary == actual for every segment.
/// </summary>
public static class SegmentRepair
{
    /// <summary>One segment whose RSeSegInfo summary disagrees with the blocks actually present.</summary>
    public sealed class Issue
    {
        /// <summary>Segment type, e.g. "PmGraphicsSegment".</summary>
        public string SegmentName = "";
        /// <summary>Which RSeStorage tree the segment lives in: "" for the document itself, or the
        /// member-storage path for an embedded model-state member.</summary>
        public string Location = "";
        /// <summary>Block count the RSeSegInfo registry claims (what Inventor expects to find).</summary>
        public long SummaryCount;
        /// <summary>Block count actually present, per the segment's own M-stream block table.</summary>
        public long ActualCount;
        /// <summary>Byte offset of the summary u32 inside that tree's RSeSegInfo stream.</summary>
        internal int PatchOffset;
        /// <summary>Full path of the RSeSegInfo stream the patch belongs to.</summary>
        internal string SegInfoPath = "";
        public override string ToString() => $"{SegmentName}: summary {SummaryCount:N0} <> actual {ActualCount:N0}";
    }

    /// <summary>Outcome of <see cref="Repair"/>.</summary>
    public sealed class RepairResult
    {
        /// <summary>Untouched copy of the file, created next to it before patching
        /// (null when there was nothing to repair).</summary>
        public string? BackupPath;
        /// <summary>The mismatches that were patched (empty when the file was already consistent).</summary>
        public IReadOnlyList<Issue> Repaired = [];
    }

    /// <summary>Scan a document for segments whose registry summary disagrees with their actual
    /// block count. Empty for healthy files; parsing is strict, so unknown format variants yield
    /// no issues rather than false alarms.</summary>
    public static IReadOnlyList<Issue> FindIssues(string filePath)
    {
        using CompoundFile cf = new(filePath);
        return Scan(cf);
    }

    /// <summary>Repair every detected mismatch: copy the file to a .pre-repair.bak sibling, then
    /// patch the RSeSegInfo summaries in place (same-size write - nothing else in the container
    /// moves). Returns the backup path and what was patched; a healthy file is left untouched.</summary>
    public static RepairResult Repair(string filePath)
    {
        using CompoundFile cf = new(filePath);
        List<Issue> issues = Scan(cf);
        if (issues.Count == 0)
        {
            return new RepairResult();
        }

        string backup = MakeBackupPath(filePath);
        File.Copy(filePath, backup);
        try
        {
            // patch each RSeStorage tree's own registry (the document's, plus any model-state member's)
            foreach (IGrouping<string, Issue> perTree in issues.GroupBy(i => i.SegInfoPath, StringComparer.OrdinalIgnoreCase))
            {
                CompoundFile.DirEntry entry = cf.Directory.First(e => e.Type == 2 && string.Equals(e.Path, perTree.Key, StringComparison.OrdinalIgnoreCase));
                byte[] segInfo = cf.ReadEntry(entry);
                foreach (Issue issue in perTree)
                {
                    BitConverter.TryWriteBytes(segInfo.AsSpan(issue.PatchOffset, 4), (uint)issue.ActualCount);
                }
                cf.OverwriteStreamInPlace(entry, segInfo);
            }
            cf.Save(filePath);   // atomic swap: the file has either its old or its new content
        }
        catch
        {
            // the target is untouched on failure (Save is atomic), so the backup has no value -
            // don't leave orphaned .bak files behind (File.Copy carries over a ReadOnly attribute,
            // which would make the delete fail silently)
            try
            {
                File.SetAttributes(backup, FileAttributes.Normal);
                File.Delete(backup);
            }
            catch { /* best-effort */ }
            throw;
        }

        return new RepairResult { BackupPath = backup, Repaired = issues };
    }

    /// <summary>"file.ipt" -> "file.ipt.pre-repair.bak" (numbered when that already exists).</summary>
    private static string MakeBackupPath(string filePath)
    {
        string candidate = filePath + ".pre-repair.bak";
        for (int n = 2; File.Exists(candidate); n++)
        {
            candidate = $"{filePath}.pre-repair({n}).bak";
        }
        return candidate;
    }

    // ---- detection ----

    /// <summary>Per-segment block-count bookkeeping: what the segment's own block table holds and,
    /// when its registry entry was located, what the RSeSegInfo summary claims.</summary>
    internal sealed class SegmentCounts
    {
        public string Name = "";
        public string StorageId = "";      // the mangled id shared by the B<id>/M<id> stream pair
        public string Location = "";       // "" = the document's own tree, else the member-storage path
        public long Actual;
        public long? Summary;
        public int PatchOffset = -1;
        public string SegInfoPath = "";
        public bool Mismatch => Summary.HasValue && Summary.Value != Actual;
    }

    internal static List<Issue> Scan(CompoundFile cf) => ToIssues(ScanCounts(cf));

    internal static List<Issue> ToIssues(IEnumerable<SegmentCounts> counts)
    {
        List<Issue> issues = [];
        foreach (SegmentCounts c in counts)
        {
            if (c.Mismatch)
            {
                issues.Add(new Issue
                {
                    SegmentName = c.Name,
                    Location = c.Location,
                    SummaryCount = c.Summary!.Value,
                    ActualCount = c.Actual,
                    PatchOffset = c.PatchOffset,
                    SegInfoPath = c.SegInfoPath,
                });
            }
        }
        return issues;
    }

    /// <summary>The scope a segment tree belongs to, from the full path of its RSeStorage storage:
    /// "" for the document's own tree, else the member-storage path (e.g. "MemberDocs/x2oe…").
    /// The single definition of the Location convention - <see cref="Issue.Location"/> values and
    /// any UI lookup key must both come from here.</summary>
    public static string LocationOf(string rseStoragePath) =>
        rseStoragePath.Equals("/RSeStorage", StringComparison.OrdinalIgnoreCase)
            ? ""
            : rseStoragePath.TrimStart('/').Replace("/RSeStorage", "", StringComparison.OrdinalIgnoreCase);

    internal static List<SegmentCounts> ScanCounts(CompoundFile cf)
    {
        List<SegmentCounts> counts = [];

        // A document holds one RSeStorage tree of its own, and model-state parts/assemblies embed
        // a further complete tree per member under MemberDocs/. Every tree carries its own
        // RSeSegInfo registry, and segment names repeat across trees - so each M stream must be
        // checked against the registry of exactly its own tree.
        foreach (CompoundFile.DirEntry segInfoEntry in cf.Directory)
        {
            if (segInfoEntry.Type != 2 || segInfoEntry.Name != "RSeSegInfo" || !IsRSeChild(segInfoEntry.Path))
            {
                continue;
            }

            byte[] segInfo;
            try { segInfo = cf.ReadEntry(segInfoEntry); } catch { continue; }
            string tree = ParentPath(segInfoEntry.Path);
            string location = LocationOf(tree);

            foreach (CompoundFile.DirEntry e in cf.Directory)
            {
                // segment metadata streams are the M<id> children of this tree's RSeStorage
                if (e.Type != 2 || e.Name.Length < 2 || e.Name[0] != 'M' ||
                    !ParentPath(e.Path).Equals(tree, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    MetaStream? meta = ParseMetaStream(cf.ReadEntry(e));
                    if (meta == null)
                    {
                        continue;
                    }

                    SegmentCounts c = new()
                    {
                        Name = meta.Name,
                        StorageId = e.Name[1..],
                        Location = location,
                        Actual = meta.BlockCount,
                        SegInfoPath = segInfoEntry.Path,
                    };
                    if (FindSummary(segInfo, meta) is (int offset, long summary))
                    {
                        c.Summary = summary;
                        c.PatchOffset = offset;
                    }
                    counts.Add(c);
                }
                catch { /* strict parsing: an unreadable segment yields no verdict, not a false alarm */ }
            }
        }
        return counts;
    }

    private static string ParentPath(string path)
    {
        int i = path.LastIndexOf('/');
        return i > 0 ? path[..i] : "";
    }

    private static bool IsRSeChild(string path)
    {
        int i = path.LastIndexOf('/');
        return i > 0 && path.AsSpan(0, i).EndsWith("/RSeStorage", StringComparison.OrdinalIgnoreCase);
    }

    internal sealed class MetaStream
    {
        public string Name = "";
        public byte[] SegmentId = [];
        public long BlockCount;
    }

    /// <summary>Parse an M&lt;id&gt; metadata stream far enough to learn the segment's name, its id
    /// and how many blocks its block-size table lists ("RSe Meta Stream Version N" header, then a
    /// zlib- or zstd-compressed payload whose first table is the block index). Returns null when
    /// any byte does not look exactly as expected.</summary>
    internal static MetaStream? ParseMetaStream(byte[] m)
    {
        int i = 0;
        if (ReadText8(m, ref i) is not string magic || !magic.StartsWith("RSe Meta Stream Version", StringComparison.Ordinal))
        {
            return null;
        }

        if (i + 2 + 16 > m.Length)
        {
            return null;
        }

        int ver = BitConverter.ToUInt16(m, i);
        i += 2 + 16;                                     // version + 8 x u16
        if (ReadText16(m, ref i) is not string name || name.Length == 0 || i + 16 + 12 > m.Length)
        {
            return null;
        }

        byte[] segId = m[i..(i + 16)];
        i += 16 + 12;                                    // segment id + 3 x u32
        if (ver < 7)
        {
            i += 4;                                      // val1 before the created date
        }

        if (ReadText8(m, ref i) == null)
        {
            return null;
        }

        if (ver < 7)
        {
            i += 4;                                      // val2 before the modified date
        }

        if (ReadText8(m, ref i) == null || i + 1 > m.Length)
        {
            return null;
        }

        byte compression = m[i++];                       // 1 = zlib, 2 = zstd
        byte[] payload;
        try
        {
            using Stream dec = compression switch
            {
                1 => new ZLibStream(new MemoryStream(m, i, m.Length - i), CompressionMode.Decompress),
                2 => new ZstdSharp.DecompressionStream(new MemoryStream(m, i, m.Length - i)),
                _ => throw new InvalidDataException($"Unknown compression flag {compression}."),
            };
            // the block table sits right at the front; 18 header bytes + one u32 per block
            using MemoryStream buf = new();
            dec.CopyTo(buf);
            payload = buf.ToArray();
        }
        catch
        {
            return null;
        }

        if (payload.Length < 22)
        {
            return null;
        }

        long count = BitConverter.ToUInt32(payload, 14); // after 7 x u16
        long trailerOff = 18 + count * 4;
        if (trailerOff + 4 > payload.Length)
        {
            return null;
        }

        // the table's own trailer restates its byte size - a strong check that this really is
        // the block table of a format revision we understand
        if (BitConverter.ToUInt32(payload, (int)trailerOff) != (count + 1) * 4)
        {
            return null;
        }

        return new MetaStream { Name = name, SegmentId = segId, BlockCount = count };
    }

    /// <summary>Locate the segment's entry in RSeSegInfo (length-prefixed UTF-16 name, verified
    /// by the segment GUID that follows it) and return the offset + value of its block-count
    /// summary: entry + name + two GUIDs + value1 + count1 + arr1[0..2] -> arr1[3]. The stepped-over
    /// fields are fingerprinted before the offset is trusted, so an unknown layout revision is
    /// declined (no verdict) instead of being read - and later patched - at the wrong offset.</summary>
    internal static (int Offset, long Value)? FindSummary(byte[] segInfo, MetaStream meta)
    {
        byte[] pattern = BuildNamePattern(meta.Name);
        for (int at = Find(segInfo, pattern, 0); at >= 0; at = Find(segInfo, pattern, at + 1))
        {
            int idOff = at + pattern.Length;
            int summaryOff = idOff + 16 + 16 + 4 + 4 + 12;
            if (summaryOff + 4 > segInfo.Length)
            {
                return null;
            }

            if (!segInfo.AsSpan(idOff, 16).SequenceEqual(meta.SegmentId))
            {
                continue;
            }

            // Layout fingerprint (schema 0x1D-0x1F, observed stable from Inventor 11 through 2027):
            // value1 is always 0, count1 is a tiny object count, arr1[0] is always 0. A revision
            // that moves these fields fails the fingerprint and gets no verdict.
            uint value1 = BitConverter.ToUInt32(segInfo, idOff + 32);
            uint count1 = BitConverter.ToUInt32(segInfo, idOff + 36);
            uint arr1_0 = BitConverter.ToUInt32(segInfo, idOff + 40);
            if (value1 != 0 || count1 > 256 || arr1_0 != 0)
            {
                return null;
            }

            return (summaryOff, BitConverter.ToUInt32(segInfo, summaryOff));
        }
        return null;
    }

    private static byte[] BuildNamePattern(string name)
    {
        byte[] pattern = new byte[4 + name.Length * 2];
        BitConverter.TryWriteBytes(pattern.AsSpan(0, 4), (uint)name.Length);
        Encoding.Unicode.GetBytes(name, pattern.AsSpan(4));
        return pattern;
    }

    private static int Find(byte[] haystack, byte[] needle, int from) =>
        from > haystack.Length ? -1 : haystack.AsSpan(from).IndexOf(needle) is int rel && rel >= 0 ? from + rel : -1;

    private static string? ReadText8(byte[] b, ref int i)
    {
        if (i + 4 > b.Length)
        {
            return null;
        }

        int n = (int)BitConverter.ToUInt32(b, i);
        if (n < 0 || n > 1024 || i + 4 + n > b.Length)
        {
            return null;
        }

        string s = Encoding.Latin1.GetString(b, i + 4, n);
        i += 4 + n;
        return s;
    }

    private static string? ReadText16(byte[] b, ref int i)
    {
        if (i + 4 > b.Length)
        {
            return null;
        }

        int n = (int)BitConverter.ToUInt32(b, i);
        if (n < 0 || n > 1024 || i + 4 + n * 2 > b.Length)
        {
            return null;
        }

        string s = Encoding.Unicode.GetString(b, i + 4, n * 2);
        i += 4 + n * 2;
        return s;
    }
}
