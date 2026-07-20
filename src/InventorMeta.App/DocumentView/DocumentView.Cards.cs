using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;

namespace ExtrabbitCode.Inventor.MetaReader.App;

/// <summary>The shared card and table toolkit: collapsible cards, click-to-copy rows,
/// property tables, key/value lists and matrices used by every detail tab.</summary>
public sealed partial class DocumentView
{
    // sort order for property-set cards: key sets first, internal sets last
    private static int SetOrder(string set) => set switch
    {
        _ when set.Contains("(internal)")              => 100,
        _ when set.StartsWith("Design Tracking Prop")  => 0,
        "Inventor User Defined Properties"             => 1,
        "Custom (User Defined) Properties"             => 1,
        "Inventor Summary Information"                 => 2,
        "Summary Information"                          => 2,
        "Inventor Document Summary Information"        => 3,
        "Document Summary Information"                 => 3,
        _                                              => 50,
    };

    // ---- card / table builders ----

    /// <summary>Wraps a row in a hover area with copy (and optional hide) buttons on the right.</summary>
    private Border CopyableRow(UIElement content, string copyText, Thickness padding, string? hideKey = null)
    {
        // Selectable text inside the row captures the pointer for selection, which swallows the
        // row's tap - so click-to-copy only worked on a fast click. The row copies the whole value
        // anyway, so turn selection off here to make the click reliable.
        DisableTextSelection(content);

        Grid grid = new();
        grid.Children.Add(content);

        Button btn = new()
        {
            Content = new FontIcon { Glyph = "", FontSize = 12 },
            VerticalAlignment = VerticalAlignment.Center,
            Padding = new Thickness(7, 3, 7, 3), MinWidth = 0, MinHeight = 0,
            CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent)
        };
        ToolTipService.SetToolTip(btn, "Copy value");
        bool can = !string.IsNullOrEmpty(copyText);
        btn.Click += (_, _) => Copy(copyText);

        // hide + copy live in a right-aligned panel that stays in layout (row never resizes)
        StackPanel actions = new()
        {
            Orientation = Orientation.Horizontal, Spacing = 4,
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0, IsHitTestVisible = false
        };
        if (hideKey != null)
        {
            actions.Children.Add(MakeHideButton(hideKey, "Hide this property"));
        }

        if (can)
        {
            actions.Children.Add(btn);
        }

        grid.Children.Add(actions);

        Border row = new()
        {
            Child = grid, Padding = padding,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) // make the whole row hit-testable
        };
        if (can)
        {
            ToolTipService.SetToolTip(row, "Click to copy");
        }

        bool has = can || hideKey != null;
        row.PointerEntered += (_, _) => { if (has) { actions.Opacity = 1; actions.IsHitTestVisible = true; } };
        row.PointerExited += (_, _) => { actions.Opacity = 0; actions.IsHitTestVisible = false; };
        row.Tapped += (_, e) => { if (can) { Copy(copyText); e.Handled = true; } };
        return row;
    }

    /// <summary>Turns off text selection on every TextBlock within a row so it doesn't intercept
    /// the row's copy tap.</summary>
    private static void DisableTextSelection(UIElement element)
    {
        switch (element)
        {
            case TextBlock tb: tb.IsTextSelectionEnabled = false; break;
            case Panel panel: foreach (UIElement child in panel.Children) { DisableTextSelection(child); } break;
            case Border border when border.Child is { } c: DisableTextSelection(c); break;
        }
    }

    private void Copy(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        try
        {
            DataPackage dp = new();
            dp.SetText(text);
            Clipboard.SetContent(dp);
            string shown = text.Length > 48 ? text[..48] + "…" : text;
            (HostWindow ?? App.MainWindowInstance as MainWindow)?.ShowToast($"Copied  “{shown}”");
        }
        catch
        {
            // ignored
        }
    }


    /// <summary>A rounded, bordered, elevated card with a clickable (collapsible) header and a body.</summary>
    private Border Card(string title, string? badge, UIElement body, string? hideKey = null)
    {
        FontIcon chevron = new()
        {
            Glyph = G(ChevronUp), FontSize = 11, Opacity = 0.6,
            VerticalAlignment = VerticalAlignment.Center
        };
        StackPanel headerContent = new() { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        headerContent.Children.Add(chevron);
        headerContent.Children.Add(new TextBlock
        {
            Text = title, FontWeight = FontWeights.SemiBold, FontSize = 14,
            VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap
        });
        if (badge != null)
        {
            headerContent.Children.Add(new Border
            {
                Style = (Style)Resources["Badge"],
                Child = new TextBlock { Text = badge, Style = (Style)Resources["BadgeText"] }
            });
        }

        Grid headerGrid = new();
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerGrid.Children.Add(headerContent);

        Button? hideBtn = null;
        if (hideKey != null)
        {
            hideBtn = MakeHideButton(hideKey, "Hide this property set");
            hideBtn.Opacity = 0;
            hideBtn.IsHitTestVisible = false;
            Grid.SetColumn(hideBtn, 1);
            headerGrid.Children.Add(hideBtn);
        }

        Border header = new() { Style = (Style)Resources["CardHeaderRow"], Child = headerGrid };
        ToolTipService.SetToolTip(header, "Click to collapse / expand");
        if (hideBtn != null)
        {
            header.PointerEntered += (_, _) => { hideBtn.Opacity = 1; hideBtn.IsHitTestVisible = true; };
            header.PointerExited += (_, _) => { hideBtn.Opacity = 0; hideBtn.IsHitTestVisible = false; };
        }

        StackPanel stack = new();
        stack.Children.Add(header);
        stack.Children.Add(body);

        header.Tapped += (_, _) =>
        {
            bool show = body.Visibility == Visibility.Collapsed;
            body.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            chevron.Glyph = G(show ? ChevronUp : ChevronDown);
        };
        _collapsibles.Add((body, chevron));

        Border card = new() { Style = (Style)Resources["DataCard"], Child = stack, Margin = new Thickness(0, 0, 0, 8) };
        card.Shadow = new ThemeShadow();
        card.Translation = new Vector3(0, 0, 8);
        return card;
    }

    /// <summary>A pair of buttons that collapse (up) or expand (down) every card in this view.</summary>
    private UIElement BuildExpandCollapseAll() => new StackPanel
    {
        Orientation = Orientation.Horizontal,
        Spacing = 6,
        HorizontalAlignment = HorizontalAlignment.Right,
        Children =
        {
            ExpandCollapseButton(ChevronUp, "Collapse all", false),
            ExpandCollapseButton(ChevronDown, "Expand all", true)
        }
    };

    private Button ExpandCollapseButton(int glyph, string tooltip, bool expand)
    {
        Button b = new()
        {
            Content = new FontIcon { Glyph = G(glyph), FontSize = 12 },
            Padding = new Thickness(11, 6, 11, 6), MinWidth = 0
        };
        ToolTipService.SetToolTip(b, tooltip);
        b.Click += (_, _) => SetAllExpanded(expand);
        return b;
    }

    /// <summary>PID / Name / Value table with divider lines between rows.</summary>
    private StackPanel PropTable(string setName, IEnumerable<(uint pid, string name, string val)> rows)
    {
        List<(uint pid, string name, string val)> list = rows
            .Where(r => !HideStore.IsHidden(HideStore.PropKey(setName, r.pid))).ToList();
        StackPanel sp = new();
        for (int i = 0; i < list.Count; i++)
        {
            (uint pid, string name, string val) = list[i];
            Grid g = new();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(46) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(196) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Cell(g, 0, pid.ToString(), 0.45, mono: true);
            Cell(g, 1, name, 0.9, semibold: true);
            Cell(g, 2, val, 0.8);
            sp.Children.Add(CopyableRow(g, val, new Thickness(14, 7, 14, 7), HideStore.PropKey(setName, pid)));
        }
        return sp;
    }

    /// <summary>Two-column key / value list (plain rows).</summary>
    private StackPanel KvList(List<(string k, string v)> rows, double keyWidth)
    {
        StackPanel sp = new();
        for (int i = 0; i < rows.Count; i++)
        {
            (string k, string v) = rows[i];
            Grid g = new();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(keyWidth) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Cell(g, 0, k, 0.55);
            Cell(g, 1, v, 0.95, semibold: true);
            sp.Children.Add(CopyableRow(g, v, new Thickness(14, 7, 14, 7)));
        }
        return sp;
    }

    /// <summary>Per-state comparison matrix: a header strip + zebra rows, active column tinted.</summary>
    private Grid Matrix(string[] headers, List<(string label, string[] vals)> rows)
    {
        int cols = headers.Length + 1;
        Grid grid = new();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) });
        for (int c = 0; c < headers.Length; c++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }

        // header row
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Border hdr = new() { Style = (Style)Resources["CardHeaderRow"], CornerRadius = new CornerRadius(0) };
        Grid.SetRow(hdr, 0); Grid.SetColumnSpan(hdr, cols); grid.Children.Add(hdr);
        MCell(grid, 0, 0, "Property", true, 0.7);
        for (int c = 0; c < headers.Length; c++)
        {
            MCell(grid, 0, c + 1, headers[c], true, 0.7);
        }

        for (int ri = 0; ri < rows.Count; ri++)
        {
            int r = ri + 1;
            (string label, string[] vals) = rows[ri];
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            MCell(grid, r, 0, label, true, 1.0);
            for (int c = 0; c < vals.Length; c++)
            {
                MCell(grid, r, c + 1, vals[c], false, 0.9);
            }
        }
        return grid;
    }

    private static void MCell(Grid g, int row, int col, string text, bool semibold, double opacity)
    {
        TextBlock t = new()
        {
            Text = text, Opacity = opacity, FontSize = 12, Margin = new Thickness(14, 7, 14, 7),
            TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true,
            FontWeight = semibold ? FontWeights.SemiBold : FontWeights.Normal
        };
        Grid.SetRow(t, row); Grid.SetColumn(t, col);
        g.Children.Add(t);
    }

    private static void Cell(Grid g, int col, string text, double opacity, bool semibold = false, bool mono = false)
    {
        TextBlock t = new()
        {
            Text = text, Opacity = opacity, FontSize = 12, TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
            FontWeight = semibold ? FontWeights.SemiBold : FontWeights.Normal
        };
        if (mono)
        {
            t.FontFamily = new FontFamily("Consolas");
        }

        Grid.SetColumn(t, col);
        g.Children.Add(t);
    }

    private Border KeyRow(string label, string value)
    {
        Grid g = new();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(118) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        TextBlock l = new() { Text = label, Opacity = 0.6, FontSize = 12, TextWrapping = TextWrapping.Wrap };
        TextBlock v = new()
        { Text = value, FontSize = 13, TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true, Margin = new Thickness(0, 0, 8, 0) };
        Grid.SetColumn(v, 1);
        g.Children.Add(l); g.Children.Add(v);
        return CopyableRow(g, value, new Thickness(0, 3, 0, 3));
    }

}
