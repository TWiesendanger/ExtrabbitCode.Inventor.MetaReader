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
    public TimeSpan AutoDismiss = TimeSpan.FromSeconds(12); // hide on its own if left untouched (Zero = never)
    public TeachingTipPlacementMode Placement = TeachingTipPlacementMode.Auto;
}

/// <summary>Shows <see cref="Tip"/>s as a WinUI TeachingTip callout pointed at a target control:
/// honours the global on/off and per-tip "Don't show again", appears after a delay, and auto-hides.</summary>
internal static class TipService
{
    /// <summary>Shows the tip; returns the TeachingTip so the caller can close it early (e.g. when the
    /// target's screen goes away), or null if tips are off / this one was dismissed.</summary>
    public static TeachingTip? Show(Panel host, FrameworkElement target, Tip tip)
    {
        if (!TipSettings.Enabled || TipSettings.IsDismissed(tip.Id)) { return null; }

        TeachingTip teaching = new()
        {
            Target = target,
            Title = tip.Title,
            Subtitle = tip.Message,
            PreferredPlacement = tip.Placement,
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

        // An open TeachingTip keeps repositioning its callout against its Target. If that target (or
        // the host) leaves the visual tree while the tip is still pending or open, the next reposition
        // dereferences a torn-down element and the process fail-fasts (Control Flow Guard, 0xC0000409 -
        // seen in the Store build). So the moment the target or host unloads, fully tear the tip down:
        // stop the timers, clear Target *before* it can reposition against freed memory, close, remove.
        void Teardown(object? _s, RoutedEventArgs? _e)
        {
            delay.Stop();
            auto?.Stop();
            target.Unloaded -= Teardown;
            host.Unloaded -= Teardown;
            teaching.Target = null;
            teaching.IsOpen = false;
            host.Children.Remove(teaching);
        }
        target.Unloaded += Teardown;
        host.Unloaded += Teardown;

        delay.Tick += (_, _) =>
        {
            delay.Stop();
            // only show if the target is still in the tree and visible (tab torn down / screen hidden)
            if (target.XamlRoot == null || target.ActualWidth <= 0 || !host.Children.Contains(teaching))
            {
                Teardown(null, null);
                return;
            }
            teaching.IsOpen = true;
            if (tip.AutoDismiss > TimeSpan.Zero)   // Zero => stay until the user acts
            {
                auto = new DispatcherTimer { Interval = tip.AutoDismiss };
                auto.Tick += (_, _) => { auto.Stop(); teaching.IsOpen = false; };
                auto.Start();
            }
        };
        teaching.Closed += (_, _) => Teardown(null, null);
        delay.Start();
        return teaching;
    }
}
