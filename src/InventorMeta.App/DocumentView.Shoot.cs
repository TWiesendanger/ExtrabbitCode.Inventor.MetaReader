using System;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ExtrabbitCode.Inventor.MetaReader.App;

/// <summary>Helpers used only by the documentation snapshotter (<see cref="DocShooter"/>).</summary>
public sealed partial class DocumentView
{
    /// <summary>Selects a detail tab by its header prefix (e.g. "Model States" matches
    /// "Model States (3)"). No-op if the tab is hidden or absent.</summary>
    public void ShootSelectTab(string headerPrefix)
    {
        TabViewItem? tab = DetailTabs.TabItems.OfType<TabViewItem>().FirstOrDefault(t =>
            t.Visibility == Visibility.Visible &&
            t.Header is string h && h.StartsWith(headerPrefix, StringComparison.Ordinal));

        if (tab != null)
        {
            DetailTabs.SelectedItem = tab;
        }
    }

    /// <summary>Resolves a named element inside this view's XAML namescope, for capturing just that
    /// region (e.g. the sidebar card or a single detail panel).</summary>
    public FrameworkElement? ShootElement(string name) => FindName(name) as FrameworkElement;
}
