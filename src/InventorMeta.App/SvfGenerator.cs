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
    /// <paramref name="inventor"/>. Long-running (opens the model in Inventor) - call off the UI thread.</summary>
    public static Result Generate(InventorInstall inventor, string filePath, string outputBaseDir, Action<string>? log = null)
    {
        void L(string m) => log?.Invoke(m);
        // Fresh entry each run - the translator creates its own output\ subfolder under here.
        try { if (Directory.Exists(outputBaseDir)) { Directory.Delete(outputBaseDir, recursive: true); } } catch { /* will overwrite */ }
        Directory.CreateDirectory(outputBaseDir);

        object? inv = null;
        bool startedByUs = false;
        try
        {
            L("Connecting to Inventor…");
            (inv, startedByUs) = ConnectOrLaunch(inventor);
            L(startedByUs ? "Launched a new Inventor instance." : "Attached to a running Inventor instance.");
            dynamic app = inv;

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
            return new Result(false, null, ex.Message);
        }
        finally
        {
            if (inv != null)
            {
                if (startedByUs)
                {
                    try { ((dynamic)inv).Quit(); } catch { /* leave it running if it refuses */ }
                }
                try { Marshal.FinalReleaseComObject(inv); } catch { /* ignore */ }
            }
        }
    }

    /// <summary>Sets a NameValueMap entry (a COM parameterized property) by late binding.</summary>
    private static void SetValue(object nameValueMap, string name, object value) =>
        nameValueMap.GetType().InvokeMember("Value", BindingFlags.SetProperty, null, nameValueMap, [name, value]);

    /// <summary>Uses a running Inventor if one is up; otherwise launches the selected release and
    /// waits for it to register as a COM server.</summary>
    private static (object app, bool startedByUs) ConnectOrLaunch(InventorInstall inventor)
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
}
