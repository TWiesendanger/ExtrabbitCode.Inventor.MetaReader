namespace ExtrabbitCode.Inventor.MetaReader.Tests;

/// <summary>
/// The "Error loading segment … in database" damage: runs of zeroed sectors inside a segment's
/// compressed payload stream. Unlike the stale-summary class, the destroyed bytes are physically
/// gone - nothing can repair it, so detection must name it clearly and the repair path must
/// refuse to touch such files. Damage is manufactured at runtime by zeroing bytes inside a
/// healthy sample's payload stream, exactly the shape seen in the wild; no corrupt binary lives
/// in the repo.
/// </summary>
public class SegmentDataCheckTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("segdata-").FullName;

    public void Dispose() => Directory.Delete(_dir, true);

    private string TempCopy(string sample)
    {
        string path = Path.Combine(_dir, Path.GetFileName(sample));
        File.Copy(Samples.PathOf(sample), path);
        return path;
    }

    /// <summary>Zero <paramref name="length"/> bytes of a segment's payload (B) stream, starting
    /// at <paramref name="offset"/> - the zeroed-sector damage seen on real customer files.
    /// Deliberately walks the streams itself instead of calling the library under test.</summary>
    private static void ZeroPayloadBytes(string path, string segmentName, int offset, int length)
    {
        using CompoundFile cf = new(path);
        foreach (CompoundFile.DirEntry m in cf.Directory)
        {
            if (m.Type != 2 || m.Name.Length < 2 || m.Name[0] != 'M' ||
                !m.Path.StartsWith("/RSeStorage/", StringComparison.Ordinal))
            {
                continue;
            }

            // the metadata stream names its segment as a length-prefixed UTF-16 string
            byte[] meta = cf.ReadEntry(m);
            byte[] pattern = [.. BitConverter.GetBytes((uint)segmentName.Length), .. System.Text.Encoding.Unicode.GetBytes(segmentName)];
            if (meta.AsSpan().IndexOf(pattern) < 0)
            {
                continue;
            }

            CompoundFile.DirEntry b = cf.Directory.Single(e => e.Type == 2 && e.Path == "/RSeStorage/B" + m.Name[1..]);
            byte[] data = cf.ReadEntry(b);
            Assert.True(offset + length <= data.Length, $"sample's {segmentName} payload is too small for the test damage");
            Array.Clear(data, offset, length);
            cf.OverwriteStreamInPlace(b, data);
            cf.Save(path);
            return;
        }
        Assert.Fail($"sample has no {segmentName} stream");
    }

    [Fact]
    public void HealthySamplesReportNoDataDamage()
    {
        foreach (string sample in new[] { Samples.Part, Samples.Assembly, Samples.TubeAndPipe, Samples.Cylinder })
        {
            InventorDocument doc = Samples.Load(sample);
            Assert.False(doc.HasDestroyedSegmentData);
            Assert.Empty(doc.DataDamage);
        }
    }

    [Fact]
    public void ZeroedSectorInsideAPayloadIsDetected()
    {
        string path = TempCopy(Samples.Part);
        ZeroPayloadBytes(path, "PmGraphicsSegment", offset: 1024, length: 512);

        InventorDocument doc = new(path);
        Assert.True(doc.HasDestroyedSegmentData);

        SegmentDataCheck.Damage d = Assert.Single(doc.DataDamage);
        Assert.Equal("PmGraphicsSegment", d.SegmentName);
        Assert.Equal("", d.Location);
        Assert.True(d.ExpectedBytes > 0);
        Assert.True(d.RecoveredBytes < d.ExpectedBytes || d.ChecksumOnly);
        Assert.True(d.LongestZeroRun >= 512);
    }

    [Fact]
    public void ZeroedStreamHeaderIsDetected()
    {
        // the first sector of the stream wiped - header, compression flag and all (seen in the
        // wild: the whole payload is unreadable, not just a stretch of it)
        string path = TempCopy(Samples.Part);
        ZeroPayloadBytes(path, "PmDCSegment", offset: 0, length: 512);

        InventorDocument doc = new(path);
        SegmentDataCheck.Damage d = Assert.Single(doc.DataDamage);
        Assert.Equal("PmDCSegment", d.SegmentName);
        Assert.Equal(0, d.RecoveredBytes);
    }

    [Fact]
    public void RepairRefusesADestroyedFileAndLeavesItUntouched()
    {
        string path = TempCopy(Samples.Part);
        // both damage classes at once: a stale summary alone would be repairable, but the
        // destroyed payload makes any repair pointless - it must be refused outright
        ZeroPayloadBytes(path, "PmGraphicsSegment", offset: 1024, length: 512);
        CorruptSummary(path, "PmBRepSegment", 2);
        byte[] beforeRepair = File.ReadAllBytes(path);

        InvalidDataException ex = Assert.Throws<InvalidDataException>(() => SegmentRepair.Repair(path));
        Assert.Contains("PmGraphicsSegment", ex.Message);
        Assert.Contains("backup", ex.Message);

        Assert.Equal(beforeRepair, File.ReadAllBytes(path));           // nothing was patched
        Assert.Empty(Directory.GetFiles(_dir, "*.bak"));               // and no backup left behind
    }

    [Fact]
    public void StaleSummaryAloneIsStillRepairable()
    {
        // the guard must not get in the way of the class that IS fixable
        string path = TempCopy(Samples.Part);
        CorruptSummary(path, "PmGraphicsSegment", 3);

        SegmentRepair.RepairResult result = SegmentRepair.Repair(path);
        Assert.Single(result.Repaired);
        Assert.Empty(SegmentRepair.FindIssues(path));
    }

    /// <summary>Same summary-bump helper as SegmentRepairTests - the entry math is deliberately
    /// re-implemented rather than borrowed from the library under test.</summary>
    private static void CorruptSummary(string path, string segmentName, uint delta)
    {
        using CompoundFile cf = new(path);
        CompoundFile.DirEntry entry = cf.Directory.Single(e => e.Type == 2 && e.Path == "/RSeStorage/RSeSegInfo");
        byte[] segInfo = cf.ReadEntry(entry);

        byte[] pattern = [.. BitConverter.GetBytes((uint)segmentName.Length), .. System.Text.Encoding.Unicode.GetBytes(segmentName)];
        int at = segInfo.AsSpan().IndexOf(pattern);
        Assert.True(at >= 0, $"sample has no {segmentName} entry");

        int summaryOff = at + pattern.Length + 32 + 8 + 12;    // name, two GUIDs, value1+count1, arr1[0..2]
        uint value = BitConverter.ToUInt32(segInfo, summaryOff);
        BitConverter.GetBytes(value + delta).CopyTo(segInfo, summaryOff);

        cf.OverwriteStreamInPlace(entry, segInfo);
        cf.Save(path);
    }
}
