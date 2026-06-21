namespace InventorMeta.Tests;

/// <summary>The low-level OLE Compound File container reader that everything else sits on.</summary>
public class CompoundFileTests
{
    [Fact]
    public void RecognisesInventorFilesAsCompoundFiles()
    {
        Assert.True(CompoundFile.LooksLikeCompoundFile(Samples.PathOf(Samples.Part)));
        Assert.True(CompoundFile.LooksLikeCompoundFile(Samples.PathOf(Samples.Assembly)));
    }

    [Fact]
    public void RejectsNonCompoundFilesWithoutCrashing()
    {
        string tmp = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tmp, "plain text - definitely not an OLE compound file");
            Assert.False(CompoundFile.LooksLikeCompoundFile(tmp));
            Assert.ThrowsAny<Exception>(() => new InventorDocument(tmp));
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    [Fact]
    public void ExposesTheContainerVersion()
    {
        using CompoundFile cf = new(Samples.PathOf(Samples.Part));
        Assert.Equal(3, cf.MajorVersion);
        Assert.Equal(512, cf.SectorSize);
    }

    [Fact]
    public void ReadsAStreamByPath()
    {
        using CompoundFile cf = new(Samples.PathOf(Samples.Part));
        byte[] ufrx = cf.ReadStream("/UFRxDoc");
        Assert.NotEmpty(ufrx);
    }

    [Fact]
    public void EnumeratesTheStorageDirectory()
    {
        using CompoundFile cf = new(Samples.PathOf(Samples.Part));
        Assert.Contains(cf.Directory, e => e.Name == "UFRxDoc" && e.Type == 2 /* stream */);
    }
}
