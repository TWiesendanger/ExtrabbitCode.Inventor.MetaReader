namespace ExtrabbitCode.Inventor.MetaReader.Tests;

/// <summary>
/// The SampleBg fixture files - the same set the app ships and the docs shooter uses -
/// copied next to the test assembly (see the .csproj). One small, hand-authored project:
/// a part with model states that is also an iPart factory, a couple of plain parts, an
/// assembly that references them, a sub-assembly, a linked image and a drawing.
/// </summary>
internal static class Samples
{
    public const string Part = "SamplePart.ipt";          // model states (member-table marker, not a true iPart)
    public const string ComplexPart = "SampleComplexPart.ipt";
    public const string Cylinder = "Cylinder.ipt";        // has a linked image
    public const string Triangle = "Triangle.ipt";        // model states with custom names
    public const string IPartSample = "iPartSample.ipt";          // a genuine published iPart factory
    public const string IPartMember = "iPartSample/Part1-01.ipt";  // a generated member of that factory
    public const string Subassembly = "Subassembly.iam";  // references Triangle
    public const string Assembly = "SampleBg.iam";        // references all the above
    public const string Drawing = "SampleBg.idw";
    public const string TubeAndPipe = "TubeAndPipe.ipt";  // a Content Center Tube & Pipe pipe component
    public const string FrameAssembly = "FrameAssembly.iam"; // a Frame Generator frame assembly
    public const string ContentCenterPart = "ContentCenterScrew.ipt"; // a plain Content Center fastener (DIN 927)
    public const string BoltedConnection = "BoltedConnection.iam"; // a Design Accelerator bolted connection
    public const string WeldingAssembly = "WeldingAssembly.iam"; // a weldment (assembly document subtype)
    public const string SheetSample = "SheetSample.ipt";  // a sheet metal part (part document subtype)
    public const string IAssemblyFactory = "iAssemblyFactory.iam";              // an iAssembly factory
    public const string IAssemblyMember = "iAssemblyFactory/Assembly8-01.iam";  // a generated member of it

    private static readonly string Dir = Path.Combine(AppContext.BaseDirectory, "SampleBg");

    public static string PathOf(string fileName) => Path.Combine(Dir, fileName);

    public static InventorDocument Load(string fileName) => new(PathOf(fileName));
}
