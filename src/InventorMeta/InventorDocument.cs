using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ExtrabbitCode.Inventor.MetaReader;

/// <summary>
/// High-level, friendly view of an Inventor document, built from the raw CFB
/// container: document type, resolved iProperties, thumbnail, referenced files,
/// model states, and version provenance - all without Autodesk Inventor installed.
/// </summary>
public sealed class InventorDocument
{
    public enum DocKind { Unknown, Part, Assembly, Drawing, Presentation }

    public string FilePath { get; }
    public string FileName => Path.GetFileName(FilePath);
    public Guid   RootClsid { get; private set; }
    public string DocumentType { get; private set; }
    public DocKind Kind { get; private set; }
    public string CfbVersionInfo { get; private set; }

    public sealed class PropEntry
    {
        public string Set = "";   public Guid SetId;
        public uint   Pid;        public string Name = "";
        public string Type = "";  public object? Value;
        public string Display => Value switch
        {
            null => "",
            bool b => b ? "Yes" : "No",
            DateTime dt => dt.ToString("yyyy-MM-dd HH:mm:ss") + " UTC",
            System.Collections.IEnumerable en and not string => string.Join(" | ", en.Cast<object>()),
            _ => Value.ToString() ?? ""
        };
    }

    /// <summary>A Content Center category a part belongs to (Tube &amp; Pipe, Frame Generator, …),
    /// parsed from the "Categories" iProperty. <see cref="Mnemonic"/> is the stable internal tag
    /// (e.g. <c>TUBEANDPIPE</c>); <see cref="DisplayName"/> is localized.</summary>
    public sealed class ContentCategory
    {
        public string DisplayName = "";    // e.g. "Tube & Pipe"
        public string InternalName = "";   // category id (GUID), e.g. 4347fa0f-2144-441d-94cd-e3e15c92b736
        public string Mnemonic = "";       // stable, language-independent tag, e.g. "TUBEANDPIPE"
        public override string ToString() => DisplayName.Length > 0 ? DisplayName : Mnemonic;
    }

    /// <summary>A single model state (a.k.a. iPart/iAssembly member) with its own properties.</summary>
    public sealed class ModelState
    {
        public string Name = "";          // e.g. "Model State1" (or the raw storage id if unmapped)
        public string StorageName = "";   // the obfuscated /MemberDocs/<id> storage name
        public bool   IsActive;           // true if this state's stored properties match the active document
        public List<PropEntry> Properties = [];
        public Dictionary<string, string> Summary = new();
    }

    public List<PropEntry> Properties { get; } = [];
    /// <summary>Best-effort key iProperties pulled from Design Tracking + Summary sets.</summary>
    public Dictionary<string, string> Summary { get; } = new();
    /// <summary>Per-model-state properties, read directly from embedded member docs - no Inventor needed.</summary>
    public List<ModelState> ModelStateDetails { get; } = [];
    public bool HasModelStates => ModelStateDetails.Count > 0;
    private readonly Dictionary<string, string> _storageToState = new(StringComparer.OrdinalIgnoreCase);
    private string _primaryName = "[Primary]";
    public List<string> References { get; } = [];

    private List<ContentCategory>? _categories;

    /// <summary>Content Center categories this part belongs to (Tube &amp; Pipe, Frame Generator, …),
    /// parsed from the readable "Categories" iProperty. Empty for an ordinary part. This is the
    /// file-readable counterpart to Inventor's <c>DocumentInterests.HasInterest</c>, whose client-id
    /// registry lives in the proprietary RSeStorage database and is not decoded here.</summary>
    public IReadOnlyList<ContentCategory> Categories => _categories ??= ParseCategories();

    /// <summary>True if the part carries a Content Center category with the given mnemonic
    /// (case-insensitive), e.g. <c>HasCategory("TUBEANDPIPE")</c> for a Tube &amp; Pipe component.</summary>
    public bool HasCategory(string mnemonic) =>
        Categories.Any(c => string.Equals(c.Mnemonic, mnemonic, StringComparison.OrdinalIgnoreCase));

    /// <summary>An Inventor design subsystem detected as participating in this document
    /// (Frame Generator, Tube &amp; Pipe, …). <see cref="Key"/> is a stable, language-independent id.</summary>
    public sealed class DocumentSubsystem
    {
        public string Key = "";          // stable id, e.g. "FrameGenerator", "TubeAndPipe"
        public string DisplayName = "";  // "Frame Generator", "Tube & Pipe"
        public override string ToString() => DisplayName.Length > 0 ? DisplayName : Key;
    }

    // Frame Generator stamps its frame document with this dedicated property set ("_com.autodesk.FG").
    private static readonly Guid FrameGeneratorSet = new("b65df8ea-ba84-4eb5-868d-466b48dab15a");
    // Design Accelerator stamps its generated documents with the "FDesign" property set.
    private static readonly Guid DesignAcceleratorSet = new("6cd3181a-0c7d-41aa-bdb3-969a9b72e1bb");
    // A weldment assembly is identified by its document subtype CLSID (Document SubType, PID 31).
    private static readonly Guid WeldmentSubType = new("28ec8354-9024-440f-a8a2-0e0e55d635b0");
    // A sheet metal part is identified the same way, by its own document subtype CLSID.
    private static readonly Guid SheetMetalSubType = new("9c464203-9bae-11d3-8bad-0060b0ce6bb4");

    /// <summary>True if the document's subtype (the "Document SubType" iProperty) is the given id.</summary>
    private bool HasSubType(Guid id) => Properties.Any(p => p.Name == "Document SubType" &&
        ((p.Value is Guid g && g == id) || (Guid.TryParse(p.Display, out Guid d) && d == id)));

    /// <summary>True for a weldment assembly (identified by its document subtype, not a localized name).</summary>
    public bool IsWeldment => HasSubType(WeldmentSubType);

    /// <summary>True for a sheet metal part (identified by its document subtype, not a localized name).</summary>
    public bool IsSheetMetal => HasSubType(SheetMetalSubType);

    private List<DocumentSubsystem>? _subsystems;

    /// <summary>Inventor design subsystems this document participates in, detected from readable
    /// markers in the file - Frame Generator (its property set), Tube &amp; Pipe (a Content Center
    /// category), etc. This is the file-readable analogue to Inventor's DocumentInterests /
    /// HasInterest, whose add-in client ids live in the proprietary RSeStorage database we do not
    /// decode. Empty for an ordinary document.</summary>
    public IReadOnlyList<DocumentSubsystem> Subsystems => _subsystems ??= DetectSubsystems();

    /// <summary>True if the document participates in the subsystem with the given key
    /// (case-insensitive), e.g. <c>HasSubsystem("FrameGenerator")</c>.</summary>
    public bool HasSubsystem(string key) =>
        Subsystems.Any(s => string.Equals(s.Key, key, StringComparison.OrdinalIgnoreCase));

    /// <summary>A single, headline classification of the document for at-a-glance display.
    /// More specific roles win over the generic Content Center membership.</summary>
    public enum DocCategory
    {
        General, ContentCenter, FrameGenerator, DesignAccelerator, Weldment, SheetMetal, Piping,
        iPartFactory, iPartMember, iAssemblyFactory, iAssemblyMember
    }

    /// <summary>True for an iPart/iAssembly <em>factory</em> (the authoring document): it carries the
    /// member table and the "Parameterized Template" flag. Model-state parts also carry the member
    /// marker, so they are excluded via <see cref="HasModelStates"/>.</summary>
    public bool IsFactory => IsIPart && !HasModelStates && PropTrue("Parameterized Template");

    /// <summary>True for a generated iPart/iAssembly <em>member</em>: it carries the member table but
    /// is not the factory (no "Parameterized Template") and is not a model-state part. iPart members
    /// also carry a "Template Row" but iAssembly members do not, so we key off "marker, not factory".</summary>
    public bool IsMember => IsIPart && !HasModelStates && !IsFactory;

    /// <summary>The document's headline category. Tube &amp; Pipe and Frame Generator win first, then
    /// the iPart/iAssembly factory/member role, then plain Content Center membership; everything else
    /// is <see cref="DocCategory.General"/>.</summary>
    public DocCategory PrimaryCategory =>
        HasSubsystem("TubeAndPipe")               ? DocCategory.Piping
      : HasSubsystem("FrameGenerator")            ? DocCategory.FrameGenerator
      : HasSubsystem("DesignAccelerator")         ? DocCategory.DesignAccelerator
      : IsWeldment                                ? DocCategory.Weldment
      : IsSheetMetal                              ? DocCategory.SheetMetal
      : Kind == DocKind.Assembly && IsFactory     ? DocCategory.iAssemblyFactory
      : Kind == DocKind.Assembly && IsMember      ? DocCategory.iAssemblyMember
      : Kind == DocKind.Part     && IsFactory     ? DocCategory.iPartFactory
      : Kind == DocKind.Part     && IsMember      ? DocCategory.iPartMember
      : Categories.Count > 0                      ? DocCategory.ContentCenter
      :                                             DocCategory.General;

    private bool PropTrue(string name) => Properties.Any(p => p.Name == name && p.Value is bool b && b);

    private List<DocumentSubsystem> DetectSubsystems()
    {
        List<DocumentSubsystem> found = [];
        if (Properties.Any(p => p.SetId == FrameGeneratorSet))
        {
            found.Add(new DocumentSubsystem { Key = "FrameGenerator", DisplayName = "Frame Generator" });
        }
        if (Properties.Any(p => p.SetId == DesignAcceleratorSet))
        {
            found.Add(new DocumentSubsystem { Key = "DesignAccelerator", DisplayName = "Design Accelerator" });
        }
        if (HasCategory("TUBEANDPIPE"))
        {
            found.Add(new DocumentSubsystem { Key = "TubeAndPipe", DisplayName = "Tube & Pipe" });
        }
        return found;
    }

    /// <summary>Linked non-model files (images, imported CAD, …) referenced by the document.</summary>
    public List<string> LinkedFiles { get; } = [];

    /// <summary>True for an iPart/iAssembly factory or one of its generated members
    /// (both carry the member table); false for an ordinary part.</summary>
    public bool IsIPart { get; private set; }

    private static readonly byte[] IPartMarker = Encoding.ASCII.GetBytes("MemberDesel");

    private static bool ContainsBytes(byte[] data, byte[] pattern)
    {
        for (int i = 0; i + pattern.Length <= data.Length; i++)
        {
            int j = 0;
            while (j < pattern.Length && data[i + j] == pattern[j]) { j++; }
            if (j == pattern.Length) { return true; }
        }
        return false;
    }
    public List<string> ModelStates { get; } = [];
    public Dictionary<string, string> VersionInfo { get; } = new();
    public byte[]? Thumbnail { get; private set; }      // normalized PNG (or BMP) bytes
    public string  ThumbnailExt { get; private set; } = "";

    public InventorDocument(string path)
    {
        FilePath = path;
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        using CompoundFile cf = new(path);
        RootClsid = cf.Directory[0].Clsid;
        DocumentType = InventorProps.DocClass.TryGetValue(RootClsid, out string? dt) ? dt : $"Unknown ({RootClsid})";
        Kind = RootClsid.ToString().ToUpperInvariant() switch
        {
            "4D29B490-49B2-11D0-93C3-7E0706000000" => DocKind.Part,
            "E60F81E1-49B3-11D0-93C3-7E0706000000" => DocKind.Assembly,
            "BBF9FDF1-52DC-11D0-8C04-0800090BE8EC" => DocKind.Drawing,
            "76283A80-50DD-11D3-A7E3-00C04F79D7BC" => DocKind.Presentation,
            _ => DocKind.Unknown
        };
        CfbVersionInfo = $"CFB v{cf.MajorVersion}, {cf.SectorSize}-byte sectors";

        // Member storages under /MemberDocs/ are the model states (and iPart/iAssembly
        // members) - each is a full embedded sub-document with its own property sets.
        HashSet<string> memberStorages = new(
            cf.Directory.Where(d => d.Type == 1)
                .Select(d => Regex.Match(d.Path, @"^/MemberDocs/([^/]+)$", RegexOptions.IgnoreCase))
                .Where(m => m.Success).Select(m => m.Groups[1].Value),
            StringComparer.OrdinalIgnoreCase);

        Dictionary<string, List<byte[]>> memberStreams = new(StringComparer.OrdinalIgnoreCase);

        foreach (CompoundFile.DirEntry e in cf.Directory.Where(d => d.Type == 2))
        {
            byte[] data;
            try { data = cf.ReadEntry(e); } catch { continue; }

            // iPart factories and their members both carry the iPart member table marker
            // ("MemberDesel"); ordinary parts do not.
            if (!IsIPart && ContainsBytes(data, IPartMarker))
            {
                IsIPart = true;
            }

            // bucket model-state (member) property-set streams by their member storage
            Match mm = Regex.Match(e.Path, @"^/MemberDocs/([^/]+)/[^/]+$", RegexOptions.IgnoreCase);
            if (e.Path.Contains("/MemberDocs/", StringComparison.OrdinalIgnoreCase))
            {
                if (mm.Success && PropertySet.IsPropertySet(data))
                {
                    string st = mm.Groups[1].Value;
                    (memberStreams.TryGetValue(st, out List<byte[]>? l) ? l : memberStreams[st] = []).Add(data);
                }
                continue;
            }

            // ----- primary (active) document property sets -----
            if (PropertySet.IsPropertySet(data))
            {
                ParsePropertySetInto(data, Properties, allowThumbnail: true);
                continue;
            }

            // ----- UFRxDoc: references, provenance, model-state↔storage mapping -----
            if (e.Name.EndsWith("UFRxDoc", StringComparison.OrdinalIgnoreCase))
            {
                ParseUFRxDoc(data, memberStorages);
            }
        }
        BuildSummaryInto(Properties, Summary);
        ExtractVersionDetails();

        // Build one ModelState per member storage (these are the NON-active states).
        foreach (KeyValuePair<string, List<byte[]>> kv in memberStreams)
        {
            ModelState ms = new()
            {
                StorageName = kv.Key,
                Name = _storageToState.TryGetValue(kv.Key, out string? nm) ? nm : kv.Key,
                IsActive = false
            };
            foreach (byte[] data in kv.Value)
            {
                ParsePropertySetInto(data, ms.Properties, allowThumbnail: false);
            }

            BuildSummaryInto(ms.Properties, ms.Summary);
            ModelStateDetails.Add(ms);
        }
        ModelStateDetails.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

        // The active/primary state IS the top-level document (it lives outside MemberDocs).
        // Surface it as a first-class state so all states are visible together.
        if (ModelStateDetails.Count > 0)
        {
            ModelStateDetails.Insert(0, new ModelState
            {
                Name = _primaryName, StorageName = "(active document)", IsActive = true,
                Properties = Properties, Summary = Summary
            });
        }
    }

    private static readonly Guid DesignTrackingFmt = new("32853F0F-3444-11D1-9E93-0060B03C1CA6");
    private static readonly Guid DesignTrackingControlFmt = new("D861FB30-3136-11D1-9E92-0060B03C1CA6");

    /// <summary>Surface the fields shown on Windows Explorer's "Details" tab (version history,
    /// who/what last touched the file) into <see cref="VersionInfo"/>. "Created with" and
    /// "Needs Migrating" are computed by Inventor, not stored, so they can't be recreated.</summary>
    private void ExtractVersionDetails()
    {
        void Add(string label, Guid set, uint pid)
        {
            PropEntry? p = Properties.FirstOrDefault(x => x.SetId == set && x.Pid == pid && x.Display.Length > 0);
            if (p != null && !VersionInfo.ContainsKey(label))
            {
                VersionInfo[label] = p.Display;
            }
        }
        Add("Current Version", DesignTrackingControlFmt, 14);
        Add("Previous Version", DesignTrackingControlFmt, 15);
        Add("Next Version", DesignTrackingControlFmt, 13);
        Add("Last update with", DesignTrackingFmt, 67);
        Add("Last saved by", DesignTrackingControlFmt, 16);
    }

    /// <summary>Parse one OLE property set, appending entries to <paramref name="into"/>.</summary>
    private void ParsePropertySetInto(byte[] data, List<PropEntry> into, bool allowThumbnail)
    {
        PropertySet.ParsedSet set = PropertySet.Parse(data);
        foreach (PropertySet.Section sec in set.Sections)
        {
            string setName = InventorProps.SetNames.TryGetValue(sec.FmtId, out string? sn)
                ? sn : (sec.FmtNameStr.Length > 0 ? sec.FmtNameStr : sec.FmtId.ToString());

            foreach (PropertySet.Prop pr in sec.Props)
            {
                if (pr.Id == 1)
                {
                    continue;            // codepage
                }

                if (pr.Id == 0x80000000)
                {
                    continue;   // internal flags
                }

                string name = pr.Name.StartsWith("PID")
                    ? InventorProps.Name(sec.FmtId, pr.Id) : pr.Name;
                object? val = pr.Value is PropertySet.Blob bl ? bl.ToString() : pr.Value;
                if (sec.FmtId == DesignTrackingFmt && pr is { Id: 40, Value: int ds })
                {
                    val = InventorProps.DesignStatus(ds);
                }

                into.Add(new PropEntry {
                    Set = setName, SetId = sec.FmtId, Pid = pr.Id,
                    Name = name, Type = pr.TypeName, Value = val
                });

                if (allowThumbnail && pr.Value is PropertySet.Blob { Kind: "CF" } cf2 && Thumbnail == null)
                {
                    ExtractThumbnail(cf2.Data);
                }
            }
        }
    }

    private void ExtractThumbnail(byte[] img)
    {
        int skip = -1;
        for (int off = 0; off < Math.Min(64, img.Length - 4); off++)
        {
            if (Eq(img, off, 0x89,0x50,0x4E,0x47) || Eq(img, off, 0x42,0x4D) || Eq(img, off, 0x28,0,0,0))
            { skip = off; break; }
        }
        if (skip < 0)
        {
            return;
        }

        byte[] body = img[skip..];
        if (Eq(body, 0, 0x89,0x50)) { Thumbnail = body; ThumbnailExt = "png"; }
        else if (Eq(body, 0, 0x42,0x4D)) { Thumbnail = body; ThumbnailExt = "bmp"; }
        else { Thumbnail = WrapDib(body); ThumbnailExt = "bmp"; }
    }

    // UFRxDoc is proprietary; we robustly scrape UTF-16 strings for the useful bits.
    private void ParseUFRxDoc(byte[] data, HashSet<string> memberStorages)
    {
        string txt = Encoding.Unicode.GetString(data);

        // model-state name ↔ member-storage mapping.
        List<string> tokens = Regex.Matches(txt, @"[\x20-\x7E]{3,}").Select(m => m.Value).ToList();
        bool IsState(string t) => Regex.IsMatch(t, @"^Model State\d*$");

        // A custom state name (not "Model State<n>") sits in a readable token right beside the
        // storage id. Exclude things that are clearly not a name: other storage ids, the
        // bracketed primary token, file paths/names.
        bool IsName(string t) =>
            t.Length is >= 2 and <= 64 &&
            !memberStorages.Contains(t) &&
            t.IndexOfAny(['\\', '/', ':']) < 0 &&
            !Regex.IsMatch(t, @"^\[.*\]$") &&
            !Regex.IsMatch(t, @"\.(?:ipt|iam|idw|ipn)$", RegexOptions.IgnoreCase) &&
            Regex.IsMatch(t, @"[A-Za-z]");

        HashSet<string> usedNames = [];
        foreach (string storage in memberStorages)
        {
            int i = tokens.IndexOf(storage);
            if (i < 0)
            {
                continue;
            }

            // Prefer a real "Model State<n>" token near the id (order varies: name before the id
            // in parts, after it in assemblies, so scan outward both ways).
            string? name = null;
            for (int d = 1; d <= 8 && name == null; d++)
            {
                if (i - d >= 0 && IsState(tokens[i - d]) && usedNames.Add(tokens[i - d]))
                {
                    name = tokens[i - d];
                }
                else if (i + d < tokens.Count && IsState(tokens[i + d]) && usedNames.Add(tokens[i + d]))
                {
                    name = tokens[i + d];
                }
            }

            // Otherwise fall back to the readable token immediately beside the id (custom name).
            if (name == null)
            {
                foreach (int j in new[] { i - 1, i + 1 })
                {
                    if (j >= 0 && j < tokens.Count && IsName(tokens[j]) && usedNames.Add(tokens[j]))
                    {
                        name = tokens[j];
                        break;
                    }
                }
            }

            if (name != null)
            {
                _storageToState[storage] = name;
            }
        }
        // the primary/active state name (e.g. "[Primary]") sits in a bracketed token
        string? prim = tokens.FirstOrDefault(t => Regex.IsMatch(t, @"^\[[^\]]+\]$"));
        if (prim != null)
        {
            _primaryName = prim;
        }

        // version/provenance lines
        foreach (Match m in Regex.Matches(txt, @"(File Schema|Software Schema|Saved From|Saved On):\s*([\x20-\x7E]+)"))
        {
            VersionInfo[m.Groups[1].Value] = m.Groups[2].Value.Trim();
        }

        // the originating template appears in a watermark: "Document <path> was created using"
        string? template = null;
        Match tm = Regex.Match(txt, @"Document ([A-Za-z]:\\[^\x00-\x1F]*?\.(?:ipt|iam|idw|ipn)) was created", RegexOptions.IgnoreCase);
        if (tm.Success) { template = tm.Groups[1].Value; VersionInfo["Template"] = template; }

        // Referenced documents + linked non-model files. Scan both UTF-16 byte alignments:
        // a path can sit at an odd byte offset (after a variable-length binary blob), which
        // a single pairwise decode from byte 0 would garble and miss.
        string[] scans = data.Length > 1
            ? [txt, Encoding.Unicode.GetString(data, 1, data.Length - 1)]
            : [txt];
        foreach (string scan in scans)
        {
            // referenced document paths (non-anchored: trailing separator bytes are common)
            foreach (Match m in Regex.Matches(scan, @"[A-Za-z]:\\[^\x00-\x1F""<>|*?]*?\.(?:ipt|iam|idw|ipn)", RegexOptions.IgnoreCase))
            {
                string p = m.Value;
                bool self = string.Equals(InventorPath.GetFileName(p), FileName, StringComparison.OrdinalIgnoreCase);
                bool isTemplate = template != null && string.Equals(p, template, StringComparison.OrdinalIgnoreCase);
                if (!self && !isTemplate && !References.Contains(p, StringComparer.OrdinalIgnoreCase))
                {
                    References.Add(p);
                }
            }

            // linked non-model files (images, imported CAD) - same absolute-path form
            foreach (Match m in Regex.Matches(scan,
                @"[A-Za-z]:\\[^\x00-\x1F""<>|*?]*?\.(?:png|jpg|jpeg|bmp|tif|tiff|gif|dwg|dxf|stp|step|igs|iges|sat)",
                RegexOptions.IgnoreCase))
            {
                string p = m.Value;
                if (!LinkedFiles.Contains(p, StringComparer.OrdinalIgnoreCase))
                {
                    LinkedFiles.Add(p);
                }
            }
        }

        // model-state names
        foreach (Match m in Regex.Matches(txt, @"Model State\d*"))
        {
            if (!ModelStates.Contains(m.Value))
            {
                ModelStates.Add(m.Value);
            }
        }
    }

    private void BuildSummaryInto(List<PropEntry> props, Dictionary<string, string> summary)
    {
        void Put(string label, string setId, uint pid)
        {
            PropEntry? p = props.FirstOrDefault(x =>
                x.SetId == new Guid(setId) && x.Pid == pid && x.Display.Length > 0);
            if (p != null && !summary.ContainsKey(label))
            {
                summary[label] = p.Display;
            }
        }
        const string DT = "32853F0F-3444-11D1-9E93-0060B03C1CA6";
        Put("Part Number", DT, 5);
        Put("Description", DT, 29);
        Put("Designer", DT, 41);
        Put("Engineer", DT, 42);
        Put("Authority", DT, 43);
        Put("Project", DT, 7);
        Put("Material", DT, 20);
        Put("Stock Number", DT, 55);
        Put("Vendor", DT, 30);
        Put("Cost", DT, 36);
        Put("Design Status", DT, 40);
        Put("Creation Date", DT, 4);
        Put("Mass", DT, 58);
        Put("Volume", DT, 60);
        Put("Surface Area", DT, 59);
        Put("Density", DT, 61);
        Put("Last Updated With", DT, 67);
        Put("Title", "F29F85E0-4FF9-1068-AB91-08002B27B3D9", 2);
        Put("Revision Number", "F29F85E0-4FF9-1068-AB91-08002B27B3D9", 9);
    }

    public string ToJson()
    {
        var o = new
        {
            file = FileName,
            documentType = DocumentType,
            rootClsid = RootClsid.ToString(),
            container = CfbVersionInfo,
            summary = Summary,
            versionInfo = VersionInfo,
            modelStates = ModelStateDetails.Select(s => new {
                name = s.Name, storage = s.StorageName, isActive = s.IsActive,
                summary = s.Summary,
                properties = s.Properties.GroupBy(p => p.Set).ToDictionary(
                    g => g.Key, g => g.Select(p => new { pid = p.Pid, name = p.Name, type = p.Type, value = p.Display }).ToArray())
            }).ToArray(),
            references = References,
            linkedFiles = LinkedFiles,
            categories = Categories.Select(c => new { displayName = c.DisplayName, internalName = c.InternalName, mnemonic = c.Mnemonic }).ToArray(),
            subsystems = Subsystems.Select(s => new { key = s.Key, displayName = s.DisplayName }).ToArray(),
            primaryCategory = PrimaryCategory.ToString(),
            isIPart = IsIPart,
            hasThumbnail = Thumbnail != null,
            properties = Properties
                .GroupBy(p => p.Set)
                .ToDictionary(g => g.Key, g => g.Select(p => new {
                    pid = p.Pid, name = p.Name, type = p.Type, value = p.Display
                }).ToArray())
        };
        return JsonSerializer.Serialize(o, new JsonSerializerOptions { WriteIndented = true });
    }

    // ---- content-center categories ----
    /// <summary>Parses the "Categories" iProperty, whose value is a small XML fragment like
    /// <c>&lt;MemberInstance&gt;&lt;Categories&gt;&lt;Category DisplayName="Tube &amp;amp; Pipe"
    /// InternalName="…" Mnemonic="TUBEANDPIPE"&gt;…&lt;/Category&gt;…</c>. Robust to attribute order
    /// and to a malformed value (returns what it can, else empty).</summary>
    private List<ContentCategory> ParseCategories()
    {
        List<ContentCategory> list = [];
        if (Properties.FirstOrDefault(p => p.Name == "Categories")?.Value is not string xml || xml.Length == 0)
        {
            return list;
        }

        foreach (Match tag in Regex.Matches(xml, @"<Category\b[^>]*>", RegexOptions.IgnoreCase))
        {
            ContentCategory c = new()
            {
                DisplayName  = Attr(tag.Value, "DisplayName"),
                InternalName = Attr(tag.Value, "InternalName"),
                Mnemonic     = Attr(tag.Value, "Mnemonic"),
            };
            if (c.DisplayName.Length + c.InternalName.Length + c.Mnemonic.Length > 0) { list.Add(c); }
        }
        return list;
    }

    private static string Attr(string tag, string name)
    {
        Match m = Regex.Match(tag, name + "\\s*=\\s*\"([^\"]*)\"", RegexOptions.IgnoreCase);
        return m.Success ? DecodeXml(m.Groups[1].Value) : "";
    }

    /// <summary>Decodes the five predefined XML entities. <c>&amp;amp;</c> is undone last so an
    /// already-decoded ampersand is never re-interpreted.</summary>
    private static string DecodeXml(string s) => s
        .Replace("&lt;", "<").Replace("&gt;", ">").Replace("&quot;", "\"").Replace("&apos;", "'")
        .Replace("&amp;", "&");

    // ---- helpers ----
    private static bool Eq(byte[] b, int o, params int[] sig)
    {
        if (o + sig.Length > b.Length)
        {
            return false;
        }

        for (int i = 0; i < sig.Length; i++)
        {
            if (b[o+i] != (byte)sig[i])
            {
                return false;
            }
        }

        return true;
    }
    private static byte[] WrapDib(byte[] dib)
    {
        uint headerSize = BitConverter.ToUInt32(dib, 0);
        ushort bpp = BitConverter.ToUInt16(dib, 14);
        uint clrUsed = dib.Length >= 36 ? BitConverter.ToUInt32(dib, 32) : 0;
        uint pal = bpp <= 8 ? (clrUsed != 0 ? clrUsed : (1u << bpp)) : 0;
        uint pix = 14 + headerSize + pal * 4;
        using MemoryStream ms = new();
        using BinaryWriter bw = new(ms);
        bw.Write((byte)'B'); bw.Write((byte)'M');
        bw.Write((uint)(14 + dib.Length)); bw.Write((uint)0); bw.Write(pix);
        bw.Write(dib);
        return ms.ToArray();
    }
}