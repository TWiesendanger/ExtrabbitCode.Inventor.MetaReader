using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ExtrabbitCode.Inventor.MetaReader.App.Dialogs;

/// <summary>First-run opt-in prompt for anonymous product analytics. Opt-in is the preselected
/// default (the primary "Yes" button is focused). The choice is persisted via
/// <see cref="AnalyticsConsent"/> so the prompt shows only once.</summary>
internal static class AnalyticsConsentDialog
{
    /// <summary>Shows the prompt if the user hasn't decided yet and records their choice;
    /// a no-op once a choice has been made.</summary>
    public static async Task MaybeAskAsync(XamlRoot xamlRoot, ElementTheme theme)
    {
        if (AnalyticsConsent.Decided) { return; }

        StackPanel body = new() { Spacing = 12, Width = 440 };
        body.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = "Help improve Inventor MetaReader by sharing anonymous usage data - which features " +
                   "are used and how often. This never includes your file names, paths, property values " +
                   "or any document content, and is hosted in the EU."
        });
        body.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Opacity = 0.75, FontSize = 12,
            Text = "You can turn this off any time in Settings."
        });
        body.Children.Add(new HyperlinkButton
        {
            Content = "Privacy policy", NavigateUri = new Uri(AppInfo.PrivacyUrl), Padding = new Thickness(0)
        });

        ContentDialog dlg = new()
        {
            Title = "Share anonymous usage data?",
            Content = body,
            PrimaryButtonText = "Yes, share",
            CloseButtonText = "No thanks",
            DefaultButton = ContentDialogButton.Primary,   // opt-in is the preselected default
            XamlRoot = xamlRoot,
            RequestedTheme = theme
        };

        ContentDialogResult result = await dlg.ShowAsync();
        AnalyticsConsent.Enabled = result == ContentDialogResult.Primary;
    }
}
