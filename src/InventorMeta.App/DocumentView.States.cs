using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace ExtrabbitCode.Inventor.MetaReader.App;

/// <summary>The Model States tab: the per-state property browser with diff matrix and
/// per-state thumbnails.</summary>
public sealed partial class DocumentView
{
    private void PopulateStates(InventorDocument doc)
    {
        StatesPanel.Children.Clear();
        if (!doc.HasModelStates)
        {
            StatesTab.Visibility = Visibility.Collapsed;
            return;
        }
        StatesTab.Visibility = Visibility.Visible;
        List<InventorDocument.ModelState> states = doc.ModelStateDetails;
        StatesTab.Header = $"Model States ({states.Count})";

        StatesPanel.Children.Add(new TextBlock
        {
            Text = "Per-state iProperties, read straight from the file - no need to switch states in Inventor.",
            Opacity = 0.7, FontSize = 13, TextWrapping = TextWrapping.Wrap
        });

        // identity of a property = (Set, Pid, Name); value may vary per state
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

            return !s.IsActive && active != null && Has(active, id) ? "(not cached)" : "-";
        }
        List<(string Set, uint Pid, string Name)> ids = states.SelectMany(s => s.Properties).Select(p => (p.Set, p.Pid, p.Name))
            .Distinct().OrderBy(i => i.Set).ThenBy(i => i.Pid).ToList();
        bool Meaningful((string Set, uint Pid, string Name) id) =>
            !id.Set.Contains("(internal)") &&
            !(id.Set.StartsWith("Design Tracking Properties") && (id.Pid == 21 || id.Pid == 46)) &&
            !(id.Set.Contains("Summary Information") && id.Pid == 17);  // thumbnail blob
        List<(string Set, uint Pid, string Name)> allDiffs = ids.Where(id => states.Select(s => ValueOf(s, id)).Distinct().Count() > 1).ToList();
        List<(string Set, uint Pid, string Name)> diffIds = allDiffs.Where(Meaningful).ToList();
        int internalDiffs = allDiffs.Count - diffIds.Count;

        // ---- differences matrix (the headline view) ----
        if (states.Count > 1)
        {
            string[] headers = states.Select(s => s.Name).ToArray();
            List<(string Name, string[])> rows = diffIds.Select(id => (id.Name, states.Select(s => ValueOf(s, id)).ToArray())).ToList();
            UIElement body = rows.Count > 0
                ? Matrix(headers, rows)
                : new TextBlock { Text = "No user-facing properties differ between states.",
                    Opacity = 0.7, Margin = new Thickness(14, 10, 14, 12), TextWrapping = TextWrapping.Wrap };
            StatesPanel.Children.Add(Card("Differences between states",
                rows.Count > 0 ? rows.Count.ToString() : null, body));
            if (internalDiffs > 0)
            {
                StatesPanel.Children.Add(new TextBlock {
                    Text = $"+ {internalDiffs} internal/volatile field(s) also differ (revision ids, save timestamps, counters)",
                    Opacity = 0.5, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, -4, 0, 0) });
            }
        }

        // ---- per-state browser ----
        foreach (InventorDocument.ModelState s in states)
        {
            StackPanel body = new();

            // Each state caches its own preview (its geometry can differ) - show it, click to enlarge.
            if (s.Thumbnail is { Length: > 0 } thumb)
            {
                Image img = new()
                {
                    Source = ThumbSource(thumb), Width = 104, Height = 104,
                    Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left
                };
                ToolTipService.SetToolTip(img, "Click to enlarge");
                img.Tapped += (_, _) => ShowThumbZoom(thumb, s.Name);
                body.Children.Add(new Border
                {
                    Child = img, CornerRadius = new CornerRadius(8), Margin = new Thickness(14, 12, 14, 6),
                    BorderThickness = new Thickness(1), HorizontalAlignment = HorizontalAlignment.Left,
                    BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"]
                });
            }

            if (s.Summary.Count > 0)
            {
                body.Children.Add(KvList(s.Summary.Select(kv => (kv.Key, kv.Value)).ToList(), 150));
            }

            body.Children.Add(new TextBlock {
                Text = $"{s.Properties.Count} properties · storage {s.StorageName}",
                Opacity = 0.5, FontSize = 11, FontFamily = new FontFamily("Consolas"),
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(14, 8, 14, 12) });
            StatesPanel.Children.Add(Card(s.Name, null, body));
        }
    }

    private static BitmapImage ThumbSource(byte[] bytes)
    {
        BitmapImage bmp = new();
        using MemoryStream ms = new(bytes);
        bmp.SetSource(ms.AsRandomAccessStream());
        return bmp;
    }

    /// <summary>Show a model-state thumbnail enlarged on a dimmed overlay; click anywhere to close.</summary>
    private void ShowThumbZoom(byte[] bytes, string title)
    {
        if (HostWindow is not MainWindow win) { return; }

        StackPanel centered = new()
        {
            Spacing = 10, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
        };
        centered.Children.Add(new TextBlock
        {
            Text = title, FontSize = 16, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.White), HorizontalAlignment = HorizontalAlignment.Center
        });
        centered.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(12),
            Child = new Image { Source = ThumbSource(bytes), MaxWidth = 720, MaxHeight = 720, Stretch = Stretch.Uniform }
        });
        centered.Children.Add(new TextBlock
        {
            Text = "Click anywhere to close", FontSize = 12, Opacity = 0.7,
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.White), HorizontalAlignment = HorizontalAlignment.Center
        });

        Grid root = new();
        root.Children.Add(centered);
        root.Tapped += (_, _) => win.HideOverlay();
        win.ShowOverlay(root, dimmed: true);
    }

}
