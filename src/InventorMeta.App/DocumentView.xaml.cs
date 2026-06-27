using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace ExtrabbitCode.Inventor.MetaReader.App;

public sealed partial class DocumentView
{
    public string FilePath { get; private set; } = "";
    public InventorDocument? Document { get; private set; }
    public string TabTitle => Document?.FileName ?? Path.GetFileName(FilePath);

    /// <summary>Status messages after (re)load go here; the host window shows them in its footer.
    /// Reassigned when the tab is adopted by another window (tear-out / reattach).</summary>
    public Action<string>? StatusSink { get; set; }

    /// <summary>The window currently hosting this view (may change after a tab tear-out).</summary>
    private MainWindow? HostWindow => App.GetWindowForElement(this) as MainWindow;

    private readonly List<(UIElement body, FontIcon chevron)> _collapsibles = [];

    public DocumentView()
    {
        InitializeComponent();
        WireTabHide(PropsTab, "All Properties");
        WireTabHide(StatesTab, "Model States");
        WireTabHide(RefsTab, "References");
        WireTabHide(StructureTab, "File Structure");

        // The WebView2 reference graph doesn't follow the XAML theme on its own; re-theme it on toggle.
        ActualThemeChanged += OnActualThemeChanged;

        // Hide/show state is global; rebuild this view whenever it changes (any tab).
        // Static handler => no capture of 'this', so the messenger's weak reference
        // lets a closed tab be collected without manual unregistration.
        WeakReferenceMessenger.Default.Register<HideChangedMessage>(this,
            static (recipient, _) => ((DocumentView)recipient).RebuildView());

        // Sidebar layout is global too, but only the (cheap) sidebar needs re-rendering.
        WeakReferenceMessenger.Default.Register<SidebarConfigChangedMessage>(this,
            static (recipient, _) => ((DocumentView)recipient).RenderSidebar());

        // Expand/collapse-all lives in the tab strip footer (shown only for card tabs).
        TabStripActions.Children.Add(BuildExpandCollapseAll());
    }

    private void OnDetailTabChanged(object sender, SelectionChangedEventArgs e)
    {
        bool hasCards = ReferenceEquals(DetailTabs.SelectedItem, PropsTab)
                     || ReferenceEquals(DetailTabs.SelectedItem, StatesTab);
        TabStripActions.Visibility = hasCards ? Visibility.Visible : Visibility.Collapsed;

        if (Document != null && DetailTabs.SelectedItem is TabViewItem { Header: string hdr })
        {
            // Strip any "(N)" count suffix (e.g. "Model States (3)") so the tab name groups cleanly.
            string tab = hdr.Split('(')[0].Trim();
            Analytics.Capture("detail_tab_viewed", new Dictionary<string, object?> { ["tab"] = tab });
        }
    }

    private bool _editingSidebar;
    private string? _dragKey;

    private static string G(int code) => ((char)code).ToString();   // Segoe Fluent glyph
    private const int ChevronUp = 0xE70E, ChevronDown = 0xE70D, HideGlyph = 0xED1A;

    private void RebuildView()
    {
        if (Document != null)
        {
            Populate(Document);
        }
    }

    /// <summary>A small hover "hide" button that hides the item with the given key.</summary>
    private Button MakeHideButton(string key, string tooltip)
    {
        Button b = new()
        {
            Content = new FontIcon { Glyph = G(HideGlyph), FontSize = 12 },
            Padding = new Thickness(6, 3, 6, 3), MinWidth = 0, MinHeight = 0,
            CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            VerticalAlignment = VerticalAlignment.Center
        };
        ToolTipService.SetToolTip(b, tooltip);
        b.Click += (_, _) => HideStore.Set(key, true);
        return b;
    }

    private void WireTabHide(TabViewItem tab, string name)
    {
        // Build the flyout fresh per right-click so it binds to the tab's current XamlRoot
        // (the document can move to another window via tab tear-out).
        tab.ContextRequested += (_, e) =>
        {
            MenuFlyout mf = new();
            MenuFlyoutItem item = new() { Text = $"Hide “{name}” tab", Icon = new FontIcon { Glyph = G(HideGlyph) } };
            item.Click += (_, _) => HideStore.Set(HideStore.TabKey(name), true);
            mf.Items.Add(item);
            if (e.TryGetPosition(tab, out Windows.Foundation.Point p))
            {
                mf.ShowAt(tab, p);
            }
            else
            {
                mf.ShowAt(tab);
            }
            e.Handled = true;
        };
    }

    private void ApplyTabVisibility()
    {
        PropsTab.Visibility = TabVis("All Properties", true);
        StatesTab.Visibility = TabVis("Model States", Document?.HasModelStates == true);
        RefsTab.Visibility = TabVis("References", true);
        StructureTab.Visibility = TabVis("File Structure", true);

        if (DetailTabs.SelectedItem is TabViewItem sel && sel.Visibility == Visibility.Collapsed)
        {
            DetailTabs.SelectedItem = DetailTabs.TabItems.OfType<TabViewItem>()
                .FirstOrDefault(t => t.Visibility == Visibility.Visible);
        }

        Visibility TabVis(string name, bool applicable) =>
            applicable && !HideStore.IsHidden(HideStore.TabKey(name)) ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RefreshHiddenUi()
    {
        int count = CollectHidden().Count;
        HiddenButton.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        HiddenCountText.Text = count == 1 ? "1 hidden" : $"{count} hidden";
    }

    /// <summary>Builds the hidden-items list fresh on click so it binds to the current
    /// window's XamlRoot (the document can move windows via tab tear-out).</summary>
    private void OnHiddenButtonClick(object sender, RoutedEventArgs e)
    {
        StackPanel panel = new() { MinWidth = 260, Spacing = 2 };
        panel.Children.Add(new TextBlock
        {
            Text = "Hidden - click Show to restore", FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(4, 2, 4, 8)
        });

        Flyout flyout = new() { Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.Bottom };
        foreach ((string key, string label) in CollectHidden())
        {
            Grid row = new() { ColumnSpacing = 10, Padding = new Thickness(4, 2, 4, 2) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            TextBlock lbl = new() { Text = label, VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 320, FontSize = 12 };
            Button show = new()
            {
                Padding = new Thickness(8, 2, 8, 2),
                Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6,
                    Children = { new FontIcon { Glyph = G(0xE7B3), FontSize = 12 }, new TextBlock { Text = "Show" } } }
            };
            string k = key;
            show.Click += (_, _) => { flyout.Hide(); HideStore.Set(k, false); };
            Grid.SetColumn(show, 1);
            row.Children.Add(lbl);
            row.Children.Add(show);
            panel.Children.Add(row);
        }

        flyout.Content = panel;
        flyout.ShowAt(HiddenButton);
    }

    private List<(string key, string label)> CollectHidden()
    {
        List<(string, string)> list = [];
        if (Document == null)
        {
            return list;
        }

        void Tab(string name, bool applicable)
        {
            if (applicable && HideStore.IsHidden(HideStore.TabKey(name)))
            {
                list.Add((HideStore.TabKey(name), "Tab · " + name));
            }
        }
        Tab("All Properties", true);
        Tab("Model States", Document.HasModelStates);
        Tab("References", true);
        Tab("File Structure", true);

        foreach (string set in Document.Properties.Select(p => p.Set).Distinct())
        {
            if (HideStore.IsHidden(HideStore.SetKey(set)))
            {
                list.Add((HideStore.SetKey(set), "Set · " + set));
            }
        }

        foreach (InventorDocument.PropEntry p in Document.Properties)
        {
            if (!HideStore.IsHidden(HideStore.SetKey(p.Set)) && HideStore.IsHidden(HideStore.PropKey(p.Set, p.Pid)))
            {
                list.Add((HideStore.PropKey(p.Set, p.Pid), $"Property · {p.Name}  ({p.Set})"));
            }
        }
        return list;
    }

    private void SetAllExpanded(bool expanded)
    {
        foreach ((UIElement body, FontIcon chevron) in _collapsibles)
        {
            body.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
            chevron.Glyph = G(expanded ? ChevronUp : ChevronDown);
        }
    }

    public bool Load(string path)
    {
        FilePath = path;
        PathText.Text = path;
        _shown3DTip = false;
        try
        {
            if (!CompoundFile.LooksLikeCompoundFile(path))
            {
                StatusSink?.Invoke($"{Path.GetFileName(path)} is not an Inventor / OLE compound file.");
                return false;
            }
            Document = new InventorDocument(path);
            Populate(Document);
            StatusSink?.Invoke($"Loaded {Document.FileName} - {Document.Properties.Count} properties, " +
                                  $"{Document.References.Count} reference(s).");
            Analytics.Capture("document_opened", new Dictionary<string, object?>
            {
                ["doc_kind"]         = Document.Kind.ToString(),
                ["extension"]        = Path.GetExtension(path).ToLowerInvariant(),
                ["has_model_states"] = Document.HasModelStates,
                ["is_ipart"]         = Document.IsIPart,
                ["reference_count"]  = Document.References.Count,
                ["property_count"]   = Document.Properties.Count,
            });
            return true;
        }
        catch (Exception ex)
        {
            StatusSink?.Invoke("Error: " + ex.Message);
            return false;
        }
    }

    public void Refresh()
    {
        if (string.IsNullOrEmpty(FilePath))
        {
            return;
        }

        if (!File.Exists(FilePath)) { StatusSink?.Invoke("File no longer exists: " + FilePath); return; }
        if (Load(FilePath))
        {
            StatusSink?.Invoke($"Refreshed {TabTitle} at {DateTime.Now:HH:mm:ss}.");
        }
    }

    private void OnRefresh(object sender, RoutedEventArgs e) => Refresh();

    private void OnShowInExplorer(object sender, RoutedEventArgs e) => RevealInExplorer(FilePath);

    /// <summary>Opens File Explorer with the file selected (or its folder if only that exists).</summary>
    private static void RevealInExplorer(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                Process.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = $"/select,\"{path}\"", UseShellExecute = true });
            }
            else if (Directory.Exists(path))
            {
                Process.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = $"\"{path}\"", UseShellExecute = true });
            }
        }
        catch { /* best-effort */ }
    }

    private async void OnExportJson(object sender, RoutedEventArgs e)
    {
        if (Document == null)
        {
            return;
        }

        FileSavePicker picker = new();
        Window owner = HostWindow ?? App.MainWindowInstance;
        IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(owner);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        picker.FileTypeChoices.Add("JSON", new List<string> { ".json" });
        picker.SuggestedFileName = Path.GetFileNameWithoutExtension(Document.FileName) + "_metadata";
        StorageFile? file = await picker.PickSaveFileAsync();
        if (file != null)
        {
            await FileIO.WriteTextAsync(file, Document.ToJson());
            StatusSink?.Invoke("Exported " + file.Name);
            Analytics.Capture("json_exported", new Dictionary<string, object?> { ["doc_kind"] = Document.Kind.ToString() });
        }
    }

    private void Populate(InventorDocument doc)
    {
        FileNameText.Text = doc.FileName;
        DocTypeText.Text  = doc.DocumentType;
        TypeIcon.Source   = AppIcons.Bitmap(doc.Kind);

        if (doc.Thumbnail != null)
        {
            BitmapImage bmp = new();
            using MemoryStream ms = new(doc.Thumbnail);
            bmp.SetSource(ms.AsRandomAccessStream());
            ThumbBrush.ImageSource = bmp;
            NoThumb.Visibility = Visibility.Collapsed;
        }
        else
        {
            ThumbBrush.ImageSource = null;
            NoThumb.Visibility = Visibility.Visible;
        }

        SetCategoryBadge(doc);

        RenderSidebar();   // also sets the 3D triggers (button visible only when the thumbnail is off)

        _collapsibles.Clear();

        PropsPanel.Children.Clear();
        foreach (IGrouping<string, InventorDocument.PropEntry> grp in doc.Properties.GroupBy(p => p.Set).OrderBy(g => SetOrder(g.Key)))
        {
            if (HideStore.IsHidden(HideStore.SetKey(grp.Key)))
            {
                continue;
            }

            StackPanel table = PropTable(grp.Key, grp.OrderBy(x => x.Pid).Select(p => (p.Pid, p.Name, p.Display)));
            PropsPanel.Children.Add(Card(grp.Key, grp.Count().ToString(), table, HideStore.SetKey(grp.Key)));
        }

        PopulateStates(doc);

        PopulateReferences(doc);

        using CompoundFile cf = new(doc.FilePath);
        StringBuilder sb = new();
        sb.AppendLine($"Root CLSID  {cf.Directory[0].Clsid}");
        sb.AppendLine($"Container   {doc.CfbVersionInfo}\n");
        foreach (CompoundFile.DirEntry en in cf.Directory.Where(d => d.Type is 1 or 2 or 5)
                     .OrderBy(d => d.Path, StringComparer.OrdinalIgnoreCase))
        {
            string size = en.Type == 2 ? en.Size.ToString("N0") : "";
            sb.AppendLine($"{en.Path,-46}{en.TypeName,-8}{size,12}");
        }
        TreeText.Text = sb.ToString();

        ApplyTabVisibility();
        RefreshHiddenUi();
    }

    /// <summary>Colours the category badge for the document's <see cref="InventorDocument.PrimaryCategory"/>
    /// and attaches the legend as its hover tooltip.</summary>
    private void SetCategoryBadge(InventorDocument doc)
    {
        (string name, Windows.UI.Color color) = DocCategoryUi.Of(doc.PrimaryCategory);
        CategoryBadgeText.Text = name;
        CategoryBadge.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(color);
        CategoryBadge.Visibility = Visibility.Visible;
        ToolTipService.SetToolTip(CategoryBadge, new ToolTip { Content = DocCategoryUi.Legend(doc.PrimaryCategory) });
    }

    private void PopulateStates(InventorDocument doc)
    {
        StatesPanel.Children.Clear();
        if (!doc.HasModelStates)
        {
            StatesTab.Visibility = Visibility.Collapsed;
            return;
        }
        StatesTab.Visibility = Visibility.Visible;
        List<InventorDocument.ModelState> states = doc.ModelStateDetails;
        StatesTab.Header = $"Model States ({states.Count})";

        StatesPanel.Children.Add(new TextBlock
        {
            Text = "Per-state iProperties, read straight from the file - no need to switch states in Inventor.",
            Opacity = 0.7, FontSize = 13, TextWrapping = TextWrapping.Wrap
        });

        // identity of a property = (Set, Pid, Name); value may vary per state
        InventorDocument.ModelState? active = states.FirstOrDefault(s => s.IsActive);
        bool Has(InventorDocument.ModelState s, (string Set, uint Pid, string Name) id) =>
            s.Properties.Any(p => p.Set == id.Set && p.Pid == id.Pid && p.Name == id.Name);
        string ValueOf(InventorDocument.ModelState s, (string Set, uint Pid, string Name) id)
        {
            InventorDocument.PropEntry? p = s.Properties.FirstOrDefault(x => x.Set == id.Set && x.Pid == id.Pid && x.Name == id.Name);
            if (p != null)
            {
                return p.Display;
            }

            return !s.IsActive && active != null && Has(active, id) ? "(not cached)" : "-";
        }
        List<(string Set, uint Pid, string Name)> ids = states.SelectMany(s => s.Properties).Select(p => (p.Set, p.Pid, p.Name))
            .Distinct().OrderBy(i => i.Set).ThenBy(i => i.Pid).ToList();
        bool Meaningful((string Set, uint Pid, string Name) id) =>
            !id.Set.Contains("(internal)") &&
            !(id.Set.StartsWith("Design Tracking Properties") && (id.Pid == 21 || id.Pid == 46)) &&
            !(id.Set.Contains("Summary Information") && id.Pid == 17);  // thumbnail blob
        List<(string Set, uint Pid, string Name)> allDiffs = ids.Where(id => states.Select(s => ValueOf(s, id)).Distinct().Count() > 1).ToList();
        List<(string Set, uint Pid, string Name)> diffIds = allDiffs.Where(Meaningful).ToList();
        int internalDiffs = allDiffs.Count - diffIds.Count;

        // ---- differences matrix (the headline view) ----
        if (states.Count > 1)
        {
            string[] headers = states.Select(s => s.Name).ToArray();
            List<(string Name, string[])> rows = diffIds.Select(id => (id.Name, states.Select(s => ValueOf(s, id)).ToArray())).ToList();
            UIElement body = rows.Count > 0
                ? Matrix(headers, rows)
                : new TextBlock { Text = "No user-facing properties differ between states.",
                    Opacity = 0.7, Margin = new Thickness(14, 10, 14, 12), TextWrapping = TextWrapping.Wrap };
            StatesPanel.Children.Add(Card("Differences between states",
                rows.Count > 0 ? rows.Count.ToString() : null, body));
            if (internalDiffs > 0)
            {
                StatesPanel.Children.Add(new TextBlock {
                    Text = $"+ {internalDiffs} internal/volatile field(s) also differ (revision ids, save timestamps, counters)",
                    Opacity = 0.5, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, -4, 0, 0) });
            }
        }

        // ---- per-state browser ----
        foreach (InventorDocument.ModelState s in states)
        {
            StackPanel body = new();

            // Each state caches its own preview (its geometry can differ) - show it, click to enlarge.
            if (s.Thumbnail is { Length: > 0 } thumb)
            {
                Image img = new()
                {
                    Source = ThumbSource(thumb), Width = 104, Height = 104,
                    Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left
                };
                ToolTipService.SetToolTip(img, "Click to enlarge");
                img.Tapped += (_, _) => ShowThumbZoom(thumb, s.Name);
                body.Children.Add(new Border
                {
                    Child = img, CornerRadius = new CornerRadius(8), Margin = new Thickness(14, 12, 14, 6),
                    BorderThickness = new Thickness(1), HorizontalAlignment = HorizontalAlignment.Left,
                    BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"]
                });
            }

            if (s.Summary.Count > 0)
            {
                body.Children.Add(KvList(s.Summary.Select(kv => (kv.Key, kv.Value)).ToList(), 150));
            }

            body.Children.Add(new TextBlock {
                Text = $"{s.Properties.Count} properties · storage {s.StorageName}",
                Opacity = 0.5, FontSize = 11, FontFamily = new FontFamily("Consolas"),
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(14, 8, 14, 12) });
            StatesPanel.Children.Add(Card(s.Name, null, body));
        }
    }

    private static BitmapImage ThumbSource(byte[] bytes)
    {
        BitmapImage bmp = new();
        using MemoryStream ms = new(bytes);
        bmp.SetSource(ms.AsRandomAccessStream());
        return bmp;
    }

    /// <summary>Show a model-state thumbnail enlarged on a dimmed overlay; click anywhere to close.</summary>
    private void ShowThumbZoom(byte[] bytes, string title)
    {
        if (HostWindow is not MainWindow win) { return; }

        StackPanel centered = new()
        {
            Spacing = 10, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
        };
        centered.Children.Add(new TextBlock
        {
            Text = title, FontSize = 16, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.White), HorizontalAlignment = HorizontalAlignment.Center
        });
        centered.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(12),
            Child = new Image { Source = ThumbSource(bytes), MaxWidth = 720, MaxHeight = 720, Stretch = Stretch.Uniform }
        });
        centered.Children.Add(new TextBlock
        {
            Text = "Click anywhere to close", FontSize = 12, Opacity = 0.7,
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.White), HorizontalAlignment = HorizontalAlignment.Center
        });

        Grid root = new();
        root.Children.Add(centered);
        root.Tapped += (_, _) => win.HideOverlay();
        win.ShowOverlay(root, dimmed: true);
    }

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

        bool showThumb = SidebarConfig.ShowThumbnail;
        ThumbHost.Visibility = showThumb ? Visibility.Visible : Visibility.Collapsed;
        ThumbDivider.Visibility = showThumb ? Visibility.Visible : Visibility.Collapsed;

        // 3D triggers (parts/assemblies): the thumbnail is the primary trigger when shown; the
        // command-bar icon is the fallback only when the thumbnail is off.
        bool is3D = Document.Kind is InventorDocument.DocKind.Part or InventorDocument.DocKind.Assembly;
        View3DButton.Visibility = is3D && !showThumb ? Visibility.Visible : Visibility.Collapsed;
        ThreeDHint.Visibility = is3D && showThumb ? Visibility.Visible : Visibility.Collapsed;

        // Discoverability tip (once per loaded doc): point at the 3D trigger when Inventor is installed.
        if (is3D && !_shown3DTip && InventorInstalls.Detect().Count > 0)
        {
            _shown3DTip = true;
            TipService.Show((Panel)Content, showThumb ? ThumbHost : View3DButton, new Tip
            {
                Id = "view.3d",
                Title = "See it in 3D",
                Message = showThumb
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

    // ---- references node graph -----------------------------------------------------
    private int _refGen;

    private void PopulateReferences(InventorDocument doc)
    {
        RefsRoot.RowDefinitions.Clear();
        RefsRoot.Children.Clear();

        // version/provenance details - version history first, then schema/template
        string[] detailOrder = ["Current Version", "Previous Version", "Next Version", "Last update with", "Last saved by"];
        List<(string, string)> provItems = [];
        foreach (string k in detailOrder)
        {
            if (doc.VersionInfo.TryGetValue(k, out string? v)) { provItems.Add((k, v)); }
        }
        foreach (KeyValuePair<string, string> kv in doc.VersionInfo)
        {
            if (!detailOrder.Contains(kv.Key)) { provItems.Add((kv.Key, kv.Value)); }
        }
        StackPanel details = KeyValueSection("Version", provItems);

        // Graph fills the top (star); the short version panel docks below it (auto), never scrolling.
        RefsRoot.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        RefsRoot.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        Border graphHost = new();
        Grid.SetRow(graphHost, 0);
        RefsRoot.Children.Add(graphHost);

        Border prov = new()
        {
            BorderThickness = new Thickness(0, 1, 0, 0),
            BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(40, 128, 128, 128)),
            Padding = new Thickness(14, 10, 14, 12),
            Child = details
        };
        Grid.SetRow(prov, 1);
        RefsRoot.Children.Add(prov);

        if (doc.References.Count == 0)
        {
            graphHost.Child = GraphInfo("No referenced files.");
            return;
        }

        int gen = ++_refGen;
        _ = BuildAndShowGraphAsync(doc, graphHost, gen);
    }

    private async Task BuildAndShowGraphAsync(InventorDocument doc, Border host, int gen)
    {
        if (doc.References.Count == 0)
        {
            host.Child = GraphInfo("No referenced files.");
            return;
        }

        host.Child = GraphInfo("Building reference graph…");

        RefNode? root = null;
        try { root = await Task.Run(() => ReferenceGraph.Build(doc)); }
        catch { /* fall through to error message */ }

        if (gen != _refGen)
        {
            return; // a newer load superseded this build
        }

        host.Child = root != null ? RenderRefGraphWeb(root, host) : GraphInfo("Couldn't build the reference graph.");
    }

    private static TextBlock GraphInfo(string text) => new()
    {
        Text = text, Opacity = 0.6, Margin = new Thickness(20),
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
    };

    private bool _graphFs;
    private bool _showThumbs;
    private GraphLayout _graphLayout;

    // Set while the reference graph is mounted: exits its fullscreen overlay. Lets the
    // node "open" actions drop out of fullscreen before switching to the opened tab.
    private Action? _exitGraphFullscreen;

    /// <summary>Opens a referenced document in a tab. If the reference graph is currently
    /// fullscreen, leave fullscreen first so the newly opened (and selected) tab is visible.</summary>
    private void OpenReference(string path)
    {
        Analytics.Capture("reference_opened");
        if (_graphFs) { _exitGraphFullscreen?.Invoke(); }
        HostWindow?.OpenDocument(path);
    }

    private UIElement RenderRefGraph(RefNode root, Border host)
    {
        // start with only level 1 visible: root expanded, everything deeper collapsed
        ForEachNode(root, n => n.Expanded = n.Depth == 0);

        CompositeTransform xf = new();
        Canvas canvas = new()
        {
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            RenderTransform = xf,
            // pin to the viewport's top-left so the pan/zoom transform's origin is (0,0)
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top
        };

        // Pan/zoom via a render transform on a clipped viewport. We avoid ScrollViewer: its
        // ZoomMode + a Canvas hits a WinUI "Layout cycle detected" crash, and it also eats
        // the left-drag we want for panning.
        Grid viewport = new() { Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        bool fitted = false;
        viewport.SizeChanged += (_, e) =>
        {
            viewport.Clip = new RectangleGeometry { Rect = new Windows.Foundation.Rect(0, 0, e.NewSize.Width, e.NewSize.Height) };
            if (!fitted && e.NewSize.Width > 0 && e.NewSize.Height > 0)
            {
                fitted = true;
                ZoomToFit(viewport, canvas, xf);
            }
        };
        viewport.Children.Add(canvas);

        LayoutAndDraw(canvas, root);
        WirePanZoom(viewport, canvas, xf);

        // floating toolbar (top-right)
        StackPanel toolbar = new()
        {
            Orientation = Orientation.Horizontal, Spacing = 4,
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 8, 14, 0)
        };
        static Button IconBtn(int glyph, string tip)
        {
            Button b = new() { Content = new FontIcon { Glyph = G(glyph), FontSize = 14 },
                Padding = new Thickness(8, 5, 8, 5), MinWidth = 0 };
            ToolTipService.SetToolTip(b, tip);
            return b;
        }

        Button fit = IconBtn(0xE9A6, "Fit to view");
        fit.Click += (_, _) => ZoomToFit(viewport, canvas, xf);
        Button expandAll = IconBtn(0xE710, "Expand all");
        expandAll.Click += (_, _) => { ForEachNode(root, n => n.Expanded = n.Children.Count > 0); LayoutAndDraw(canvas, root); };
        Button collapseAll = IconBtn(0xE738, "Collapse all");
        collapseAll.Click += (_, _) => { ForEachNode(root, n => n.Expanded = n.Depth == 0); LayoutAndDraw(canvas, root); };

        // thumbnails toggle: redraw the graph with each node showing its document's preview
        ToggleButton thumbs = new()
        {
            Content = new FontIcon { Glyph = G(0xE8B9), FontSize = 14 },
            Padding = new Thickness(8, 5, 8, 5), MinWidth = 0, IsChecked = _showThumbs
        };
        ToolTipService.SetToolTip(thumbs, "Show thumbnails");
        thumbs.Click += (_, _) =>
        {
            _showThumbs = thumbs.IsChecked == true;
            LayoutAndDraw(canvas, root);
            // re-fit after the relayout settles
            DispatcherQueue.TryEnqueue(() => ZoomToFit(viewport, canvas, xf));
        };

        // Fullscreen: pop the whole viewport (graph + this toolbar) into a window-filling overlay
        // and switch the OS window to true fullscreen; the button toggles back, as does Esc.
        FontIcon fullIcon = new() { Glyph = G(0xE740), FontSize = 14 };
        Button full = new() { Content = fullIcon, Padding = new Thickness(8, 5, 8, 5), MinWidth = 0 };
        ToolTipService.SetToolTip(full, "Fullscreen");
        Grid? overlay = null;
        void SetFullscreen(bool on)
        {
            MainWindow? win = HostWindow;
            if (win is null || on == _graphFs) { return; }

            if (on)
            {
                host.Child = null;                       // detach from the References tab
                overlay = new Grid();
                overlay.Children.Add(viewport);
                win.ShowOverlay(overlay);
                win.AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
                _graphFs = true;
                Analytics.Capture("references_fullscreen");
            }
            else
            {
                win.AppWindow.SetPresenter(AppWindowPresenterKind.Default);
                overlay?.Children.Remove(viewport);
                win.HideOverlay();
                host.Child = viewport;                   // reattach to the tab
                overlay = null;
                _graphFs = false;
            }

            fullIcon.Glyph = G(_graphFs ? 0xE73F : 0xE740);   // BackToWindow / FullScreen
            ToolTipService.SetToolTip(full, _graphFs ? "Exit fullscreen" : "Fullscreen");
            fitted = false;                              // re-fit once the new size settles
            full.Focus(FocusState.Programmatic);
        }
        full.Click += (_, _) => SetFullscreen(!_graphFs);
        _exitGraphFullscreen = () => SetFullscreen(false);
        KeyboardAccelerator esc = new() { Key = Windows.System.VirtualKey.Escape };
        esc.Invoked += (_, e) => { if (_graphFs) { SetFullscreen(false); e.Handled = true; } };
        full.KeyboardAccelerators.Add(esc);

        toolbar.Children.Add(fit);
        toolbar.Children.Add(expandAll);
        toolbar.Children.Add(collapseAll);
        toolbar.Children.Add(thumbs);
        toolbar.Children.Add(full);
        viewport.Children.Add(toolbar);

        // Discoverability tip: point at the thumbnails toggle (unless already on).
        if (!_showThumbs)
        {
            TipService.Show(viewport, thumbs, new Tip
            {
                Id = "refs.thumbnails",
                Title = "Did you know?",
                Message = "You can show each referenced file's preview thumbnail right in the graph.",
                ActionText = "Show thumbnails",
                Action = () =>
                {
                    thumbs.IsChecked = true;
                    _showThumbs = true;
                    LayoutAndDraw(canvas, root);
                    ZoomToFit(viewport, canvas, xf);
                }
            });
        }

        return viewport;
    }

    /// <summary>Scales and centres the graph so all of it fits the viewport (zoom extents).</summary>
    private static void ZoomToFit(Grid viewport, Canvas canvas, CompositeTransform xf)
    {
        double vw = viewport.ActualWidth, vh = viewport.ActualHeight, cw = canvas.Width, ch = canvas.Height;
        if (vw <= 0 || vh <= 0 || cw <= 0 || ch <= 0) { return; }

        const double margin = 28;
        double scale = Math.Clamp(Math.Min((vw - margin) / cw, (vh - margin) / ch), 0.2, 1.0);
        xf.ScaleX = xf.ScaleY = scale;
        xf.TranslateX = (vw - cw * scale) / 2;
        xf.TranslateY = (vh - ch * scale) / 2;
    }

    private static void ForEachNode(RefNode n, Action<RefNode> action)
    {
        action(n);
        foreach (RefNode c in n.Children) { ForEachNode(c, action); }
    }

    /// <summary>Lays out the visible (expanded) part of the tree and (re)draws the canvas.</summary>
    private void LayoutAndDraw(Canvas canvas, RefNode root)
    {
        const double colStep = 300, pad = 16;
        // bigger nodes when thumbnails are shown so each preview has room
        double nodeW = _showThumbs ? 252 : 224;
        double nodeH = _showThumbs ? 104 : 56;
        double rowStep = _showThumbs ? 120 : 74;

        // tidy left-to-right layout over visible nodes; a collapsed node counts as a leaf
        int leaf = 0, maxDepth = 0;
        void Assign(RefNode n)
        {
            maxDepth = Math.Max(maxDepth, n.Depth);
            if (!n.Expanded || n.Children.Count == 0) { n.Row = leaf++; return; }
            foreach (RefNode c in n.Children) { Assign(c); }
            n.Row = (n.Children[0].Row + n.Children[^1].Row) / 2.0;
        }
        Assign(root);

        List<RefNode> vis = [];
        void Collect(RefNode n) { vis.Add(n); if (n.Expanded) { foreach (RefNode c in n.Children) { Collect(c); } } }
        Collect(root);

        double X(RefNode n) => pad + n.Depth * colStep;
        double Y(RefNode n) => pad + n.Row * rowStep;

        canvas.Children.Clear();
        canvas.Width = pad * 2 + maxDepth * colStep + nodeW;
        canvas.Height = pad * 2 + Math.Max(0, leaf - 1) * rowStep + nodeH;

        Brush link = new SolidColorBrush(Windows.UI.Color.FromArgb(150, 130, 130, 130));
        foreach (RefNode n in vis)
        {
            if (!n.Expanded) { continue; }
            foreach (RefNode c in n.Children)
            {
                double sx = X(n) + nodeW, sy = Y(n) + nodeH / 2, ex = X(c), ey = Y(c) + nodeH / 2;
                double dx = (ex - sx) * 0.5;
                PathFigure fig = new() { StartPoint = new Windows.Foundation.Point(sx, sy) };
                fig.Segments.Add(new BezierSegment
                {
                    Point1 = new Windows.Foundation.Point(sx + dx, sy),
                    Point2 = new Windows.Foundation.Point(ex - dx, ey),
                    Point3 = new Windows.Foundation.Point(ex, ey)
                });
                PathGeometry geo = new();
                geo.Figures.Add(fig);
                canvas.Children.Add(new Microsoft.UI.Xaml.Shapes.Path { Data = geo, Stroke = link, StrokeThickness = 1.5 });
            }
        }

        foreach (RefNode n in vis)
        {
            Border box = RefNodeBox(n, nodeW, nodeH, canvas, root);
            Canvas.SetLeft(box, X(n));
            Canvas.SetTop(box, Y(n));
            canvas.Children.Add(box);
        }
    }

    private Border RefNodeBox(RefNode n, double w, double h, Canvas canvas, RefNode root)
    {
        Grid g = new() { ColumnSpacing = 8, Padding = new Thickness(10, 6, 8, 6) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        ImageSource? thumb = _showThumbs ? BitmapFromBytes(n.Thumbnail) : null;
        FrameworkElement iconEl;
        if (thumb != null)
        {
            iconEl = new Border
            {
                Width = h - 20, Height = h - 20, CornerRadius = new CornerRadius(4),
                VerticalAlignment = VerticalAlignment.Center,
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(28, 128, 128, 128)),
                Child = new Image { Source = thumb, Stretch = Stretch.Uniform, Margin = new Thickness(2) }
            };
        }
        else if (n.IsLinkedFile)
        {
            iconEl = new FontIcon { Glyph = G(IsImageFile(n.Name) ? 0xE8B9 : 0xE7C3),
                FontSize = _showThumbs ? 40 : 22, Width = _showThumbs ? 56 : 30, Opacity = 0.85,
                VerticalAlignment = VerticalAlignment.Center };
        }
        else
        {
            double s = _showThumbs ? 48 : 30;
            iconEl = new Image { Width = s, Height = s, Source = AppIcons.Bitmap(n.Kind),
                VerticalAlignment = VerticalAlignment.Center };
        }

        StackPanel text = new() { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = n.Name, FontWeight = FontWeights.SemiBold, FontSize = 13,
            TextTrimming = TextTrimming.CharacterEllipsis });
        // iPart classification (done in the core graph): model-state parts carry the same marker
        // but aren't iParts, so they classify as neither.
        bool isMember = n.IPart == IPartRole.Member;
        bool isFactory = n.IPart == IPartRole.Factory;

        string sub = n.Cyclic ? "↻ already shown above"
            : !n.Resolved ? (n.IsLinkedFile ? "linked file - not found" : "file not found")
            : n.ReadError ? "unreadable"
            : n.IsLinkedFile ? "linked " + Path.GetExtension(n.Name).TrimStart('.').ToLowerInvariant()
            : isFactory ? "iPart factory"
            : isMember ? "iPart member"
            : KindLabel(n.Kind);
        TextBlock subtitle = new() { Text = sub, FontSize = 11, Opacity = 0.6, TextTrimming = TextTrimming.CharacterEllipsis };
        if (!n.Resolved)
        {
            subtitle.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 209, 96, 96));
            subtitle.Opacity = 0.95;
        }
        else if (isFactory || isMember)
        {
            subtitle.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 214, 158, 46)); // gold
            subtitle.Opacity = 1.0;
            subtitle.FontWeight = FontWeights.SemiBold;
        }
        text.Children.Add(subtitle);
        Grid.SetColumn(text, 1);
        g.Children.Add(iconEl);
        g.Children.Add(text);

        // +/- toggle for nodes that have children
        if (n.Children.Count > 0)
        {
            Button toggle = new()
            {
                Width = 24, Height = 24, MinWidth = 0, Padding = new Thickness(0),
                CornerRadius = new CornerRadius(12), VerticalAlignment = VerticalAlignment.Center,
                Content = new FontIcon { Glyph = G(n.Expanded ? 0xE738 : 0xE710), FontSize = 11 }
            };
            ToolTipService.SetToolTip(toggle, n.Expanded ? "Collapse" : $"Expand ({n.Children.Count})");
            toggle.Click += (_, _) => { n.Expanded = !n.Expanded; LayoutAndDraw(canvas, root); };
            Grid.SetColumn(toggle, 2);
            g.Children.Add(toggle);
        }

        Border box = new() { Style = (Style)Resources["DataCard"], Width = w, Height = h, Child = g };
        if (n.Depth == 0)
        {
            box.BorderBrush = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];
            box.BorderThickness = new Thickness(2);
        }
        else if (isFactory)
        {
            box.BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 214, 158, 46)); // gold
            box.BorderThickness = new Thickness(2);
        }
        ToolTipService.SetToolTip(box, n.Path);

        bool canOpen = n.Resolved && n.Depth > 0 && !n.Cyclic && !n.IsLinkedFile;
        if (canOpen)
        {
            box.Tapped += (_, _) => OpenReference(n.Path);
            ToolTipService.SetToolTip(box, n.Path + "\nClick to open · right-click for more");
        }

        if (n.Resolved)
        {
            box.ContextRequested += (_, e) =>
            {
                MenuFlyout menu = new();
                if (canOpen)
                {
                    MenuFlyoutItem open = new() { Text = "Open in a tab", Icon = new FontIcon { Glyph = G(0xE8E5) } };
                    open.Click += (_, _) => OpenReference(n.Path);
                    menu.Items.Add(open);
                }
                MenuFlyoutItem reveal = new() { Text = "Show in Explorer", Icon = new FontIcon { Glyph = G(0xEC50) } };
                reveal.Click += (_, _) => RevealInExplorer(n.Path);
                menu.Items.Add(reveal);
                if (e.TryGetPosition(box, out Windows.Foundation.Point p)) { menu.ShowAt(box, p); }
                else { menu.ShowAt(box); }
                e.Handled = true;
            };
        }
        return box;
    }

    private static bool IsImageFile(string name) =>
        Path.GetExtension(name).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".tif" or ".tiff" or ".gif";

    /// <summary>Decodes raw image bytes (a document's cached preview) into a usable image source.</summary>
    private static ImageSource? BitmapFromBytes(byte[]? bytes)
    {
        if (bytes is not { Length: > 0 }) { return null; }
        try
        {
            BitmapImage bmp = new();
            using MemoryStream ms = new(bytes);
            bmp.SetSource(ms.AsRandomAccessStream());
            return bmp;
        }
        catch { return null; }
    }

    /// <summary>Left-drag on empty space (or middle-drag anywhere) pans; wheel zooms at the cursor.</summary>
    private static void WirePanZoom(Grid viewport, Canvas canvas, CompositeTransform xf)
    {
        bool panning = false;
        Windows.Foundation.Point start = default;
        double tx0 = 0, ty0 = 0;

        viewport.PointerPressed += (_, e) =>
        {
            Microsoft.UI.Input.PointerPoint pp = e.GetCurrentPoint(viewport);
            bool onBackground = ReferenceEquals(e.OriginalSource, canvas) || ReferenceEquals(e.OriginalSource, viewport);
            if (!pp.Properties.IsMiddleButtonPressed && !(pp.Properties.IsLeftButtonPressed && onBackground))
            {
                return;
            }
            panning = true; start = pp.Position; tx0 = xf.TranslateX; ty0 = xf.TranslateY;
            viewport.CapturePointer(e.Pointer);
        };
        viewport.PointerMoved += (_, e) =>
        {
            if (!panning) { return; }
            Windows.Foundation.Point p = e.GetCurrentPoint(viewport).Position;
            xf.TranslateX = tx0 + (p.X - start.X);
            xf.TranslateY = ty0 + (p.Y - start.Y);
        };
        void End(object _, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            if (panning) { panning = false; viewport.ReleasePointerCapture(e.Pointer); }
        }
        viewport.PointerReleased += End;
        viewport.PointerCanceled += End;
        viewport.PointerCaptureLost += (_, _) => panning = false;

        viewport.PointerWheelChanged += (_, e) =>
        {
            Microsoft.UI.Input.PointerPoint pp = e.GetCurrentPoint(viewport);
            double factor = pp.Properties.MouseWheelDelta > 0 ? 1.12 : 1 / 1.12;
            double scale = Math.Clamp(xf.ScaleX * factor, 0.2, 3.0);
            factor = scale / xf.ScaleX;
            Windows.Foundation.Point c = pp.Position;
            xf.TranslateX = c.X - (c.X - xf.TranslateX) * factor;
            xf.TranslateY = c.Y - (c.Y - xf.TranslateY) * factor;
            xf.ScaleX = xf.ScaleY = scale;
            e.Handled = true;
        };
    }

    private static string KindLabel(InventorDocument.DocKind k) => k switch
    {
        InventorDocument.DocKind.Part => "Part",
        InventorDocument.DocKind.Assembly => "Assembly",
        InventorDocument.DocKind.Drawing => "Drawing",
        InventorDocument.DocKind.Presentation => "Presentation",
        _ => "Document"
    };
}
