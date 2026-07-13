using System;
using System.Threading.Tasks;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace ExtrabbitCode.Inventor.MetaReader.App;

/// <summary>Dialogs around the SVF engine choice: the first-use chooser (Inventor vs the built-in
/// best-effort converter) and the one-time notice shown when no Inventor is installed.</summary>
internal static class EngineDialogs
{
    public const string SupportMail = "extrabbitcode@gmail.com";

    /// <summary>Builds the mailto: link used to report a wrongly displayed model.</summary>
    public static Uri ReportUri(string? fileName = null) => new(
        $"mailto:{SupportMail}"
        + "?subject=" + Uri.EscapeDataString("MetaReader: model displayed wrong" + (fileName is null ? "" : $" - {fileName}"))
        + "&body=" + Uri.EscapeDataString(
            "Hi!\n\nThis model doesn't look right in MetaReader's built-in 3D view"
            + (fileName is null ? "" : $" ({fileName})") + ".\n\n"
            + "Please attach the part/assembly file (plus referenced parts for an assembly) so the converter can be fixed.\n"));

    /// <summary>First-use chooser between the two engines - two side-by-side cards with their
    /// trade-offs. Returns the picked engine, or null if the user backed out.
    /// <paramref name="built"/> hands the dialog instance to the demo tour, which shows the
    /// chooser on camera (setting its theme and closing it again).</summary>
    public static async Task<SvfEngine?> ShowChooserAsync(XamlRoot xamlRoot, Action<ContentDialog>? built = null)
    {
        SvfEngine? picked = null;

        ContentDialog dlg = new()
        {
            Title = "How should 3D views be generated?",
            CloseButtonText = "Not now",
            DefaultButton = ContentDialogButton.None,
            XamlRoot = xamlRoot
        };
        // the two cards need more room than ContentDialog's default 548px cap - without this the
        // right card gets clipped
        dlg.Resources["ContentDialogMaxWidth"] = 720d;
        dlg.Content = BuildChooserBody(e => { picked = e; dlg.Hide(); });

        built?.Invoke(dlg);
        await dlg.ShowAsync();
        return picked;
    }

    /// <summary>The chooser's body - intro line plus the two engine cards. Also hosted by the
    /// documentation snapshotter, which can't photograph a real ContentDialog (popups render in
    /// their own layer, outside the window content that RenderTargetBitmap sees).</summary>
    internal static StackPanel BuildChooserBody(Action<SvfEngine> onPick)
    {
        Grid cards = new() { ColumnSpacing = 12 };
        cards.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        cards.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        Button inventor = EngineCard(
            "ms-appx:///Assets/inventor.png", "Autodesk Inventor", "Exact translation",
            pros: ["Exact geometry, positions and rotations", "Official Inventor translator"],
            cons: ["Slower - starts Inventor in the background", "Needs Inventor installed on this PC"]);
        Button local = EngineCard(
            "ms-appx:///Assets/extrabbit.png", "Built-in converter", "Fast, best effort",
            pros: ["No Inventor needed on this PC", "Fast - reads the mesh cached in the file"],
            cons: ["Best effort: positions or rotations can be off", "Simplified materials"]);

        inventor.Click += (_, _) => onPick(SvfEngine.Inventor);
        local.Click += (_, _) => onPick(SvfEngine.Local);

        Grid.SetColumn(inventor, 0); cards.Children.Add(inventor);
        Grid.SetColumn(local, 1); cards.Children.Add(local);

        StackPanel body = new() { Spacing = 12, Width = 620 };
        body.Children.Add(new TextBlock
        {
            Text = "Pick the engine that turns your models into 3D views. Either way, the 3D viewer "
                 + "itself downloads from the internet the first time you use it. Change this anytime "
                 + "in Settings → 3D Viewer.",
            Opacity = 0.75, FontSize = 13, TextWrapping = TextWrapping.Wrap
        });
        body.Children.Add(cards);
        return body;
    }

    /// <summary>One-time notice when no Inventor is installed: 3D views come from the built-in
    /// reverse-engineered converter and are best effort; wrong models can be reported by mail.</summary>
    public static async Task ShowBestEffortInfoAsync(XamlRoot xamlRoot)
    {
        StackPanel body = new() { Spacing = 10, Width = 460 };
        body.Children.Add(new TextBlock
        {
            Text = "No Autodesk Inventor was found on this PC, so MetaReader renders 3D views with its "
                 + "built-in converter - it reads the mesh cached inside the file, no Inventor needed.",
            TextWrapping = TextWrapping.Wrap
        });
        body.Children.Add(new TextBlock
        {
            Text = "This is a best-effort view: in some models, part positions or rotations can be off. "
                 + "If a model looks wrong, send it over and the converter gets fixed:",
            TextWrapping = TextWrapping.Wrap, Opacity = 0.85
        });
        body.Children.Add(new HyperlinkButton
        {
            Content = SupportMail, NavigateUri = ReportUri(), Padding = new Thickness(0)
        });

        ContentDialog dlg = new()
        {
            Title = "Best-effort 3D view",
            Content = body,
            PrimaryButtonText = "Got it",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = xamlRoot
        };
        await dlg.ShowAsync();
    }

    /// <summary>One selectable engine card: icon, name, tagline, then pro (✓) and con (−) lines.</summary>
    private static Button EngineCard(string iconAsset, string title, string tagline, string[] pros, string[] cons)
    {
        StackPanel content = new() { Spacing = 4 };

        content.Children.Add(new Image
        {
            Source = new BitmapImage(new Uri(iconAsset)), Height = 40,
            HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 2, 0, 6)
        });
        content.Children.Add(new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeights.SemiBold });
        content.Children.Add(new TextBlock { Text = tagline, FontSize = 12, Opacity = 0.65, Margin = new Thickness(0, 0, 0, 8) });

        foreach (string p in pros) { content.Children.Add(TraitLine("", "SystemFillColorSuccessBrush", p)); }
        foreach (string c in cons) { content.Children.Add(TraitLine("", "SystemFillColorCautionBrush", c)); }

        return new Button
        {
            Content = content,
            Padding = new Thickness(16, 14, 16, 14),
            CornerRadius = new CornerRadius(8),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Top,
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultSolidBrush"],
            BorderThickness = new Thickness(1)
        };
    }

    private static Grid TraitLine(string glyph, string brushKey, string text)
    {
        Grid g = new() { ColumnSpacing = 8, Margin = new Thickness(0, 2, 0, 2) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        FontIcon icon = new()
        {
            Glyph = glyph, FontSize = 12, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 3, 0, 0),
            Foreground = (Brush)Application.Current.Resources[brushKey]
        };
        TextBlock label = new() { Text = text, FontSize = 12, Opacity = 0.85, TextWrapping = TextWrapping.Wrap };

        Grid.SetColumn(icon, 0); g.Children.Add(icon);
        Grid.SetColumn(label, 1); g.Children.Add(label);
        return g;
    }
}
