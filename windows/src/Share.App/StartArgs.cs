using System.IO;

namespace Share.App;

/// <summary>Command line: [--background] [--data-dir dir] [--allow-local-http] [--send files… | files… | share://pair?…]</summary>
public sealed record StartArgs(Command? Command, bool Background, string? DataDir, bool AllowLocalHttp)
{
    public static StartArgs Parse(string[] args)
    {
        var background = false;
        var allowHttp = false;
        string? dataDir = null;
        var i = 0;
        for (; i < args.Length; i++)
        {
            if (args[i] is "--background" or "-ToastActivated" or "-Embedding") background = true;
            else if (args[i] == "--allow-local-http") allowHttp = true;
            else if (args[i] == "--data-dir") dataDir = ++i < args.Length ? args[i] : null;
            else break;
        }
        var rest = args.Skip(i).ToList();
        if (rest.Count > 0 && rest[0] == "--send") rest.RemoveAt(0); // everything behind it is a file, whatever it looks like
        Command? command;
        if (rest.Count == 0) command = background ? null : new Command.Show();
        else if (rest[0].StartsWith("share:", StringComparison.OrdinalIgnoreCase)) command = Core.PairUri.Parse(rest[0]) is null ? new Command.Show() : new Command.Pair(rest[0]);
        else command = new Command.Send([.. rest.Select(Full)]);
        return new StartArgs(command, background, dataDir, allowHttp);
    }

    private static string Full(string path)
    {
        try
        {
            return path.Length == 0 ? path : Path.GetFullPath(path);
        }
        catch (Exception e) when (e is ArgumentException or IOException or NotSupportedException)
        {
            return path; // reported when it is opened
        }
    }
}
