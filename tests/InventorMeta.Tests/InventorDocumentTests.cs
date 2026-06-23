namespace ExtrabbitCode.Inventor.MetaReader.Tests;

public class InventorDocumentTests
{
    [Theory]
    [InlineData(Samples.Part, InventorDocument.DocKind.Part)]
    [InlineData(Samples.ComplexPart, InventorDocument.DocKind.Part)]
    [InlineData(Samples.Cylinder, InventorDocument.DocKind.Part)]
    [InlineData(Samples.Triangle, InventorDocument.DocKind.Part)]
    [InlineData(Samples.Subassembly, InventorDocument.DocKind.Assembly)]
    [InlineData(Samples.Assembly, InventorDocument.DocKind.Assembly)]
    [InlineData(Samples.Drawing, InventorDocument.DocKind.Drawing)]
    public void DetectsDocumentKindFromBytes(string file, InventorDocument.DocKind expected)
    {
        InventorDocument doc = Samples.Load(file);
        Assert.Equal(expected, doc.Kind);
    }

    [Theory]
    [InlineData(Samples.Part, "SamplePart")]
    [InlineData(Samples.ComplexPart, "SampleComplexPart")]
    [InlineData(Samples.Cylinder, "Cylinder")]
    [InlineData(Samples.Triangle, "Triangle")]
    [InlineData(Samples.Subassembly, "Subassembly")]
    [InlineData(Samples.Assembly, "SampleBg")]
    public void ReadsPartNumber(string file, string expected)
    {
        InventorDocument doc = Samples.Load(file);
        Assert.Equal(expected, doc.Summary["Part Number"]);
    }

    [Fact]
    public void ReadsCommonIProperties()
    {
        InventorDocument doc = Samples.Load(Samples.Part);
        Assert.Equal("tobia", doc.Summary["Designer"]);
        Assert.Equal("Proj1", doc.Summary["Project"]);
    }

    [Fact]
    public void ExtractsThePreviewThumbnail()
    {
        InventorDocument doc = Samples.Load(Samples.Part);
        Assert.NotNull(doc.Thumbnail);
        Assert.NotEmpty(doc.Thumbnail!);
    }

    [Fact]
    public void DetectsTheMemberTableMarker()
    {
        // IsIPart comes from the member-table marker, which Inventor uses for BOTH published
        // iParts and ordinary model states - so a model-state-only part reads as IsIPart too.
        // A plain part with neither has no member table.
        Assert.True(Samples.Load(Samples.Part).IsIPart);       // model states (+ iPart factory)
        Assert.True(Samples.Load(Samples.Triangle).IsIPart);   // model states only
        Assert.False(Samples.Load(Samples.Cylinder).IsIPart);  // plain part
        Assert.False(Samples.Load(Samples.ComplexPart).IsIPart);
    }

    [Fact]
    public void DistinguishesATrueIPartFromModelStateParts()
    {
        // A genuine published iPart factory carries the member-table marker but has NO model
        // states (its members are table rows, generated on placement)...
        InventorDocument iPart = Samples.Load(Samples.IPartSample);
        Assert.True(iPart.IsIPart);
        Assert.False(iPart.HasModelStates);

        // ...whereas a model-state part carries the same marker *because of* its states.
        InventorDocument modelStatePart = Samples.Load(Samples.Part);
        Assert.True(modelStatePart.IsIPart);
        Assert.True(modelStatePart.HasModelStates);
    }

    [Fact]
    public void ResolvesCustomModelStateNames()
    {
        // Triangle carries non-default state names ("Body(Body)2", "SecondStateTest") alongside
        // the default "Model State1" - the resolver must recover the custom names, not the raw ids.
        InventorDocument doc = Samples.Load(Samples.Triangle);

        Assert.True(doc.HasModelStates);
        Assert.Equal(4, doc.ModelStateDetails.Count);
        var names = doc.ModelStateDetails.Select(s => s.Name).ToList();
        Assert.Equal("[Primary]", names[0]);
        Assert.Contains("Body(Body)2", names);
        Assert.Contains("Model State1", names);
        Assert.Contains("SecondStateTest", names);
    }

    [Fact]
    public void ReadsAllThreeModelStates()
    {
        InventorDocument doc = Samples.Load(Samples.Part);

        Assert.True(doc.HasModelStates);
        Assert.Equal(
            ["[Primary]", "Model State1", "Model State2"],
            doc.ModelStateDetails.Select(s => s.Name));

        InventorDocument.ModelState primary = doc.ModelStateDetails[0];
        Assert.Equal("[Primary]", primary.Name);
        Assert.True(primary.IsActive);
        Assert.DoesNotContain(doc.ModelStateDetails.Skip(1), s => s.IsActive);
    }

    [Fact]
    public void EachModelStateCarriesItsOwnProject()
    {
        InventorDocument doc = Samples.Load(Samples.Part);

        string ProjectOf(string state) =>
            doc.ModelStateDetails.Single(s => s.Name == state).Summary["Project"];

        Assert.Equal("Proj1", ProjectOf("[Primary]"));
        Assert.Equal("Proj2", ProjectOf("Model State1"));
        Assert.Equal("Proj3", ProjectOf("Model State2"));
    }

    [Fact]
    public void AssemblyListsItsReferencedComponents()
    {
        InventorDocument doc = Samples.Load(Samples.Assembly);

        List<string> names = doc.References.Select(InventorPath.GetFileName).ToList();
        Assert.Contains("SamplePart.ipt", names);
        Assert.Contains("SampleComplexPart.ipt", names);
        Assert.Contains("Cylinder.ipt", names);
        Assert.Contains("Subassembly.iam", names);
    }

    [Fact]
    public void ReadsLinkedNonModelFiles()
    {
        InventorDocument doc = Samples.Load(Samples.Cylinder);
        Assert.Contains(doc.LinkedFiles, f => InventorPath.GetFileName(f) == "extrabbitcodeBadge.png");
    }

    [Fact]
    public void DrawingReferencesTheModelItDocuments()
    {
        InventorDocument doc = Samples.Load(Samples.Drawing);
        Assert.Equal(InventorDocument.DocKind.Drawing, doc.Kind);
        Assert.Contains("SampleBg.iam", doc.References.Select(InventorPath.GetFileName));
    }

    [Fact]
    public void ThumbnailBytesMatchTheirDeclaredFormat()
    {
        InventorDocument doc = Samples.Load(Samples.Part);
        Assert.NotNull(doc.Thumbnail);
        byte[] t = doc.Thumbnail!;
        switch (doc.ThumbnailExt)
        {
            case "png": Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, t.Take(4).ToArray()); break;
            case "bmp": Assert.Equal("BM"u8.ToArray(), t.Take(2).ToArray()); break;
            default: Assert.Fail($"unexpected thumbnail extension '{doc.ThumbnailExt}'"); break;
        }
    }

    [Fact]
    public void ProvenanceCoversSchemaTemplateAndOrigin()
    {
        Dictionary<string, string> version = Samples.Load(Samples.Assembly).VersionInfo;
        foreach (string key in new[] { "File Schema", "Software Schema", "Saved From", "Saved On", "Template" })
        {
            Assert.True(version.ContainsKey(key), $"VersionInfo missing '{key}'");
        }

        Assert.Matches(@"^\d+\.\d+", version["File Schema"]);
    }

    [Fact]
    public void SurfacesExplorerVersionHistory()
    {
        Dictionary<string, string> version = Samples.Load(Samples.Assembly).VersionInfo;
        foreach (string key in new[] { "Current Version", "Previous Version", "Next Version", "Last update with", "Last saved by" })
        {
            Assert.True(version.ContainsKey(key), $"VersionInfo missing '{key}'");
        }
    }

    [Theory]
    [InlineData(Samples.Part)]
    [InlineData(Samples.ComplexPart)]
    [InlineData(Samples.Cylinder)]
    [InlineData(Samples.Triangle)]
    [InlineData(Samples.Subassembly)]
    [InlineData(Samples.Assembly)]
    [InlineData(Samples.Drawing)]
    public void EverySampleParsesWithAKnownKind(string file)
    {
        InventorDocument doc = Samples.Load(file);
        Assert.NotEqual(InventorDocument.DocKind.Unknown, doc.Kind);
        Assert.NotEmpty(doc.Properties);
    }
}
