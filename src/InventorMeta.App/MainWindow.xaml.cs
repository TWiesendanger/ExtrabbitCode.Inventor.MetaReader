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
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Windows.Graphics;

namespace InventorMeta.App;

public sealed partial class MainWindow
{
    private ElementTheme _theme;

    public MainWindow()
    {
        InitializeComponent();
        Title = "Inventor Metadata Viewer";
        try { Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); }
        catch
        {
            // ignored
        }

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
        string[] cli = Environment.GetCommandLineArgs();
        DispatcherQueue.TryEnqueue(() =>
        {
            foreach (string a in cli.Skip(1).Where(File.Exists))
            {
                OpenFile(a);
            }
        });
    }

    private AppWindow? _appWindow;

    private void SetupWindow()
    {
        try
        {
            IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            WindowId id = Win32Interop.GetWindowIdFromWindow(hwnd);
            _appWindow = AppWindow.GetFromWindowId(id);

            string ico = Path.Combine(AppContext.BaseDirectory, "Assets", "appicon.ico");
            if (File.Exists(ico))
            {
                _appWindow.SetIcon(ico);
            }

            DisplayArea? area = DisplayArea.GetFromWindowId(
                id, DisplayAreaFallback.Primary);

            // restore last size, or a wider default
            int w = Math.Clamp(AppSettings.GetInt("win.w", 1360), 900, 8000);
            int h = Math.Clamp(AppSettings.GetInt("win.h", 880), 600, 6000);
            _appWindow.Resize(new SizeInt32(w, h));

            int x = AppSettings.GetInt("win.x", int.MinValue);
            int y = AppSettings.GetInt("win.y", int.MinValue);
            if (x != int.MinValue && y != int.MinValue && area != null)
            {
                x = Math.Clamp(x, area.WorkArea.X - w + 120, area.WorkArea.X + area.WorkArea.Width - 120);
                y = Math.Clamp(y, area.WorkArea.Y, area.WorkArea.Y + area.WorkArea.Height - 80);
                _appWindow.Move(new PointInt32(x, y));
            }
            else if (area != null)
            {
                _appWindow.Move(new PointInt32(
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
            if (_appWindow == null)
            {
                return;
            }

            SizeInt32 s = _appWindow.Size; PointInt32 p = _appWindow.Position;
            if (s.Width < 300 || s.Height < 200)
            {
                return; // skip minimized / odd states
            }

            AppSettings.SetMany(
                ("win.w", s.Width.ToString()), ("win.h", s.Height.ToString()),
                ("win.x", p.X.ToString()), ("win.y", p.Y.ToString()));
        }
        catch { /* best-effort */ }
    }

    private async void OnInfoClick(object sender, RoutedEventArgs e)
    {
        AboutDialog dlg = new() { XamlRoot = Content.XamlRoot, RequestedTheme = _theme };
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
        DispatcherTimer t = new() { Interval = TimeSpan.FromMilliseconds(1700) };
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
        FileOpenPicker picker = new();
        IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        foreach (string ext in new[] { ".ipt", ".iam", ".idw", ".ipn", "*" })
        {
            picker.FileTypeFilter.Add(ext);
        }

        IReadOnlyList<StorageFile>? files = await picker.PickMultipleFilesAsync();
        foreach (StorageFile f in files)
        {
            OpenFile(f.Path);
        }
    }

    private void OnAddTab(TabView sender, object args) => OnOpenClick(sender, null!);

    private void OpenFile(string path)
    {
        // if already open, just select that tab
        foreach (TabViewItem t in DocTabs.TabItems.OfType<TabViewItem>())
        {
            if (t.Content is DocumentView dv0 &&
                string.Equals(dv0.FilePath, path, StringComparison.OrdinalIgnoreCase))
            { DocTabs.SelectedItem = t; return; }
        }

        DocumentView dv = new();
        dv.StatusChanged += s => StatusText.Text = s;
        if (!dv.Load(path))
        {
            return;   // invalid file: status already set, no tab added
        }

        TabViewItem tab = new()
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
        if (DocTabs.SelectedItem is TabViewItem { Content: DocumentView { Document: not null } dv })
        {
            StatusText.Text = $"{dv.Document.FileName} - {dv.Document.DocumentType}";
        }
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
        if (!e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            return;
        }

        DragOperationDeferral? def = e.GetDeferral();
        try
        {
            IReadOnlyList<IStorageItem>? items = await e.DataView.GetStorageItemsAsync();
            foreach (StorageFile it in items.OfType<StorageFile>())
            {
                OpenFile(it.Path);
            }
        }
        finally { def.Complete(); }
    }
}