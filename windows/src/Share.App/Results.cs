using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Share.Core;

namespace Share.App;

/// <summary>What can be done with a finished download.</summary>
public sealed partial class Results(AppHost host) : IOpener
{
    // beyond what Windows itself considers risky
    private static readonly HashSet<string> Risky = new(
        [".exe", ".com", ".scr", ".bat", ".cmd", ".ps1", ".psm1", ".msi", ".msp", ".msix", ".msixbundle", ".appx", ".appxbundle", ".appinstaller", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh", ".hta", ".lnk", ".url", ".reg", ".jar", ".py", ".pyw", ".cpl", ".inf", ".scf", ".library-ms", ".search-ms", ".settingcontent-ms", ".iso", ".img", ".vhd", ".vhdx", ".chm", ".application", ".gadget", ".pif", ".diagcab"],
        StringComparer.OrdinalIgnoreCase);

    /// <summary>Programs and the like are shown in the folder, never started from here.</summary>
    public static bool IsDangerous(Download d) => d.Refs.Any(IsDangerous);

    internal static bool IsDangerous(string path)
    {
        var ext = System.IO.Path.GetExtension(path);
        return Risky.Contains(ext) || (ext.Length > 0 && AssocIsDangerous(ext));
    }

    [LibraryImport("shlwapi.dll", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AssocIsDangerous(string assoc);

    public void Open(Download d)
    {
        if (d.Refs.Count == 0) return;
        if (d.Refs.Count > 1 || IsDangerous(d)) ShowInFolder(d.Refs[0]);
        else Start(new ProcessStartInfo(d.Refs[0]) { UseShellExecute = true }, () => ShowInFolder(d.Refs[0]));
    }

    public void OpenLink(string url)
    {
        if (Format.IsWebLink(url)) Start(new ProcessStartInfo(url) { UseShellExecute = true }, () => host.Tell("Link kann nicht geöffnet werden"));
    }

    private void ShowInFolder(string path) =>
        Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\""), () => host.Tell("Datei nicht gefunden"));

    private static void Start(ProcessStartInfo psi, Action onError)
    {
        try
        {
            Process.Start(psi)?.Dispose();
        }
        catch (Exception e) when (e is Win32Exception or FileNotFoundException or InvalidOperationException)
        {
            Trace.TraceWarning("cannot open: " + e.Message);
            onError();
        }
    }
}
