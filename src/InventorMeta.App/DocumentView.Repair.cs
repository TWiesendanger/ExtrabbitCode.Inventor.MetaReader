using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace ExtrabbitCode.Inventor.MetaReader.App;

/// <summary>The segment-repair flow: the "Damaged file" badge, the one-time risk warning, the
/// in-place repair with backup, and the post-repair "Open in Inventor" shortcut.</summary>
public sealed partial class DocumentView
{
    /// <summary>True once this view repaired the current file; keeps the "Repaired" chip and its
    /// "Open in Inventor" shortcut visible until another file is loaded into the view.</summary>
    private bool _repairedThisSession;

    /// <summary>Shows the "Damaged file" badge + repair button when the file carries stale segment
    /// summaries (the damage that makes Inventor refuse to open it while MetaReader reads on) -
    /// or, right after this view repaired the file, the "Repaired" chip with an Inventor shortcut.</summary>
    private void SetDamageBadge(InventorDocument doc)
    {
        bool damaged = !doc.IsStep && doc.HasRepairableSegmentDamage;
        DamagePanel.Visibility = damaged ? Visibility.Visible : Visibility.Collapsed;
        RepairedPanel.Visibility = !damaged && _repairedThisSession ? Visibility.Visible : Visibility.Collapsed;
        if (!damaged)
        {
            return;
        }

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
            StatusSink?.Invoke("Could not open the file in Inventor: " + ex.Message);
        }
        finally
        {
            OpenInInventorButton.IsEnabled = true;
        }
    }

}
