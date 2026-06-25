using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Windows.Graphics;

namespace ExtrabbitCode.Inventor.MetaReader.App;

public sealed partial class MainWindow
{
    private ElementTheme _theme;

    /// <summary>The first window; torn-out tab windows are created with <c>isPrimary: false</c>.</summary>
    private readonly bool _isPrimary;

    public MainWindow() : this(isPrimary: true) { }

    public MainWindow(bool isPrimary)
    {
        _isPrimary = isPrimary;
        InitializeComponent();

        // Torn-out windows are shown by the framework the instant they're created, before
        // their XAML paints. A Mica backdrop fills that gap (DWM-level) so the window never
        // flashes black during a tear-out drag.
        if (!isPrimary)
        {
            SystemBackdrop = new MicaBackdrop();
        }

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

        App.ActiveWindows.Add(this);
        Closed += (_, _) => App.ActiveWindows.Remove(this);

        // open any files passed on the command line (primary window only)
        if (_isPrimary)
        {
            string[] cli = Environment.GetCommandLineArgs();
            DispatcherQueue.TryEnqueue(() =>
            {
                foreach (string a in cli.Skip(1).Where(File.Exists))
                {
                    OpenFile(a);
                }
            });

            // First-run analytics opt-in, then record the app launch. Runs once the root is in the
            // visual tree (so the dialog has a XamlRoot). MaybeAskAsync is a no-op after the first time.
            if (Content is FrameworkElement root)
            {
                root.Loaded += OnRootLoadedForAnalytics;
            }
        }
    }

    private bool _startupTracked;

    private async void OnRootLoadedForAnalytics(object sender, RoutedEventArgs e)
    {
        if (_startupTracked) { return; }
        _startupTracked = true;
        ((FrameworkElement)sender).Loaded -= OnRootLoadedForAnalytics;

        await AnalyticsConsentDialog.MaybeAskAsync(Content.XamlRoot, _theme);
        Analytics.Capture("app_opened");
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

            // Torn-out windows are sized and positioned by the tear-out drag; don't
            // restore or persist the primary window's saved bounds for them.
            if (!_isPrimary)
            {
                return;
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
            if (_appWindow == null || App.ShootMode)
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

    private async void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        Analytics.Capture("settings_opened");
        await SettingsDialog.ShowAsync(Content.XamlRoot);
    }

    private void OnThemeButtonClick(object sender, RoutedEventArgs e)
    {
        _theme = _theme == ElementTheme.Light ? ElementTheme.Dark : ElementTheme.Light;
        ThemeManager.Apply(this, _theme);
        ThemeManager.Save(_theme);
        UpdateThemeIcon();
        Analytics.Capture("theme_changed", new Dictionary<string, object?> { ["theme"] = _theme == ElementTheme.Light ? "light" : "dark" });
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

    /// <summary>Opens a document in a tab (used by the reference graph's click-to-open).</summary>
    public void OpenDocument(string path) => OpenFile(path);

    /// <summary>Shows <paramref name="content"/> as a window-filling overlay. With
    /// <paramref name="dimmed"/> the host is a translucent scrim (so the app shows through behind a
    /// centred panel - the 3D viewer); otherwise it's opaque (the reference graph fullscreen).</summary>
    public void ShowOverlay(UIElement content, bool dimmed = false)
    {
        OverlayHost.Background = dimmed
            ? new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(0xB3, 0, 0, 0))
            : (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SolidBackgroundFillColorBaseBrush"];
        OverlayHost.Children.Clear();
        OverlayHost.Children.Add(content);
        OverlayHost.Visibility = Visibility.Visible;
    }

    public void HideOverlay()
    {
        OverlayHost.Children.Clear();
        OverlayHost.Visibility = Visibility.Collapsed;
    }

    private void OpenFile(string path)
    {
        // if already open, just select that tab
        foreach (TabViewItem t in DocTabs.TabItems.OfType<TabViewItem>())
        {
            if (t.Content is DocumentView dv0 &&
                string.Equals(dv0.FilePath, path, StringComparison.OrdinalIgnoreCase))
            { DocTabs.SelectedItem = t; return; }
        }

        DocumentView dv = new() { StatusSink = SetStatus };
        if (!dv.Load(path))
        {
            Serilog.Log.Warning("Could not open {File}", path);
            return;   // invalid file: status already set, no tab added
        }

        Serilog.Log.Information("Opened {File} ({Kind})", path, dv.Document?.Kind);

        TabViewItem tab = new()
        {
            Header = dv.TabTitle,
            Content = dv,
            IconSource = AppIcons.IconSource(dv.Document?.Kind ?? InventorDocument.DocKind.Unknown)
        };
        ToolTipService.SetToolTip(tab, $"{path}\nCtrl+W to close");
        WireTabContextMenu(tab);
        DocTabs.TabItems.Add(tab);
        DocTabs.SelectedItem = tab;
        UpdateEmptyState();
    }

    // ---------- tab lifecycle ----------
    private void OnTabClose(TabView sender, TabViewTabCloseRequestedEventArgs args)
    {
        DocTabs.TabItems.Remove(args.Tab);
        AfterTabRemoved();
    }

    /// <summary>Ctrl+W closes the selected document tab.</summary>
    private void OnCloseTabShortcut(Microsoft.UI.Xaml.Input.KeyboardAccelerator sender,
        Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args)
    {
        if (DocTabs.SelectedItem is TabViewItem tab)
        {
            DocTabs.TabItems.Remove(tab);
            AfterTabRemoved();
            args.Handled = true;
        }
    }

    private void OnTabSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DocTabs.SelectedItem is TabViewItem { Content: DocumentView { Document: not null } dv })
        {
            StatusText.Text = $"{dv.Document.FileName} - {dv.Document.DocumentType}";
        }
    }

    public void SetStatus(string message) => StatusText.Text = message;

    private void UpdateEmptyState()
    {
        // Only the primary window shows the empty drop-zone; a torn-out window keeps its
        // TabView visible and closes itself once its last tab leaves.
        bool showEmpty = _isPrimary && DocTabs.TabItems.Count == 0;
        EmptyState.Visibility = showEmpty ? Visibility.Visible : Visibility.Collapsed;
        DocTabs.Visibility    = showEmpty ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>After a tab leaves this window: refresh the empty state, and close the
    /// window if it's a torn-out (non-primary) window that now has no tabs.</summary>
    private void AfterTabRemoved()
    {
        UpdateEmptyState();
        if (!_isPrimary && DocTabs.TabItems.Count == 0)
        {
            // Defer so we don't close the window while its own context-menu click is unwinding.
            DispatcherQueue.TryEnqueue(Close);
        }
    }

    // ---------- move a tab to / from its own window ----------
    // Done explicitly via the tab's right-click menu rather than WinUI's CanTearOutTabs
    // drag feature, which access-violates inside Microsoft.UI.Xaml.dll on micro-drags.

    /// <summary>Right-click menu on a tab: open it in a new window, or (in a torn-out
    /// window) move it back to the main window. The menu is built fresh on each request
    /// and bound to the tab's <em>current</em> window, so it survives moving between windows.</summary>
    private void WireTabContextMenu(TabViewItem tab)
    {
        tab.ContextRequested += (_, e) =>
        {
            if (App.GetWindowForElement(tab) is not MainWindow host)
            {
                return;
            }

            MenuFlyout menu = new();
            MenuFlyoutItem newWin = new()
            {
                Text = "Open in new window",
                Icon = new FontIcon { Glyph = ((char)0xE78B).ToString() }
            };
            newWin.Click += (_, _) => host.OpenInNewWindow(tab);
            menu.Items.Add(newWin);

            if (!host._isPrimary)
            {
                MenuFlyoutItem toMain = new()
                {
                    Text = "Move to main window",
                    Icon = new FontIcon { Glyph = ((char)0xE8A7).ToString() }
                };
                toMain.Click += (_, _) => host.MoveToMain(tab);
                menu.Items.Add(toMain);
            }

            if (e.TryGetPosition(tab, out Windows.Foundation.Point p))
            {
                menu.ShowAt(tab, p);
            }
            else
            {
                menu.ShowAt(tab);
            }
            e.Handled = true;
        };
    }

    private void OpenInNewWindow(TabViewItem tab)
    {
        if (!_isPrimary && DocTabs.TabItems.Count <= 1)
        {
            return; // already alone in its own window
        }

        MainWindow torn = new(isPrimary: false);
        ThemeManager.Apply(torn, _theme);

        DocTabs.TabItems.Remove(tab);
        torn.AdoptTab(tab);

        if (_appWindow is { } a)
        {
            torn.AppWindow.Resize(a.Size);
            torn.AppWindow.Move(new PointInt32(a.Position.X + 48, a.Position.Y + 48));
        }
        torn.Activate();
        AfterTabRemoved();
    }

    private void MoveToMain(TabViewItem tab)
    {
        if (App.MainWindowInstance is not MainWindow main || ReferenceEquals(main, this))
        {
            return;
        }

        DocTabs.TabItems.Remove(tab);
        main.AdoptTab(tab);
        main.Activate();
        AfterTabRemoved();
    }

    /// <summary>Takes ownership of a tab (re-homes its status sink, selects it). The tab's
    /// context menu resolves its host window on demand, so it needs no re-wiring here.</summary>
    public void AdoptTab(TabViewItem tab)
    {
        if (tab.Content is DocumentView dv)
        {
            dv.StatusSink = SetStatus;
        }
        DocTabs.TabItems.Add(tab);
        DocTabs.SelectedItem = tab;
        UpdateEmptyState();
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