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
        DocsButton.NavigateUri   = new Uri(AppInfo.DocsUrl);
        GitHubButton.NavigateUri = new Uri(AppInfo.GitHubUrl);

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
}