using System.Runtime.InteropServices;
using System.Xml.Linq;

namespace Share.App.Tests;

public sealed class NotifierTests
{
    private static readonly DateTimeOffset Soon = DateTimeOffset.UtcNow.AddMinutes(5);

    private static Transfer Files(string id, params string[] names) => new(id, "s", Kind.Files, Soon) { Files = [.. names.Select(n => new FileItem(n, 1536))] };
    private static Transfer Text(string id, Kind kind, string text) => new(id, "s", kind, Soon) { Text = text };

    private static (string[] Texts, (string Label, Command? Command)[] Buttons, Command? Click) Read(string xml)
    {
        var x = XDocument.Parse(xml).Root!;
        return ([.. x.Descendants("text").Select(t => t.Value)],
            [.. x.Descendants("action").Select(a => (a.Attribute("content")!.Value, ToastArgs.Parse(a.Attribute("arguments")!.Value)))],
            ToastArgs.Parse(x.Attribute("launch")?.Value ?? ""));
    }

    [Fact]
    public void ToastXmlHasAcceptDecline()
    {
        var (texts, buttons, click) = Read(ToastXml.Offer(Files("t1", "app.apk"), "Pixel"));
        Assert.Equal(["Datei von Pixel", "app.apk, 1,5 KB"], texts);
        Assert.Equal([("Annehmen", new Command.Accept("t1")), ("Ablehnen", new Command.Decline("t1"))], buttons);
        Assert.Equal(new Command.Show(), click); // a click on the text never accepts
    }

    [Fact]
    public void TextIsEscaped()
    {
        var (texts, _, _) = Read(ToastXml.Offer(Text("t1", Kind.Text, "<b>&\"x\" ]]> \u0001 😀"), "A & <B>"));
        Assert.Equal("Text von A & <B>", texts[0]);
        Assert.Contains("<b>&\"x\" ]]>", texts[1]);
        Assert.Contains("😀", texts[1]);
    }

    [Fact]
    public void ResultButtons()
    {
        var done = new Download(Files("d", "a.pdf")) { Phase = Phase.Done, Path = DataPath.Lan, Refs = ["C:\\a.pdf"] };
        var (texts, buttons, _) = Read(ToastXml.Result(done));
        Assert.Equal(["a.pdf empfangen", "1,5 KB · lokal"], texts);
        Assert.Equal([("Öffnen", new Command.OpenDownload("d"))], buttons);
        Assert.Equal("Im Ordner zeigen", Read(ToastXml.Result(done with { Refs = ["C:\\a.exe"] })).Buttons.Single().Label);
        var failed = Read(ToastXml.Result(new Download(Files("d", "a.pdf")) { Phase = Phase.Failed, Error = "Speicher voll" }));
        Assert.Equal(["a.pdf fehlgeschlagen", "Speicher voll"], failed.Texts);
        Assert.Empty(failed.Buttons);
    }

    [Fact]
    public void OnlyWebLinksOpen()
    {
        Assert.Equal([("Öffnen", new Command.OpenLink("https://x.de/?a=b;c&d"))], Read(ToastXml.Text(Text("t", Kind.Link, "https://x.de/?a=b;c&d"), "Pixel")).Buttons);
        foreach (var bad in new[] { "file:///c:/x.exe", "ms-settings:", "javascript:alert(1)" }) Assert.Empty(Read(ToastXml.Text(Text("t", Kind.Link, bad), "x")).Buttons);
        Assert.Empty(Read(ToastXml.Text(Text("t", Kind.Text, "https://x.de"), "x")).Buttons);
        Assert.Equal("Text von Pixel kopiert", Read(ToastXml.Text(Text("t", Kind.Text, "x"), "Pixel")).Texts[0]);
        // long links would not fit into a notification's arguments
        Assert.Empty(Read(ToastXml.Text(Text("t", Kind.Link, "https://x.de/" + new string('a', 3000)), "x")).Buttons);
    }

    [Fact]
    public void EndedListsReceivers()
    {
        var t = Files("e", "a.txt") with { Deliveries = [new Delivery("1", Status.Delivered) { Path = DataPath.Lan }, new Delivery("2", Status.Declined)] };
        Assert.Equal(["a.txt: Angebot beendet", "Gerät 1: zugestellt\nGerät 2: abgelehnt"], Read(ToastXml.Ended(t, id => "Gerät " + id)).Texts);
    }

    [Fact]
    public void Message() => Assert.Equal(["Titel"], Read(ToastXml.Message("Titel", "")).Texts);

    [Fact]
    public void ArgsRoundTrip()
    {
        Command[] all = [new Command.Show(), new Command.Accept("a;b=c&d"), new Command.Decline("x"), new Command.OpenDownload("y"), new Command.OpenLink("https://x/?a=1;b=2&c=%20")];
        foreach (var c in all) Assert.Equal(c, ToastArgs.Parse(ToastArgs.Build(c)));
    }

    [Fact]
    public void UnknownArgsIgnored()
    {
        foreach (var bad in new[] { "", "x", "action=format", "action=accept", "action=link;url=file:///c:/x.exe", "=;=", "action" }) Assert.Null(ToastArgs.Parse(bad));
    }

    [Fact]
    public void ExecutableResultsAreShownNotRun()
    {
        foreach (var bad in new[] { "a.exe", "A.EXE", "a.bat", "a.ps1", "a.lnk", "a.js", "a.msi", "a.scr", "a.hta", "a.reg", "a.jar", "a.url", "a.iso", "a.pdf.exe" }) Assert.True(Results.IsDangerous("C:\\d\\" + bad), bad);
        foreach (var ok in new[] { "a.pdf", "a.jpg", "a.mp4", "a.txt", "a.zip", "a.apk", "a", "a.exe.txt" }) Assert.False(Results.IsDangerous("C:\\d\\" + ok), ok);
    }

    [Fact]
    public async Task ClipboardBusyIsRetried()
    {
        var n = 0;
        Assert.True(await Desktop.RetryAsync(() =>
        {
            if (n++ < 3) throw new COMException("CLIPBRD_E_CANT_OPEN", unchecked((int)0x800401D0));
        }, TimeSpan.FromMilliseconds(1)));
        Assert.Equal(4, n);
        Assert.False(await Desktop.RetryAsync(() => throw new COMException("busy"), TimeSpan.FromMilliseconds(1)));
    }

    /// <summary>Notifications may be forbidden, or the service may be missing altogether.</summary>
    [Fact]
    public void BrokenNotificationServiceIsHarmless()
    {
        var n = new ToastNotifier(new BrokenToasts());
        n.Offer(Files("t1", "a"), "x");
        n.RemoveOffer("t1");
        n.Message("a", "b");
        Assert.False(n.Enabled);
    }

    private sealed class BrokenToasts : IToasts
    {
        public bool Enabled => throw new COMException();
        public void Show(string xml, string tag, DateTimeOffset? expires) => throw new COMException();
        public void Remove(string tag) => throw new COMException();
    }

    private sealed class RecordedToasts : IToasts
    {
        public readonly List<(string Tag, DateTimeOffset? Expires)> Shown = [];
        public readonly List<string> Removed = [];
        public bool Enabled => true;
        public void Show(string xml, string tag, DateTimeOffset? expires) => Shown.Add((tag, expires));
        public void Remove(string tag) => Removed.Add(tag);
    }

    [Fact]
    public void TagsAndExpiry()
    {
        var r = new RecordedToasts();
        var n = new ToastNotifier(r);
        n.Offer(Files("t1", "a"), "x");
        n.Offer(Files("t1", "a"), "x");
        n.Offer(Files("old", "a") with { ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1) }, "x"); // too late to answer
        n.RemoveOffer("t1");
        n.Message("Senden fehlgeschlagen", "a");
        n.Message("Senden fehlgeschlagen", "b");
        Assert.Equal([("o:t1", (DateTimeOffset?)Soon), ("o:t1", Soon)], r.Shown.Take(2));
        Assert.Equal(["o:t1"], r.Removed);
        Assert.Equal(4, r.Shown.Count);
        Assert.NotEqual(r.Shown[2].Tag, r.Shown[3].Tag); // one failure must not hide another
        Assert.All(r.Shown, s => Assert.True(s.Tag.Length <= 64));
    }
}
