using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace ExtrabbitCode.Inventor.MetaReader.App.Document;

/// <summary>iLogic: the sidebar's "iLogic" and trigger chips, and the iLogic tab listing every rule
/// with its options, triggers and read-only code - all read from the file, no Inventor needed.</summary>
public sealed partial class DocumentView
{
    private const int CodeGlyph = 0xE943, TriggerGlyph = 0xE945;

    /// <summary>Shows the outlined "iLogic" chip when the document contains rules and the trigger chip
    /// when it has event triggers; their tooltips list the rules and the event -> rule pairs.</summary>
    private void SetILogicBadges(InventorDocument doc)
    {
        ILogicInfo il = doc.ILogic;

        ILogicChip.Visibility = il.HasRules ? Visibility.Visible : Visibility.Collapsed;
        if (il.HasRules)
        {
            ToolTipService.SetToolTip(ILogicChip, new ToolTip
            {
                Content = RulesTooltip(il), PlacementTarget = ILogicChip, Placement = PlacementMode.Bottom
            });
        }

        TriggerChip.Visibility = il.HasTriggers ? Visibility.Visible : Visibility.Collapsed;
        if (il.HasTriggers)
        {
            TriggerChipText.Text = il.Triggers.Count == 1 ? "1 trigger" : $"{il.Triggers.Count} triggers";
            ToolTipService.SetToolTip(TriggerChip, new ToolTip
            {
                Content = TriggersTooltip(il), PlacementTarget = TriggerChip, Placement = PlacementMode.Bottom
            });
        }
    }

    private static FrameworkElement RulesTooltip(ILogicInfo il)
    {
        StackPanel panel = new() { Spacing = 4, Padding = new Thickness(2), MaxWidth = 320 };
        panel.Children.Add(new TextBlock
        {
            Text = il.Rules.Count switch { 0 => "Contains iLogic rules", 1 => "1 iLogic rule", int n => $"{n} iLogic rules" },
            FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 2)
        });
        foreach (ILogicRule r in il.Rules)
        {
            panel.Children.Add(new TextBlock
            {
                Text = r.IsSuppressed ? r.Name + "  (suppressed)" : r.Name,
                Opacity = r.IsSuppressed ? 0.6 : 1.0, TextWrapping = TextWrapping.Wrap
            });
        }
        panel.Children.Add(new TextBlock
        {
            Text = il.Rules.Count > 0
                ? "Click to see the code in the iLogic tab."
                : "Their names and code are not readable in this file version.",
            Opacity = 0.7, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0)
        });
        return panel;
    }

    private static FrameworkElement TriggersTooltip(ILogicInfo il)
    {
        StackPanel panel = new() { Spacing = 4, Padding = new Thickness(2), MaxWidth = 360 };
        panel.Children.Add(new TextBlock
        {
            Text = "iLogic event triggers", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 2)
        });
        Grid grid = new() { ColumnSpacing = 8, RowSpacing = 3 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        int row = 0;
        foreach (ILogicTrigger t in il.Triggers)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            AddCell(new TextBlock { Text = t.EventLabel }, 0);
            AddCell(new TextBlock { Text = "→", Opacity = 0.6 }, 1);
            AddCell(new TextBlock { Text = t.RuleName, FontWeight = FontWeights.SemiBold }, 2);
            row++;
        }
        panel.Children.Add(grid);
        return panel;

        void AddCell(TextBlock tb, int col)
        {
            Grid.SetRow(tb, row);
            Grid.SetColumn(tb, col);
            grid.Children.Add(tb);
        }
    }

    private void OnILogicChipTapped(object sender, TappedRoutedEventArgs e)
    {
        if (ILogicTab.Visibility == Visibility.Visible)
        {
            DetailTabs.SelectedItem = ILogicTab;
            e.Handled = true;
        }
    }

    /// <summary>Fills the iLogic tab: one card per rule (options, triggers, code) in the iLogic browser's
    /// order, plus triggers that run rules not stored in this document (external rules).</summary>
    private void PopulateILogic(InventorDocument doc)
    {
        ILogicPanel.Children.Clear();
        ILogicInfo il = doc.ILogic;
        ILogicTab.Header = il.Rules.Count > 0 ? $"iLogic ({il.Rules.Count})" : "iLogic";
        if (il.IsEmpty)
        {
            return;
        }

        ILogicPanel.Children.Add(new TextBlock
        {
            Text = "iLogic rules stored in this document, read straight from the file - no Inventor needed.",
            Opacity = 0.7, FontSize = 13, TextWrapping = TextWrapping.Wrap
        });

        if (il.HasRules && il.Rules.Count == 0)
        {
            ILogicPanel.Children.Add(new TextBlock
            {
                Text = "This document contains iLogic rules, but their names and code are not readable in this file version.",
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 0, 4, 0)
            });
        }

        foreach (ILogicRule r in il.Rules)
        {
            ILogicPanel.Children.Add(Card(r.Name, null, RuleBody(r)));
        }

        List<ILogicTrigger> external = il.Triggers.Where(t => !il.Rules.Any(r => r.Name == t.RuleName)).ToList();
        if (external.Count > 0)
        {
            StackPanel body = new() { Padding = new Thickness(14, 10, 14, 12), Spacing = 6 };
            body.Children.Add(new TextBlock
            {
                Text = "These events run rules that are not stored in this document (external rules).",
                Opacity = 0.7, FontSize = 12, TextWrapping = TextWrapping.Wrap
            });
            foreach (ILogicTrigger t in external)
            {
                body.Children.Add(TriggerLine(t.EventLabel + "  →  " + t.RuleName));
            }
            ILogicPanel.Children.Add(Card("Event triggers", external.Count.ToString(), body));
        }
    }

    private UIElement RuleBody(ILogicRule r)
    {
        StackPanel body = new() { Padding = new Thickness(14, 10, 14, 12), Spacing = 10 };

        // options that differ from iLogic's defaults, as outlined chips
        List<string> options = [];
        if (r.IsSuppressed) { options.Add("Suppressed"); }
        if (r.Flags != null && !r.RunsAutomatically) { options.Add("Doesn't run automatically"); }
        if (r.IsStraightVb) { options.Add("Straight VB code"); }
        if (r.FireDependentImmediately) { options.Add("Fires dependent rules immediately"); }
        if (r.Flags != null && !r.SilentOperation) { options.Add("Not silent"); }
        if (options.Count > 0)
        {
            Common.WrapPanel chips = new();
            foreach (string o in options)
            {
                chips.Children.Add(new Border
                {
                    Style = (Style)Resources["FeatureChip"],
                    Child = new TextBlock { Text = o, Style = (Style)Resources["FeatureChipText"] }
                });
            }
            body.Children.Add(chips);
        }

        foreach (ILogicTrigger t in r.Triggers)
        {
            body.Children.Add(TriggerLine("Runs on: " + t.EventLabel));
        }

        if (r.Text == null)
        {
            body.Children.Add(new TextBlock { Text = "The rule's code is not readable in this file version.", Opacity = 0.7 });
            return body;
        }

        Grid codeHeader = new();
        codeHeader.Children.Add(new TextBlock
        {
            Text = "Code", FontWeight = FontWeights.SemiBold, FontSize = 12, Opacity = 0.7,
            VerticalAlignment = VerticalAlignment.Center
        });
        Button copy = new()
        {
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 5,
                Children =
                {
                    new FontIcon { Glyph = G(0xE8C8), FontSize = 11 },
                    new TextBlock { Text = "Copy code", FontSize = 11 },
                }
            },
            Padding = new Thickness(8, 1, 8, 2), MinWidth = 0, HorizontalAlignment = HorizontalAlignment.Right
        };
        string code = r.Text;
        copy.Click += (_, _) => Copy(code);
        codeHeader.Children.Add(copy);
        body.Children.Add(codeHeader);

        body.Children.Add(new Border
        {
            Style = (Style)Resources["CodeBlock"],
            Child = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                HorizontalScrollMode = ScrollMode.Auto, VerticalScrollMode = ScrollMode.Disabled,
                Content = new TextBlock
                {
                    Text = code.Length > 0 ? code : "(empty rule)",
                    FontFamily = new FontFamily("Consolas"), FontSize = 13,   // as the File Structure tab
                    IsTextSelectionEnabled = true, TextWrapping = TextWrapping.NoWrap,
                    Margin = new Thickness(12, 10, 12, 10)
                }
            }
        });
        return body;
    }

    private static UIElement TriggerLine(string text) => new StackPanel
    {
        Orientation = Orientation.Horizontal, Spacing = 6,
        Children =
        {
            new FontIcon { Glyph = G(TriggerGlyph), FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Opacity = 0.8 },
            new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center },
        }
    };
}
