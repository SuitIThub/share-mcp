using System.Net;

namespace Share.Core;

/// <summary>Content of the pairing QR code: share://pair?code=…&amp;url=…</summary>
public sealed record PairUri(string Url, string Code)
{
    public override string ToString() => $"share://pair?code={Uri.EscapeDataString(Code)}&url={Uri.EscapeDataString(Url)}";

    public static PairUri? Parse(string s)
    {
        s = s.Trim();
        if (!s.StartsWith("share://pair?", StringComparison.OrdinalIgnoreCase) && !s.StartsWith("share://pair/?", StringComparison.OrdinalIgnoreCase)) return null;
        string code = "", url = "";
        foreach (var part in s[(s.IndexOf('?') + 1)..].Split('&'))
        {
            var i = part.IndexOf('=');
            if (i < 0) continue;
            var value = WebUtility.UrlDecode(part[(i + 1)..]).Trim();
            if (part[..i] == "code") code = value.ToUpperInvariant();
            if (part[..i] == "url") url = value;
        }
        return code.Length == 0 || url.Length == 0 ? null : new PairUri(url, code);
    }
}
