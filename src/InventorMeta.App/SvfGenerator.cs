using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;

namespace ExtrabbitCode.Inventor.MetaReader.App;

/// <summary>
/// Generates an SVF viewable for a model by driving Inventor's SVF/collaboration translator add-in
/// over COM (the same SaveCopyAs the sample iLogic macro uses). Late-bound so it works against any
/// installed Inventor release without a version-specific interop reference. Requires Inventor
/// installed; viewing an already-cached SVF does not.
/// </summary>
public static class SvfGenerator
{
    // The SVF / "collaboration" translator add-in, and the file-browse IO mechanism (13059).
    private const string SvfTranslatorAddInId = "{C200B99B-B7DD-4114-A5E9-6557AB5ED8EC}";
    private const int FileBrowseIOMechanism = 13059;

    public sealed record Result(bool Ok, string? BubblePath, string? Error);

    /// <summary>Translates <paramref name="filePath"/> to SVF in <paramref name="outputBaseDir"/> using
    /// <paramref name="inventor"/>. Long-running (opens the model in Inventor) - call off the UI thread.
    /// When <paramref name="hideWhenStarted"/> is set, an Inventor instance we launch ourselves is kept
    /// hidden (a session the user already had open is never touched). When <paramref name="silent"/> is
    /// set, Inventor's dialog prompts are suppressed for the duration.</summary>
    public static Result Generate(InventorInstall inventor, string filePath, string outputBaseDir,
        bool hideWhenStarted = true, bool silent = true, Action<string>? log = null)
    {
        void L(string m) => log?.Invoke(m);
        // Fresh entry each run - the translator creates its own output\ subfolder under here.
        try { if (Directory.Exists(outputBaseDir)) { Directory.Delete(outputBaseDir, recursive: true); } } catch { /* will overwrite */ }
        Directory.CreateDirectory(outputBaseDir);

        object? inv = null;
        bool startedByUs = false;
        bool? prevSilent = null;   // previous SilentOperation, restored on a borrowed session
        IDisposable? filter = null;
        try
        {
            // A running Inventor that's momentarily busy (mid-operation or showing a dialog) rejects
            // incoming COM calls; without a message filter that surfaces instantly as
            // RPC_E_CALL_REJECTED. The filter retries the busy call instead of failing.
            filter = OleMessageFilter.Register(L);
            L("Connecting to Inventor…");
            (inv, startedByUs) = ConnectOrLaunch(inventor);
            L(startedByUs ? "Launched a new Inventor instance." : "Attached to a running Inventor instance.");
            dynamic app = inv;

            // Suppress Inventor's dialog prompts during the unattended translation; remember the prior
            // value so a session the user already had open is restored afterward.
            if (silent)
            {
                try { prevSilent = (bool)app.SilentOperation; app.SilentOperation = true; L("SilentOperation = true"); }
                catch { L("Couldn't set SilentOperation."); }
            }

            // Only hide an instance we launched ourselves - never change a running session's visibility.
            if (startedByUs && hideWhenStarted)
            {
                try { app.Visible = false; L("Inventor hidden (launched by us)."); }
                catch { L("Couldn't hide Inventor."); }
            }

            dynamic addin = app.ApplicationAddIns.ItemById(SvfTranslatorAddInId);
            try { L($"Add-in: '{addin.DisplayName}', activated={addin.Activated}"); }
            catch { L("Add-in resolved (couldn't read DisplayName/Activated)."); }

            dynamic doc = app.Documents.Open(filePath, /* OpenVisible */ false);
            try
            {
                try { L($"Opened document: {doc.DisplayName}"); } catch { /* ignore */ }

                dynamic to = app.TransientObjects;
                dynamic context = to.CreateTranslationContext();
                context.Type = FileBrowseIOMechanism;

                dynamic options = to.CreateNameValueMap();
                dynamic data = to.CreateDataMedium();
                data.FileName = Path.Combine(outputBaseDir, "result.collaboration");

                bool hasOpts = (bool)addin.HasSaveCopyAsOptions(doc, context, options);
                L($"HasSaveCopyAsOptions: {hasOpts}");
                if (hasOpts)
                {
                    SetValue(options, "EnableExpressTranslation", false);
                    SetValue(options, "SVFFileOutputDir", outputBaseDir);
                    SetValue(options, "ExportFileProperties", true);
                    SetValue(options, "ObfuscateLabels", false);
                }

                L("Running SaveCopyAs…");
                addin.SaveCopyAs(doc, context, options, data);
                L("SaveCopyAs returned.");
            }
            finally
            {
                try { doc.Close(/* SkipSave */ true); } catch { /* best effort */ }
            }

            // The translator writes <base>\output\bubble.json + resources under <base>\output\1\,
            // and the manifest references them as "$file$/output/...". The LMV viewer sets $file$ to
            // the folder of the loaded manifest, so we copy bubble.json up to the <base> root - then
            // loading <base>\bubble.json makes $file$ = <base> and "$file$/output/..." resolves.
            string? produced = Directory.Exists(outputBaseDir)
                ? Directory.EnumerateFiles(outputBaseDir, "bubble.json", SearchOption.AllDirectories).FirstOrDefault()
                : null;
            if (produced == null)
            {
                return new Result(false, null, $"Translation finished but no bubble.json was found under {outputBaseDir}.");
            }

            string rootBubble = Path.Combine(outputBaseDir, "bubble.json");
            try
            {
                if (!string.Equals(produced, rootBubble, StringComparison.OrdinalIgnoreCase))
                {
                    File.Copy(produced, rootBubble, overwrite: true);
                }
            }
            catch (Exception ex)
            {
                return new Result(false, null, "Couldn't place the manifest at the entry root: " + ex.Message);
            }

            return new Result(true, rootBubble, null);
        }
        catch (Exception ex)
        {
            // Log the raw HRESULT + type so a failure is diagnosable; show the user a friendlier line.
            L($"FAILED: {ex.GetType().Name} 0x{ex.HResult:X8} - {ex.Message}");
            return new Result(false, null, FriendlyError(ex));
        }
        finally
        {
            if (inv != null)
            {
                // Restore dialog suppression on a borrowed session (an instance we started is about to
                // quit, so there's nothing to put back).
                if (!startedByUs && prevSilent.HasValue)
                {
                    try { ((dynamic)inv).SilentOperation = prevSilent.Value; } catch { /* ignore */ }
                }
                if (startedByUs)
                {
                    try { ((dynamic)inv).Quit(); } catch { /* leave it running if it refuses */ }
                }
                try { Marshal.FinalReleaseComObject(inv); } catch { /* ignore */ }
            }
            filter?.Dispose();   // revoke the message filter last, so it still covers Quit / release above
        }
    }

    /// <summary>Sets a NameValueMap entry (a COM parameterized property) by late binding.</summary>
    private static void SetValue(object nameValueMap, string name, object value) =>
        nameValueMap.GetType().InvokeMember("Value", BindingFlags.SetProperty, null, nameValueMap, [name, value]);

    // HRESULTs a busy/modal Inventor throws at an incoming COM call.
    private const int RPC_E_CALL_REJECTED = unchecked((int)0x80010001);
    private const int RPC_E_SERVERCALL_RETRYLATER = unchecked((int)0x8001010A);
    private const int RPC_E_SERVERCALL_REJECTED = unchecked((int)0x80010004);

    /// <summary>Turns a COM failure into a message worth showing the user; the raw HRESULT is logged
    /// separately for diagnosis. (Shared with <see cref="InventorOpener"/>.)</summary>
    internal static string FriendlyError(Exception ex) => ex.HResult switch
    {
        RPC_E_CALL_REJECTED or RPC_E_SERVERCALL_RETRYLATER or RPC_E_SERVERCALL_REJECTED =>
            "Inventor was busy and didn't respond in time. Close any open dialog in Inventor (or close "
            + "that Inventor session so MetaReader can start its own), then try again.",
        _ => ex.Message,
    };

    /// <summary>Uses a running Inventor if one is up; otherwise launches the selected release and
    /// waits for it to register as a COM server. (Shared with <see cref="InventorOpener"/>.)</summary>
    internal static (object app, bool startedByUs) ConnectOrLaunch(InventorInstall inventor)
    {
        if (TryGetActiveInventor(out object? running)) { return (running!, false); }

        Process.Start(new ProcessStartInfo(inventor.ExePath) { UseShellExecute = true });
        for (int i = 0; i < 150; i++)   // up to ~2.5 min for a cold Inventor start
        {
            Thread.Sleep(1000);
            if (TryGetActiveInventor(out object? app)) { return (app!, true); }
        }

        throw new TimeoutException($"Couldn't connect to {inventor.DisplayName} after launching it.");
    }

    private static bool TryGetActiveInventor(out object? app)
    {
        app = null;
        try
        {
            CLSIDFromProgID("Inventor.Application", out Guid clsid);
            GetActiveObject(ref clsid, IntPtr.Zero, out object obj);
            app = obj;
            return true;
        }
        catch { return false; }
    }

    // Marshal.GetActiveObject was dropped in modern .NET, so bind the OLE entry points directly.
    [DllImport("ole32.dll", PreserveSig = false)]
    private static extern void CLSIDFromProgID([MarshalAs(UnmanagedType.LPWStr)] string progId, out Guid clsid);

    [DllImport("oleaut32.dll", PreserveSig = false)]
    private static extern void GetActiveObject(ref Guid rclsid, IntPtr pvReserved,
        [MarshalAs(UnmanagedType.IUnknown)] out object ppunk);

    [DllImport("ole32.dll")]
    private static extern int CoRegisterMessageFilter(IOleMessageFilter? newFilter, out IOleMessageFilter? oldFilter);

    // The classic OLE IMessageFilter (KB316353): registered on the STA thread that drives Inventor so
    // that a call rejected by a busy Inventor is retried instead of failing instantly.
    [ComImport, Guid("00000016-0000-0000-C000-000000000046"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IOleMessageFilter
    {
        [PreserveSig] int HandleInComingCall(int dwCallType, IntPtr hTaskCaller, int dwTickCount, IntPtr lpInterfaceInfo);
        [PreserveSig] int RetryRejectedCall(IntPtr hTaskCallee, int dwTickCount, int dwRejectType);
        [PreserveSig] int MessagePending(IntPtr hTaskCallee, int dwTickCount, int dwPendingType);
    }

    internal sealed class OleMessageFilter : IOleMessageFilter
    {
        private const int SERVERCALL_RETRYLATER = 2;   // dwRejectType values
        private const int SERVERCALL_REJECTED = 1;
        private const int PENDINGMSG_WAITDEFPROCESS = 2;
        private const int RetryBudgetMs = 60_000;      // keep retrying a busy Inventor for up to a minute

        /// <summary>Registers the filter on the current STA thread; returns a token that revokes it
        /// (restoring any previous filter) on dispose, or null if it couldn't be registered.</summary>
        public static IDisposable? Register(Action<string> log)
        {
            if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
            {
                log("No COM message filter (thread isn't STA).");
                return null;
            }
            int hr = CoRegisterMessageFilter(new OleMessageFilter(), out IOleMessageFilter? previous);
            if (hr < 0)
            {
                log($"CoRegisterMessageFilter failed (0x{hr:X8}).");
                return null;
            }
            log("COM message filter registered (retries a busy Inventor).");
            return new Revoker(previous);
        }

        private sealed class Revoker(IOleMessageFilter? previous) : IDisposable
        {
            public void Dispose() { try { CoRegisterMessageFilter(previous, out _); } catch { /* revoking */ } }
        }

        // Accept incoming calls (SERVERCALL_ISHANDLED).
        public int HandleInComingCall(int dwCallType, IntPtr hTaskCaller, int dwTickCount, IntPtr lpInterfaceInfo) => 0;

        // Retry a rejected/busy call for up to a minute, then cancel (>=100 => wait that many ms and retry;
        // -1 => cancel, so the COM call throws).
        public int RetryRejectedCall(IntPtr hTaskCallee, int dwTickCount, int dwRejectType) =>
            dwRejectType is SERVERCALL_RETRYLATER or SERVERCALL_REJECTED && dwTickCount < RetryBudgetMs ? 200 : -1;

        // While we're waiting on an outgoing call, let the message pump keep running.
        public int MessagePending(IntPtr hTaskCallee, int dwTickCount, int dwPendingType) => PENDINGMSG_WAITDEFPROCESS;
    }
}
