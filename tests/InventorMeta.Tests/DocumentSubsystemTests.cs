using System.Linq;

namespace ExtrabbitCode.Inventor.MetaReader.Tests;

public class DocumentSubsystemTests
{
    [Fact]
    public void FrameAssemblyIsDetectedAsFrameGenerator()
    {
        InventorDocument doc = Samples.Load(Samples.FrameAssembly);

        Assert.True(doc.HasSubsystem("FrameGenerator"));
        Assert.True(doc.HasSubsystem("framegenerator"));   // case-insensitive
        Assert.Contains(doc.Subsystems, s => s.Key == "FrameGenerator" && s.DisplayName == "Frame Generator");
        Assert.False(doc.HasSubsystem("TubeAndPipe"));     // it is not a Tube & Pipe doc
    }

    [Fact]
    public void FrameGeneratorPropertySetIsNamed()
    {
        // The {b65df8ea-…} FMTID ("_com.autodesk.FG") now resolves to a friendly set name.
        InventorDocument doc = Samples.Load(Samples.FrameAssembly);
        Assert.Contains(doc.Properties, p => p.Set == "Frame Generator");
    }

    [Fact]
    public void BoltedConnectionIsDetectedAsDesignAccelerator()
    {
        // Design Accelerator (bolted connection, shaft, gear, …) all stamp the generic "FDesign" set;
        // it has no subtype, so the file-readable category is Design Accelerator, not "Bolted Connection".
        InventorDocument doc = Samples.Load(Samples.BoltedConnection);

        Assert.True(doc.HasSubsystem("DesignAccelerator"));
        Assert.Equal(InventorDocument.DocCategory.DesignAccelerator, doc.PrimaryCategory);
    }

    [Fact]
    public void WeldmentIsRecognizedByItsDocumentSubtype()
    {
        // Identified by the Document SubType CLSID, not the localized "Weldment" name.
        InventorDocument doc = Samples.Load(Samples.WeldingAssembly);

        Assert.True(doc.IsWeldment);
        Assert.Equal(InventorDocument.DocCategory.Weldment, doc.PrimaryCategory);
    }

    [Fact]
    public void PlainAssemblyIsNotAWeldment()
    {
        Assert.False(Samples.Load(Samples.Assembly).IsWeldment);   // SampleBg.iam
    }

    [Fact]
    public void SheetMetalPartIsRecognizedByItsDocumentSubtype()
    {
        // Identified by the Document SubType CLSID, not the localized "Sheet Metal" name.
        InventorDocument doc = Samples.Load(Samples.SheetSample);

        Assert.True(doc.IsSheetMetal);
        Assert.Equal(InventorDocument.DocCategory.SheetMetal, doc.PrimaryCategory);
    }

    [Fact]
    public void PlainPartIsNotSheetMetal()
    {
        Assert.False(Samples.Load(Samples.Part).IsSheetMetal);   // SamplePart.ipt
    }

    [Fact]
    public void TubeAndPipePartIsDetectedAsTubeAndPipeSubsystem()
    {
        InventorDocument doc = Samples.Load(Samples.TubeAndPipe);

        Assert.True(doc.HasSubsystem("TubeAndPipe"));
        Assert.False(doc.HasSubsystem("FrameGenerator"));
    }

    [Fact]
    public void OrdinaryPartHasNoSubsystems()
    {
        InventorDocument doc = Samples.Load(Samples.Part);   // SamplePart.ipt - a plain part

        Assert.Empty(doc.Subsystems);
        Assert.False(doc.HasSubsystem("FrameGenerator"));
    }

    [Theory]
    [InlineData(Samples.FrameAssembly,    InventorDocument.DocCategory.FrameGenerator)]
    [InlineData(Samples.TubeAndPipe,      InventorDocument.DocCategory.Piping)]        // Piping wins over Content Center
    [InlineData(Samples.ContentCenterPart, InventorDocument.DocCategory.ContentCenter)] // DIN 927 fastener
    [InlineData(Samples.IPartSample,      InventorDocument.DocCategory.iPartFactory)]
    [InlineData(Samples.IPartMember,      InventorDocument.DocCategory.iPartMember)]
    [InlineData(Samples.IAssemblyFactory, InventorDocument.DocCategory.iAssemblyFactory)]
    [InlineData(Samples.IAssemblyMember,  InventorDocument.DocCategory.iAssemblyMember)]
    [InlineData(Samples.WeldingAssembly,  InventorDocument.DocCategory.Weldment)]
    [InlineData(Samples.SheetSample,      InventorDocument.DocCategory.SheetMetal)]
    [InlineData(Samples.Part,             InventorDocument.DocCategory.General)]        // SamplePart: model states, not an iPart
    [InlineData(Samples.Cylinder,         InventorDocument.DocCategory.General)]
    public void PrimaryCategoryClassifiesTheDocument(string sample, InventorDocument.DocCategory expected) =>
        Assert.Equal(expected, Samples.Load(sample).PrimaryCategory);

    [Fact]
    public void ModelStatePartIsNotMistakenForAnIPartFactory()
    {
        // SamplePart carries "Parameterized Template" = true but is a model-state part, not an iPart.
        InventorDocument doc = Samples.Load(Samples.Part);
        Assert.True(doc.HasModelStates);
        Assert.False(doc.IsFactory);
        Assert.False(doc.IsMember);
    }

    [Fact]
    public void IPartFactoryAndMemberRolesAreDistinguished()
    {
        Assert.True(Samples.Load(Samples.IPartSample).IsFactory);
        Assert.False(Samples.Load(Samples.IPartSample).IsMember);

        Assert.True(Samples.Load(Samples.IPartMember).IsMember);
        Assert.False(Samples.Load(Samples.IPartMember).IsFactory);
    }
}
