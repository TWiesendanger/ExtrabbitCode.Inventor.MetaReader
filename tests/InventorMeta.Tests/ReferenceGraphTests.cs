namespace InventorMeta.Tests;

public class ReferenceGraphTests
{
    private static RefNode AssemblyGraph() => ReferenceGraph.Build(Samples.Load(Samples.Assembly));

    [Fact]
    public void RootIsTheOpenedAssembly()
    {
        RefNode root = AssemblyGraph();
        Assert.Equal("SampleBg.iam", root.Name);
        Assert.Equal(InventorDocument.DocKind.Assembly, root.Kind);
        Assert.True(root.Resolved);
        Assert.Equal(0, root.Depth);
    }

    [Fact]
    public void ResolvesEveryDirectComponentOnDisk()
    {
        List<RefNode> components = AssemblyGraph().Children.Where(c => !c.IsLinkedFile).ToList();

        foreach (string expected in new[] { "Cylinder.ipt", "SampleComplexPart.ipt", "SamplePart.ipt", "Subassembly.iam" })
        {
            Assert.Contains(components, c => c.Name == expected);
        }

        Assert.All(components, c => Assert.True(c.Resolved, $"{c.Name} should resolve"));
    }

    [Fact]
    public void RecursesIntoSubAssemblies()
    {
        RefNode sub = AssemblyGraph().Children.Single(c => c.Name == "Subassembly.iam");
        Assert.Contains(sub.Children, c => c.Name == "Triangle.ipt" && c.Resolved);
    }

    [Fact]
    public void ClassifiesAGenuineIPartFactory()
    {
        // iPartSample is a real published iPart - opened on its own it is a factory leaf.
        RefNode root = ReferenceGraph.Build(Samples.Load(Samples.IPartSample));
        Assert.Equal(IPartRole.Factory, root.IPart);
    }

    [Fact]
    public void LabelsIPartMembersReferencingTheirFactory()
    {
        // The assembly places several members generated from iPartSample; each must classify as a
        // member and reference the shared iPartSample factory.
        List<RefNode> members = AssemblyGraph().Children.Where(c => c.IPart == IPartRole.Member).ToList();

        Assert.NotEmpty(members);
        Assert.All(members, m =>
            Assert.Contains(m.Children, c => c.Name == "iPartSample.ipt" && c.IPart == IPartRole.Factory));
    }

    [Fact]
    public void DoesNotClassifyModelStatePartsAsIParts()
    {
        // SamplePart carries the member-table marker but is really a model-state part, so it must
        // NOT be labelled an iPart factory - the mislabel the iPartSample fixture exposed.
        RefNode samplePart = AssemblyGraph().Children.Single(c => c.Name == "SamplePart.ipt");
        Assert.True(samplePart.IsIPart);          // carries the marker...
        Assert.True(samplePart.HasModelStates);   // ...because of model states
        Assert.Equal(IPartRole.None, samplePart.IPart);
    }

    [Fact]
    public void AttachesLinkedFilesAsLeaves()
    {
        RefNode cylinder = AssemblyGraph().Children.Single(c => c.Name == "Cylinder.ipt");
        Assert.Contains(cylinder.Children, c => c.IsLinkedFile && c.Name == "extrabbitcodeBadge.png");
    }
}
