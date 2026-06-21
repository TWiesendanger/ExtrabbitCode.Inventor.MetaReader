using System;
using System.Collections.Generic;
using System.IO;

namespace InventorMeta;

/// <summary>One node in the reference tree (a document and the documents it references).</summary>
public sealed class RefNode
{
    public string Path = "";
    public string Name = "";
    public InventorDocument.DocKind Kind;
    public bool Resolved;     // file found on disk
    public bool Cyclic;       // already present higher in this branch
    public bool ReadError;    // resolved but couldn't be parsed
    public bool Truncated;    // children omitted because the node cap was hit
    public bool IsLinkedFile; // a linked non-model file (image / imported CAD), not an Inventor document
    public bool IsIPart;      // an iPart/iAssembly factory or member (carries the member table)
    public int Depth;
    public double Row;        // assigned at layout time by the renderer
    public bool Expanded;     // UI: whether this node's children are shown
    public List<RefNode> Children = [];
}

/// <summary>
/// Builds the reference tree for a document. Each referenced path is resolved on disk and
/// container documents are recursed into; linked non-model files (images, imported CAD)
/// become leaf nodes. References store absolute paths that may no longer be valid, so we
/// resolve in three steps — next to the parent, then the stored path, then by filename
/// anywhere in the assembly's folder tree — which keeps copied / re-organised projects intact.
/// </summary>
public static class ReferenceGraph
{
    private const int MaxNodes = 400;
    private const int MaxDepth = 16;

    public static RefNode Build(InventorDocument root) =>
        new Builder(Path.GetDirectoryName(root.FilePath)).Build(root);

    private sealed class Builder
    {
        private readonly Dictionary<string, string> _index;   // filename -> full path, across the project tree
        private int _count = 1;

        public Builder(string? rootDir) => _index = IndexTree(ProjectRoot(rootDir) ?? rootDir);

        public RefNode Build(InventorDocument root)
        {
            RefNode rootNode = new()
            {
                Path = root.FilePath, Name = root.FileName, Kind = root.Kind,
                Resolved = true, IsIPart = root.IsIPart, Depth = 0
            };
            HashSet<string> branch = new(StringComparer.OrdinalIgnoreCase) { Norm(root.FilePath) };
            Expand(rootNode, root, branch);
            return rootNode;
        }

        /// <summary>Adds children from an already-read document: referenced model documents
        /// (recursed into) followed by linked non-model files (leaves).</summary>
        private void Expand(RefNode node, InventorDocument doc, HashSet<string> branch)
        {
            string? baseDir = Path.GetDirectoryName(node.Path);

            foreach (string r in doc.References)
            {
                if (_count >= MaxNodes) { node.Truncated = true; return; }

                RefNode child = MakeNode(r, baseDir, node.Depth + 1, linked: false);
                _count++;
                node.Children.Add(child);

                if (!child.Resolved) { continue; }
                string norm = Norm(child.Path);
                if (branch.Contains(norm)) { child.Cyclic = true; continue; }
                if (node.Depth + 1 >= MaxDepth) { continue; }

                try
                {
                    InventorDocument cdoc = new(child.Path);
                    child.Kind = cdoc.Kind;
                    child.IsIPart = cdoc.IsIPart;
                    branch.Add(norm);
                    Expand(child, cdoc, branch);
                    branch.Remove(norm);
                }
                catch { child.ReadError = true; }
            }

            foreach (string lf in doc.LinkedFiles)
            {
                if (_count >= MaxNodes) { node.Truncated = true; return; }
                node.Children.Add(MakeNode(lf, baseDir, node.Depth + 1, linked: true));
                _count++;
            }
        }

        private RefNode MakeNode(string refPath, string? baseDir, int depth, bool linked)
        {
            string? resolved = Resolve(refPath, baseDir);
            return new RefNode
            {
                Path = resolved ?? refPath,
                Name = Path.GetFileName(refPath),
                Kind = linked ? InventorDocument.DocKind.Unknown : KindFromExt(refPath),
                Resolved = resolved != null,
                IsLinkedFile = linked,
                Depth = depth
            };
        }

        private string? Resolve(string refPath, string? baseDir)
        {
            try
            {
                string name = Path.GetFileName(refPath);
                if (baseDir != null)
                {
                    string sameFolder = Path.Combine(baseDir, name);   // 1) next to the parent
                    if (File.Exists(sameFolder)) { return sameFolder; }
                }
                if (File.Exists(refPath)) { return refPath; }          // 2) the stored absolute path
                if (_index.TryGetValue(name, out string? hit)) { return hit; }  // 3) anywhere in the project tree
            }
            catch { /* malformed path -> unresolved */ }
            return null;
        }
    }

    /// <summary>Walks up from a document's folder to the Inventor project folder (the one
    /// holding the .ipj), which scopes both the workspace and its libraries. Null if none.</summary>
    private static string? ProjectRoot(string? dir)
    {
        string? d = dir;
        for (int i = 0; i < 8 && d != null; i++)
        {
            try { if (Directory.GetFiles(d, "*.ipj").Length > 0) { return d; } } catch { /* skip */ }
            d = Path.GetDirectoryName(d);
        }
        return null;
    }

    /// <summary>Filename -&gt; full path for every file under <paramref name="dir"/> (bounded).</summary>
    private static Dictionary<string, string> IndexTree(string? dir)
    {
        Dictionary<string, string> index = new(StringComparer.OrdinalIgnoreCase);
        if (dir == null) { return index; }

        int budget = 20000;
        Queue<string> queue = new();
        queue.Enqueue(dir);
        while (queue.Count > 0 && budget > 0)
        {
            string d = queue.Dequeue();
            try
            {
                foreach (string f in Directory.EnumerateFiles(d))
                {
                    index.TryAdd(Path.GetFileName(f), f);   // first match wins
                    if (--budget <= 0) { break; }
                }
            }
            catch { continue; }
            try
            {
                foreach (string sub in Directory.EnumerateDirectories(d)) { queue.Enqueue(sub); }
            }
            catch { /* skip inaccessible */ }
        }
        return index;
    }

    private static string Norm(string p)
    {
        try { return Path.GetFullPath(p); } catch { return p; }
    }

    private static InventorDocument.DocKind KindFromExt(string p) =>
        Path.GetExtension(p).ToLowerInvariant() switch
        {
            ".ipt" => InventorDocument.DocKind.Part,
            ".iam" => InventorDocument.DocKind.Assembly,
            ".idw" => InventorDocument.DocKind.Drawing,
            ".ipn" => InventorDocument.DocKind.Presentation,
            _ => InventorDocument.DocKind.Unknown
        };
}
