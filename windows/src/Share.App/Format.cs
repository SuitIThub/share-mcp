using System.Globalization;
using System.Text.RegularExpressions;
using Share.Core;

namespace Share.App;

/// <summary>Texts shared by notifications and windows.</summary>
public static partial class Format
{
    private static readonly CultureInfo German = CultureInfo.GetCultureInfo("de-DE");

    public static string Size(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        string[] units = ["KB", "MB", "GB", "TB"];
        var v = bytes / 1024.0;
        var i = 0;
        for (; v >= 1023.95 && i < units.Length - 1; i++) v /= 1024;
        return string.Format(German, "{0:F1} {1}", v, units[i]);
    }

    public static string Remaining(DateTimeOffset expires, DateTimeOffset now)
    {
        var s = (long)Math.Floor((expires - now).TotalSeconds);
        return s <= 0 ? "abgelaufen" : s <= 60 ? $"noch {s} s" : $"noch {(s + 59) / 60} Min";
    }

    public static string OfferTitle(Transfer t, string sender) => t.Kind switch
    {
        Kind.Text => $"Text von {sender}",
        Kind.Link => $"Link von {sender}",
        _ => t.Files.Count == 1 ? $"Datei von {sender}" : $"{t.Files.Count} Dateien von {sender}",
    };

    public static string OfferBody(Transfer t)
    {
        var body = t.Kind != Kind.Files ? Shorten(t.Text, 100)
            : t.Files.Count == 1 ? $"{t.Files[0].Name}, {Size(t.TotalSize)}"
            : $"{t.Files.Count} Dateien, {Size(t.TotalSize)}: {Shorten(string.Join(", ", t.Files.Take(50).Select(f => f.Name)), 200)}";
        return string.IsNullOrWhiteSpace(t.Message) ? body : $"{body}\n{Shorten(t.Message, 200)}";
    }

    /// <summary>Everything about an offer, one item per line.</summary>
    public static string Details(Transfer t)
    {
        const int listed = 100;
        var lines = new List<string>();
        if (!string.IsNullOrWhiteSpace(t.Message)) lines.Add(t.Message);
        if (t.Kind != Kind.Files) lines.Add(t.Text);
        lines.AddRange(t.Files.Take(listed).Select(f => $"{f.Name} · {Size(f.Size)}"));
        if (t.Files.Count > listed) lines.Add($"und {t.Files.Count - listed} weitere");
        if (t.Files.Count > 1) lines.Add(Size(t.TotalSize));
        return string.Join("\n", lines);
    }

    public static string ShortName(Transfer t) => t.Kind switch
    {
        Kind.Text => "Text",
        Kind.Link => "Link",
        _ => (t.Files.Count > 0 ? t.Files[0].Name : "") + (t.Files.Count > 1 ? $" +{t.Files.Count - 1}" : ""),
    };

    /// <summary>100 only when complete.</summary>
    public static int Percent(long done, long total) => done >= total ? 100 : Math.Clamp((int)((double)done / total * 100), 0, 99);

    public static string Path(DataPath? p) => p switch { DataPath.Lan => "lokal", DataPath.Relay => "über Server", _ => "" };

    public static string Status(Delivery d) => d.Status switch
    {
        Core.Status.Waiting => "wartet",
        Core.Status.Accepted => "angenommen",
        Core.Status.Downloading => d.Path is null ? "lädt" : $"lädt ({Path(d.Path)})",
        Core.Status.Delivered => "zugestellt",
        Core.Status.Declined => "abgelehnt",
        Core.Status.Missed => "verpasst",
        Core.Status.Cancelled => "abgebrochen",
        _ => "fehlgeschlagen",
    };

    /// <summary>Links of any other scheme are only copied, never opened.</summary>
    public static bool IsWebLink(string s) => WebLink().IsMatch(s);

    /// <summary>A single web address is a link, everything else text.</summary>
    public static Kind KindOf(string text) => IsWebLink(text.Trim()) ? Kind.Link : Kind.Text;

    [GeneratedRegex(@"\Ahttps?://\S+\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WebLink();

    /// <summary>At most max code points.</summary>
    private static string Shorten(string s, int max)
    {
        var i = 0;
        for (var n = 0; n < max && i < s.Length; n++) i += char.IsSurrogatePair(s, i) ? 2 : 1;
        return i >= s.Length ? s : s[..i] + "…";
    }
}
