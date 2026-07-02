using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace ExtrabbitCode.Inventor.MetaReader.App;

/// <summary>The "Try a sample" gallery shown from the start page: a list of bundled sample files,
/// each with the capability it demonstrates. Clicking one opens it in a tab; "Open all" opens the
/// lot. Opening is done through the <paramref name="open"/> callback so the dialog stays decoupled
/// from the window.</summary>
internal static class SamplesGallery
{
    public static async Task ShowAsync(XamlRoot xamlRoot, Action<string> open)
    {
        var samples = SampleFiles.AvailableSamples().ToList();

        StackPanel body = new() { Spacing = 10, Width = 460 };
        body.Children.Add(new TextBlock
        {
            Text = "Explore what MetaReader reads. Each opens in its own tab - no Autodesk Inventor required.",
            Opacity = 0.75, FontSize = 13, TextWrapping = TextWrapping.Wrap
        });

        ContentDialog dlg = new()
        {
            Title = "Try a sample",
            PrimaryButtonText = samples.Count > 1 ? "Open all" : "",
            CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = xamlRoot
        };

        foreach ((SampleFiles.Sample sample, string path) in samples)
        {
            Button card = SampleCard(sample);
            card.Click += (_, _) => { dlg.Hide(); open(path); };
            body.Children.Add(card);
        }

        if (samples.Count == 0)
        {
            body.Children.Add(new TextBlock
            {
                Text = "The bundled samples aren't available in this build.",
                Opacity = 0.6, FontSize = 13, TextWrapping = TextWrapping.Wrap
            });
        }

        dlg.Content = new ScrollViewer { Content = body, MaxHeight = 520, HorizontalScrollMode = ScrollMode.Disabled };

        if (await dlg.ShowAsync() == ContentDialogResult.Primary)
        {
            foreach ((_, string path) in samples) { open(path); }
        }
    }

    /// <summary>A full-width clickable card: type icon, title and one-line capability blurb.</summary>
    private static Button SampleCard(SampleFiles.Sample sample)
    {
        Image icon = new()
        {
            Width = 34, Height = 34, VerticalAlignment = VerticalAlignment.Center,
            Source = new BitmapImage(AppIcons.Uri(sample.Kind))
        };

        StackPanel text = new() { VerticalAlignment = VerticalAlignment.Center, Spacing = 2 };
        text.Children.Add(new TextBlock { Text = sample.Title, FontWeight = FontWeights.SemiBold });
        text.Children.Add(new TextBlock
        {
            Text = sample.Blurb, Opacity = 0.65, FontSize = 12, TextWrapping = TextWrapping.Wrap
        });
        text.Children.Add(new TextBlock
        {
            Text = sample.FileName, FontFamily = new FontFamily("Consolas"), FontSize = 11, Opacity = 0.45
        });

        FontIcon chevron = new() { Glyph = "", FontSize = 12, Opacity = 0.5, VerticalAlignment = VerticalAlignment.Center };

        Grid g = new() { ColumnSpacing = 14 };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(icon, 0); g.Children.Add(icon);
        Grid.SetColumn(text, 1); g.Children.Add(text);
        Grid.SetColumn(chevron, 2); g.Children.Add(chevron);

        return new Button
        {
            Content = g,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(14, 12, 14, 12),
            CornerRadius = new CornerRadius(8),
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultSolidBrush"],
            BorderThickness = new Thickness(1)
        };
    }
}
