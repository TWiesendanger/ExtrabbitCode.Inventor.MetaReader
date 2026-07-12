using System.Text.RegularExpressions;

namespace ExtrabbitCode.Inventor.MetaReader.Tests;

/// <summary>
/// Release discipline, enforced: the app version, the About history and the what's-new dialog
/// must move together, so "bumped the version but forgot the release notes" (or the reverse)
/// fails the build instead of shipping. The WinUI app can't be referenced from a plain test
/// project, so these parse the app's SOURCES, copied next to the test assembly by the csproj.
/// </summary>
public class ReleaseDisciplineTests
{
    private static string Source(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "AppSources", name));

    private static string CsprojVersion()
    {
        Match m = Regex.Match(Source("InventorMeta.App.csproj"), @"<Version>([\d.]+)</Version>");
        Assert.True(m.Success, "InventorMeta.App.csproj has no <Version> element");
        return m.Groups[1].Value;
    }

    /// <summary>Versions of AppInfo.History, in declaration order (entries look like ("1.2.0.0", [).</summary>
    private static List<Version> HistoryVersions()
    {
        MatchCollection ms = Regex.Matches(Source("AppInfo.cs"), @"\(\s*""(\d+(?:\.\d+)+)""\s*,\s*\[");
        Assert.True(ms.Count > 0, "no History entries found in AppInfo.cs");
        return ms.Select(m => Version.Parse(m.Groups[1].Value)).ToList();
    }

    /// <summary>Versions of the what's-new dialog's release groups, in declaration order
    /// (groups look like new("1.2.0", [...]) - section titles never parse as versions).</summary>
    private static List<Version> DialogVersions()
    {
        MatchCollection ms = Regex.Matches(Source("WhatsNewDialog.cs"), @"new\(\s*""(\d+(?:\.\d+)+)""\s*,");
        Assert.True(ms.Count > 0, "no Release groups found in WhatsNewDialog.cs");
        return ms.Select(m => Version.Parse(m.Groups[1].Value)).ToList();
    }

    private static Version ThreePart(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build));

    [Fact]
    public void App_version_has_release_notes_in_the_About_history()
    {
        Assert.Equal(Version.Parse(CsprojVersion()), HistoryVersions()[0]);
    }

    [Fact]
    public void About_history_is_strictly_descending()
    {
        List<Version> vs = HistoryVersions();
        for (int i = 1; i < vs.Count; i++)
        {
            Assert.True(vs[i - 1] > vs[i], $"History out of order: {vs[i - 1]} is followed by {vs[i]}");
        }
    }

    [Fact]
    public void Whats_new_dialog_leads_with_the_current_version()
    {
        Assert.Equal(ThreePart(Version.Parse(CsprojVersion())), ThreePart(DialogVersions()[0]));
    }

    [Fact]
    public void Whats_new_dialog_releases_are_descending_and_all_have_About_history()
    {
        List<Version> history = HistoryVersions();
        List<Version> dialog = DialogVersions();
        for (int i = 1; i < dialog.Count; i++)
        {
            Assert.True(dialog[i - 1] > dialog[i],
                $"Dialog releases out of order: {dialog[i - 1]} is followed by {dialog[i]}");
        }
        foreach (Version v in dialog)
        {
            Assert.True(history.Any(h => ThreePart(h) == ThreePart(v)),
                $"Dialog release {v} has no matching entry in AppInfo.History");
        }
    }
}
