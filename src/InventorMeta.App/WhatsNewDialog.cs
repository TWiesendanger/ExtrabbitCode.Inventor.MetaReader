using System;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace ExtrabbitCode.Inventor.MetaReader.App;

/// <summary>
/// The "What's new" overlay: highlights of the current release with short demo GIFs. Shown
/// automatically ONCE per installed version (first launch after an update), and reopenable any
/// time from the footer's "What's new" button. Clicking outside the card dismisses it; the GIFs
/// sit still until clicked, so nothing flashes at the user uninvited.
/// </summary>
internal static class WhatsNewDialog
{
    private const string SeenVersionKey = "whatsnew.version";

    private sealed record Section(string Title, string Blurb, string? GifAsset);

    private static readonly Section[] Sections =
    [
        new("Redlining: draw on the model",
            "Circle a problem, add text, or paint directly onto the geometry with the 3D pen. " +
            "Markup lives on layers and is saved with the model.",
            "redlining.gif"),
        new("3D views, with or without Inventor",
            "A built-in converter renders parts and assemblies on machines without Inventor. " +
            "Body coloring gives every part its own colour with one click.",
            "viewer3d.gif"),
        new("Reference graph with thumbnails",
            "Every node can draw its part's preview image, and the camera work is all yours: " +
            "zoom, drag, three layouts.",
            "references.gif"),
        new("Also new",
            "STEP import (.stp/.step) with a full header readout and 3D view. Rebindable keyboard " +
            "shortcuts. A richer sample gallery, including a real-world fishing-reel assembly.",
            null),
    ];

    /// <summary>Shows the dialog if this app version hasn't presented it yet. Returns whether it
    /// was shown, so the caller can hold back other first-run UI.</summary>
    public static bool MaybeShow(MainWindow win)
    {
        if (AppSettings.Get(SeenVersionKey) == AppInfo.Version) { return false; }
        AppSettings.Set(SeenVersionKey, AppInfo.Version);
        Show(win);
        return true;
    }

    /// <summary>Builds and shows the overlay (also used by the footer's "What's new" button).</summary>
    public static void Show(MainWindow win)
    {
        StackPanel content = new() { Spacing = 18 };
        foreach (Section s in Sections)
        {
            StackPanel section = new() { Spacing = 6 };
            section.Children.Add(new TextBlock
            {
                Text = s.Title, FontSize = 16, FontWeight = FontWeights.SemiBold
            });
            section.Children.Add(new TextBlock
            {
                Text = s.Blurb, Opacity = 0.8, FontSize = 13, TextWrapping = TextWrapping.Wrap
            });
            if (s.GifAsset != null) { section.Children.Add(GifCard(s.GifAsset)); }
            content.Children.Add(section);
        }

        ScrollViewer scroll = new()
        {
            Content = content,
            Padding = new Thickness(24, 0, 24, 20),
            HorizontalScrollMode = ScrollMode.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };

        TextBlock title = new()
        {
            Text = "What's new in " + string.Join('.', AppInfo.Version.Split('.'), 0, 3),
            FontSize = 20, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center
        };
        Button close = new()
        {
            Content = new FontIcon { Glyph = "\uE711", FontSize = 13 },
            Width = 34, Height = 34, Padding = new Thickness(0), CornerRadius = new CornerRadius(17),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center
        };
        ToolTipService.SetToolTip(close, "Close (Esc)");
        Grid header = new() { Padding = new Thickness(24, 18, 16, 14) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(close, 1);
        header.Children.Add(title);
        header.Children.Add(close);

        Grid layout = new();
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(scroll, 1);
        layout.Children.Add(header);
        layout.Children.Add(scroll);

        Border card = new()
        {
            Child = layout,
            Width = 760,
            MaxHeight = 700,
            CornerRadius = new CornerRadius(10),
            Background = (Brush)Application.Current.Resources["SolidBackgroundFillColorBaseBrush"],
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultSolidBrush"],
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        card.Tapped += (_, e) => e.Handled = true;       // clicks inside don't dismiss

        Grid root = new() { Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        root.Children.Add(card);

        void Close() => win.HideOverlay();
        root.Tapped += (_, _) => Close();                // click-away dismisses
        close.Click += (_, _) => Close();
        KeyboardAccelerator esc = new() { Key = Windows.System.VirtualKey.Escape };
        esc.Invoked += (_, e) => { e.Handled = true; Close(); };
        close.KeyboardAccelerators.Add(esc);

        win.ShowOverlay(root, dimmed: true);
        close.Focus(FocusState.Programmatic);
    }

    /// <summary>A demo GIF that sits on its first frame until clicked - click again to pause.
    /// A play badge signals the interaction and hides while the animation runs.</summary>
    private static UIElement GifCard(string asset)
    {
        BitmapImage bmp = new() { UriSource = new Uri($"ms-appx:///Assets/whatsnew/{asset}"), AutoPlay = false };
        Image img = new() { Source = bmp, Stretch = Stretch.Uniform };

        Border badge = new()
        {
            Width = 52, Height = 52, CornerRadius = new CornerRadius(26),
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0xD0, 0x1B, 0x1B, 0x1B)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
            Child = new FontIcon
            {
                Glyph = "\uE768",   // play
                FontSize = 20,
                Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
                Margin = new Thickness(4, 0, 0, 0)
            }
        };

        Grid holder = new();
        holder.Children.Add(img);
        holder.Children.Add(badge);

        Border frame = new()
        {
            Child = holder,
            CornerRadius = new CornerRadius(8),
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultSolidBrush"],
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 6, 0, 0)
        };

        frame.Tapped += (_, e) =>
        {
            e.Handled = true;                            // a play click never dismisses the dialog
            if (bmp.IsPlaying) { bmp.Stop(); badge.Visibility = Visibility.Visible; }
            else { bmp.Play(); badge.Visibility = Visibility.Collapsed; }
        };
        return frame;
    }
}
