using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Windows;

namespace Share.App;

public static class Program
{
    [STAThread]
    public static int Main(string[] argv)
    {
        var args = StartArgs.Parse(argv);
        var data = args.DataDir ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Share");
        using var ipc = new Ipc(InstanceName(data));
        if (!ipc.IsFirst)
        {
            if (args.Command is null) return 0; // started in the background, but already running
            Native.AllowSetForegroundWindow(Native.AnyProcess);
            if (ipc.TrySend(args.Command)) return 0;
            MessageBox.Show("Share läuft bereits, reagiert aber nicht. Beende es im Task-Manager und versuche es noch einmal.", "Share", MessageBoxButton.OK, MessageBoxImage.Warning);
            return 1;
        }
        Log(data);
        return new ShareApplication(args, ipc, data).Run();
    }

    /// <summary>One instance per user and data folder.</summary>
    internal static string InstanceName(string dataDir)
    {
        var sid = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
        return "Share-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sid + "|" + Path.GetFullPath(dataDir).ToUpperInvariant())))[..24];
    }

    /// <summary>A small log for troubleshooting; never contains tokens or contents.</summary>
    private static void Log(string data)
    {
        try
        {
            Directory.CreateDirectory(data);
            var file = Path.Combine(data, "share.log");
            if (File.Exists(file) && new FileInfo(file).Length > 1 << 20) File.Delete(file);
            Trace.Listeners.Add(new TextWriterTraceListener(new StreamWriter(new FileStream(file, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true }) { TraceOutputOptions = TraceOptions.DateTime });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // runs without a log
        }
    }
}
