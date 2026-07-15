using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace ExtrabbitCode.Inventor.MetaReader.App;

/// <summary>The File Structure tab: the segment-database card (size bars, block counts, damage
/// chips) above a collapsible storage tree with human annotations, plus the classic plain-text
/// dump behind "Copy as text".</summary>
public sealed partial class DocumentView
{
    // ---------------------------------------------------------------------------------------
    // File Structure tab: a segment-database card (size bars, block counts, damage chips) above
    // a collapsible storage tree with human annotations. "Copy as text" keeps the classic dump
    // for support tickets. The damage Inventor only reports as an opaque "Error in reading RSe
    // segment" becomes visible right at the streams involved.
    // ---------------------------------------------------------------------------------------

    private static readonly Windows.UI.Color DamageRed = Windows.UI.Color.FromArgb(255, 0xC4, 0x2B, 0x1C);
    private const int BarDamaged = unchecked((int)0xFFE24B4A), BarHealthy = unchecked((int)0xFF1D9E75);

    private void ClearStructureCards()
    {
        for (int i = StructurePanel.Children.Count - 1; i >= 0; i--)
        {
            if (!ReferenceEquals(StructurePanel.Children[i], TreeText))
            {
                StructurePanel.Children.RemoveAt(i);
            }
        }
    }

    private void PopulateStructureTree(InventorDocument doc)
    {
        using CompoundFile cf = new(doc.FilePath);
        if (doc.Segments.Count > 0)
        {
            StructurePanel.Children.Add(BuildSegmentDatabaseCard(doc));
        }
        StructurePanel.Children.Add(BuildStorageTreeCard(doc, cf));
    }

    private static SolidColorBrush ArgbBrush(int argb) => new(Windows.UI.Color.FromArgb(
        (byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));

    private SolidColorBrush ErrorText() => new(ActualTheme == ElementTheme.Dark
        ? Windows.UI.Color.FromArgb(255, 0xFF, 0x99, 0x8A) : DamageRed);

    private SolidColorBrush OkText() => new(ActualTheme == ElementTheme.Dark
        ? Windows.UI.Color.FromArgb(255, 0x6C, 0xCB, 0x5F) : Windows.UI.Color.FromArgb(255, 0x15, 0x80, 0x3D));

    private static Border DamageChip(string text) => new()
    {
        CornerRadius = new CornerRadius(9), Padding = new Thickness(8, 1, 8, 2),
        Background = new SolidColorBrush(DamageRed),
        VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right,
        Child = new TextBlock { Text = text, FontSize = 11, Foreground = new SolidColorBrush(Microsoft.UI.Colors.White) }
    };

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:N0} KB",
        _ => $"{bytes / (1024.0 * 1024.0):N1} MB",
    };

    /// <summary>One row per RSe segment: name, payload-size bar (relative to the largest segment),
    /// actual block count and the registry verdict - a red "claims N" chip where the summary is
    /// stale, a green "ok" otherwise.</summary>
    private Border BuildSegmentDatabaseCard(InventorDocument doc)
    {
        IReadOnlyList<InventorDocument.Segment> segs = doc.Segments;
        long total = segs.Sum(s => s.DataSize);
        long max = Math.Max(1, segs.Max(s => s.DataSize));
        List<SegmentRepair.Issue> memberIssues = doc.SegmentIssues.Where(i => i.Location.Length > 0).ToList();
        int damaged = segs.Count(s => s.HasCountMismatch) + memberIssues.Count;

        StackPanel body = new() { Margin = new Thickness(14, 4, 14, 12) };

        StackPanel summary = new() { Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(0, 2, 0, 8) };
        summary.Children.Add(new TextBlock
        {
            Text = $"{segs.Count} segments · {FormatSize(total)} payload",
            Opacity = 0.6, FontSize = 12, VerticalAlignment = VerticalAlignment.Center
        });
        if (damaged > 0)
        {
            summary.Children.Add(DamageChip($"{damaged} damaged"));
        }
        body.Children.Add(summary);

        body.Children.Add(SegmentGrid(
            Dim("segment"), Dim("size"), Dim("blocks", right: true), Dim("registry", right: true),
            bottomRule: true));

        foreach (InventorDocument.Segment s in segs)
        {
            FrameworkElement registry = s.HasCountMismatch
                ? DamageChip($"claims {s.SummaryBlockCount:N0}")
                : s.SummaryBlockCount is null
                    ? new TextBlock { Text = "?", FontSize = 12, Opacity = 0.5, HorizontalAlignment = HorizontalAlignment.Right }
                    : new TextBlock { Text = "ok", FontSize = 12, Foreground = OkText(), HorizontalAlignment = HorizontalAlignment.Right };

            Grid row = SegmentGrid(
                Mono(s.Name),
                SizeBar((double)s.DataSize / max, s.HasCountMismatch),
                Mono(s.ActualBlockCount?.ToString("N0") ?? "–", right: true),
                registry,
                bottomRule: !ReferenceEquals(s, segs[^1]) || memberIssues.Count > 0);
            string when = s.Modified is { } dt ? $" · modified {dt:yyyy-MM-dd HH:mm}" : "";
            ToolTipService.SetToolTip(row,
                $"{FormatSize(s.DataSize)} payload (B{s.Id}) · {FormatSize(s.MetaSize)} block index (M{s.Id}){when}");
            body.Children.Add(row);
        }

        foreach (SegmentRepair.Issue issue in memberIssues)
        {
            body.Children.Add(new TextBlock
            {
                Text = $"model-state member {issue.Location}: {issue.SegmentName} registry claims " +
                       $"{issue.SummaryCount:N0}, {issue.ActualCount:N0} stored",
                FontSize = 12, Foreground = ErrorText(), TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 8, 0, 0)
            });
        }

        return Card("Segment database", segs.Count.ToString(), body);
    }

    private static TextBlock Dim(string text, bool right = false) => new()
    {
        Text = text, FontSize = 11, Opacity = 0.5, VerticalAlignment = VerticalAlignment.Center,
        HorizontalAlignment = right ? HorizontalAlignment.Right : HorizontalAlignment.Left
    };

    private static TextBlock Mono(string text, bool right = false) => new()
    {
        Text = text, FontSize = 12, FontFamily = new FontFamily("Consolas"),
        VerticalAlignment = VerticalAlignment.Center,
        HorizontalAlignment = right ? HorizontalAlignment.Right : HorizontalAlignment.Left
    };

    private static Grid SegmentGrid(FrameworkElement name, FrameworkElement bar, FrameworkElement blocks, FrameworkElement registry, bool bottomRule)
    {
        Grid g = new() { ColumnSpacing = 10, Padding = new Thickness(0, 6, 0, 6) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(165) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(78) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(118) });
        Grid.SetColumn(bar, 1); Grid.SetColumn(blocks, 2); Grid.SetColumn(registry, 3);
        g.Children.Add(name); g.Children.Add(bar); g.Children.Add(blocks); g.Children.Add(registry);
        if (bottomRule)
        {
            g.BorderThickness = new Thickness(0, 0, 0, 1);
            g.BorderBrush = (Brush)Application.Current.Resources["DividerStrokeColorDefaultBrush"];
        }
        return g;
    }

    /// <summary>A flat proportional bar: red for a damaged segment, green for a healthy one, on a
    /// subtle full-width track.</summary>
    private static Border SizeBar(double fraction, bool damaged)
    {
        Grid fillGrid = new();
        fillGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(fraction, 0.006), GridUnitType.Star) });
        fillGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(1 - fraction, 0.0001), GridUnitType.Star) });
        fillGrid.Children.Add(new Border
        {
            Height = 8, CornerRadius = new CornerRadius(4),
            Background = ArgbBrush(damaged ? BarDamaged : BarHealthy)
        });
        return new Border
        {
            Height = 8, CornerRadius = new CornerRadius(4), VerticalAlignment = VerticalAlignment.Center,
            Background = (Brush)Application.Current.Resources["ControlFillColorSecondaryBrush"],
            Child = fillGrid
        };
    }

    // ---- storage tree ----

    private sealed class StorageNode
    {
        public CompoundFile.DirEntry? Entry;
        public string Name = "";
        public string Path = "";
        public List<StorageNode> Children = [];
        public int StreamCount;   // streams in the whole subtree
    }

    private Border BuildStorageTreeCard(InventorDocument doc, CompoundFile cf)
    {
        Dictionary<string, InventorDocument.Segment> segsById = new(StringComparer.Ordinal);
        foreach (InventorDocument.Segment s in doc.Segments) { segsById[s.Id] = s; }
        Dictionary<string, int> issuesByLocation = doc.SegmentIssues
            .GroupBy(i => i.Location, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

        StackPanel body = new() { Margin = new Thickness(14, 4, 14, 12) };

        Grid info = new() { Margin = new Thickness(0, 2, 0, 6) };
        info.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        info.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        TextBlock containerInfo = new()
        {
            Text = doc.CfbVersionInfo, Opacity = 0.6, FontSize = 12, VerticalAlignment = VerticalAlignment.Center
        };
        ToolTipService.SetToolTip(containerInfo, $"Root CLSID {cf.Directory[0].Clsid}");
        info.Children.Add(containerInfo);
        Button copyBtn = new()
        {
            Padding = new Thickness(8, 3, 8, 4), MinWidth = 0, FontSize = 12,
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 5,
                Children =
                {
                    new FontIcon { Glyph = G(0xE8C8), FontSize = 12 },
                    new TextBlock { Text = "Copy as text", FontSize = 12 }
                }
            }
        };
        ToolTipService.SetToolTip(copyBtn, "Copy the full structure as plain text (for tickets and diffs)");
        // built now, while the container is open - the click must not touch the disk (the file
        // may be gone or locked by then) and the copied text should match what the tab shows
        string dumpText = BuildStructureText(doc, cf);
        copyBtn.Click += (_, _) => Copy(dumpText);
        Grid.SetColumn(copyBtn, 1);
        info.Children.Add(copyBtn);
        body.Children.Add(info);

        StorageNode root = BuildStorageHierarchy(cf);
        foreach (StorageNode child in root.Children)
        {
            RenderStorageNode(body, child, 0, segsById, issuesByLocation);
        }

        return Card("Storage tree", root.StreamCount.ToString(), body);
    }

    private static StorageNode BuildStorageHierarchy(CompoundFile cf)
    {
        StorageNode root = new() { Path = "" };
        Dictionary<string, StorageNode> byPath = new(StringComparer.OrdinalIgnoreCase) { [""] = root };

        foreach (CompoundFile.DirEntry en in cf.Directory.Where(d => d.Type is 1 or 2)
                     .OrderBy(d => d.Path, StringComparer.OrdinalIgnoreCase))
        {
            string[] parts = en.Path.TrimStart('/').Split('/');
            StorageNode parent = root;
            string path = "";
            for (int i = 0; i < parts.Length; i++)
            {
                path += "/" + parts[i];
                if (!byPath.TryGetValue(path, out StorageNode? node))
                {
                    node = new StorageNode { Name = parts[i], Path = path };
                    byPath[path] = node;
                    parent.Children.Add(node);
                }
                if (i == parts.Length - 1) { node.Entry = en; }
                parent = node;
            }
        }

        int CountStreams(StorageNode n)
        {
            n.StreamCount = (n.Entry?.Type == 2 ? 1 : 0) + n.Children.Sum(CountStreams);
            return n.StreamCount;
        }
        CountStreams(root);
        return root;
    }

    private void RenderStorageNode(StackPanel into, StorageNode node, int depth,
        Dictionary<string, InventorDocument.Segment> segsById, Dictionary<string, int> issuesByLocation)
    {
        bool isStorage = node.Entry == null || node.Entry.Type == 1;
        StackPanel line = new()
        {
            Orientation = Orientation.Horizontal, Spacing = 8,
            Margin = new Thickness(depth * 18, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center
        };

        if (isStorage)
        {
            // big generic subtrees (the VBA project, member documents) start folded so the
            // interesting streams aren't buried; the document's own RSeStorage stays open
            bool collapsed = node.StreamCount > 8 &&
                             !node.Path.Equals("/RSeStorage", StringComparison.OrdinalIgnoreCase);

            FontIcon chevron = new() { Glyph = G(collapsed ? 0xE76C : 0xE70D), FontSize = 10, Opacity = 0.6, VerticalAlignment = VerticalAlignment.Center };
            TextBlock summary = new()
            {
                Text = $"· {node.StreamCount} stream{(node.StreamCount == 1 ? "" : "s")}",
                FontSize = 11, Opacity = 0.5, VerticalAlignment = VerticalAlignment.Center,
                Visibility = collapsed ? Visibility.Visible : Visibility.Collapsed
            };
            line.Children.Add(chevron);
            line.Children.Add(new FontIcon { Glyph = G(0xE8B7), FontSize = 14, Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center });
            line.Children.Add(Mono(node.Name));
            line.Children.Add(summary);

            StackPanel children = new() { Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible };
            foreach (StorageNode child in node.Children)
            {
                RenderStorageNode(children, child, depth + 1, segsById, issuesByLocation);
            }

            Border row = new()
            {
                Child = line, Padding = new Thickness(2, 3, 2, 3),
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent)
            };
            row.Tapped += (_, _) =>
            {
                bool show = children.Visibility == Visibility.Collapsed;
                children.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
                chevron.Glyph = G(show ? 0xE70D : 0xE76C);
                summary.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
            };
            into.Children.Add(row);
            into.Children.Add(children);
        }
        else
        {
            line.Children.Add(new FontIcon { Glyph = G(0xE7C3), FontSize = 13, Opacity = 0.55, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(18, 0, 0, 0) });
            line.Children.Add(Mono(node.Name));
            line.Children.Add(new TextBlock { Text = FormatSize(node.Entry!.Size), FontSize = 11, Opacity = 0.5, VerticalAlignment = VerticalAlignment.Center });
            (string note, string? chip) = StreamAnnotation(node.Entry, segsById, issuesByLocation);
            if (note.Length > 0)
            {
                line.Children.Add(new TextBlock { Text = note, FontSize = 11, Opacity = 0.65, VerticalAlignment = VerticalAlignment.Center });
            }
            if (chip != null)
            {
                line.Children.Add(DamageChip(chip));
            }
            into.Children.Add(new Border { Child = line, Padding = new Thickness(2, 3, 2, 3) });
        }
    }

    /// <summary>What to say about one stream in the tree: names the segment behind each mangled
    /// M/B stream, labels the registries, and hands back a red-chip text where summaries are
    /// stale. Member trees (MemberDocs/…/RSeStorage) get their registry chip too.</summary>
    private static (string Note, string? Chip) StreamAnnotation(CompoundFile.DirEntry en,
        Dictionary<string, InventorDocument.Segment> segsById, Dictionary<string, int> issuesByLocation)
    {
        int lastSlash = en.Path.LastIndexOf('/');
        string parent = lastSlash > 0 ? en.Path[..lastSlash] : "";
        bool inRseTree = parent.EndsWith("/RSeStorage", StringComparison.OrdinalIgnoreCase);
        if (en.Type != 2 || !inRseTree)
        {
            return ("", null);
        }

        if (en.Name == "RSeSegInfo")
        {
            // the library owns the Location convention - deriving it here again would let the
            // dictionary key and the lookup key drift apart
            string location = SegmentRepair.LocationOf(parent);
            return issuesByLocation.TryGetValue(location, out int n) && n > 0
                ? ("segment registry", $"{n} stale summar{(n == 1 ? "y" : "ies")}")
                : ("segment registry", null);
        }

        // named annotations only for the document's own segments (the sidebar list)
        if (parent.Equals("/RSeStorage", StringComparison.OrdinalIgnoreCase) &&
            en.Name.Length > 1 && segsById.TryGetValue(en.Name[1..], out InventorDocument.Segment? seg))
        {
            if (en.Name[0] == 'M')
            {
                return ($"{seg.Name} block index", null);
            }
            if (en.Name[0] == 'B')
            {
                string blocks = seg.ActualBlockCount is long n ? $" · {n:N0} blocks" : "";
                return seg.HasCountMismatch
                    ? ($"{seg.Name} payload{blocks}", $"registry claims {seg.SummaryBlockCount:N0}")
                    : ($"{seg.Name} payload{blocks}", null);
            }
        }
        return ("", null);
    }

    /// <summary>The classic plain-text dump (paths, types, sizes plus the segment annotations) -
    /// what the tab used to show, kept for tickets, mails and diffs via "Copy as text". Works on
    /// the already-loaded document and its open container: no disk access of its own.</summary>
    private static string BuildStructureText(InventorDocument doc, CompoundFile cf)
    {
        Dictionary<string, InventorDocument.Segment> segsById = new(StringComparer.Ordinal);
        foreach (InventorDocument.Segment s in doc.Segments) { segsById[s.Id] = s; }
        Dictionary<string, int> issuesByLocation = doc.SegmentIssues
            .GroupBy(i => i.Location, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

        StringBuilder sb = new();
        sb.AppendLine($"Root CLSID  {cf.Directory[0].Clsid}");
        sb.AppendLine($"Container   {doc.CfbVersionInfo}");
        if (doc.HasRepairableSegmentDamage)
        {
            sb.AppendLine();
            sb.AppendLine("!! Segment database damaged - the registry promises more blocks than the streams hold:");
            foreach (SegmentRepair.Issue i in doc.SegmentIssues)
            {
                string where = i.Location.Length == 0 ? "" : $"   (member {i.Location})";
                sb.AppendLine($"!!   {i.SegmentName,-22} registry {i.SummaryCount,10:N0}  <>  {i.ActualCount:N0} stored{where}");
            }
            sb.AppendLine("!! Inventor refuses to open the file over this; the Repair button patches the registry (backup included).");
        }
        sb.AppendLine();
        foreach (CompoundFile.DirEntry en in cf.Directory.Where(d => d.Type is 1 or 2 or 5)
                     .OrderBy(d => d.Path, StringComparer.OrdinalIgnoreCase))
        {
            string size = en.Type == 2 ? en.Size.ToString("N0") : "";
            (string note, string? chip) = StreamAnnotation(en, segsById, issuesByLocation);
            string line = $"{en.Path,-46}{en.TypeName,-8}{size,12}";
            if (note.Length > 0) { line += "   " + note; }
            if (chip != null) { line += $"  !! {chip}"; }
            sb.AppendLine(line);
        }
        return sb.ToString();
    }

}
