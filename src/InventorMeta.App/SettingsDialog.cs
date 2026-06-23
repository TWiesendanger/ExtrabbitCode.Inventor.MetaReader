using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace ExtrabbitCode.Inventor.MetaReader.App;

/// <summary>The app settings dialog. Currently the 3D viewer: which Inventor generates viewables,
/// an optional shared (network) cache, edge display, and clearing the local cache.</summary>
internal static class SettingsDialog
{
    public static async Task ShowAsync(XamlRoot xamlRoot)
    {
        System.Collections.Generic.IReadOnlyList<InventorInstall> installs = InventorInstalls.Detect();
        SvfStore store = new(ViewerSettings.NetworkPath);

        ComboBox version = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
        version.Items.Add("Ask each time");
        foreach (InventorInstall i in installs) { version.Items.Add(i.DisplayName); }
        int savedIdx = installs.ToList().FindIndex(i => i.Year == ViewerSettings.InventorYear);
        version.SelectedIndex = savedIdx >= 0 ? savedIdx + 1 : 0;
        if (installs.Count == 0) { version.IsEnabled = false; }

        TextBox network = new()
        {
            Text = ViewerSettings.NetworkPath ?? "", HorizontalAlignment = HorizontalAlignment.Stretch,
            PlaceholderText = @"\\server\share\svf-cache  (optional)"
        };

        TextBlock cacheInfo = new() { Opacity = 0.7, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        void RefreshCache() => cacheInfo.Text = $"Local cache: {Mb(store.LocalSizeBytes())}";
        RefreshCache();
        Button open = new() { Content = "Open" };
        open.Click += (_, _) =>
        {
            try
            {
                System.IO.Directory.CreateDirectory(store.LocalRoot);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = store.LocalRoot, UseShellExecute = true
                });
            }
            catch { /* best effort */ }
        };
        Button clear = new() { Content = "Clear" };
        clear.Click += (_, _) => { store.ClearLocal(); RefreshCache(); };
        StackPanel cacheCtl = new()
        {
            Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center,
            Children = { cacheInfo, open, clear }
        };

        StackPanel body = new() { Spacing = 10, Width = 460 };
        body.Children.Add(SectionHeader("3D Viewer"));
        body.Children.Add(Row("Inventor version", "Which installed Inventor generates the 3D viewable.", version));
        body.Children.Add(Row("Shared cache folder", "A network path so viewables are reused across users. Leave empty for local only.", network));
        body.Children.Add(Row("Cached viewables", "SVF files generated on this PC.", cacheCtl));

        Button openLog = new() { Content = "Open log" };
        openLog.Click += (_, _) => AppLog.OpenLatest();
        body.Children.Add(Row("Diagnostics log", "App and 3D-viewer activity (today's log file).", openLog));

        body.Children.Add(SectionHeader("Privacy"));
        ToggleSwitch analytics = new() { IsOn = AnalyticsConsent.Enabled };
        body.Children.Add(Row("Share anonymous usage data",
            "Which features are used and how often. Never includes file names, paths, property values or document content; hosted in the EU.",
            analytics));
        HyperlinkButton privacyLink = new()
        {
            Content = "Privacy policy", NavigateUri = new System.Uri(AppInfo.PrivacyUrl),
            Padding = new Thickness(0), Margin = new Thickness(2, -4, 0, 0)
        };
        body.Children.Add(privacyLink);

        ContentDialog dlg = new()
        {
            Title = "Settings",
            Content = new ScrollViewer { Content = body, MaxHeight = 540, HorizontalScrollMode = ScrollMode.Disabled },
            PrimaryButtonText = "Save", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary,
            XamlRoot = xamlRoot
        };

        if (await dlg.ShowAsync() != ContentDialogResult.Primary) { return; }

        ViewerSettings.NetworkPath = string.IsNullOrWhiteSpace(network.Text) ? null : network.Text.Trim();
        ViewerSettings.InventorYear = version.SelectedIndex <= 0 ? 0 : installs[version.SelectedIndex - 1].Year;
        AnalyticsConsent.Enabled = analytics.IsOn;
    }

    private static TextBlock SectionHeader(string text) =>
        new() { Text = text, FontWeight = FontWeights.SemiBold, FontSize = 15, Margin = new Thickness(0, 2, 0, 4) };

    /// <summary>A settings card: label + description on the left; full-width inputs (text/combo) stack
    /// beneath the text, compact controls (toggle/button) sit to the right.</summary>
    private static Border Row(string label, string description, FrameworkElement control)
    {
        StackPanel text = new() { VerticalAlignment = VerticalAlignment.Center, Spacing = 2 };
        text.Children.Add(new TextBlock { Text = label, FontWeight = FontWeights.SemiBold });
        text.Children.Add(new TextBlock { Text = description, Opacity = 0.65, FontSize = 12, TextWrapping = TextWrapping.Wrap });

        if (control is TextBox or ComboBox)
        {
            StackPanel stack = new() { Spacing = 8 };
            stack.Children.Add(text);
            stack.Children.Add(control);
            return Card(stack);
        }

        Grid g = new() { ColumnSpacing = 16 };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(text, 0);
        g.Children.Add(text);
        control.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(control, 1);
        g.Children.Add(control);
        return Card(g);
    }

    private static Border Card(UIElement child) => new()
    {
        Padding = new Thickness(14, 12, 14, 12), CornerRadius = new CornerRadius(8), Child = child,
        Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
        BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultSolidBrush"],
        BorderThickness = new Thickness(1)
    };

    private static string Mb(long bytes) =>
        bytes < 1024 * 1024 ? $"{bytes / 1024.0:0} KB" : $"{bytes / (1024.0 * 1024.0):0.0} MB";
}
