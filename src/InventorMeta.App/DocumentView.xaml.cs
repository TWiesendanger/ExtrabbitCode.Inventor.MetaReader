using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
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
        bool isStep = Document?.IsStep == true;
        PropsTab.Visibility = TabVis("All Properties", true);
        StatesTab.Visibility = TabVis("Model States", Document?.HasModelStates == true);
        RefsTab.Visibility = TabVis("References", !isStep);
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
        Tab("References", !Document.IsStep);
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
        if (!string.Equals(FilePath, path, StringComparison.OrdinalIgnoreCase))
        {
            _repairedThisSession = false;   // the "Repaired" chip belongs to the previous file
        }
        FilePath = path;
        PathText.Text = path;
        _shown3DTip = false;
        try
        {
            long size = new FileInfo(path).Length;
            System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
            Document = new InventorDocument(path);
            Populate(Document);
            Serilog.Log.Information(
                "Loaded {File} ({Size:N0} B) in {Ms} ms - {Props} properties, {Refs} references, {States} model states, {Segments} segments",
                Document.FileName, size, sw.ElapsedMilliseconds, Document.Properties.Count,
                Document.References.Count, Document.ModelStateDetails.Count, Document.Segments.Count);
            string counts = Document.IsStep
                ? $"{Document.Properties.Count} STEP metadata field(s)."
                : $"{Document.Properties.Count} properties, {Document.References.Count} reference(s).";
            StatusSink?.Invoke($"Loaded {Document.FileName} - {counts}");
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
            Serilog.Log.Error(ex, "Failed to load {File}", path);
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
        PropsTab.Header = "All Properties";
        StatesTab.Header = "Model States";
        RefsTab.Header = "References";
        StructureTab.Header = doc.IsStep ? "STEP Source" : "File Structure";

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
        SetDamageBadge(doc);

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

        ClearStructureCards();
        if (doc.IsStep)
        {
            TreeText.Text = doc.StructureText;
            TreeText.Visibility = Visibility.Visible;
        }
        else
        {
            TreeText.Visibility = Visibility.Collapsed;
            PopulateStructureTree(doc);
        }

        ApplyTabVisibility();
        RefreshHiddenUi();
    }

    /// <summary>The category badge's hover legend, kept so the demo tour can open it programmatically
    /// (its painted cursor never moves the OS pointer, so it can't trigger a real hover).</summary>
    private ToolTip? _categoryTip;

    /// <summary>Colours the category badge for the document's <see cref="InventorDocument.PrimaryCategory"/>
    /// and attaches the legend as its hover tooltip.</summary>
    private void SetCategoryBadge(InventorDocument doc)
    {
        (string name, Windows.UI.Color color) = DocCategoryUi.Of(doc.PrimaryCategory);
        CategoryBadgeText.Text = name;
        CategoryBadge.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(color);
        CategoryBadge.Visibility = Visibility.Visible;
        _categoryTip = new ToolTip
        {
            Content = DocCategoryUi.Legend(doc.PrimaryCategory),
            PlacementTarget = CategoryBadge,
            Placement = PlacementMode.Bottom
        };
        ToolTipService.SetToolTip(CategoryBadge, _categoryTip);
    }

}
