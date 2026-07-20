using System;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.UI;

namespace ExtrabbitCode.Inventor.MetaReader.App.Common;

/// <summary>Light/dark theme: apply to the window, theme the caption buttons, and persist the choice.</summary>
internal static class ThemeManager
{
    public static ElementTheme Load() =>
        Enum.TryParse(AppSettings.Get("theme"), out ElementTheme t) ? t : ElementTheme.Dark;

    public static void Save(ElementTheme theme) => AppSettings.Set("theme", theme.ToString());

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