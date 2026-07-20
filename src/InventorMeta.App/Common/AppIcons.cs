using System;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

namespace ExtrabbitCode.Inventor.MetaReader.App.Common;

/// <summary>Maps an Inventor document kind to its bundled type icon (Assets\*.png).</summary>
internal static class AppIcons
{
    public static string Asset(InventorDocument.DocKind kind) => kind switch
    {
        InventorDocument.DocKind.Part         => "part.png",
        InventorDocument.DocKind.Assembly     => "assembly.png",
        InventorDocument.DocKind.Drawing      => "drawing.png",
        InventorDocument.DocKind.Presentation => "presentation.png",
        InventorDocument.DocKind.Step         => "step.png",
        _                                     => "other.png",
    };

    public static Uri Uri(InventorDocument.DocKind kind) =>
        new($"ms-appx:///Assets/{Asset(kind)}");

    public static BitmapImage Bitmap(InventorDocument.DocKind kind) => new(Uri(kind));

    public static IconSource IconSource(InventorDocument.DocKind kind) =>
        new ImageIconSource { ImageSource = Bitmap(kind) };
}
