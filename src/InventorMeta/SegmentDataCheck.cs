using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace ExtrabbitCode.Inventor.MetaReader;

/// <summary>
/// Detects the second, unrepairable damage class: destroyed segment payload data.
///
/// Each RSeStorage segment stores its payload in the B&lt;id&gt; stream as ONE compressed blob
/// (an 18-byte header - a fixed 16-byte format id, one version byte, one compression byte
/// (1 = zlib, 2 = zstd) - followed by the compressed image). Files damaged this way carry runs
/// of zeroed 512-byte sectors inside that blob, the classic signature of a disk fault or an
/// interrupted copy. The compressed stream has no resync points, so every byte after the first
/// damaged one is unrecoverable - and Inventor parses the complete decompressed image of every
/// segment when it opens a file (verified experimentally against Inventor 2027: any altered
/// content terminates the load, even inside the regenerable graphics cache - "Error loading
/// segment … in database").
///
/// Nothing can repair this: the bytes are physically gone. This check exists so the damage can
/// be NAMED instead of guessed at - which segments, how much survives - and so no repair is
/// offered that would fail. The fix is restoring the file from a backup (Vault or another PDM,
/// the OldVersions folder, or a file backup). Contrast with <see cref="SegmentRepair"/>, which
/// handles the stale-summary class where no payload data is lost.
/// </summary>
public static class SegmentDataCheck
{
    /// <summary>The 16-byte format id every segment payload blob opens with.</summary>
    private static ReadOnlySpan<byte> BlobMagic =>
        [0x9E, 0xC2, 0x2B, 0xA4, 0xD4, 0x11, 0xE8, 0x01, 0x60, 0x00, 0x2D, 0xB3, 0xEE, 0x29, 0xFB, 0xB0];

    /// <summary>One segment whose payload data is destroyed.</summary>
    public sealed class Damage
    {
        /// <summary>Segment type, e.g. "PmGraphicsSegment".</summary>
        public string SegmentName = "";
        /// <summary>Which RSeStorage tree the segment lives in: "" for the document itself, or
        /// the member-storage path of an embedded model-state member (same convention as
        /// <see cref="SegmentRepair.Issue.Location"/>).</summary>
        public string Location = "";
        /// <summary>Size of the compressed payload stream on disk.</summary>
        public long CompressedSize;
        /// <summary>Bytes that still decompress cleanly before the stream breaks.</summary>
        public long RecoveredBytes;
        /// <summary>Bytes the segment's own block table promises at minimum (the decompressed
        /// image is at least this long, plus internal bookkeeping after the blocks).</summary>
        public long ExpectedBytes;
        /// <summary>True for the freak case where the zeroed bytes landed inside stored (raw)
        /// compression blocks: the stream still decodes end to end and only the checksum fails.
        /// The content silently carries zero-filled holes - just as fatal for Inventor.</summary>
        public bool ChecksumOnly;
        /// <summary>Longest run of 0x00 inside the compressed stream - multiple KB here is the
        /// zeroed-sector signature.</summary>
        public long LongestZeroRun;

        public override string ToString() =>
            $"{SegmentName}: {RecoveredBytes:N0} of >= {ExpectedBytes:N0} B readable";
    }

    /// <summary>Scan a document for segments whose payload data is destroyed. Empty for healthy
    /// files. Decompresses every segment payload, so this costs real time on big files - call it
    /// deliberately, not per property. Parsing is strict: format revisions without the known blob
    /// header yield no verdict rather than a false alarm.</summary>
    public static IReadOnlyList<Damage> FindDamage(string filePath)
    {
        using CompoundFile cf = new(filePath);
        return Scan(cf);
    }

    internal static List<Damage> Scan(CompoundFile cf)
    {
        List<Damage> damage = [];

        // Only claim a wiped header when this file demonstrably uses the known blob format -
        // i.e. at least one sibling payload stream carries the magic. Keeps ancient or unknown
        // format revisions verdict-free instead of "damaged".
        bool fileUsesBlobFormat = cf.Directory.Any(e =>
        {
            if (e.Type != 2 || e.Name.Length < 2 || e.Name[0] != 'B' || !SegmentRepair.IsRSeChild(e.Path))
            {
                return false;
            }
            try
            {
                byte[] head = cf.ReadEntry(e);
                return head.Length >= 18 && head.AsSpan(0, 16).SequenceEqual(BlobMagic);
            }
            catch { return false; }
        });

        foreach (CompoundFile.DirEntry m in cf.Directory)
        {
            if (m.Type != 2 || m.Name.Length < 2 || m.Name[0] != 'M' || !SegmentRepair.IsRSeChild(m.Path))
            {
                continue;
            }

            SegmentRepair.MetaStream? meta;
            try { meta = SegmentRepair.ParseMetaStream(cf.ReadEntry(m)); } catch { continue; }
            if (meta == null)
            {
                continue;   // unknown metadata revision -> no verdict
            }

            string tree = SegmentRepair.ParentPath(m.Path);
            string bName = "B" + m.Name[1..];
            CompoundFile.DirEntry? b = cf.Directory.FirstOrDefault(e =>
                e.Type == 2 && e.Name == bName &&
                SegmentRepair.ParentPath(e.Path).Equals(tree, StringComparison.OrdinalIgnoreCase));
            if (b == null)
            {
                continue;
            }

            byte[] data;
            try { data = cf.ReadEntry(b); } catch { continue; }

            Damage? found = Check(data, meta.BlockBytes, fileUsesBlobFormat);
            if (found != null)
            {
                found.SegmentName = meta.Name;
                found.Location = SegmentRepair.LocationOf(tree);
                damage.Add(found);
            }
        }
        return damage;
    }

    /// <summary>Verdict for one payload stream: null when it decompresses cleanly to at least the
    /// block-table total (healthy) or when its format isn't the known blob layout (no verdict).</summary>
    private static Damage? Check(byte[] data, long expectedBytes, bool fileUsesBlobFormat)
    {
        if (data.Length >= 18 && data.AsSpan(0, 16).SequenceEqual(BlobMagic) && data[17] is 1 or 2)
        {
            (bool ok, long produced, bool checksumOnly) = Decompress(data);
            if (ok && produced >= expectedBytes)
            {
                return null;
            }

            return new Damage
            {
                CompressedSize = data.Length,
                RecoveredBytes = produced,
                ExpectedBytes = expectedBytes,
                ChecksumOnly = checksumOnly,
                LongestZeroRun = LongestZeroRun(data),
            };
        }

        // No (readable) blob header. When the rest of the file proves the format, a header that
        // was zeroed away is the same sector damage - the whole payload is unreadable.
        if (fileUsesBlobFormat && data.Take(Math.Min(data.Length, 18)).All(v => v == 0))
        {
            return new Damage
            {
                CompressedSize = data.Length,
                RecoveredBytes = 0,
                ExpectedBytes = expectedBytes,
                LongestZeroRun = LongestZeroRun(data),
            };
        }

        return null;   // unknown layout -> no verdict
    }

    /// <summary>Inflate the blob, counting output only. zlib streams are decoded as bare deflate
    /// with the adler32 verified by hand against the trailing 4 bytes - the framework's own
    /// trailer validation has proven unreliable, and the checksum is exactly what catches the
    /// freak case where zeroed bytes landed inside stored blocks and the structure still decodes.</summary>
    private static (bool Ok, long Produced, bool ChecksumOnly) Decompress(byte[] data)
    {
        long produced = 0;
        bool clean = false;
        uint a = 1, b = 0;   // adler32 state
        try
        {
            using MemoryStream src = new(data, data[17] == 1 ? 20 : 18, data.Length - (data[17] == 1 ? 20 : 18));
            using Stream dec = data[17] == 1
                ? new DeflateStream(src, CompressionMode.Decompress)
                : new ZstdSharp.DecompressionStream(src);
            byte[] buf = new byte[64 * 1024];
            for (int n; (n = dec.Read(buf, 0, buf.Length)) > 0;)
            {
                produced += n;
                if (data[17] == 1)
                {
                    const uint Mod = 65521;
                    for (int k = 0; k < n; k++)
                    {
                        a = (a + buf[k]) % Mod;
                        b = (b + a) % Mod;
                    }
                }
            }
            clean = true;
        }
        catch
        {
            // fell over inside the damaged region; 'produced' is what decoded before it
        }

        if (!clean)
        {
            return (false, produced, false);
        }

        if (data[17] == 1 && data.Length >= 22)
        {
            // the stream's last 4 bytes are the big-endian adler32 of the decompressed image
            uint stored = (uint)((data[^4] << 24) | (data[^3] << 16) | (data[^2] << 8) | data[^1]);
            if (stored != (b << 16 | a))
            {
                return (false, produced, true);
            }
        }
        return (true, produced, false);
    }

    private static long LongestZeroRun(byte[] data)
    {
        long best = 0, run = 0;
        foreach (byte v in data)
        {
            run = v == 0 ? run + 1 : 0;
            if (run > best)
            {
                best = run;
            }
        }
        return best;
    }
}
