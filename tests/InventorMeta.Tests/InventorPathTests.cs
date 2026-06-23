namespace ExtrabbitCode.Inventor.MetaReader.Tests;

public class InventorPathTests
{
    [Theory]
    // Inventor stores Windows-style paths regardless of host OS: extracting the file name must
    // work the same on Linux/macOS, where System.IO.Path does not treat '\' as a separator.
    [InlineData(@"C:\Users\tobia\Downloads\claudeTemp\SampleBg.iam", "SampleBg.iam")]
    [InlineData(@"C:\Data\OneDrive - MuM - OM\Daten\Konstruktion\Triangle.ipt", "Triangle.ipt")]
    [InlineData("/home/runner/work/repo/SampleBg/Cylinder.ipt", "Cylinder.ipt")]
    [InlineData(@"SampleBg\mixed/Sub.iam", "Sub.iam")]
    [InlineData("plain.ipt", "plain.ipt")]
    [InlineData("", "")]
    public void GetFileNameSplitsOnBothSeparators(string input, string expected) =>
        Assert.Equal(expected, InventorPath.GetFileName(input));
}
