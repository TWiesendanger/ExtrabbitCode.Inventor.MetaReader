using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;

namespace ExtrabbitCode.Inventor.MetaReader.App.Document;

/// <summary>The configurable left sidebar: rendering, edit mode, drag-to-reorder and the
/// add/remove property flow.</summary>
public sealed partial class DocumentView
{
    // ---- configurable left sidebar -------------------------------------------------

    private void OnToggleSidebarEdit(object sender, RoutedEventArgs e)
    {
        _editingSidebar = EditSidebarToggle.IsChecked == true;
        RenderSidebar();
    }

    /// <summary>Renders the sidebar (thumbnail + chosen properties) per the current SidebarConfig.</summary>
    private bool _shown3DTip;

    private void RenderSidebar()
    {
        if (Document == null)
        {
            return;
        }

        bool showThumb = SidebarConfig.ShowThumbnail && !Document.IsStep;
        ThumbHost.Visibility = showThumb ? Visibility.Visible : Visibility.Collapsed;
        ThumbDivider.Visibility = showThumb ? Visibility.Visible : Visibility.Collapsed;

        // 3D triggers (parts/assemblies): the thumbnail is the primary trigger when shown; the
        // command-bar icon is the fallback only when the thumbnail is off.
        bool is3D = Document.Kind is InventorDocument.DocKind.Part or InventorDocument.DocKind.Assembly or InventorDocument.DocKind.Step;
        View3DButton.Visibility = is3D && !showThumb ? Visibility.Visible : Visibility.Collapsed;
        ThreeDHint.Visibility = is3D && showThumb ? Visibility.Visible : Visibility.Collapsed;

        // Discoverability tip (once per loaded doc): STEP uses the bundled OCCT converter; Inventor
        // documents still need Inventor to generate a viewable on a cache miss.
        if (is3D && !_shown3DTip && (Document.IsStep || InventorInstalls.Detect().Count > 0))
        {
            _shown3DTip = true;
            TipService.Show((Panel)Content, showThumb ? ThumbHost : View3DButton, new Tip
            {
                Id = "view.3d",
                Title = "See it in 3D",
                Message = Document.IsStep
                    ? "Convert this STEP file locally and open it in the Autodesk viewer."
                    : showThumb
                    ? "Click the preview to open this model in the interactive 3D viewer."
                    : "Open this model in the interactive 3D viewer.",
                Glyph = 0xF158,                       // 3D cube glyph
                ActionText = "Open 3D view",
                Action = () => _ = OpenViewer3DAsync()
            });
        }

        // every property present in the file, keyed for lookup (value may be empty)
        Dictionary<string, InventorDocument.PropEntry> byKey = new(StringComparer.Ordinal);
        foreach (InventorDocument.PropEntry p in Document.Properties)
        {
            byKey[SidebarConfig.K(p.SetId, p.Pid)] = p;
        }

        List<string> keys = SidebarConfig.Keys;

        SidebarEditPanel.Children.Clear();
        if (_editingSidebar)
        {
            ToggleSwitch thumb = new() { Header = "Show thumbnail", IsOn = showThumb };
            thumb.Toggled += (_, _) => SidebarConfig.ShowThumbnail = thumb.IsOn;
            SidebarEditPanel.Children.Add(thumb);
            SidebarEditPanel.Children.Add(new TextBlock
            {
                Text = "Drag to reorder, remove with ✕, or add more below.",
                Opacity = 0.6, FontSize = 12, TextWrapping = TextWrapping.Wrap
            });
        }

        // Render EVERY configured property so the sidebar layout is identical across files;
        // a property that's absent from this file (or blank) simply shows an empty value.
        KeyPropsPanel.Children.Clear();
        foreach (string key in keys)
        {
            (string label, string value) = ResolveRow(key, byKey);
            KeyPropsPanel.Children.Add(_editingSidebar
                ? EditableKeyRow(key, label, value)
                : KeyRow(label, value));
        }
        if (keys.Count == 0 && !_editingSidebar)
        {
            KeyPropsPanel.Children.Add(new TextBlock { Text = "No properties to show", Opacity = 0.5, FontSize = 12 });
        }

        SidebarAddPanel.Children.Clear();
        if (_editingSidebar)
        {
            SidebarAddPanel.Children.Add(BuildAddPropertyButton(byKey, keys));
            HyperlinkButton reset = new() { Content = "Reset to defaults", Padding = new Thickness(0, 2, 0, 0) };
            reset.Click += (_, _) => SidebarConfig.ResetDefaults();
            SidebarAddPanel.Children.Add(reset);
        }
    }

    /// <summary>Resolves a configured key to its (label, value). Value is empty when the
    /// property is absent from this file or blank, keeping the sidebar consistent.</summary>
    private static (string label, string value) ResolveRow(
        string key, IReadOnlyDictionary<string, InventorDocument.PropEntry> byKey)
    {
        if (byKey.TryGetValue(key, out InventorDocument.PropEntry? p))
        {
            return (p.Name, p.Display);
        }

        (Guid setId, uint pid) = SidebarConfig.Parse(key);
        return (InventorProps.Name(setId, pid), "");
    }

    /// <summary>A draggable sidebar property row with a remove control (edit mode).</summary>
    private Border EditableKeyRow(string key, string label, string value)
    {
        Grid g = new() { ColumnSpacing = 8 };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        FontIcon grip = new() { Glyph = G(0xE76F), FontSize = 14, Opacity = 0.4,
            VerticalAlignment = VerticalAlignment.Center };
        ToolTipService.SetToolTip(grip, "Drag to reorder");

        StackPanel text = new();
        text.Children.Add(new TextBlock { Text = label, Opacity = 0.6, FontSize = 12, TextWrapping = TextWrapping.Wrap });
        text.Children.Add(new TextBlock { Text = value, FontSize = 13, TextWrapping = TextWrapping.Wrap });
        Grid.SetColumn(text, 1);

        Button remove = MiniButton(0xE711, "Remove", () => RemoveKey(key));
        Grid.SetColumn(remove, 2);

        g.Children.Add(grip);
        g.Children.Add(text);
        g.Children.Add(remove);

        Border row = new()
        {
            Padding = new Thickness(0, 4, 0, 4), Child = g, Tag = key,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), // hit-testable drop target
            CanDrag = true, AllowDrop = true
        };
        row.DragStarting += (_, e) =>
        {
            _dragKey = key;
            e.Data.RequestedOperation = DataPackageOperation.Move;
            e.Data.SetText(key);
        };
        row.DragOver += (_, e) =>
        {
            e.AcceptedOperation = _dragKey != null && _dragKey != key
                ? DataPackageOperation.Move : DataPackageOperation.None;
        };
        row.Drop += (s, e) =>
        {
            bool after = e.GetPosition((UIElement)s).Y > ((FrameworkElement)s).ActualHeight / 2;
            ReorderTo(key, after);
        };
        return row;
    }

    private Button MiniButton(int glyph, string tooltip, Action onClick)
    {
        Button b = new()
        {
            Content = new FontIcon { Glyph = G(glyph), FontSize = 12 },
            Padding = new Thickness(6, 4, 6, 4), MinWidth = 0,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(0)
        };
        ToolTipService.SetToolTip(b, tooltip);
        b.Click += (_, _) => onClick();
        return b;
    }

    private Button BuildAddPropertyButton(
        IReadOnlyDictionary<string, InventorDocument.PropEntry> present, List<string> current)
    {
        HashSet<string> shownKeys = new(current, StringComparer.Ordinal);
        List<InventorDocument.PropEntry> candidates = present
            .Where(kv => !shownKeys.Contains(kv.Key) && kv.Value.Display.Length > 0).Select(kv => kv.Value)
            .OrderBy(p => p.Set, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        Button add = new()
        {
            Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children =
            {
                new FontIcon { Glyph = G(0xE710), FontSize = 12 }, new TextBlock { Text = "Add property" }
            } }
        };

        // Build the flyout fresh on click so it binds to the current window's XamlRoot.
        add.Click += (_, _) =>
        {
            StackPanel menu = new() { Spacing = 1, MinWidth = 280 };
            Flyout flyout = new() { Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.Bottom };

            if (candidates.Count == 0)
            {
                menu.Children.Add(new TextBlock { Text = "All available properties are shown",
                    Opacity = 0.6, FontSize = 12, Margin = new Thickness(4) });
            }
            foreach (InventorDocument.PropEntry p in candidates)
            {
                string key = SidebarConfig.K(p.SetId, p.Pid);
                Button row = new()
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(0),
                    Padding = new Thickness(8, 5, 8, 5),
                    Content = new StackPanel { Children =
                    {
                        new TextBlock { Text = p.Name, FontSize = 13 },
                        new TextBlock { Text = p.Set + "  ·  " + p.Display, Opacity = 0.55, FontSize = 11,
                            TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 300 }
                    } }
                };
                row.Click += (_, _) => { flyout.Hide(); AddKey(key); };
                menu.Children.Add(row);
            }

            flyout.Content = new ScrollViewer { MaxHeight = 360, Content = menu };
            flyout.ShowAt(add);
        };
        return add;
    }

    private static void AddKey(string key)
    {
        List<string> keys = SidebarConfig.Keys;
        if (!keys.Contains(key))
        {
            keys.Add(key);
            SidebarConfig.SetKeys(keys);
        }
    }

    private static void RemoveKey(string key)
    {
        List<string> keys = SidebarConfig.Keys;
        if (keys.Remove(key))
        {
            SidebarConfig.SetKeys(keys);
        }
    }

    /// <summary>Moves the dragged key before/after a target key, then persists the new order.</summary>
    private void ReorderTo(string targetKey, bool after)
    {
        string? drag = _dragKey;
        _dragKey = null;
        if (drag == null || drag == targetKey)
        {
            return;
        }

        List<string> keys = SidebarConfig.Keys;
        if (!keys.Remove(drag))
        {
            return;
        }

        int ti = keys.IndexOf(targetKey);
        if (ti < 0)
        {
            return;
        }

        keys.Insert(after ? ti + 1 : ti, drag);
        SidebarConfig.SetKeys(keys);
    }

    private static StackPanel Section(string title, string[] lines)
    {
        StackPanel sp = new() { Spacing = 4 };
        sp.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, FontSize = 14 });
        foreach (string l in lines)
        {
            sp.Children.Add(new TextBlock { Text = l,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 12, Opacity = 0.85, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
        }

        return sp;
    }

    /// <summary>A titled label/value grid (one row per field), like Inventor's iProperties
    /// "Details" page - long values ellipsize with the full text on hover.</summary>
    private static StackPanel KeyValueSection(string title, List<(string label, string value)> items)
    {
        StackPanel sp = new() { Spacing = 8 };
        sp.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, FontSize = 14 });

        Grid g = new() { ColumnSpacing = 16, RowSpacing = 5 };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        for (int i = 0; i < items.Count; i++)
        {
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            TextBlock lbl = new() { Text = items[i].label, FontSize = 12, Opacity = 0.6,
                FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetRow(lbl, i);
            Grid.SetColumn(lbl, 0);
            g.Children.Add(lbl);

            TextBlock val = new() { Text = items[i].value, FontSize = 12, Opacity = 0.95,
                TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis,
                IsTextSelectionEnabled = true, VerticalAlignment = VerticalAlignment.Center };
            ToolTipService.SetToolTip(val, items[i].value);
            Grid.SetRow(val, i);
            Grid.SetColumn(val, 1);
            g.Children.Add(val);
        }

        sp.Children.Add(g);
        return sp;
    }

}
