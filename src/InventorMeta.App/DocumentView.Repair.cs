using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ExtrabbitCode.Inventor.MetaReader.App;

/// <summary>The segment-repair flow: the "Damaged file" badge, the one-time risk warning, the
/// in-place repair with backup, and the post-repair "Open in Inventor" shortcut.</summary>
public sealed partial class DocumentView
{
    /// <summary>True once this view repaired the current file; keeps the "Repaired" chip and its
    /// "Open in Inventor" shortcut visible until another file is loaded into the view.</summary>
    private bool _repairedThisSession;

    /// <summary>Shows the "Damaged file" badge when the file carries stale segment summaries
    /// (with the repair button - that class is fixable in place) or destroyed segment data
    /// (without it - nothing can repair that; the tooltip explains why and points at backups).
    /// Right after this view repaired the file, the "Repaired" chip with an Inventor shortcut
    /// shows instead. The destroyed-data check decompresses every segment, so it runs in the
    /// background and upgrades the badge when it lands.</summary>
    private void SetDamageBadge(InventorDocument doc)
    {
        bool repairable = !doc.IsStep && doc.HasRepairableSegmentDamage;
        DamagePanel.Visibility = repairable ? Visibility.Visible : Visibility.Collapsed;
        RepairButton.Visibility = Visibility.Visible;
        RepairedPanel.Visibility = !repairable && _repairedThisSession ? Visibility.Visible : Visibility.Collapsed;
        if (repairable)
        {
            Serilog.Log.Warning("Repairable segment damage in {File}: {Issues}",
                doc.FileName, string.Join("; ", doc.SegmentIssues));
            StackPanel tip = new() { Spacing = 4, Padding = new Thickness(2), MaxWidth = 420 };
            tip.Children.Add(new TextBlock
            {
                Text = "The segment registry disagrees with the data actually stored - Inventor refuses " +
                       "to open the file (\"the number of objects found in the segment does not match the " +
                       "segment summary\"):",
                TextWrapping = TextWrapping.Wrap
            });
            foreach (SegmentRepair.Issue issue in doc.SegmentIssues)
            {
                string where = issue.Location.Length == 0 ? "" : $"  ({issue.Location})";
                tip.Children.Add(new TextBlock
                {
                    Text = $"•  {issue.SegmentName}{where}: summary says {issue.SummaryCount:N0} blocks, {issue.ActualCount:N0} are present",
                    FontWeight = FontWeights.SemiBold
                });
            }
            tip.Children.Add(new TextBlock
            {
                Text = "Repair updates the summaries in place - no model data is touched, and an untouched " +
                       "backup copy is saved next to the file first.",
                Opacity = 0.7, TextWrapping = TextWrapping.Wrap
            });
            ToolTipService.SetToolTip(DamageBadge, new ToolTip { Content = tip });
        }

        if (!doc.IsStep)
        {
            _ = UpgradeBadgeForDestroyedDataAsync(doc);
        }
    }

    /// <summary>Runs the deep payload check off the UI thread; when it finds destroyed segment
    /// data, the badge switches to the not-repairable presentation: no repair button (a repair
    /// cannot bring destroyed bytes back and would fail), a tooltip that explains the damage and
    /// says what actually helps - restoring a backup.</summary>
    private async Task UpgradeBadgeForDestroyedDataAsync(InventorDocument doc)
    {
        IReadOnlyList<SegmentDataCheck.Damage> damage;
        System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            damage = await Task.Run(() => doc.DataDamage);
        }
        catch (Exception ex)
        {
            Serilog.Log.Debug(ex, "Deep segment data check failed for {File}", doc.FileName);
            return;   // unreadable container -> no verdict, keep the current badge
        }

        Serilog.Log.Debug("Deep segment data check for {File}: {Count} damaged segment(s) in {Ms} ms",
            doc.FileName, damage.Count, sw.ElapsedMilliseconds);
        if (damage.Count == 0 || !ReferenceEquals(Document, doc))
        {
            return;   // healthy, or the view moved on to another file meanwhile
        }

        Serilog.Log.Warning("Destroyed segment data in {File}: {Damage} - not repairable, a backup must be restored",
            doc.FileName, string.Join("; ", damage));

        DamagePanel.Visibility = Visibility.Visible;
        RepairButton.Visibility = Visibility.Collapsed;
        RepairedPanel.Visibility = Visibility.Collapsed;

        StackPanel tip = new() { Spacing = 4, Padding = new Thickness(2), MaxWidth = 440 };
        tip.Children.Add(new TextBlock
        {
            Text = "Parts of this file's internal database are destroyed - runs of zeroed bytes sit " +
                   "where compressed segment data used to be, the typical result of a disk fault or " +
                   "an interrupted copy:",
            TextWrapping = TextWrapping.Wrap
        });
        foreach (SegmentDataCheck.Damage d in damage)
        {
            string where = d.Location.Length == 0 ? "" : $"  ({d.Location})";
            string detail = d.ChecksumOnly
                ? "content silently altered"
                : $"only {FormatSize(d.RecoveredBytes)} of at least {FormatSize(d.ExpectedBytes)} readable";
            tip.Children.Add(new TextBlock
            {
                Text = $"•  {d.SegmentName}{where}: {detail}",
                FontWeight = FontWeights.SemiBold
            });
        }
        tip.Children.Add(new TextBlock
        {
            Text = "No repair is offered because none can work: the destroyed bytes are physically " +
                   "gone, the compressed stream cannot be re-synchronized past the damage, and " +
                   "Inventor needs every segment intact to open a file. Restore an older version " +
                   "instead - Vault or another PDM, the OldVersions folder next to the file, or a backup.",
            Opacity = 0.7, TextWrapping = TextWrapping.Wrap
        });
        ToolTipService.SetToolTip(DamageBadge, new ToolTip { Content = tip });
        StatusSink?.Invoke("Segment data is destroyed in this file - not repairable; restore a backup. Hover the badge for details.");
    }

    /// <summary>Settings key remembering that the user has read and typed away the one-time
    /// repair risk warning on this machine.</summary>
    private const string RepairAckKey = "RepairRiskAcknowledged";
    private const string RepairAckPhrase = "I understand the risk";

    /// <summary>The one-time gate in front of the repair: a prominent warning that the repair
    /// rewrites the file's internal database and is a last resort, acknowledged by typing a fixed
    /// phrase. Returns true when the user typed it (remembered via <see cref="RepairAckKey"/>).</summary>
    private async Task<bool> ConfirmRepairRiskOnceAsync()
    {
        if (AppSettings.GetBool(RepairAckKey, false))
        {
            return true;
        }

        TextBox ackBox = new() { PlaceholderText = RepairAckPhrase, Margin = new Thickness(0, 6, 0, 0) };
        StackPanel content = new() { Spacing = 10, MaxWidth = 480 };
        content.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 10,
            Children =
            {
                new FontIcon
                {
                    Glyph = G(0xE7BA), FontSize = 26,   // warning triangle
                    Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(DamageRed),
                    VerticalAlignment = VerticalAlignment.Center
                },
                new TextBlock
                {
                    Text = "This repair rewrites the file's internal database.",
                    FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap,
                    VerticalAlignment = VerticalAlignment.Center, MaxWidth = 420
                }
            }
        });
        content.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = "MetaReader patches the RSeStorage segment registry inside the Inventor file - a " +
                   "proprietary structure Autodesk offers no supported way to modify. Treat this as a " +
                   "last resort:\n\n" +
                   "•  First try to restore a healthy version instead - Vault or another PDM, the " +
                   "OldVersions folder, or a backup.\n" +
                   "•  An untouched backup copy is saved next to the file before every repair. " +
                   "Keep it until the result is verified.\n" +
                   "•  Always open the repaired file in Autodesk Inventor and check it before you " +
                   "continue working with it."
        });
        content.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Opacity = 0.85,
            Text = $"Type \"{RepairAckPhrase}\" to enable the repair. You are only asked once on this computer."
        });
        content.Children.Add(ackBox);

        ContentDialog dlg = new()
        {
            Title = "Read this before repairing",
            XamlRoot = XamlRoot,
            PrimaryButtonText = "Continue",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            IsPrimaryButtonEnabled = false,
            Content = content,
        };
        ackBox.TextChanged += (_, _) =>
            dlg.IsPrimaryButtonEnabled = string.Equals(ackBox.Text.Trim(), RepairAckPhrase, StringComparison.OrdinalIgnoreCase);

        if (await dlg.ShowAsync() != ContentDialogResult.Primary)
        {
            return false;
        }

        AppSettings.Set(RepairAckKey, "True");
        Analytics.Capture("repair_risk_acknowledged");
        return true;
    }

    private async void OnRepairSegments(object sender, RoutedEventArgs e)
    {
        if (Document == null || !Document.HasRepairableSegmentDamage || !RepairButton.IsEnabled)
        {
            return;
        }

        // Disabled for the whole flow: a queued second click would otherwise open a second
        // ContentDialog on this XamlRoot, which WinUI answers with an unhandled COMException.
        RepairButton.IsEnabled = false;
        try
        {
            if (!await ConfirmRepairRiskOnceAsync())
            {
                return;
            }

            string names = string.Join(", ", Document.SegmentIssues.Select(i => i.SegmentName).Distinct());
            ContentDialog dlg = new()
            {
                Title = "Repair the segment database?",
                XamlRoot = XamlRoot,
                PrimaryButtonText = "Repair",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                Content = new TextBlock
                {
                    Text = $"{Document.SegmentIssues.Count} stale segment summar{(Document.SegmentIssues.Count == 1 ? "y" : "ies")} " +
                           $"({names}) will be patched in place so Inventor opens the file again.\n\n" +
                           "No model data is changed. An untouched backup copy is saved next to the file first.",
                    TextWrapping = TextWrapping.Wrap
                },
            };
            if (await dlg.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }

            // Repair rescans the file fresh - when someone fixed it since this view loaded,
            // nothing is patched, no backup exists, and the UI must not claim otherwise.
            Serilog.Log.Information("Repairing segment summaries in {File}", FilePath);
            SegmentRepair.RepairResult result = SegmentRepair.Repair(FilePath);
            if (result.Repaired.Count == 0)
            {
                StatusSink?.Invoke("Nothing to repair - the file is already consistent (fixed outside this view?).");
            }
            else
            {
                StatusSink?.Invoke($"Repaired {result.Repaired.Count} segment summar{(result.Repaired.Count == 1 ? "y" : "ies")}; " +
                                   $"backup saved as {Path.GetFileName(result.BackupPath)}.");
                Analytics.Capture("segments_repaired", new Dictionary<string, object?>
                {
                    ["segments"] = result.Repaired.Count,
                    ["doc_kind"] = Document.Kind.ToString(),
                });
                _repairedThisSession = true;   // Refresh -> SetDamageBadge swaps in the "Repaired" chip
            }
            Refresh();
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "Repair failed for {File}", FilePath);
            StatusSink?.Invoke("Repair failed: " + ex.Message);
        }
        finally
        {
            RepairButton.IsEnabled = true;
        }
    }

    /// <summary>Hands the (just repaired) file to Autodesk Inventor - a running session when one is
    /// up, otherwise the preferred release is launched - so the fix can be verified immediately.</summary>
    private async void OnOpenInInventor(object sender, RoutedEventArgs e)
    {
        if (HostWindow is not MainWindow win || !OpenInInventorButton.IsEnabled)
        {
            return;
        }

        // Disabled before the first await: ResolveInventorAsync can show the version-chooser
        // dialog, and a double-click reaching a second ShowAsync would crash the app.
        OpenInInventorButton.IsEnabled = false;
        try
        {
            InventorInstall? inventor = await ResolveInventorAsync(win);
            if (inventor == null)
            {
                StatusSink?.Invoke("No Autodesk Inventor installation was found on this machine.");
                return;
            }

            StatusSink?.Invoke($"Opening {Path.GetFileName(FilePath)} in {inventor.DisplayName}…");
            string? error = await InventorOpener.OpenAsync(inventor, FilePath);
            StatusSink?.Invoke(error == null
                ? $"{Path.GetFileName(FilePath)} is open in Inventor."
                : "Inventor could not open the file: " + error);
            Analytics.Capture("opened_in_inventor", new Dictionary<string, object?>
            {
                ["after_repair"] = _repairedThisSession,
                ["succeeded"] = error == null,
            });
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "Open in Inventor failed for {File}", FilePath);
            StatusSink?.Invoke("Could not open the file in Inventor: " + ex.Message);
        }
        finally
        {
            OpenInInventorButton.IsEnabled = true;
        }
    }

}
