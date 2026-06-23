using System;
using System.IO;
using System.Linq;
using System.Text;
using ExtrabbitCode.Inventor.MetaReader;
using System.Collections.Generic;

Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

if (args.Length < 2)
{
    Console.WriteLine("""
        Inventor metadata reader - reads .ipt/.iam/.idw/.ipn without Autodesk Inventor.

          invmeta info    <file>              friendly report (type, iProperties, refs, version)
          invmeta states  <file>              per-model-state iProperties (parts/assemblies)
          invmeta json    <file>              full metadata as JSON
          invmeta props   <file>              every property in every OLE property set
          invmeta graph   <file>              recursive reference tree (resolved on disk)
          invmeta tree    <file>              raw CFB storage/stream tree
          invmeta thumb   <file> [outBase]    extract the embedded preview image
          invmeta extract <file> <outDir>     dump every stream to disk
          invmeta cat     <file> <streamPath> hex+ascii dump of one stream
        """);
    return 1;
}

string cmd = args[0].ToLowerInvariant();
string file = args[1];

if (!CompoundFile.LooksLikeCompoundFile(file))
{
    Console.Error.WriteLine($"{file} is not an OLE compound file (Inventor .ipt/.iam/.idw).");
    return 2;
}

switch (cmd)
{
    case "info":    Info(new InventorDocument(file)); break;
    case "states":  States(new InventorDocument(file)); break;
    case "json":    Console.WriteLine(new InventorDocument(file).ToJson()); break;
    case "thumb":   Thumb(new InventorDocument(file), args.Length > 2 ? args[2] : Path.GetFileNameWithoutExtension(file) + "_thumb"); break;
    case "props":   Props(file); break;
    case "graph":   Graph(new InventorDocument(file)); break;
    case "tree":    Tree(file); break;
    case "extract": Extract(file, args[2]); break;
    case "cat":     Cat(file, args[2]); break;
    default: Console.Error.WriteLine($"Unknown command '{cmd}'."); return 1;
}
return 0;

static void Graph(InventorDocument doc)
{
    RefNode root = ReferenceGraph.Build(doc);
    void Print(RefNode n, string indent, bool last)
    {
        string branch = n.Depth == 0 ? "" : (last ? "└─ " : "├─ ");
        string flag = !n.Resolved ? "  [NOT FOUND]" : n.Cyclic ? "  [cyclic]" : n.ReadError ? "  [unreadable]" : "";
        string type = n.IsLinkedFile ? "linked" : n.Kind.ToString();
        string ipart = n.IPart switch { IPartRole.Member => "  [iPart member]", IPartRole.Factory => "  [iPart factory]", _ => "" };
        Console.WriteLine($"{indent}{branch}{n.Name}  ({type}){ipart}{flag}");
        if (!n.Resolved && n.Depth > 0)
        {
            Console.WriteLine($"{indent}{(n.Depth == 0 ? "" : last ? "   " : "│  ")}   ↳ {n.Path}");
        }
        string childIndent = indent + (n.Depth == 0 ? "" : last ? "   " : "│  ");
        for (int i = 0; i < n.Children.Count; i++)
        {
            Print(n.Children[i], childIndent, i == n.Children.Count - 1);
        }
        if (n.Truncated)
        {
            Console.WriteLine($"{childIndent}… (truncated)");
        }
    }
    Print(root, "", true);
}

static void Info(InventorDocument doc)
{
    Line('=');
    Console.WriteLine($"  {doc.FileName}");
    Console.WriteLine($"  {doc.DocumentType}   [{doc.CfbVersionInfo}]");
    Line('=');

    if (doc.Summary.Count > 0)
    {
        Console.WriteLine("\nKEY iPROPERTIES");
        foreach (KeyValuePair<string, string> kv in doc.Summary)
        {
            Console.WriteLine($"  {kv.Key,-20} {kv.Value}");
        }
    }
    if (doc.VersionInfo.Count > 0)
    {
        Console.WriteLine("\nVERSION / PROVENANCE");
        foreach (KeyValuePair<string, string> kv in doc.VersionInfo)
        {
            Console.WriteLine($"  {kv.Key,-20} {kv.Value}");
        }
    }
    if (doc.HasModelStates)
    {
        Console.WriteLine($"\nMODEL STATES ({doc.ModelStateDetails.Count})\n  " +
                          string.Join(", ", doc.ModelStateDetails.Select(s => s.Name + (s.IsActive ? " (active)" : ""))) +
                          "\n  → run 'invmeta states' to compare iProperties per state");
    }

    if (doc.References.Count > 0)
    {
        Console.WriteLine("\nREFERENCED FILES");
        foreach (string r in doc.References)
        {
            Console.WriteLine($"  {r}");
        }
    }
    Console.WriteLine($"\nThumbnail : {(doc.Thumbnail != null ? $"{doc.Thumbnail.Length:N0} bytes ({doc.ThumbnailExt})" : "none")}");
    Console.WriteLine($"Properties: {doc.Properties.Count} across {doc.Properties.Select(p => p.Set).Distinct().Count()} sets  (use 'props' to list all)");
}

static void States(InventorDocument doc)
{
    Line('=');
    Console.WriteLine($"  {doc.FileName} - {doc.DocumentType}");
    Line('=');
    if (!doc.HasModelStates)
    {
        Console.WriteLine("\nThis document has no embedded model states / members.");
        Console.WriteLine("(Only multi-state parts and assemblies store per-state properties.)");
        return;
    }

    List<InventorDocument.ModelState> states = doc.ModelStateDetails;
    Console.WriteLine($"\n{states.Count} model state(s):");
    foreach (InventorDocument.ModelState s in states)
    {
        Console.WriteLine($"  • {s.Name}{(s.IsActive ? "  [active]" : "")}");
    }

    // property identity = (Set, Pid, Name); value can vary per state
    InventorDocument.ModelState? active = states.FirstOrDefault(s => s.IsActive);
    bool Has(InventorDocument.ModelState s, (string Set, uint Pid, string Name) id) =>
        s.Properties.Any(p => p.Set == id.Set && p.Pid == id.Pid && p.Name == id.Name);
    string ValueOf(InventorDocument.ModelState s, (string Set, uint Pid, string Name) id)
    {
        InventorDocument.PropEntry? p = s.Properties.FirstOrDefault(x => x.Set == id.Set && x.Pid == id.Pid && x.Name == id.Name);
        if (p != null)
        {
            return p.Display;
        }

        // present in the active document but absent here = Inventor hasn't flushed this
        // state's override into its cache (only happens once the state is recomputed/saved)
        return !s.IsActive && active != null && Has(active, id) ? "(not cached)" : "-";
    }
    List<(string Set, uint Pid, string Name)> ids = states.SelectMany(s => s.Properties).Select(p => (p.Set, p.Pid, p.Name))
                    .Distinct().OrderBy(i => i.Set).ThenBy(i => i.Pid).ToList();
    List<(string Set, uint Pid, string Name)> diffIds = ids.Where(id => states.Select(s => ValueOf(s, id)).Distinct().Count() > 1).ToList();

    // keep user-facing differences; set internal bookkeeping (revision ids, save
    // counters/timestamps) aside so the meaningful values stand out
    bool Meaningful((string Set, uint Pid, string Name) id) =>
        !id.Set.Contains("(internal)") &&
        !(id.Set.StartsWith("Design Tracking Properties") && id.Pid is 21 or 46) &&  // revision GUIDs
        !(id.Set.Contains("Summary Information") && id.Pid == 17);                    // thumbnail blob
    List<(string Set, uint Pid, string Name)> meaningful = diffIds.Where(Meaningful).ToList();
    int internalCount = diffIds.Count - meaningful.Count;

    Console.WriteLine($"\nPROPERTIES THAT DIFFER BETWEEN STATES ({meaningful.Count}):");
    if (meaningful.Count == 0)
    {
        Console.WriteLine("  (no user-facing properties differ between states)");
    }
    else
    {
        foreach ((string Set, uint Pid, string Name) id in meaningful)
        {
            Console.WriteLine($"\n  {id.Name}   ({id.Set})");
            foreach (InventorDocument.ModelState s in states)
            {
                Console.WriteLine($"      {s.Name,-16}{(s.IsActive ? "*" : " ")} {ValueOf(s, id)}");
            }
        }
    }

    if (internalCount > 0)
    {
        Console.WriteLine($"\n  (+ {internalCount} internal/volatile field(s) also differ: revision ids, save timestamps, counters)");
    }

    Console.WriteLine("\nKEY iPROPERTIES PER STATE:");
    string[] labels =
    [
        "Part Number", "Description", "Material", "Mass", "Volume",
                         "Surface Area", "Design Status", "Project", "Stock Number"
    ];
    Console.Write($"  {"Property",-16}");
    foreach (InventorDocument.ModelState s in states)
    {
        Console.Write($"{s.Name + (s.IsActive ? "*" : ""),-22}");
    }

    Console.WriteLine();
    foreach (string lbl in labels)
    {
        if (!states.Any(s => s.Summary.ContainsKey(lbl)))
        {
            continue;
        }

        Console.Write($"  {lbl,-16}");
        foreach (InventorDocument.ModelState s in states)
        {
            Console.Write($"{(s.Summary.TryGetValue(lbl, out string? v) ? v : "-"),-22}");
        }

        Console.WriteLine();
    }
    Console.WriteLine("\n  (* = active state; the top-level / [Primary] document)");
}

static void Thumb(InventorDocument doc, string outBase)
{
    if (doc.Thumbnail == null) { Console.WriteLine("No thumbnail in this file."); return; }
    string p = outBase + "." + doc.ThumbnailExt;
    File.WriteAllBytes(p, doc.Thumbnail);
    Console.WriteLine($"Wrote {p} ({doc.Thumbnail.Length:N0} bytes)");
}

static void Props(string file)
{
    using CompoundFile cf = new(file);
    foreach (CompoundFile.DirEntry e in cf.Directory.Where(d => d.Type == 2).OrderBy(d => d.Path, StringComparer.OrdinalIgnoreCase))
    {
        byte[] data; try { data = cf.ReadEntry(e); } catch { continue; }
        if (!PropertySet.IsPropertySet(data))
        {
            continue;
        }

        PropertySet.ParsedSet set = PropertySet.Parse(data);
        foreach (PropertySet.Section sec in set.Sections)
        {
            string name = InventorProps.SetNames.TryGetValue(sec.FmtId, out string? sn) ? sn : sec.FmtId.ToString();
            Console.WriteLine($"\n[{name}]  {sec.FmtId}");
            foreach (PropertySet.Prop pr in sec.Props.OrderBy(x => x.Id))
            {
                if (pr.Id is 1 or 0x80000000)
                {
                    continue;
                }

                string nm = pr.Name.StartsWith("PID") ? InventorProps.Name(sec.FmtId, pr.Id) : pr.Name;
                string v = pr.Value?.ToString() ?? "";
                if (v.Length > 160)
                {
                    v = v[..160] + "…";
                }

                Console.WriteLine($"   {pr.Id,5}  {nm,-26} {pr.TypeName,-10} {v}");
            }
        }
    }
}

static void Tree(string file)
{
    using CompoundFile cf = new(file);
    Console.WriteLine($"{file}   CFB v{cf.MajorVersion}, sector {cf.SectorSize}");
    Console.WriteLine($"Root CLSID {cf.Directory[0].Clsid}\n");
    foreach (CompoundFile.DirEntry e in cf.Directory.Where(d => d.Type is 1 or 2 or 5)
                                  .OrderBy(d => d.Path, StringComparer.OrdinalIgnoreCase))
    {
        string size = e.Type == 2 ? e.Size.ToString("N0") : "";
        Console.WriteLine($"{e.Path,-50}{e.TypeName,-9}{size,12}");
    }
}

static void Extract(string file, string outDir)
{
    using CompoundFile cf = new(file);
    Directory.CreateDirectory(outDir);
    int n = 0;
    foreach (CompoundFile.DirEntry e in cf.Directory.Where(d => d.Type == 2))
    {
        string rel = e.Path.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
        foreach (char c in Path.GetInvalidFileNameChars())
        {
            rel = rel.Replace(c, '_');
        }

        string outPath = Path.Combine(outDir, rel + ".bin");
        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        File.WriteAllBytes(outPath, cf.ReadEntry(e)); n++;
    }
    Console.WriteLine($"Extracted {n} streams to {outDir}");
}

static void Cat(string file, string streamPath)
{
    using CompoundFile cf = new(file);
    byte[] data = cf.ReadStream(streamPath);
    Console.WriteLine($"{streamPath}  ({data.Length:N0} bytes)");
    int len = Math.Min(data.Length, 2048);
    for (int i = 0; i < len; i += 16)
    {
        StringBuilder hex = new(); StringBuilder asc = new();
        for (int j = 0; j < 16; j++)
        {
            if (i + j < len) { byte b = data[i+j]; hex.Append(b.ToString("X2")).Append(' '); asc.Append(b is >= 32 and < 127 ? (char)b : '.'); }
            else
            {
                hex.Append("   ");
            }

            if (j == 7)
            {
                hex.Append(' ');
            }
        }
        Console.WriteLine($"{i:X8}  {hex} {asc}");
    }
}

static void Line(char c) => Console.WriteLine(new string(c, 64));
