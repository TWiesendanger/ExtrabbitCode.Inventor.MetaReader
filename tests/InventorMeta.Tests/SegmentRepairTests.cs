namespace ExtrabbitCode.Inventor.MetaReader.Tests;

/// <summary>
/// The "number of objects found in the segment does not match the segment summary" damage:
/// detection compares each segment's RSeSegInfo block-count summary against its metadata block
/// table; repair patches the summary in place after backing the file up. Damage is manufactured
/// at runtime by bumping a healthy sample's summary the same way the files in the wild are off,
/// so no corrupt binary needs to live in the repo.
/// </summary>
public class SegmentRepairTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("segrepair-").FullName;

    public void Dispose() => Directory.Delete(_dir, true);

    private string TempCopy(string sample)
    {
        string path = Path.Combine(_dir, Path.GetFileName(sample));
        File.Copy(Samples.PathOf(sample), path);
        return path;
    }

    /// <summary>Bump a segment's block-count summary in the document's own RSeSegInfo - the exact
    /// shape of the damage Inventor refuses to load. Deliberately re-implements the entry math
    /// instead of calling the library under test.</summary>
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

    private static void CorruptGraphicsSummary(string path) => CorruptSummary(path, "PmGraphicsSegment", 3);

    [Fact]
    public void HealthySamplesReportNoDamage()
    {
        foreach (string sample in new[] { Samples.Part, Samples.Assembly, Samples.TubeAndPipe })
        {
            InventorDocument doc = Samples.Load(sample);
            Assert.False(doc.HasRepairableSegmentDamage);
            Assert.Empty(doc.SegmentIssues);
            Assert.All(doc.Segments, s => Assert.False(s.HasCountMismatch));
        }
    }

    [Fact]
    public void BlockCountsAreReadForEverySegment()
    {
        InventorDocument doc = Samples.Load(Samples.Part);
        Assert.NotEmpty(doc.Segments);
        Assert.All(doc.Segments, s =>
        {
            Assert.NotNull(s.ActualBlockCount);     // the strict M-stream parse understood the format
            Assert.NotNull(s.SummaryBlockCount);    // and the registry entry was located
            Assert.Equal(s.SummaryBlockCount, s.ActualBlockCount);
        });
        Assert.True(doc.Segments.Single(s => s.IsGraphicsCache).ActualBlockCount > 0);
    }

    [Fact]
    public void StaleSummaryIsDetected()
    {
        string path = TempCopy(Samples.Part);
        CorruptGraphicsSummary(path);

        InventorDocument doc = new(path);
        Assert.True(doc.HasRepairableSegmentDamage);

        SegmentRepair.Issue issue = Assert.Single(doc.SegmentIssues);
        Assert.Equal("PmGraphicsSegment", issue.SegmentName);
        Assert.Equal("", issue.Location);
        Assert.Equal(issue.ActualCount + 3, issue.SummaryCount);

        InventorDocument.Segment gfx = doc.Segments.Single(s => s.IsGraphicsCache);
        Assert.True(gfx.HasCountMismatch);
        Assert.Equal(gfx.ActualBlockCount + 3, gfx.SummaryBlockCount);
    }

    [Fact]
    public void RepairRestoresTheOriginalBytesAndBacksUp()
    {
        string path = TempCopy(Samples.Part);
        byte[] pristine = File.ReadAllBytes(path);
        CorruptGraphicsSummary(path);
        byte[] corrupted = File.ReadAllBytes(path);

        SegmentRepair.RepairResult result = SegmentRepair.Repair(path);

        // the backup is the untouched (still corrupt) input ...
        Assert.NotNull(result.BackupPath);
        Assert.Equal(path + ".pre-repair.bak", result.BackupPath);
        Assert.Equal(corrupted, File.ReadAllBytes(result.BackupPath!));

        // ... and the repair puts the summary back to the actual count, i.e. the pristine bytes
        Assert.Equal("PmGraphicsSegment", Assert.Single(result.Repaired).SegmentName);
        Assert.Equal(pristine, File.ReadAllBytes(path));
        Assert.Empty(SegmentRepair.FindIssues(path));
    }

    /// <summary>The signature seen on every damaged customer file so far: PmGraphics, PmDC and
    /// PmBRep summaries all stale at once, off by a few counts each. Reproduced here on the free
    /// sample (real files can't be committed), detected as three issues and repaired in one go.</summary>
    [Fact]
    public void RealWorldMultiSegmentDamageIsRepairedInOneGo()
    {
        string path = TempCopy(Samples.Part);
        byte[] pristine = File.ReadAllBytes(path);
        CorruptSummary(path, "PmGraphicsSegment", 3);
        CorruptSummary(path, "PmDCSegment", 4);
        CorruptSummary(path, "PmBRepSegment", 4);

        InventorDocument doc = new(path);
        Assert.Equal(["PmBRepSegment", "PmDCSegment", "PmGraphicsSegment"],
                     doc.SegmentIssues.Select(i => i.SegmentName).Order().ToArray());

        SegmentRepair.RepairResult result = SegmentRepair.Repair(path);
        Assert.Equal(3, result.Repaired.Count);
        Assert.Equal(pristine, File.ReadAllBytes(path));
        Assert.Empty(SegmentRepair.FindIssues(path));
    }

    [Fact]
    public void RepairBackupsNeverOverwriteEachOther()
    {
        string path = TempCopy(Samples.Part);
        CorruptGraphicsSummary(path);
        string first = SegmentRepair.Repair(path).BackupPath!;
        CorruptGraphicsSummary(path);
        string second = SegmentRepair.Repair(path).BackupPath!;

        Assert.NotEqual(first, second);
        Assert.True(File.Exists(first));
        Assert.True(File.Exists(second));
    }

    [Fact]
    public void HealthyFileIsLeftUntouched()
    {
        string path = TempCopy(Samples.Part);
        byte[] pristine = File.ReadAllBytes(path);

        SegmentRepair.RepairResult result = SegmentRepair.Repair(path);

        Assert.Null(result.BackupPath);
        Assert.Empty(result.Repaired);
        Assert.Equal(pristine, File.ReadAllBytes(path));
        Assert.Empty(Directory.GetFiles(_dir, "*.bak"));
    }
}
