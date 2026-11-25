using System.Text;

namespace Share.Core;

public static class Names
{
    private const string BadChars = "<>:\"|?*";
    private const int MaxNameBytes = 200;

    private static readonly HashSet<string> Reserved = new(
        new[] { "CON", "PRN", "AUX", "NUL" }.Concat(Enumerable.Range(1, 9).SelectMany(i => new[] { $"COM{i}", $"LPT{i}" })),
        StringComparer.OrdinalIgnoreCase);

    /// <summary>Reduces a sender-supplied name to a safe base name (same rules as the Go and Kotlin clients).</summary>
    public static string SafeFileName(string name)
    {
        var s = name[(name.LastIndexOfAny(['/', '\\']) + 1)..];
        s = string.Concat(s.Select(c => c < 32 || BadChars.Contains(c) ? '_' : c));
        s = s.Trim().TrimEnd('.', ' ');
        if (s.Length == 0) return "file";
        if (Reserved.Contains(s.Split('.')[0])) s = "_" + s;
        if (Encoding.UTF8.GetByteCount(s) > MaxNameBytes)
        {
            var ext = Extension(s);
            if (Encoding.UTF8.GetByteCount(ext) > 20) ext = "";
            s = TruncateUtf8(s, MaxNameBytes - Encoding.UTF8.GetByteCount(ext)) + ext;
        }
        return s;
    }

    /// <summary>name, or "stem (n).ext" if it is taken.</summary>
    public static string UniqueName(string name, Func<string, bool> exists)
    {
        if (!exists(name)) return name;
        var ext = Extension(name);
        var stem = name[..^ext.Length];
        for (var i = 1; ; i++)
        {
            var candidate = $"{stem} ({i}){ext}";
            if (!exists(candidate)) return candidate;
        }
    }

    /// <summary>Extension including the dot; empty for names without one and for dot files.</summary>
    private static string Extension(string s)
    {
        var i = s.LastIndexOf('.');
        return i <= 0 ? "" : s[i..];
    }

    private static string TruncateUtf8(string s, int max)
    {
        var bytes = 0;
        var chars = 0;
        foreach (var r in s.EnumerateRunes())
        {
            if (bytes + r.Utf8SequenceLength > max) break;
            bytes += r.Utf8SequenceLength;
            chars += r.Utf16SequenceLength;
        }
        return s[..chars];
    }
}
