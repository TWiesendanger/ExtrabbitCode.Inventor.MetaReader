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
        public MainWindow()
        {
            InitializeComponent();
            Title = "Inventor Metadata Viewer";
            try { Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); } catch { }

            // modern Windows 11 look: Mica backdrop + content drawn into the title bar
            try { SystemBackdrop = new Microsoft.UI.Xaml.Media.MicaBackdrop(); } catch { }
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(AppTitleBar);
            VersionText.Text = "v" + AppInfo.Version;
            SetWindowIcon();

            UpdateEmptyState();

            // open any files passed on the command line
            var cli = Environment.GetCommandLineArgs();
            DispatcherQueue.TryEnqueue(() =>
            {
                foreach (var a in cli.Skip(1).Where(File.Exists)) OpenFile(a);
            });
        }

        private void SetWindowIcon()
        {
            try
            {
                string ico = Path.Combine(AppContext.BaseDirectory, "Assets", "appicon.ico");
                if (!File.Exists(ico)) return;
                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
                var id = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
                Microsoft.UI.Windowing.AppWindow.GetFromWindowId(id).SetIcon(ico);
            }
            catch { /* icon is cosmetic */ }
        }

        private async void OnInfoClick(object sender, RoutedEventArgs e)
        {
            var dlg = new AboutDialog { XamlRoot = Content.XamlRoot };
            await dlg.ShowAsync();
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
