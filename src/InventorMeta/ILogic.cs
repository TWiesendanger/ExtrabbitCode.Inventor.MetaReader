using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace ExtrabbitCode.Inventor.MetaReader;

/// <summary>One iLogic rule stored inside the document, read from the document's attribute segment.</summary>
public sealed class ILogicRule
{
    /// <summary>The rule's name as shown in the iLogic browser, e.g. "Rule0".</summary>
    public string Name { get; init; } = "";

    /// <summary>The attribute set holding the rule, e.g. "iLogicRule_Name0". The number is an internal
    /// slot id - it is not the rule's position and has gaps once rules were deleted.</summary>
    public string AttributeSet { get; init; } = "";

    /// <summary>The rule's source code exactly as written in the rule editor, or null when the rule's
    /// attributes could not be matched to its name (the name itself is still reliable).</summary>
    public string? Text { get; init; }

    /// <summary>The raw rule-option bit field (iLogicRuleFlags), or null when not readable. Bits, in the
    /// order iLogic's own RuleOptions declares them: 0x01 quick-run mode (on by default),
    /// 0x02 suppressed, 0x04 straight VB code, 0x08 fire dependent rules immediately,
    /// 0x10 don't run automatically, 0x20 silent operation off.</summary>
    public int? Flags { get; init; }

    /// <summary>True when the rule is suppressed (it is kept in the file but never runs).</summary>
    public bool IsSuppressed => Flags is int f && (f & 0x02) != 0;

    /// <summary>True when "Straight VB code" is on (no iLogic preprocessing of the rule text).</summary>
    public bool IsStraightVb => Flags is int f && (f & 0x04) != 0;

    /// <summary>True when "Fire dependent rules immediately" is on.</summary>
    public bool FireDependentImmediately => Flags is int f && (f & 0x08) != 0;

    /// <summary>True when the rule runs automatically on parameter changes ("Don't run automatically" off).</summary>
    public bool RunsAutomatically => Flags is int f && (f & 0x10) == 0;

    /// <summary>True when "Silent operation" is on (the default).</summary>
    public bool SilentOperation => Flags is int f && (f & 0x20) == 0;

    /// <summary>The event triggers that run this rule.</summary>
    public IReadOnlyList<ILogicTrigger> Triggers { get; internal set; } = [];

    public override string ToString() => Name;
}

/// <summary>One iLogic event trigger: an Inventor event that runs a named rule.</summary>
public sealed class ILogicTrigger
{
    /// <summary>The event's internal, language-independent id, e.g. "BeforeDocSave".</summary>
    public string Event { get; init; } = "";

    /// <summary>The event's name as the iLogic Event Triggers dialog shows it (English), e.g.
    /// "Before Save Document"; the raw <see cref="Event"/> id for an event this reader doesn't know.</summary>
    public string EventLabel { get; init; } = "";

    /// <summary>Position of this rule among the rules on the same event (0 = runs first).</summary>
    public int Index { get; init; }

    /// <summary>The rule the event runs - the name of a rule in this document, or of an external rule.</summary>
    public string RuleName { get; init; } = "";

    public override string ToString() => $"{EventLabel} -> {RuleName}";
}

/// <summary>Everything readable about iLogic in a document, without Inventor.</summary>
public sealed class ILogicInfo
{
    public static readonly ILogicInfo None = new();

    /// <summary>True when the document contains at least one iLogic rule (active or suppressed).
    /// Inventor stamps the "_iLogic" property set while the document has rules and removes it
    /// when the last rule is deleted.</summary>
    public bool HasRules { get; init; }

    /// <summary>The raw RuleStatus value of the "_iLogic" property set ("InUse"), or null.</summary>
    public string? RuleStatus { get; init; }

    /// <summary>The rules, in the iLogic browser's order. Can be empty even when <see cref="HasRules"/>
    /// is true, if the attribute segment could not be read (an unknown format revision).</summary>
    public IReadOnlyList<ILogicRule> Rules { get; init; } = [];

    /// <summary>The event triggers configured on the document, in property-set order.</summary>
    public IReadOnlyList<ILogicTrigger> Triggers { get; init; } = [];

    public bool HasTriggers => Triggers.Count > 0;

    /// <summary>True when there is nothing iLogic-related to show.</summary>
    public bool IsEmpty => !HasRules && !HasTriggers;
}

/// <summary>
/// Reads iLogic rules and event triggers straight from an Inventor document.
///
/// Three places in the file carry iLogic data:
/// <list type="bullet">
/// <item>the "_iLogic" OLE property set ({2EB795DD-…}, RuleStatus = "InUse") - present exactly while
///   the document has rules;</item>
/// <item>the "_iLogicEventsRules" property set ({2C540830-…}) - one property per trigger, named
///   event + index ("BeforeDocSave0"), whose value is the rule name;</item>
/// <item>the document's attributes (the "FBAttributeSegment" RSeStorage segment, zstd/zlib compressed):
///   an "iLogicRuleListSet" attribute set whose "iLogicRuleList" byte array maps each rule name to
///   the attribute set holding it ("iLogicRule_Name&lt;n&gt;"), and per rule the attributes
///   iLogicRuleText (the code) and iLogicRuleFlags (the options).</item>
/// </list>
/// Verified against Inventor 2027 parts and assemblies; see docs/INVENTOR-FILE-FORMAT.md.
/// </summary>
public static class ILogicReader
{
    /// <summary>FMTID of the "_iLogic" property set (present while the document has rules).</summary>
    public static readonly Guid RulesPropertySet = new("2eb795dd-e92e-4c88-9a5c-00bd21aea7c8");

    /// <summary>FMTID of the "_iLogicEventsRules" property set (the event triggers).</summary>
    public static readonly Guid TriggersPropertySet = new("2c540830-0723-455e-a8e2-891722eb4c3e");

    /// <summary>English labels of the trigger events, keyed by iLogic's InventorEventCode names (the
    /// ids used in the trigger property names). Labels come from iLogic's own EventNames resources.</summary>
    private static readonly Dictionary<string, string> EventLabels = new(StringComparer.Ordinal)
    {
        ["BeforeDocOpen"] = "Before Open Document",
        ["AfterDocOpen"] = "After Open Document",
        ["DocClose"] = "Close Document",
        ["BeforeDocSave"] = "Before Save Document",
        ["AfterDocSave"] = "After Save Document",
        ["AfterDocNew"] = "New Document",
        ["AfterAnyParamChange"] = "Any Model Parameter Change",
        ["AfterAnyUserParamChange"] = "Any User Parameter Change",
        ["PartBodyChanged"] = "Part Geometry Change",
        ["AfterMaterialChange"] = "Material Change",
        ["AfterAnyiPropertyChange"] = "iProperty Change",
        ["AfterDrawingViewsUpdate"] = "Drawing View Change",
        ["AfterFeatureSuppressionChange"] = "Feature Suppression Change",
        ["AfterComponentSuppressionChange"] = "Component Suppression Change",
        ["AfterComponentReplace"] = "iPart or iAssembly Change Component",
        ["AfterModelStateActivated"] = "Model State Activated",
        ["BeforeVaultCheckIn"] = "Before Vault Check-In",
    };

    /// <summary>The English label for an event id, or the id itself when unknown.</summary>
    public static string EventLabel(string eventId) =>
        EventLabels.TryGetValue(eventId, out string? label) ? label : eventId;

    // Attributes that belong to one rule's attribute set.
    private static readonly HashSet<string> RuleAttributes = new(StringComparer.Ordinal)
    {
        "iLogicType", "iLogicDataVersion", "iLogicRuleText", "iLogicRuleFlags",
        "iLogicRuleParams", "iLogicRuleExecText", "iLogicRuleRefAssems",
    };

    private static readonly byte[] RuleListMarker = Encoding.Unicode.GetBytes("iLogicRuleList");

    /// <summary>Reads the iLogic data of <paramref name="doc"/>. Only decompresses the attribute
    /// segment when the "_iLogic" property set says the document has rules, so documents without
    /// iLogic cost nothing extra.</summary>
    public static ILogicInfo Read(InventorDocument doc)
    {
        if (doc.IsStep)
        {
            return ILogicInfo.None;
        }

        bool hasRulesSet = doc.Properties.Any(p => p.SetId == RulesPropertySet);
        string? status = doc.Properties.FirstOrDefault(p => p.SetId == RulesPropertySet && p.Name == "RuleStatus")?.Display;
        List<ILogicTrigger> triggers = ReadTriggers(doc);

        List<ILogicRule> rules = [];
        if (hasRulesSet)
        {
            try { rules = ReadRules(doc); }
            catch { rules = []; }   // unreadable segment -> we still know rules exist
        }

        foreach (ILogicRule rule in rules)
        {
            rule.Triggers = triggers.Where(t => string.Equals(t.RuleName, rule.Name, StringComparison.Ordinal)).ToList();
        }

        return new ILogicInfo
        {
            HasRules = hasRulesSet || rules.Count > 0,
            RuleStatus = status,
            Rules = rules,
            Triggers = triggers,
        };
    }

    private static readonly Regex TriggerName = new(@"^(?<event>.*?)(?<index>\d+)$", RegexOptions.CultureInvariant);

    private static List<ILogicTrigger> ReadTriggers(InventorDocument doc)
    {
        List<ILogicTrigger> list = [];
        foreach (InventorDocument.PropEntry p in doc.Properties.Where(p => p.SetId == TriggersPropertySet))
        {
            // PID 255 is the set's own name; everything else is "<event><index>" = rule name.
            if (p.Pid == 255 || p.Value is not string rule || rule.Length == 0)
            {
                continue;
            }
            Match m = TriggerName.Match(p.Name);
            string ev = m.Success && m.Groups["event"].Length > 0 ? m.Groups["event"].Value : p.Name;
            int index = m.Success && m.Groups["event"].Length > 0 ? int.Parse(m.Groups["index"].Value) : 0;
            list.Add(new ILogicTrigger { Event = ev, EventLabel = EventLabel(ev), Index = index, RuleName = rule });
        }
        return list;
    }

    private static List<ILogicRule> ReadRules(InventorDocument doc)
    {
        // The attribute segment is small; prefer it by type name, fall back to every segment for a
        // document type whose attribute segment is named differently.
        List<InventorDocument.Segment> candidates = doc.Segments
            .Where(s => s.Name.Contains("Attribute", StringComparison.OrdinalIgnoreCase)).ToList();
        if (candidates.Count == 0)
        {
            candidates = doc.Segments.ToList();
        }

        using CompoundFile cf = new(doc.FilePath);
        foreach (InventorDocument.Segment seg in candidates)
        {
            CompoundFile.DirEntry? entry = cf.Directory.FirstOrDefault(e =>
                e.Type == 2 && string.Equals(e.Path, "/RSeStorage/B" + seg.Id, StringComparison.OrdinalIgnoreCase));
            if (entry == null)
            {
                continue;
            }
            byte[]? image = SegmentDataCheck.TryInflate(cf.ReadEntry(entry));
            if (image == null || image.AsSpan().IndexOf(RuleListMarker) < 0)
            {
                continue;
            }
            List<ILogicRule> rules = ParseAttributeSegment(image);
            if (rules.Count > 0)
            {
                return rules;
            }
        }
        return [];
    }

    /// <summary>One attribute record found in the decompressed attribute segment.</summary>
    private sealed record Attribute(int Offset, string Name, byte Type, object? Value);

    /// <summary>Parses the rules out of a decompressed attribute segment.
    ///
    /// Attribute records are <c>u32 nameChars | UTF-16 name | u8 type | 39 00 00 | value</c> with
    /// type 1 = int32, 3 = string (<c>u32 chars | UTF-16</c>), 4 = byte array
    /// (<c>02 00 00 30 | u32 len | u32 len | u32 | bytes</c>). Attribute sets are declared first, in
    /// order; their attributes follow, set by set, in that same order. So the k-th group of rule
    /// attributes belongs to the k-th declared "iLogicRule_…" set.</summary>
    internal static List<ILogicRule> ParseAttributeSegment(byte[] img)
    {
        List<Attribute> attrs = ScanAttributes(img);
        Attribute? listAttr = attrs.FirstOrDefault(a => a.Name == "iLogicRuleList" && a.Value is byte[]);
        if (listAttr == null)
        {
            return [];
        }
        List<(string Rule, string Set)> entries = ParseRuleList((byte[])listAttr.Value!);
        if (entries.Count == 0)
        {
            return [];
        }

        // Group consecutive rule attributes; a repeated name or a non-rule attribute starts a new group.
        List<List<Attribute>> groups = [];
        List<Attribute>? current = null;
        foreach (Attribute a in attrs)
        {
            if (!RuleAttributes.Contains(a.Name))
            {
                current = null;
                continue;
            }
            if (current == null || current.Any(x => x.Name == a.Name))
            {
                current = [];
                groups.Add(current);
            }
            current.Add(a);
        }

        // Order the rule sets by where their declaration sits in the segment (a set record carries its
        // name with a character-count prefix; the rule list blob uses a byte-count prefix instead).
        List<string> setOrder = entries
            .Select(e => (e.Set, Offset: FindCountedString(img, e.Set)))
            .Where(x => x.Offset >= 0)
            .OrderBy(x => x.Offset)
            .Select(x => x.Set)
            .ToList();
        Dictionary<string, List<Attribute>> bySet = new(StringComparer.Ordinal);
        if (setOrder.Count == entries.Count && groups.Count == setOrder.Count)
        {
            for (int k = 0; k < groups.Count; k++)
            {
                bySet[setOrder[k]] = groups[k];
            }
        }

        List<ILogicRule> rules = [];
        foreach ((string name, string set) in entries)
        {
            bySet.TryGetValue(set, out List<Attribute>? g);
            rules.Add(new ILogicRule
            {
                Name = name,
                AttributeSet = set,
                Text = g?.FirstOrDefault(a => a.Name == "iLogicRuleText")?.Value as string,
                Flags = g?.FirstOrDefault(a => a.Name == "iLogicRuleFlags")?.Value as int?,
            });
        }
        return rules;
    }

    /// <summary>iLogicRuleList: <c>u8 version | u32 count | count × (u32 bytes | UTF-16 rule name |
    /// u32 bytes | UTF-16 attribute set name)</c>.</summary>
    private static List<(string Rule, string Set)> ParseRuleList(byte[] b)
    {
        List<(string, string)> list = [];
        if (b.Length < 5)
        {
            return list;
        }
        int count = BitConverter.ToInt32(b, 1);
        int i = 5;
        for (int k = 0; k < count; k++)
        {
            if (!ReadByteCountedString(b, ref i, out string rule) || !ReadByteCountedString(b, ref i, out string set))
            {
                return [];   // malformed: better nothing than misaligned names
            }
            list.Add((rule, set));
        }
        return list;
    }

    private static bool ReadByteCountedString(byte[] b, ref int i, out string s)
    {
        s = "";
        if (i + 4 > b.Length)
        {
            return false;
        }
        int bytes = BitConverter.ToInt32(b, i);
        if (bytes < 0 || bytes % 2 != 0 || i + 4 + bytes > b.Length)
        {
            return false;
        }
        s = Encoding.Unicode.GetString(b, i + 4, bytes);
        i += 4 + bytes;
        return true;
    }

    /// <summary>Offset of <c>u32 chars | UTF-16 text</c> in <paramref name="img"/>, or -1.</summary>
    private static int FindCountedString(byte[] img, string text)
    {
        byte[] pattern = new byte[4 + text.Length * 2];
        BitConverter.GetBytes(text.Length).CopyTo(pattern, 0);
        Encoding.Unicode.GetBytes(text).CopyTo(pattern, 4);
        return img.AsSpan().IndexOf(pattern);
    }

    private static List<Attribute> ScanAttributes(byte[] img)
    {
        List<Attribute> list = [];
        int i = 0;
        while (i + 4 <= img.Length)
        {
            if (TryReadAttribute(img, i, out Attribute? a, out int end))
            {
                list.Add(a!);
                i = end;   // skip the value, so rule code can never be mistaken for a record
            }
            else
            {
                i++;
            }
        }
        return list;
    }

    private static bool TryReadAttribute(byte[] img, int i, out Attribute? attr, out int end)
    {
        attr = null;
        end = i;
        int n = BitConverter.ToInt32(img, i);
        if (n < 3 || n > 128)
        {
            return false;
        }
        int s = i + 4, t = s + 2 * n;
        if (t + 4 > img.Length)
        {
            return false;
        }
        for (int k = 0; k < n; k++)
        {
            byte lo = img[s + 2 * k], hi = img[s + 2 * k + 1];
            if (hi != 0 || !(lo is >= (byte)'A' and <= (byte)'Z' or >= (byte)'a' and <= (byte)'z' or >= (byte)'0' and <= (byte)'9' or (byte)'_'))
            {
                return false;
            }
        }
        if (img[t + 1] != 0x39 || img[t + 2] != 0 || img[t + 3] != 0)
        {
            return false;   // a name without a value marker: an attribute set declaration, not an attribute
        }

        string name = Encoding.Unicode.GetString(img, s, 2 * n);
        int v = t + 4;
        switch (img[t])
        {
            case 1 when v + 4 <= img.Length:
                attr = new Attribute(i, name, 1, BitConverter.ToInt32(img, v));
                end = v + 4;
                return true;
            case 3 when v + 4 <= img.Length:
            {
                int chars = BitConverter.ToInt32(img, v);
                if (chars < 0 || v + 4 + 2L * chars > img.Length)
                {
                    return false;
                }
                attr = new Attribute(i, name, 3, Encoding.Unicode.GetString(img, v + 4, 2 * chars));
                end = v + 4 + 2 * chars;
                return true;
            }
            case 4 when v + 16 <= img.Length && img[v] == 0x02 && img[v + 3] == 0x30:
            {
                int len = BitConverter.ToInt32(img, v + 4);
                if (len < 0 || v + 16L + len > img.Length)
                {
                    return false;
                }
                attr = new Attribute(i, name, 4, img.AsSpan(v + 16, len).ToArray());
                end = v + 16 + len;
                return true;
            }
            default:
                // other value types (e.g. double) aren't needed here; record the name and move on
                attr = new Attribute(i, name, img[t], null);
                end = v;
                return true;
        }
    }
}
