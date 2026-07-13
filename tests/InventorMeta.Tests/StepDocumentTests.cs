using System.Text;

namespace ExtrabbitCode.Inventor.MetaReader.Tests;

public class StepDocumentTests
{
    [Fact]
    public void ReadsStepHeaderAndGeometrySummary()
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".stp");
        File.WriteAllText(path, """
            ISO-10303-21;
            HEADER;
            FILE_DESCRIPTION(('fixture'),'2;1');
            FILE_NAME('fixture.stp','2026-07-03T20:55:43+02:00',('tobia'),('MuM'),
              'ST-DEVELOPER v20.1','Autodesk Inventor 2026','');
            FILE_SCHEMA(('AUTOMOTIVE_DESIGN { 1 0 10303 214 3 1 1 }'));
            ENDSEC;
            DATA;
            #1=ADVANCED_BREP_SHAPE_REPRESENTATION('',(#2),#9);
            #2=MANIFOLD_SOLID_BREP('Volumenk\X\F6rper1',#3);
            #3=CLOSED_SHELL('',(#4));
            #4=ADVANCED_FACE('',(#5),#6,.T.);
            #5=FACE_BOUND('',#8,.T.);
            #6=PLANE('',#7);
            #7=AXIS2_PLACEMENT_3D('',#10,#11,#12);
            #8=EDGE_LOOP('',());
            #9=GEOMETRIC_REPRESENTATION_CONTEXT(3);
            #10=CARTESIAN_POINT('',(0.,0.,0.));
            #11=DIRECTION('',(0.,0.,1.));
            #12=DIRECTION('',(1.,0.,0.));
            ENDSEC;
            END-ISO-10303-21;
            """, Encoding.ASCII);

        try
        {
            InventorDocument doc = new(path);

            Assert.True(doc.IsStep);
            Assert.Equal(InventorDocument.DocKind.Step, doc.Kind);
            Assert.Equal(InventorDocument.DocCategory.NeutralCad, doc.PrimaryCategory);
            Assert.Equal("Autodesk Inventor 2026", doc.Summary["Originating System"]);
            Assert.Contains("AUTOMOTIVE_DESIGN", doc.Summary["Schema"]);
            Assert.Contains(doc.Properties, p => p.Name == "Solid Bodies" && p.Display.Contains("Volumenkörper1"));
            Assert.Contains(doc.Properties, p => p.Name == "Manifold Solid B-Reps" && p.Display == "1");
            Assert.Contains("MANIFOLD_SOLID_BREP", doc.StructureText);
            Assert.Null(doc.Thumbnail);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // The bundled SampleSteps files: the same part exported by Inventor 2027 in three schemas,
    // so every application protocol the app claims to open is covered.
    [Theory]
    [InlineData("Line Guide Drive Shaft_203.stp", "CONFIG_CONTROL_DESIGN", "CONFIG_CONTROL_DESIGN", "Line Guide Drive Shaft", 1)]
    [InlineData("Line Guide Drive Shaft_214.step", "AUTOMOTIVE_DESIGN", "214", "Line Guide Drive Shaft", 1)]
    [InlineData("Line Guide Drive Shaft_242.stp", "AP242_MANAGED_MODEL_BASED_3D_ENGINEERING", "442", "Line Guide Drive Shaft", 1)]
    public void ReadsTheBundledStepSamples(
        string fileName,
        string schemaNeedle,
        string protocolNeedle,
        string productNeedle,
        int expectedSolids)
    {
        InventorDocument doc = new(StepSample(fileName));

        Assert.True(doc.IsStep);
        Assert.Equal(InventorDocument.DocKind.Step, doc.Kind);
        Assert.Equal("STEP model (.stp/.step)", doc.DocumentType);
        Assert.Equal(InventorDocument.DocCategory.NeutralCad, doc.PrimaryCategory);
        Assert.Null(doc.Thumbnail);
        Assert.Equal("", doc.ThumbnailExt);
        Assert.False(doc.HasModelStates);
        Assert.Empty(doc.References);

        Assert.Contains(schemaNeedle, doc.Summary["Schema"]);
        Assert.Contains(protocolNeedle, doc.CfbVersionInfo);
        Assert.Equal("Autodesk Inventor 2027", doc.Summary["Originating System"]);
        Assert.Equal("tobia", doc.Summary["Author"]);
        Assert.Contains(productNeedle, Prop(doc, "Products"));
        Assert.Equal(expectedSolids.ToString(), Prop(doc, "Manifold Solid B-Reps"));
        Assert.True(int.Parse(Prop(doc, "Advanced B-Rep Representations")) > 0);
        Assert.Contains("MANIFOLD_SOLID_BREP", doc.StructureText);
    }

    [Theory]
    [InlineData("Line Guide Drive Shaft_203.stp")]
    [InlineData("Line Guide Drive Shaft_214.step")]
    public void HandlesBothStepExtensions(string fileName)
    {
        InventorDocument doc = new(StepSample(fileName));

        Assert.True(doc.IsStep);
        Assert.Equal(Path.GetFileName(StepSample(fileName)), doc.FileName);
        Assert.StartsWith("STEP ISO-10303-21", doc.CfbVersionInfo);
    }

    [Fact]
    public void JsonExportIncludesStepSpecificMetadata()
    {
        InventorDocument doc = new(StepSample("Line Guide Drive Shaft_214.step"));

        string json = doc.ToJson();

        Assert.Contains("\"documentType\": \"STEP model (.stp/.step)\"", json);
        Assert.Contains("\"primaryCategory\": \"NeutralCad\"", json);
        Assert.Contains("Line Guide Drive Shaft", json);
        Assert.Contains("AUTOMOTIVE_DESIGN", json);
    }

    private static string StepSample(string fileName) =>
        Path.Combine(AppContext.BaseDirectory, "StepSamples", fileName);

    private static string Prop(InventorDocument doc, string name) =>
        doc.Properties.Single(p => p.Name == name).Display;
}
