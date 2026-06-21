using System.Text.Json;

namespace InventorMeta.Tests;

public class JsonExportTests
{
    [Fact]
    public void ToJsonProducesValidMetadata()
    {
        InventorDocument doc = Samples.Load(Samples.Part);

        using JsonDocument json = JsonDocument.Parse(doc.ToJson());
        JsonElement root = json.RootElement;

        Assert.Equal("SamplePart.ipt", root.GetProperty("file").GetString());
        Assert.True(root.GetProperty("isIPart").GetBoolean());
        Assert.True(root.GetProperty("hasThumbnail").GetBoolean());
        Assert.Equal(3, root.GetProperty("modelStates").GetArrayLength());
        Assert.Equal("SamplePart", root.GetProperty("summary").GetProperty("Part Number").GetString());
    }

    [Fact]
    public void AssemblyJsonListsReferencedComponents()
    {
        InventorDocument doc = Samples.Load(Samples.Assembly);

        using JsonDocument json = JsonDocument.Parse(doc.ToJson());
        List<string?> refs = json.RootElement.GetProperty("references").EnumerateArray()
            .Select(e => Path.GetFileName(e.GetString()))
            .ToList();

        Assert.Contains("Subassembly.iam", refs);
    }
}
