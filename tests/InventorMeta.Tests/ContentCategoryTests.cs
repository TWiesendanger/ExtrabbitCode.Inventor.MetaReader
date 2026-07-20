namespace ExtrabbitCode.Inventor.MetaReader.Tests;

public class ContentCategoryTests
{
    [Fact]
    public void TubeAndPipePartExposesItsCategories()
    {
        InventorDocument doc = Samples.Load(Samples.TubeAndPipe);

        InventorDocument.ContentCategory tp = doc.Categories.Single(c => c.Mnemonic == "TUBEANDPIPE");
        Assert.Equal("Tube & Pipe", tp.DisplayName);                       // &amp; is decoded
        Assert.Equal("4347fa0f-2144-441d-94cd-e3e15c92b736", tp.InternalName);

        // The same Categories blob lists the more specific kinds too.
        Assert.Contains(doc.Categories, c => c.Mnemonic == "PIPES");
    }

    [Fact]
    public void HasCategoryIsCaseInsensitive()
    {
        InventorDocument doc = Samples.Load(Samples.TubeAndPipe);

        Assert.True(doc.HasCategory("TUBEANDPIPE"));
        Assert.True(doc.HasCategory("tubeandpipe"));      // case-insensitive
        Assert.False(doc.HasCategory("FRAMEGENERATOR"));  // a category this part does not carry
    }

    [Fact]
    public void OrdinaryPartHasNoCategories()
    {
        InventorDocument doc = Samples.Load(Samples.Part);   // SamplePart.ipt - a plain part

        Assert.Empty(doc.Categories);
        Assert.False(doc.HasCategory("TUBEANDPIPE"));
    }

    [Fact]
    public void JsonExportListsCategories()
    {
        InventorDocument doc = Samples.Load(Samples.TubeAndPipe);

        string json = doc.ToJson();
        Assert.Contains("\"mnemonic\": \"TUBEANDPIPE\"", json);
        Assert.Contains("4347fa0f-2144-441d-94cd-e3e15c92b736", json);   // the category's InternalName
    }
}
