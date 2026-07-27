using System;
using System.Collections.Generic;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace ExtrabbitCode.Inventor.MetaReader.App.Common;

/// <summary>Light/dark theme: apply to the window, theme the caption buttons, and persist the choice.</summary>
internal static class ThemeManager
{
    public static ElementTheme Load() =>
        Enum.TryParse(AppSettings.Get("theme"), out ElementTheme t) ? t : ElementTheme.Dark;

    public static void Save(ElementTheme theme) => AppSettings.Set("theme", theme.ToString());

    /// <summary>Resolves a Fluent theme brush against a specific <paramref name="theme"/> instead of the
    /// app-level (system) theme. Surfaces built in code-behind read theme brushes from
    /// <c>Application.Current.Resources</c>, which resolves against the app/system theme - so on a
    /// machine whose system theme differs from the app's toggle, those surfaces render in the wrong
    /// theme. Pass the window's or element's theme here to keep them consistent.</summary>
    public static Brush ThemeBrush(string key, ElementTheme theme)
    {
        string tk = theme == ElementTheme.Light ? "Light" : "Default";   // the "Default" dictionary is dark
        foreach (ResourceDictionary d in FlattenDicts(Application.Current.Resources))
        {
            if (d.ThemeDictionaries.TryGetValue(tk, out object? o) && o is ResourceDictionary td &&
                td.TryGetValue(key, out object? v))
            {
                if (v is Brush b) { return b; }
                if (v is Color c) { return new SolidColorBrush(c); }
            }
        }
        return (Brush)Application.Current.Resources[key];   // fall back to the app-theme brush
    }

    private static IEnumerable<ResourceDictionary> FlattenDicts(ResourceDictionary d)
    {
        yield return d;
        foreach (ResourceDictionary m in d.MergedDictionaries)
        {
            foreach (ResourceDictionary x in FlattenDicts(m)) { yield return x; }
        }
    }

    public static void Apply(Window window, ElementTheme theme)
    {
        if (window.Content is FrameworkElement fe)
        {
            fe.RequestedTheme = theme;
        }

        // recolor the system caption buttons (min/max/close) so they stay legible
        try
        {
            IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            WindowId id = Win32Interop.GetWindowIdFromWindow(hwnd);
            AppWindowTitleBar? bar = AppWindow.GetFromWindowId(id).TitleBar;
            bool dark = theme != ElementTheme.Light;
            Color fg = dark ? Colors.White : Color.FromArgb(255, 32, 32, 32);
            Color hover = dark ? Color.FromArgb(20, 255, 255, 255) : Color.FromArgb(20, 0, 0, 0);
            bar.ButtonBackgroundColor = Colors.Transparent;
            bar.ButtonInactiveBackgroundColor = Colors.Transparent;
            bar.ButtonForegroundColor = fg;
            bar.ButtonHoverForegroundColor = fg;
            bar.ButtonHoverBackgroundColor = hover;
            bar.ButtonPressedForegroundColor = fg;
        }
        catch { /* caption theming is cosmetic */ }
    }
}