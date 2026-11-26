using System.Text.Json;

namespace ExtrabbitCode.Inventor.MetaReader.Tests;

/// <summary>
/// iLogic rules and event triggers, read without Inventor. The fixtures in Fixtures/iLogic were saved
/// by Inventor 2027.2: OneRule* are the same part with one rule "Test" (plain / with a Before Save
/// trigger / suppressed / deleted again); the others were generated through the iLogic automation API
/// (ThreeRules: RuleA plain, RuleB suppressed, RuleC "don't run automatically", plus two triggers).
/// </summary>
public class ILogicTests
{
    private static InventorDocument Load(string name) =>
        new(Path.Combine(AppContext.BaseDirectory, "Fixtures", "iLogic", name));

    [Fact]
    public void RuleNameCodeAndDefaultOptionsAreRead()
    {
        ILogicInfo il = Load("OneRule.ipt").ILogic;

        Assert.True(il.HasRules);
        Assert.Equal("InUse", il.RuleStatus);
        ILogicRule rule = Assert.Single(il.Rules);
        Assert.Equal("Test", rule.Name);
        Assert.Equal("iLogicRule_Name0", rule.AttributeSet);
        Assert.Equal("' this is a test", rule.Text);
        Assert.Equal(1, rule.Flags);
        Assert.False(rule.IsSuppressed);
        Assert.True(rule.RunsAutomatically);
        Assert.True(rule.SilentOperation);
        Assert.False(rule.FireDependentImmediately);
        Assert.False(rule.IsStraightVb);
        Assert.False(il.HasTriggers);
    }

    [Fact]
    public void EventTriggerNamesItsEventAndRule()
    {
        ILogicInfo il = Load("OneRuleTriggered.ipt").ILogic;

        ILogicTrigger t = Assert.Single(il.Triggers);
        Assert.Equal("BeforeDocSave", t.Event);
        Assert.Equal("Before Save Document", t.EventLabel);
        Assert.Equal(0, t.Index);
        Assert.Equal("Test", t.RuleName);
        Assert.Same(t, Assert.Single(Assert.Single(il.Rules).Triggers));
    }

    [Fact]
    public void SuppressedRuleIsFlaggedButStillCounts()
    {
        // The _iLogic property set says "InUse" either way; suppression lives in the rule flags.
        ILogicInfo il = Load("OneRuleSuppressed.ipt").ILogic;

        Assert.True(il.HasRules);
        Assert.Equal("InUse", il.RuleStatus);
        ILogicRule rule = Assert.Single(il.Rules);
        Assert.True(rule.IsSuppressed);
        Assert.Equal(3, rule.Flags);
    }

    [Fact]
    public void DeletingTheLastRuleRemovesEveryMarker()
    {
        ILogicInfo il = Load("RuleDeleted.ipt").ILogic;

        Assert.True(il.IsEmpty);
        Assert.False(il.HasRules);
        Assert.Empty(il.Rules);
    }

    [Fact]
    public void SeveralRulesKeepTheirNamesCodeAndOptions()
    {
        ILogicInfo il = Load("ThreeRules.ipt").ILogic;

        Assert.Equal(["RuleA", "RuleB", "RuleC"], il.Rules.Select(r => r.Name));
        // attribute set slots are not consecutive - names must come from the rule list, not the slot
        Assert.Equal(["iLogicRule_Name0", "iLogicRule_Name3", "iLogicRule_Name4"], il.Rules.Select(r => r.AttributeSet));

        ILogicRule a = il.Rules[0], b = il.Rules[1], c = il.Rules[2];
        Assert.StartsWith("' rule A runs before save", a.Text);
        Assert.Equal("' rule B is suppressed", b.Text);
        Assert.Contains("rule C: straight VB, not automatic", c.Text);

        Assert.False(a.IsSuppressed);
        Assert.True(b.IsSuppressed);
        Assert.False(c.RunsAutomatically);
        Assert.Equal(17, c.Flags);

        Assert.Equal(["Before Save Document"], a.Triggers.Select(t => t.EventLabel));
        Assert.Empty(b.Triggers);
        Assert.Equal(["After Open Document"], c.Triggers.Select(t => t.EventLabel));
    }

    [Theory]
    [InlineData("FireDependentImmediately.ipt", 9)]
    [InlineData("NotSilent.ipt", 33)]
    public void RuleOptionBitsAreDecoded(string file, int flags)
    {
        ILogicRule rule = Assert.Single(Load(file).ILogic.Rules);

        Assert.Equal(flags, rule.Flags);
        Assert.Equal(flags == 9, rule.FireDependentImmediately);
        Assert.Equal(flags != 33, rule.SilentOperation);
        Assert.False(rule.IsSuppressed);
        Assert.True(rule.RunsAutomatically);
    }

    [Fact]
    public void NonAsciiRuleNamesAndCodeSurvive()
    {
        ILogicRule rule = Assert.Single(Load("UnicodeRule.ipt").ILogic.Rules);

        Assert.Equal("Größe prüfen 1", rule.Name);
        Assert.StartsWith("' Prüfung: Länge ≤ 10 €", rule.Text);
    }

    [Fact]
    public void AssemblyRulesAreRead()
    {
        ILogicInfo il = Load("AssemblyRules.iam").ILogic;

        Assert.Equal(["AsmRule", "AsmSuppressed"], il.Rules.Select(r => r.Name));
        Assert.False(il.Rules[0].IsSuppressed);
        Assert.True(il.Rules[1].IsSuppressed);
    }

    [Fact]
    public void EmptyTriggerSetWithoutRulesIsNotILogic()
    {
        // SampleBg.idw carries an "iLogicEventsRules" set (from its template) with no triggers in it.
        InventorDocument doc = Samples.Load(Samples.Drawing);

        Assert.Contains(doc.Properties, p => p.SetId == ILogicReader.TriggersPropertySet);
        Assert.True(doc.ILogic.IsEmpty);
    }

    [Fact]
    public void DocumentsWithoutILogicReportNothing()
    {
        Assert.True(Samples.Load(Samples.Part).ILogic.IsEmpty);
        Assert.True(Samples.Load(Samples.Assembly).ILogic.IsEmpty);
    }

    [Fact]
    public void ILogicPropertySetsHaveFriendlyNames()
    {
        InventorDocument doc = Load("OneRuleTriggered.ipt");

        Assert.Contains(doc.Properties, p => p.Set == "iLogic" && p.Name == "RuleStatus");
        Assert.Contains(doc.Properties, p => p.Set == "iLogic Event Triggers" && p.Name == "BeforeDocSave0");
    }

    [Fact]
    public void UnknownEventFallsBackToItsId()
    {
        Assert.Equal("Before Save Document", ILogicReader.EventLabel("BeforeDocSave"));
        Assert.Equal("SomeFutureEvent", ILogicReader.EventLabel("SomeFutureEvent"));
    }

    [Fact]
    public void JsonExportCarriesRulesAndTriggers()
    {
        using JsonDocument json = JsonDocument.Parse(Load("OneRuleTriggered.ipt").ToJson());
        JsonElement il = json.RootElement.GetProperty("iLogic");

        Assert.True(il.GetProperty("hasRules").GetBoolean());
        JsonElement rule = il.GetProperty("rules")[0];
        Assert.Equal("Test", rule.GetProperty("name").GetString());
        Assert.Equal("' this is a test", rule.GetProperty("text").GetString());
        Assert.False(rule.GetProperty("isSuppressed").GetBoolean());
        JsonElement trigger = il.GetProperty("triggers")[0];
        Assert.Equal("BeforeDocSave", trigger.GetProperty("event").GetString());
        Assert.Equal("Test", trigger.GetProperty("rule").GetString());
    }
}
