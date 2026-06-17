using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace InventorMeta;

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

        // model-state name ↔ member-storage mapping: a member-storage id sits next to
        // its "Model State<n>" name in the token stream - but the order varies (name
        // before storage in parts, storage before name in assemblies), so check both.
        List<string> tokens = Regex.Matches(txt, @"[\x20-\x7E]{3,}").Select(m => m.Value).ToList();
        bool IsState(string t) => Regex.IsMatch(t, @"^Model State\d*$");
        HashSet<string> usedNames = [];
        foreach (string storage in memberStorages)
        {
            int i = tokens.IndexOf(storage);
            if (i < 0)
            {
                continue;
            }

            // expand outward from the storage id, nearest first, to find its state name
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

        // referenced document paths (non-anchored: trailing separator bytes are common)
        foreach (Match m in Regex.Matches(txt, @"[A-Za-z]:\\[^\x00-\x1F""<>|*?]*?\.(?:ipt|iam|idw|ipn)", RegexOptions.IgnoreCase))
        {
            string p = m.Value;
            bool self = string.Equals(Path.GetFileName(p), FileName, StringComparison.OrdinalIgnoreCase);
            bool isTemplate = template != null && string.Equals(p, template, StringComparison.OrdinalIgnoreCase);
            if (!self && !isTemplate && !References.Contains(p, StringComparer.OrdinalIgnoreCase))
            {
                References.Add(p);
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
            hasThumbnail = Thumbnail != null,
            properties = Properties
                .GroupBy(p => p.Set)
                .ToDictionary(g => g.Key, g => g.Select(p => new {
                    pid = p.Pid, name = p.Name, type = p.Type, value = p.Display
                }).ToArray())
        };
        return JsonSerializer.Serialize(o, new JsonSerializerOptions { WriteIndented = true });
    }

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