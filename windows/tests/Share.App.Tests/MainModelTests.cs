using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Share.App.Tests;

public sealed class FakeOpener : IOpener
{
    public readonly List<string> Calls = [];
    public void Open(Download d) => Calls.Add("open " + d.Transfer.Id);
    public void OpenLink(string url) => Calls.Add("link " + url);
}

public sealed class MainModelTests
{
    private readonly System.Collections.Concurrent.ConcurrentQueue<Action> _ui = new(); // the UI thread's queue

    private MainModel Model(HostRig r, FakeOpener? opener = null) => new(r.Host, _ui.Enqueue, opener ?? new FakeOpener());

    private void Drain()
    {
        while (_ui.TryDequeue(out var a)) a();
    }

    private Task Until(HostRig r, MainModel m, Func<bool> c, string what) => r.Until(() =>
    {
        Drain();
        m.Refresh();
        return c();
    }, what);

    private static IFileSource Slow(HostRig r, string name, int size) => new SlowSource(DiskFile.Open(r.File(name, size)), 50_000, 20);

    [Fact]
    public async Task ShowsIncomingWithDetails()
    {
        await using var r = await new HostRig().Start();
        var m = Model(r);
        await r.Peer.OfferFilesAsync([DiskFile.Open(r.File("a.txt", 1024)), DiskFile.Open(r.File("b.bin", 2048))], ["PC"], false, "schau mal");
        await Until(r, m, () => m.Incoming.Count == 1, "incoming");
        var row = m.Incoming[0];
        Assert.Equal("2 Dateien von Peer", row.Title);
        Assert.Equal("2 Dateien, 3,0 KB: a.txt, b.bin\nschau mal", row.Body);
        Assert.Equal("noch 10 Min", row.Remaining);
        Assert.Equal("schau mal\na.txt · 1,0 KB\nb.bin · 2,0 KB\n3,0 KB", row.Details);
        Assert.False(m.NoIncoming);
    }

    [Fact]
    public async Task AcceptAndDecline()
    {
        await using var r = await new HostRig().Start();
        var m = Model(r);
        var t1 = await r.Peer.OfferTextAsync(Kind.Text, "eins", ["PC"], false);
        var t2 = await r.Peer.OfferTextAsync(Kind.Text, "zwei", ["PC"], false);
        await Until(r, m, () => m.Incoming.Count == 2, "incoming");
        m.Incoming.Single(x => x.Key == t1.Id).Accept.Execute(null);
        m.Incoming.Single(x => x.Key == t2.Id).Decline.Execute(null);
        await Until(r, m, () => m.Incoming.Count == 0 && r.Desktop.Copied.Contains("eins"), "answered");
        Assert.True(m.NoIncoming);
    }

    [Fact]
    public async Task ShowsProgressPathAndPerReceiverStatus()
    {
        await using var r = await new HostRig().Start();
        await r.Go.AccountAsync("Tablet");
        await r.Until(() => r.Engine.State.Devices.Count == 2, "devices");
        var m = Model(r);
        var t = await r.Engine.OfferFilesAsync([Slow(r, "app.apk", 5_000_000)], ["Peer", "Tablet"], false);
        await r.Until(() => r.Peer.State.Incoming.Count > 0, "seen");
        await r.Peer.AcceptAsync(t.Id);
        await Until(r, m, () => m.Running.Count == 1 && m.Running[0].Text.Contains("Peer: lädt (lokal) "), "progress");
        var row = m.Running[0];
        Assert.Equal("↑ app.apk", row.Title);
        Assert.Contains("Tablet: wartet", row.Text);
        Assert.True(row.CanCancel);
        Assert.False(row.CanDismiss);
        row.Cancel.Execute(null);
        await Until(r, m, () => m.Running.Count == 0, "cancelled");
    }

    [Fact]
    public async Task DownloadRowsUpdateInPlace()
    {
        await using var r = await new HostRig().Start();
        var opener = new FakeOpener();
        var m = Model(r, opener);
        var t = await r.Peer.OfferFilesAsync([Slow(r, "video.mp4", 3_000_000)], ["PC"], false);
        await Until(r, m, () => m.Incoming.Count == 1, "incoming");
        m.Incoming[0].Accept.Execute(null);
        await Until(r, m, () => m.Running.Count == 1 && m.Running[0].Progress > 0, "running");
        var row = m.Running[0];
        Assert.Equal("↓ video.mp4 (Peer)", row.Title);
        Assert.EndsWith(" % · lokal", row.Text);
        var changed = new List<string?>();
        row.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        await Until(r, m, () => row.CanDismiss, "done");
        Assert.Same(row, m.Running[0]); // focus and selection stay
        Assert.Contains(nameof(RunningRow.Progress), changed);
        Assert.Equal("Im Download-Ordner", row.Text);
        Assert.Null(row.Progress);
        Assert.Equal("Öffnen", row.OpenLabel);
        row.Open.Execute(null);
        Assert.Equal(["open " + t.Id], opener.Calls);
        row.Dismiss.Execute(null);
        await Until(r, m, () => m.Running.Count == 0, "dismissed");
    }

    [Fact]
    public async Task CancelDownload()
    {
        await using var r = await new HostRig().Start();
        var m = Model(r);
        var t = await r.Peer.OfferFilesAsync([Slow(r, "x", 5_000_000)], ["PC"], false);
        await r.Until(() => r.Engine.State.Incoming.Count > 0, "incoming");
        r.Host.Run(e => e.AcceptAsync(t.Id));
        await Until(r, m, () => m.Running.Count == 1 && m.Running[0].Progress > 0, "running");
        m.Running[0].Cancel.Execute(null);
        await Until(r, m, () => m.Running.Count == 0, "gone");
    }

    [Fact]
    public async Task FailedShowsMessage()
    {
        await using var r = await new HostRig().Start();
        var m = Model(r);
        var f = r.File("x.bin", 1000);
        var t = await r.Peer.OfferFilesAsync([DiskFile.Open(f)], ["PC"], false);
        File.WriteAllBytes(f, new byte[3]);
        await r.Until(() => r.Engine.State.Incoming.Count > 0, "incoming");
        r.Host.Run(e => e.AcceptAsync(t.Id));
        await Until(r, m, () => m.Running.Count == 1 && m.Running[0].Failed, "failed");
        Assert.Equal("Datei wurde beim Sender geändert", m.Running[0].Text);
        Assert.True(m.Running[0].CanDismiss);
    }

    [Fact]
    public async Task ReceivedAndSentStayUntilDismissed()
    {
        await using var r = await new HostRig().Start();
        var opener = new FakeOpener();
        var m = Model(r, opener);
        var l = await r.Peer.OfferTextAsync(Kind.Link, "https://example.org", ["PC"], false);
        var bad = await r.Peer.OfferTextAsync(Kind.Link, "file:///c:/x.exe", ["PC"], false);
        await r.Until(() => r.Engine.State.Incoming.Count == 2, "incoming");
        r.Host.Run(e => e.AcceptAsync(l.Id));
        r.Host.Run(e => e.AcceptAsync(bad.Id));
        await Until(r, m, () => m.Running.Count == 2, "received");
        var row = m.Running.Single(x => x.Key.EndsWith(l.Id));
        Assert.Equal("https://example.org", row.Text);
        Assert.Equal("Öffnen", row.OpenLabel);
        row.Open.Execute(null);
        Assert.Equal(["link https://example.org"], opener.Calls);
        Assert.Null(m.Running.Single(x => x.Key.EndsWith(bad.Id)).OpenLabel);
        row.Copy.Execute(null);
        await r.Until(() => r.Desktop.Copied.Count(c => c == "https://example.org") == 2, "copied again");
        var o = await r.Engine.OfferTextAsync(Kind.Text, "x", ["Peer"], false);
        await r.Until(() => r.Peer.State.Incoming.Count > 0, "seen");
        await r.Peer.DeclineAsync(o.Id);
        await Until(r, m, () => m.Running.Any(x => x.Text == "Peer: abgelehnt" && x.CanDismiss), "outcome");
    }

    [Fact]
    public async Task SendDisabledWithoutTarget()
    {
        await using var r = await new HostRig().Start();
        var m = Model(r);
        m.Refresh();
        Assert.False(m.Targets.CanSend);
        var peer = m.Targets.Rows.Single(t => t.Name == "Peer");
        Assert.True(peer.Online);
        peer.Selected = true;
        Assert.True(m.Targets.CanSend);
        m.SendText("hallo");
        await r.Until(() => r.Peer.State.Incoming.Any(t => t.Text == "hallo" && t.Kind == Kind.Text), "sent");
        m.SendText(" https://example.org ");
        await r.Until(() => r.Peer.State.Incoming.Any(t => t.Text == "https://example.org" && t.Kind == Kind.Link), "link sent");
        m.SendFiles([r.File("a.txt", 10)]);
        await r.Until(() => r.Peer.State.Incoming.Any(t => t.Kind == Kind.Files), "files sent");
    }

    [Fact]
    public async Task AllClearsSingleTargets()
    {
        await using var r = await new HostRig().Start();
        var m = Model(r);
        m.Refresh();
        var peer = m.Targets.Rows[0];
        peer.Selected = true;
        m.Targets.All = true;
        Assert.False(peer.Selected);
        Assert.True(m.Targets.CanSend);
        Assert.Empty(m.Targets.SelectedIds);
        m.SendText("alle");
        await r.Until(() => r.Peer.State.Incoming.Any(t => t.Broadcast), "broadcast");
        peer.Selected = true;
        Assert.False(m.Targets.All);
        Assert.Equal([peer.Key], m.Targets.SelectedIds);
    }

    [Fact]
    public async Task VanishedDeviceIsNoTargetAnyMore()
    {
        await using var r = await new HostRig().Start();
        var tablet = await r.Go.AccountAsync("Tablet");
        var m = Model(r);
        await Until(r, m, () => m.Targets.Rows.Count == 2, "devices");
        m.Targets.Rows.Single(t => t.Name == "Tablet").Selected = true;
        using (var api = new Api(tablet)) await api.DeleteMeAsync();
        await Until(r, m, () => m.Targets.Rows.Count == 1, "gone");
        Assert.False(m.Targets.CanSend);
    }

    [Fact]
    public async Task SelectionSurvivesUpdates()
    {
        await using var r = await new HostRig().Start();
        var m = Model(r);
        m.Refresh();
        var row = m.Targets.Rows[0];
        row.Selected = true;
        await r.Go.AccountAsync("Aaa");
        await Until(r, m, () => m.Targets.Rows.Count == 2, "new device");
        Assert.Equal(["Aaa", "Peer"], m.Targets.Rows.Select(t => t.Name));
        Assert.Same(row, m.Targets.Rows[1]);
        Assert.True(row.Selected);
    }

    [Fact]
    public async Task EmptyAndUnpairedState()
    {
        await using var r = await new HostRig().Start(paired: false);
        var m = Model(r);
        m.Refresh();
        Assert.Equal("nicht gekoppelt", m.Status);
        Assert.True(m.NoIncoming && m.NoDevices && m.NoRunning);
        r.Host.SetAccount(await r.Go.AccountAsync("PC"));
        await Until(r, m, () => m.Status == "verbunden", "connected");
        r.Go.Stop();
        await Until(r, m, () => m.Status == "verbinde …", "connecting");
    }

    [Fact]
    public async Task RevokedPairingIsShown()
    {
        await using var r = await new HostRig().Start();
        var m = Model(r);
        using (var api = new Api(r.Engine.Api.Account)) await api.DeleteMeAsync();
        r.Go.Restart();
        await Until(r, m, () => m.Status == "nicht mehr gekoppelt" && m.NeedsPairing, "revoked");
    }

    [Fact]
    public async Task StateFromWorkerThreadsIsMarshalled()
    {
        await using var r = await new HostRig().Start();
        var posted = 0;
        Action? last = null;
        var m = new MainModel(r.Host, a =>
        {
            posted++;
            last = a;
        }, new FakeOpener());
        await r.Peer.OfferTextAsync(Kind.Text, "x", ["PC"], false);
        await r.Until(() => r.Engine.State.Incoming.Count > 0 && posted > 0, "posted");
        Assert.Empty(m.Incoming); // nothing happens on the worker thread
        Assert.Equal(1, posted); // many changes, one update
        last!();
        Assert.Single(m.Incoming);
    }

    [Fact]
    public async Task MessagesAppearAndCanBeClosed()
    {
        await using var r = await new HostRig().Start();
        var m = Model(r);
        using (r.Host.UiShown()) r.Host.Tell("Keine Verbindung", "zum Server");
        Drain();
        Assert.Equal("Keine Verbindung: zum Server", m.Message);
        Assert.True(m.HasMessage);
        m.CloseMessage.Execute(null);
        Assert.False(m.HasMessage);
    }

    [Fact]
    public async Task PasteTextImageAndFiles()
    {
        await using var r = await new HostRig().Start();
        var m = Model(r);
        var got = new List<Payload>();
        m.PayloadOffered += got.Add;

        Assert.True(m.Paste(new DataObject(DataFormats.UnicodeText, "hallo")));
        Assert.Equal(new Payload.Text("hallo"), got[0]);

        Assert.True(m.Paste(new DataObject(DataFormats.FileDrop, new[] { "C:\\a.txt", "C:\\b.txt" })));
        Assert.Equal(["C:\\a.txt", "C:\\b.txt"], Assert.IsType<Payload.Files>(got[1]).Paths);

        var bmp = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgr24, null, new byte[12], 6);
        Assert.True(m.Paste(new DataObject(DataFormats.Bitmap, bmp)));
        var img = Assert.IsType<Payload.Files>(got[2]);
        Assert.True(r.Host.IsTemporary(img.Paths.Single()));
        Assert.EndsWith(".png", img.Paths.Single());
        Assert.Equal([0x89, (byte)'P', (byte)'N', (byte)'G'], File.ReadAllBytes(img.Paths.Single())[..4]);

        Assert.False(m.Paste(new DataObject()));
        Assert.False(m.Paste(new DataObject(DataFormats.UnicodeText, "  ")));
        Assert.Equal(3, got.Count);
        // a web browser puts both text and a picture on the clipboard for a copied image; files win over text, text over pictures
        var both = new DataObject();
        both.SetData(DataFormats.UnicodeText, "x");
        both.SetData(DataFormats.FileDrop, new[] { "C:\\a" });
        m.Paste(both);
        Assert.IsType<Payload.Files>(got[3]);
    }

    [Fact]
    public async Task FoldersAreReportedOthersOffered()
    {
        await using var r = await new HostRig().Start();
        var m = Model(r);
        m.Refresh();
        m.Targets.All = true;
        m.SendFiles([r.Tmp.Path, r.File("ok.txt", 5)]);
        await r.Until(() => r.Peer.State.Incoming.Any(t => t.Files.Count == 1), "offered");
        await r.Until(() => r.Notifier.Has("message „") && r.Notifier.Calls.Any(c => c.Contains("Ordner")), "told");
    }
}
