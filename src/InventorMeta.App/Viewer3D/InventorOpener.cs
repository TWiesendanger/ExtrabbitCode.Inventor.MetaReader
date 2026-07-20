using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace ExtrabbitCode.Inventor.MetaReader.App;

/// <summary>
/// Opens a document in Autodesk Inventor itself - into a running session when one is up (any
/// release), otherwise by launching the given install. Used after a segment repair so the result
/// can be verified immediately in the real application. Late-bound COM on a dedicated STA thread,
/// like <see cref="SvfGenerator"/>; the session is made visible and brought to the front BEFORE
/// the open, so any prompt Inventor raises (e.g. Resolve Link) is right in front of the user.
/// </summary>
internal static class InventorOpener
{
    /// <summary>Open <paramref name="filePath"/> in Inventor. Returns null on success, or a
    /// user-showable error message. Never closes or hides an Inventor session.</summary>
    public static Task<string?> OpenAsync(InventorInstall inventor, string filePath)
    {
        TaskCompletionSource<string?> tcs = new();
        Thread t = new(() =>
        {
            object? app = null;
            using IDisposable? filter = SvfGenerator.OleMessageFilter.Register(
                m => Serilog.Log.Information("Open in Inventor: {Step}", m));
            try
            {
                (app, bool startedByUs) = SvfGenerator.ConnectOrLaunch(inventor);
                dynamic inv = app;
                inv.Visible = true;
                TryFocus((IntPtr)(long)inv.MainFrameHWND);
                _ = inv.Documents.Open(filePath, true);
                tcs.SetResult(null);
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning("Open in Inventor failed (0x{HResult:X8}): {Error}", ex.HResult, ex.Message);
                tcs.SetResult(SvfGenerator.FriendlyError(ex));
            }
            finally
            {
                if (app != null) { try { Marshal.ReleaseComObject(app); } catch { /* teardown */ } }
            }
        }) { IsBackground = true };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        return tcs.Task;
    }

    private static void TryFocus(IntPtr hwnd)
    {
        try { if (hwnd != IntPtr.Zero) { SetForegroundWindow(hwnd); } } catch { /* focus is best-effort */ }
    }

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
}
