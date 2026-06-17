using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using InventorMeta;

namespace InventorMeta.App
{
    public sealed partial class MainWindow : Window
    {
        private ElementTheme _theme;

        public MainWindow()
        {
            InitializeComponent();
            Title = "Inventor Metadata Viewer";
            try { Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); } catch { }

            ExtendsContentIntoTitleBar = true;
            SetTitleBar(AppTitleBar);
            VersionText.Text = "v" + AppInfo.Version;
            SetupWindow();

            // light / dark theme (persisted)
            _theme = ThemeManager.Load();
            ThemeManager.Apply(this, _theme);
            UpdateThemeIcon();

            UpdateEmptyState();

            // open any files passed on the command line
            var cli = Environment.GetCommandLineArgs();
            DispatcherQueue.TryEnqueue(() =>
            {
                foreach (var a in cli.Skip(1).Where(File.Exists)) OpenFile(a);
            });
        }

        private Microsoft.UI.Windowing.AppWindow? _appWindow;

        private void SetupWindow()
        {
            try
            {
                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
                var id = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
                _appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(id);

                string ico = Path.Combine(AppContext.BaseDirectory, "Assets", "appicon.ico");
                if (File.Exists(ico)) _appWindow.SetIcon(ico);

                var area = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(
                    id, Microsoft.UI.Windowing.DisplayAreaFallback.Primary);

                // restore last size, or a wider default
                int w = Math.Clamp(AppSettings.GetInt("win.w", 1360), 900, 8000);
                int h = Math.Clamp(AppSettings.GetInt("win.h", 880), 600, 6000);
                _appWindow.Resize(new Windows.Graphics.SizeInt32(w, h));

                int x = AppSettings.GetInt("win.x", int.MinValue);
                int y = AppSettings.GetInt("win.y", int.MinValue);
                if (x != int.MinValue && y != int.MinValue && area != null)
                {
                    x = Math.Clamp(x, area.WorkArea.X - w + 120, area.WorkArea.X + area.WorkArea.Width - 120);
                    y = Math.Clamp(y, area.WorkArea.Y, area.WorkArea.Y + area.WorkArea.Height - 80);
                    _appWindow.Move(new Windows.Graphics.PointInt32(x, y));
                }
                else if (area != null)
                {
                    _appWindow.Move(new Windows.Graphics.PointInt32(
                        area.WorkArea.X + (area.WorkArea.Width - w) / 2,
                        area.WorkArea.Y + (area.WorkArea.Height - h) / 2));
                }

                Closed += (_, _) => SaveWindowBounds();
            }
            catch { /* window chrome is best-effort */ }
        }

        private void SaveWindowBounds()
        {
            try
            {
                if (_appWindow == null) return;
                var s = _appWindow.Size; var p = _appWindow.Position;
                if (s.Width < 300 || s.Height < 200) return; // skip minimized / odd states
                AppSettings.SetMany(
                    ("win.w", s.Width.ToString()), ("win.h", s.Height.ToString()),
                    ("win.x", p.X.ToString()), ("win.y", p.Y.ToString()));
            }
            catch { /* best-effort */ }
        }

        private async void OnInfoClick(object sender, RoutedEventArgs e)
        {
            var dlg = new AboutDialog { XamlRoot = Content.XamlRoot, RequestedTheme = _theme };
            await dlg.ShowAsync();
        }

        private void OnThemeButtonClick(object sender, RoutedEventArgs e)
        {
            _theme = _theme == ElementTheme.Light ? ElementTheme.Dark : ElementTheme.Light;
            ThemeManager.Apply(this, _theme);
            ThemeManager.Save(_theme);
            UpdateThemeIcon();
        }

        private DispatcherTimer? _toastTimer;

        /// <summary>Show a brief in-app toast (e.g. copy confirmation).</summary>
        public void ShowToast(string message)
        {
            ToastText.Text = message;
            Toast.Visibility = Visibility.Visible;
            Toast.Opacity = 1;

            _toastTimer ??= CreateToastTimer();
            _toastTimer.Stop();
            _toastTimer.Start();
        }

        private DispatcherTimer CreateToastTimer()
        {
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1700) };
            t.Tick += (_, _) => { t.Stop(); Toast.Opacity = 0; Toast.Visibility = Visibility.Collapsed; };
            return t;
        }

        private void UpdateThemeIcon()
        {
            bool dark = _theme != ElementTheme.Light;
            ThemeIcon.Glyph = ((char)(dark ? 0xE708 : 0xE706)).ToString(); // moon when dark, sun when light
            ToolTipService.SetToolTip(ThemeButton, dark ? "Switch to light theme" : "Switch to dark theme");
        }

        // ---------- opening ----------
        private async void OnOpenClick(object sender, RoutedEventArgs e)
        {
            var picker = new FileOpenPicker();
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            foreach (var ext in new[] { ".ipt", ".iam", ".idw", ".ipn", "*" }) picker.FileTypeFilter.Add(ext);
            var files = await picker.PickMultipleFilesAsync();
            foreach (var f in files) OpenFile(f.Path);
        }

        private void OnAddTab(TabView sender, object args) => OnOpenClick(sender, null!);

        private void OpenFile(string path)
        {
            // if already open, just select that tab
            foreach (var t in DocTabs.TabItems.OfType<TabViewItem>())
                if (t.Content is DocumentView dv0 &&
                    string.Equals(dv0.FilePath, path, StringComparison.OrdinalIgnoreCase))
                { DocTabs.SelectedItem = t; return; }

            var dv = new DocumentView();
            dv.StatusChanged += s => StatusText.Text = s;
            if (!dv.Load(path)) return;   // invalid file: status already set, no tab added

            var tab = new TabViewItem
            {
                Header = dv.TabTitle,
                Content = dv,
                IconSource = AppIcons.IconSource(dv.Document?.Kind ?? InventorDocument.DocKind.Unknown)
            };
            ToolTipService.SetToolTip(tab, path);
            DocTabs.TabItems.Add(tab);
            DocTabs.SelectedItem = tab;
            UpdateEmptyState();
        }

        // ---------- tab lifecycle ----------
        private void OnTabClose(TabView sender, TabViewTabCloseRequestedEventArgs args)
        {
            DocTabs.TabItems.Remove(args.Tab);
            UpdateEmptyState();
        }

        private void OnTabSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (DocTabs.SelectedItem is TabViewItem { Content: DocumentView dv } && dv.Document != null)
                StatusText.Text = $"{dv.Document.FileName} - {dv.Document.DocumentType}";
        }

        private void UpdateEmptyState()
        {
            bool any = DocTabs.TabItems.Count > 0;
            EmptyState.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
            DocTabs.Visibility    = any ? Visibility.Visible : Visibility.Collapsed;
        }

        // ---------- drag & drop ----------
        private void OnDragOver(object sender, DragEventArgs e)
        {
            if (e.DataView.Contains(StandardDataFormats.StorageItems))
            {
                e.AcceptedOperation = DataPackageOperation.Copy;
                e.DragUIOverride.Caption = "Open in a new tab";
                e.DragUIOverride.IsCaptionVisible = true;
                e.DragUIOverride.IsGlyphVisible = true;
            }
        }

        private async void OnDrop(object sender, DragEventArgs e)
        {
            if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
            var def = e.GetDeferral();
            try
            {
                var items = await e.DataView.GetStorageItemsAsync();
                foreach (var it in items.OfType<StorageFile>()) OpenFile(it.Path);
            }
            finally { def.Complete(); }
        }
    }
}
