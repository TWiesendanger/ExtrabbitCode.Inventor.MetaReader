using System;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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
            foreach (var grp in doc.Properties.GroupBy(p => p.Set))
            {
                var table = NewPropTable();
                int row = 0;
                foreach (var p in grp.OrderBy(x => x.Pid))
                {
                    table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                    AddCell(table, row, 0, p.Pid.ToString(), 0.5);
                    AddCell(table, row, 1, p.Name, 1.0, true);
                    AddCell(table, row, 2, p.Display, 0.85);
                    row++;
                }
                PropsPanel.Children.Add(new Expander
                {
                    Header = $"{grp.Key}   ({grp.Count()})",
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    IsExpanded = grp.Key.StartsWith("Design Tracking Properties"),
                    Content = table
                });
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

            // ---- differences matrix (the killer view) ----
            if (states.Count > 1)
            {
                var diffGrid = new Grid { Margin = new Thickness(0, 4, 0, 0) };
                diffGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(210) });
                foreach (var _ in states) diffGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                int r = 0;
                diffGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                AddCell(diffGrid, r, 0, "Property", 0.6, true);
                for (int c = 0; c < states.Count; c++)
                    AddCell(diffGrid, r, c + 1, states[c].Name + (states[c].IsActive ? "  ●" : ""), 0.6, true);
                r++;
                foreach (var id in diffIds)
                {
                    diffGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                    AddCell(diffGrid, r, 0, id.Name, 1.0, true);
                    for (int c = 0; c < states.Count; c++)
                        AddCell(diffGrid, r, c + 1, ValueOf(states[c], id), 0.85);
                    r++;
                }
                var diffContent = new StackPanel { Spacing = 6 };
                diffContent.Children.Add(diffGrid);
                if (internalDiffs > 0)
                    diffContent.Children.Add(new TextBlock {
                        Text = $"+ {internalDiffs} internal/volatile field(s) also differ (revision ids, save timestamps, counters)",
                        Opacity = 0.5, FontSize = 11, TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(0, 4, 0, 0) });
                StatesPanel.Children.Add(new Expander
                {
                    Header = diffIds.Count > 0
                        ? $"Differences between states  ({diffIds.Count})"
                        : "Differences between states  (no user-facing properties differ)",
                    IsExpanded = true,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    Content = diffContent
                });
            }

            // ---- per-state full property browser ----
            foreach (var s in states)
            {
                var inner = new StackPanel { Spacing = 6 };
                if (s.Summary.Count > 0)
                {
                    var sumGrid = new Grid();
                    sumGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
                    sumGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    int rr = 0;
                    foreach (var kv in s.Summary)
                    {
                        sumGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                        AddCell(sumGrid, rr, 0, kv.Key, 0.6);
                        AddCell(sumGrid, rr, 1, kv.Value, 0.95, true);
                        rr++;
                    }
                    inner.Children.Add(sumGrid);
                }
                inner.Children.Add(new TextBlock {
                    Text = $"{s.Properties.Count} properties · storage {s.StorageName}",
                    Opacity = 0.5, FontSize = 11, FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
                    Margin = new Thickness(0, 4, 0, 0) });

                StatesPanel.Children.Add(new Expander
                {
                    Header = s.Name + (s.IsActive ? "   (active)" : ""),
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    Content = inner
                });
            }
        }

        // ---- helpers ----
        private static Grid NewPropTable()
        {
            var table = new Grid { Margin = new Thickness(0, 4, 0, 0) };
            table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(50) });
            table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
            table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            return table;
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

        private static void AddCell(Grid table, int row, int col, string text, double opacity, bool semibold = false)
        {
            var t = new TextBlock
            {
                Text = text, Opacity = opacity, FontSize = 12, Margin = new Thickness(2, 2, 8, 2),
                TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true,
                FontWeight = semibold ? FontWeights.SemiBold : FontWeights.Normal
            };
            Grid.SetRow(t, row); Grid.SetColumn(t, col);
            table.Children.Add(t);
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
