using System.Linq;

namespace ExtrabbitCode.Inventor.MetaReader.Tests;

public class SegmentInventoryTests
{
    [Fact]
    public void ListsRSeStorageSegmentsByNameAndSize()
    {
        InventorDocument doc = Samples.Load(Samples.Part);   // SamplePart.ipt

        Assert.NotEmpty(doc.Segments);
        // Every part carries geometry and a (regenerable) graphics cache.
        Assert.Contains(doc.Segments, s => s.Name == "PmBRepSegment" && s.DataSize > 0);
        Assert.Contains(doc.Segments, s => s.IsGraphicsCache);

        // Reported largest-first, and each has both a data and a metadata stream.
        Assert.True(doc.Segments.Zip(doc.Segments.Skip(1)).All(p => p.First.DataSize >= p.Second.DataSize));
        Assert.All(doc.Segments, s => Assert.True(s.DataSize > 0 && s.MetaSize > 0));
    }

    [Fact]
    public void GraphicsCacheIsIdentified()
    {
        InventorDocument doc = Samples.Load(Samples.Part);
        InventorDocument.Segment gfx = doc.Segments.Single(s => s.IsGraphicsCache);
        Assert.Equal("PmGraphicsSegment", gfx.Name);
    }
}
