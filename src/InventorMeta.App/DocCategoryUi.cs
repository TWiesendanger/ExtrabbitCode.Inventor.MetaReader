using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace ExtrabbitCode.Inventor.MetaReader.App;

/// <summary>Presentation for <see cref="InventorDocument.DocCategory"/>: a colour + label per
/// category, and the legend control shown when hovering the category badge.</summary>
internal static class DocCategoryUi
{
    // Listed most-specific first; General last. The badge and the hover legend share these.
    // Chosen for adequate contrast with the badge's white text and to stay distinct from each other.
    public static readonly (InventorDocument.DocCategory Cat, string Name, Color Color)[] All =
    [
        (InventorDocument.DocCategory.ContentCenter,    "Content Center",    Color.FromArgb(255, 0x25, 0x63, 0xEB)), // blue
        (InventorDocument.DocCategory.FrameGenerator,   "Frame Generator",   Color.FromArgb(255, 0xB4, 0x53, 0x09)), // amber
        (InventorDocument.DocCategory.DesignAccelerator,"Design Accelerator",Color.FromArgb(255, 0x15, 0x80, 0x3D)), // green
        (InventorDocument.DocCategory.Weldment,         "Weldment",          Color.FromArgb(255, 0xB9, 0x1C, 0x1C)), // red
        (InventorDocument.DocCategory.Piping,           "Piping",            Color.FromArgb(255, 0x0F, 0x76, 0x6E)), // teal
        (InventorDocument.DocCategory.iPartFactory,     "iPart Factory",     Color.FromArgb(255, 0x6D, 0x28, 0xD9)), // violet
        (InventorDocument.DocCategory.iPartMember,      "iPart Member",      Color.FromArgb(255, 0x8B, 0x5C, 0xF6)), // light violet
        (InventorDocument.DocCategory.iAssemblyFactory, "iAssembly Factory", Color.FromArgb(255, 0xBE, 0x18, 0x5D)), // rose
        (InventorDocument.DocCategory.iAssemblyMember,  "iAssembly Member",  Color.FromArgb(255, 0xDB, 0x27, 0x77)), // light rose
        (InventorDocument.DocCategory.General,          "General",           Color.FromArgb(255, 0x4B, 0x55, 0x63)), // slate
    ];

    public static (string Name, Color Color) Of(InventorDocument.DocCategory cat)
    {
        foreach ((InventorDocument.DocCategory c, string n, Color col) in All)
        {
            if (c == cat) { return (n, col); }
        }
        return ("General", Color.FromArgb(255, 0x6B, 0x72, 0x80));
    }

    /// <summary>A legend panel (swatch + label per category) with the document's own category
    /// emphasised - used as the badge's hover tooltip content.</summary>
    public static FrameworkElement Legend(InventorDocument.DocCategory current)
    {
        StackPanel panel = new() { Spacing = 6, Padding = new Thickness(2) };
        panel.Children.Add(new TextBlock
        {
            Text = "Document categories", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 2)
        });

        foreach ((InventorDocument.DocCategory cat, string name, Color color) in All)
        {
            bool isCurrent = cat == current;
            StackPanel row = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
            row.Children.Add(new Border
            {
                Width = 12, Height = 12, CornerRadius = new CornerRadius(3),
                Background = new SolidColorBrush(color), VerticalAlignment = VerticalAlignment.Center
            });
            row.Children.Add(new TextBlock
            {
                Text = isCurrent ? name + "  (this document)" : name,
                FontWeight = isCurrent ? FontWeights.SemiBold : FontWeights.Normal,
                Opacity = isCurrent ? 1.0 : 0.75,
                VerticalAlignment = VerticalAlignment.Center
            });
            panel.Children.Add(row);
        }
        return panel;
    }
}
