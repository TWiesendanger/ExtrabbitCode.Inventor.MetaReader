using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage;
using Windows.Storage.Pickers;
using InventorMeta;

namespace InventorMeta.App
{
    public sealed partial class DocumentView : UserControl
    {
        public string FilePath { get; private set; } = "";
        public InventorDocument? Document { get; private set; }
        public string TabTitle => Document?.FileName ?? Path.GetFileName(FilePath);

        /// <summary>Raised with a status message after (re)load; the window shows it in the footer.</summary>
        public event Action<string>? StatusChanged;

        public DocumentView() { InitializeComponent(); }

        public bool Load(string path)
        {
            FilePath = path;
            PathText.Text = path;
            try
            {
                if (!CompoundFile.LooksLikeCompoundFile(path))
                {
                    StatusChanged?.Invoke($"{Path.GetFileName(path)} is not an Inventor / OLE compound file.");
                    return false;
                }
                Document = new InventorDocument(path);
                Populate(Document);
                StatusChanged?.Invoke($"Loaded {Document.FileName} - {Document.Properties.Count} properties, " +
                                      $"{Document.References.Count} reference(s).");
                return true;
            }
            catch (Exception ex)
            {
                StatusChanged?.Invoke("Error: " + ex.Message);
                return false;
            }
        }

        public void Refresh()
        {
            if (string.IsNullOrEmpty(FilePath)) return;
            if (!File.Exists(FilePath)) { StatusChanged?.Invoke("File no longer exists: " + FilePath); return; }
            if (Load(FilePath)) StatusChanged?.Invoke($"Refreshed {TabTitle} at {DateTime.Now:HH:mm:ss}.");
        }

        private void OnRefresh(object sender, RoutedEventArgs e) => Refresh();

        private async void OnExportJson(object sender, RoutedEventArgs e)
        {
            if (Document == null) return;
            var picker = new FileSavePicker();
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindowInstance);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            picker.FileTypeChoices.Add("JSON", new System.Collections.Generic.List<string> { ".json" });
            picker.SuggestedFileName = Path.GetFileNameWithoutExtension(Document.FileName) + "_metadata";
            var file = await picker.PickSaveFileAsync();
            if (file != null)
            {
                await FileIO.WriteTextAsync(file, Document.ToJson());
                StatusChanged?.Invoke("Exported " + file.Name);
            }
        }

        private void Populate(InventorDocument doc)
        {
            FileNameText.Text = doc.FileName;
            DocTypeText.Text  = doc.DocumentType;
            TypeIcon.Source   = AppIcons.Bitmap(doc.Kind);

            if (doc.Thumbnail != null)
            {
                var bmp = new BitmapImage();
                using var ms = new MemoryStream(doc.Thumbnail);
                bmp.SetSource(ms.AsRandomAccessStream());
                ThumbImage.Source = bmp;
                ThumbImage.Visibility = Visibility.Visible;
                NoThumb.Visibility = Visibility.Collapsed;
            }
            else
            {
                ThumbImage.Source = null;
                ThumbImage.Visibility = Visibility.Collapsed;
                NoThumb.Visibility = Visibility.Visible;
            }

            KeyPropsPanel.Children.Clear();
            foreach (var kv in doc.Summary) KeyPropsPanel.Children.Add(KeyRow(kv.Key, kv.Value));
            if (doc.VersionInfo.TryGetValue("Saved From", out var sf)) KeyPropsPanel.Children.Add(KeyRow("Saved From", sf));

            PropsPanel.Children.Clear();
            foreach (var grp in doc.Properties.GroupBy(p => p.Set).OrderBy(g => SetOrder(g.Key)))
            {
                var table = PropTable(grp.OrderBy(x => x.Pid).Select(p => (p.Pid, p.Name, p.Display)));
                PropsPanel.Children.Add(Card(grp.Key, grp.Count().ToString(), table));
            }

            PopulateStates(doc);

            RefsPanel.Children.Clear();
            RefsPanel.Children.Add(Section("Referenced files",
                doc.References.Count > 0 ? doc.References.ToArray() : new[] { "(none)" }));
            RefsPanel.Children.Add(Section("Version / provenance",
                doc.VersionInfo.Select(kv => $"{kv.Key}: {kv.Value}").ToArray()));

            using var cf = new CompoundFile(doc.FilePath);
            var sb = new StringBuilder();
            sb.AppendLine($"Root CLSID  {cf.Directory[0].Clsid}");
            sb.AppendLine($"Container   {doc.CfbVersionInfo}\n");
            foreach (var en in cf.Directory.Where(d => d.Type is 1 or 2 or 5)
                                           .OrderBy(d => d.Path, StringComparer.OrdinalIgnoreCase))
            {
                string size = en.Type == 2 ? en.Size.ToString("N0") : "";
                sb.AppendLine($"{en.Path,-46}{en.TypeName,-8}{size,12}");
            }
            TreeText.Text = sb.ToString();
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
            var states = doc.ModelStateDetails;
            StatesTab.Header = $"Model States ({states.Count})";

            StatesPanel.Children.Add(new TextBlock
            {
                Text = "Per-state iProperties, read straight from the file - no need to switch states in Inventor.",
                Opacity = 0.7, FontSize = 13, TextWrapping = TextWrapping.Wrap
            });

            // identity of a property = (Set, Pid, Name); value may vary per state
            var active = states.FirstOrDefault(s => s.IsActive);
            bool Has(InventorDocument.ModelState s, (string Set, uint Pid, string Name) id) =>
                s.Properties.Any(p => p.Set == id.Set && p.Pid == id.Pid && p.Name == id.Name);
            string ValueOf(InventorDocument.ModelState s, (string Set, uint Pid, string Name) id)
            {
                var p = s.Properties.FirstOrDefault(x => x.Set == id.Set && x.Pid == id.Pid && x.Name == id.Name);
                if (p != null) return p.Display;
                return !s.IsActive && active != null && Has(active, id) ? "(not cached)" : "-";
            }
            var ids = states.SelectMany(s => s.Properties).Select(p => (p.Set, p.Pid, p.Name))
                            .Distinct().OrderBy(i => i.Set).ThenBy(i => i.Pid).ToList();
            bool Meaningful((string Set, uint Pid, string Name) id) =>
                !id.Set.Contains("(internal)") &&
                !(id.Set.StartsWith("Design Tracking Properties") && (id.Pid == 21 || id.Pid == 46)) &&
                !(id.Set.Contains("Summary Information") && id.Pid == 17);  // thumbnail blob
            var allDiffs = ids.Where(id => states.Select(s => ValueOf(s, id)).Distinct().Count() > 1).ToList();
            var diffIds = allDiffs.Where(Meaningful).ToList();
            int internalDiffs = allDiffs.Count - diffIds.Count;

            // ---- differences matrix (the headline view) ----
            if (states.Count > 1)
            {
                var headers = states.Select(s => s.Name).ToArray();
                var act = states.Select(s => s.IsActive).ToArray();
                var rows = diffIds.Select(id => (id.Name, states.Select(s => ValueOf(s, id)).ToArray())).ToList();
                UIElement body = rows.Count > 0
                    ? Matrix(headers, act, rows)
                    : new TextBlock { Text = "No user-facing properties differ between states.",
                        Opacity = 0.7, Margin = new Thickness(14, 10, 14, 12), TextWrapping = TextWrapping.Wrap };
                StatesPanel.Children.Add(Card("Differences between states",
                    rows.Count > 0 ? rows.Count.ToString() : null, body));
                if (internalDiffs > 0)
                    StatesPanel.Children.Add(new TextBlock {
                        Text = $"+ {internalDiffs} internal/volatile field(s) also differ (revision ids, save timestamps, counters)",
                        Opacity = 0.5, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, -4, 0, 0) });
            }

            // ---- per-state browser ----
            foreach (var s in states)
            {
                var body = new StackPanel();
                if (s.Summary.Count > 0)
                    body.Children.Add(KvList(s.Summary.Select(kv => (kv.Key, kv.Value)).ToList(), 150));
                body.Children.Add(new TextBlock {
                    Text = $"{s.Properties.Count} properties · storage {s.StorageName}",
                    Opacity = 0.5, FontSize = 11, FontFamily = new FontFamily("Consolas"),
                    TextWrapping = TextWrapping.Wrap, Margin = new Thickness(14, 8, 14, 12) });
                StatesPanel.Children.Add(Card(s.Name, s.IsActive ? "active" : null, body));
            }
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

        /// <summary>A rounded, bordered, elevated card with a header strip and a body.</summary>
        private Border Card(string title, string? badge, UIElement body)
        {
            var headerContent = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            headerContent.Children.Add(new TextBlock
            {
                Text = title, FontWeight = FontWeights.SemiBold, FontSize = 14,
                VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap
            });
            if (badge != null)
                headerContent.Children.Add(new Border
                {
                    Style = (Style)Resources["Badge"],
                    Child = new TextBlock { Text = badge, Style = (Style)Resources["BadgeText"] }
                });

            var stack = new StackPanel();
            stack.Children.Add(new Border { Style = (Style)Resources["CardHeaderRow"], Child = headerContent });
            stack.Children.Add(body);

            var card = new Border { Style = (Style)Resources["DataCard"], Child = stack, Margin = new Thickness(0, 0, 0, 4) };
            try { card.Shadow = new ThemeShadow(); card.Translation = new Vector3(0, 0, 8); } catch { }
            return card;
        }

        /// <summary>PID / Name / Value table with divider lines between rows.</summary>
        private StackPanel PropTable(IEnumerable<(uint pid, string name, string val)> rows)
        {
            var list = rows.ToList();
            var sp = new StackPanel();
            for (int i = 0; i < list.Count; i++)
            {
                var (pid, name, val) = list[i];
                var g = new Grid();
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(46) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(196) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                Cell(g, 0, pid.ToString(), 0.45, mono: true);
                Cell(g, 1, name, 0.9, semibold: true);
                Cell(g, 2, val, 0.8);
                sp.Children.Add(new Border { Child = g, Padding = new Thickness(14, 7, 14, 7) });
            }
            return sp;
        }

        /// <summary>Two-column key / value list (plain rows).</summary>
        private StackPanel KvList(List<(string k, string v)> rows, double keyWidth)
        {
            var sp = new StackPanel();
            for (int i = 0; i < rows.Count; i++)
            {
                var (k, v) = rows[i];
                var g = new Grid();
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(keyWidth) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                Cell(g, 0, k, 0.55);
                Cell(g, 1, v, 0.95, semibold: true);
                sp.Children.Add(new Border { Child = g, Padding = new Thickness(14, 7, 14, 7) });
            }
            return sp;
        }

        /// <summary>Per-state comparison matrix: a header strip + zebra rows, active column tinted.</summary>
        private Grid Matrix(string[] headers, bool[] active, List<(string label, string[] vals)> rows)
        {
            int cols = headers.Length + 1;
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) });
            for (int c = 0; c < headers.Length; c++)
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            // header row
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var hdr = new Border { Style = (Style)Resources["CardHeaderRow"], CornerRadius = new CornerRadius(0) };
            Grid.SetRow(hdr, 0); Grid.SetColumnSpan(hdr, cols); grid.Children.Add(hdr);
            MCell(grid, 0, 0, "Property", true, 0.7);
            for (int c = 0; c < headers.Length; c++)
                MCell(grid, 0, c + 1, headers[c] + (active[c] ? "  ●" : ""), true, 0.7);

            for (int ri = 0; ri < rows.Count; ri++)
            {
                int r = ri + 1;
                var (label, vals) = rows[ri];
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                MCell(grid, r, 0, label, true, 1.0);
                for (int c = 0; c < vals.Length; c++)
                    MCell(grid, r, c + 1, vals[c], false, 0.9);
            }
            return grid;
        }

        private static void MCell(Grid g, int row, int col, string text, bool semibold, double opacity)
        {
            var t = new TextBlock
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
            var t = new TextBlock
            {
                Text = text, Opacity = opacity, FontSize = 12, TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true, VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0),
                FontWeight = semibold ? FontWeights.SemiBold : FontWeights.Normal
            };
            if (mono) t.FontFamily = new FontFamily("Consolas");
            Grid.SetColumn(t, col);
            g.Children.Add(t);
        }

        private static Grid KeyRow(string label, string value)
        {
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(118) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var l = new TextBlock { Text = label, Opacity = 0.6, FontSize = 12, TextWrapping = TextWrapping.Wrap };
            var v = new TextBlock { Text = value, FontSize = 13, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
            Grid.SetColumn(v, 1);
            g.Children.Add(l); g.Children.Add(v);
            return g;
        }

        private static StackPanel Section(string title, string[] lines)
        {
            var sp = new StackPanel { Spacing = 4 };
            sp.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, FontSize = 14 });
            foreach (var l in lines)
                sp.Children.Add(new TextBlock { Text = l,
                    FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
                    FontSize = 12, Opacity = 0.85, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
            return sp;
        }
    }
}
