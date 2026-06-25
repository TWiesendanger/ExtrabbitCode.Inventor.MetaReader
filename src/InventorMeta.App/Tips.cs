using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ExtrabbitCode.Inventor.MetaReader.App;

/// <summary>A single discoverability tip: a short message that points at a control, optionally with
/// an action button that performs the very thing it's hinting at.</summary>
internal sealed class Tip
{
    public required string Id;                              // stable key for "Don't show again"
    public required string Title;
    public required string Message;
    public string? ActionText;                             // null = no action button
    public Action? Action;
    public int Glyph = 0xEA80;                             // Segoe Fluent "Lightbulb"
    public TimeSpan Delay = TimeSpan.FromSeconds(2.5);     // wait before showing, so it isn't jarring
    public TimeSpan AutoDismiss = TimeSpan.FromSeconds(12); // hide on its own if left untouched
}

/// <summary>Shows <see cref="Tip"/>s as a WinUI TeachingTip callout pointed at a target control:
/// honours the global on/off and per-tip "Don't show again", appears after a delay, and auto-hides.</summary>
internal static class TipService
{
    public static void Show(Panel host, FrameworkElement target, Tip tip)
    {
        if (!TipSettings.Enabled || TipSettings.IsDismissed(tip.Id)) { return; }

        TeachingTip teaching = new()
        {
            Target = target,
            Title = tip.Title,
            Subtitle = tip.Message,
            PreferredPlacement = TeachingTipPlacementMode.Auto,
            IsLightDismissEnabled = true,
            IconSource = new FontIconSource { Glyph = ((char)tip.Glyph).ToString() }
        };
        if (tip.ActionText != null)
        {
            teaching.ActionButtonContent = tip.ActionText;
            teaching.ActionButtonClick += (s, _) => { tip.Action?.Invoke(); TipSettings.Dismiss(tip.Id); s.IsOpen = false; };
        }
        teaching.CloseButtonContent = "Don't show again";
        teaching.CloseButtonClick += (s, _) => { TipSettings.Dismiss(tip.Id); s.IsOpen = false; };

        host.Children.Add(teaching);

        DispatcherTimer? auto = null;
        DispatcherTimer delay = new() { Interval = tip.Delay };
        delay.Tick += (_, _) =>
        {
            delay.Stop();
            // only show if the target is still in the tree (tab not torn down / doc not reloaded)
            if (target.XamlRoot == null || !host.Children.Contains(teaching)) { host.Children.Remove(teaching); return; }
            teaching.IsOpen = true;
            auto = new DispatcherTimer { Interval = tip.AutoDismiss };   // auto-hide if left untouched
            auto.Tick += (_, _) => { auto.Stop(); teaching.IsOpen = false; };
            auto.Start();
        };
        teaching.Closed += (_, _) => { auto?.Stop(); host.Children.Remove(teaching); };
        delay.Start();
    }
}
