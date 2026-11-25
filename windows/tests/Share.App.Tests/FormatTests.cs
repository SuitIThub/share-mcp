namespace Share.App.Tests;

public class FormatTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 24, 10, 0, 0, TimeSpan.Zero);

    private static Transfer Files(string message, params (string Name, long Size)[] f) => new("t", "s", Kind.Files, Now) { Message = message, Files = [.. f.Select(x => new FileItem(x.Name, x.Size))] };
    private static Transfer Files(params (string Name, long Size)[] f) => Files("", f);
    private static Transfer Text(string s, Kind kind = Kind.Text) => new("t", "s", kind, Now) { Text = s };

    [Theory]
    [InlineData(0L, "0 B")]
    [InlineData(1023L, "1023 B")]
    [InlineData(1024L, "1,0 KB")]
    [InlineData(1536L, "1,5 KB")]
    [InlineData(1024L * 1024 - 1, "1,0 MB")]
    [InlineData(12L * 1024 * 1024, "12,0 MB")]
    [InlineData(5L * 1024 * 1024 * 1024, "5,0 GB")]
    [InlineData(3L * 1024 * 1024 * 1024 * 1024, "3,0 TB")]
    public void Sizes(long n, string s) => Assert.Equal(s, Format.Size(n));

    [Theory]
    [InlineData(490, "noch 9 Min")]
    [InlineData(600, "noch 10 Min")]
    [InlineData(61, "noch 2 Min")]
    [InlineData(60, "noch 60 s")]
    [InlineData(40, "noch 40 s")]
    [InlineData(0, "abgelaufen")]
    [InlineData(-5, "abgelaufen")]
    public void Remaining(int seconds, string s) => Assert.Equal(s, Format.Remaining(Now.AddSeconds(seconds), Now));

    [Fact]
    public void Bodies()
    {
        const long mb4 = 4L * 1024 * 1024;
        Assert.Equal("3 Dateien, 12,0 MB: a.txt, b.txt, c.txt", Format.OfferBody(Files(("a.txt", mb4), ("b.txt", mb4), ("c.txt", mb4))));
        Assert.Equal("app.apk, 1,5 KB", Format.OfferBody(Files(("app.apk", 1536))));
        Assert.Equal("app.apk, 1,5 KB\nneue Version", Format.OfferBody(Files("neue Version", ("app.apk", 1536))));
        Assert.Equal("Hallo", Format.OfferBody(Text("Hallo")));
        Assert.Equal(new string('a', 100), Format.OfferBody(Text(new string('a', 100))));
        Assert.Equal(new string('a', 100) + "…", Format.OfferBody(Text(new string('a', 101))));
        Assert.Equal(new string('a', 99) + "😀…", Format.OfferBody(Text(new string('a', 99) + "😀😀")));
        var many = Format.OfferBody(Files([.. Enumerable.Range(1, 500).Select(i => ($"datei{i}.txt", 1L))]));
        Assert.True(many.Length < 300 && many.StartsWith("500 Dateien, 500 B: datei1.txt, ") && many.EndsWith('…'), many);
    }

    [Fact]
    public void TitlesAndNames()
    {
        Assert.Equal("Datei von Handy", Format.OfferTitle(Files(("a", 1)), "Handy"));
        Assert.Equal("2 Dateien von Handy", Format.OfferTitle(Files(("a", 1), ("b", 1)), "Handy"));
        Assert.Equal("Text von Handy", Format.OfferTitle(Text("x"), "Handy"));
        Assert.Equal("Link von Handy", Format.OfferTitle(Text("x", Kind.Link), "Handy"));
        Assert.Equal("app.apk", Format.ShortName(Files(("app.apk", 1))));
        Assert.Equal("a.txt +2", Format.ShortName(Files(("a.txt", 1), ("b", 1), ("c", 1))));
        Assert.Equal("Text", Format.ShortName(Text("x")));
        Assert.Equal("Link", Format.ShortName(Text("x", Kind.Link)));
        Assert.Equal("", Format.ShortName(Files()));
    }

    [Theory]
    [InlineData(0, 100, 0)]
    [InlineData(43_500, 100_000, 43)]
    [InlineData(999, 1000, 99)]
    [InlineData(1000, 1000, 100)]
    [InlineData(0, 0, 100)]
    [InlineData(long.MaxValue / 2, long.MaxValue, 50)]
    public void Percent(long done, long total, int want) => Assert.Equal(want, Format.Percent(done, total));

    [Fact]
    public void HttpTextIsSentAsLink()
    {
        foreach (var l in new[] { "https://x.de", " https://x.de/a?b=c#d\n" }) Assert.Equal(Kind.Link, Format.KindOf(l));
        foreach (var t in new[] { "hallo", "schau: https://x.de", "https://x.de und mehr", "https://x.de\nhttps://y.de", "x.de", "" }) Assert.Equal(Kind.Text, Format.KindOf(t));
    }

    [Fact]
    public void OnlyWebLinksOpen()
    {
        foreach (var ok in new[] { "https://x.de", "http://x", "HTTPS://X" }) Assert.True(Format.IsWebLink(ok), ok);
        foreach (var bad in new[] { "ms-settings:x", "javascript:alert(1)", "file:///x", "\\\\server\\x", "C:\\x.exe", "x.de", "", "https://", "https://a b", "https://a\nb" }) Assert.False(Format.IsWebLink(bad), bad);
    }
}
