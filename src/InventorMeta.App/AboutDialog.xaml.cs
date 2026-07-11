using System;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ExtrabbitCode.Inventor.MetaReader.App;

public sealed partial class AboutDialog
{
    public AboutDialog()
    {
        InitializeComponent();

        NameText.Text    = AppInfo.Name;
        DescText.Text    = AppInfo.Description;
        VersionText.Text = "Version " + AppInfo.Version;
        DocsButton.NavigateUri      = new Uri(AppInfo.DocsUrl);
        GitHubButton.NavigateUri    = new Uri(AppInfo.GitHubUrl);
        ExtrabbitButton.NavigateUri = new Uri(AppInfo.ExtrabbitUrl);
        NoticesButton.NavigateUri   = new Uri(AppInfo.GitHubUrl + "/blob/master/THIRD-PARTY-NOTICES.md");

        foreach ((string version, string[] notes) in AppInfo.History)
        {
            StackPanel block = new() { Spacing = 2 };
            block.Children.Add(new TextBlock { Text = version, FontWeight = FontWeights.SemiBold });
            foreach (string note in notes)
            {
                block.Children.Add(new TextBlock
                {
                    Text = "• " + note,
                    Opacity = 0.85,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(8, 0, 0, 0)
                });
            }

            HistoryPanel.Children.Add(block);
        }
    }

    /// <summary>Opens the bundled LGPL-2.1 license text (shipped next to the OCCT WASM module) in the
    /// default text viewer.</summary>
    private void OnShowLgplLicense(object sender, RoutedEventArgs e)
    {
        try
        {
            string path = System.IO.Path.Combine(
                AppContext.BaseDirectory, "Assets", "stepviewer", "vendor", "license_occt_import_js.txt");
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = path, UseShellExecute = true
            });
        }
        catch { /* best effort - the text is also linked from THIRD-PARTY-NOTICES.md */ }
    }
}