using System.Collections.Concurrent;

namespace Share.App.Tests;

public sealed class SendModelTests
{
    private readonly ConcurrentQueue<Action> _ui = new();

    private Task<SendModel> Open(HostRig r, Payload p, IReadOnlyList<string>? targets = null, bool all = false) => SendModel.OpenAsync(r.Host, _ui.Enqueue, p, targets ?? [], all);

    private Task Until(HostRig r, Func<bool> c, string what) => r.Until(() =>
    {
        while (_ui.TryDequeue(out var a)) a();
        return c();
    }, what);

    [Fact]
    public async Task OpensHandlesImmediately()
    {
        await using var r = await new HostRig().Start();
        var p = r.File("weg.bin", 100_000);
        var m = await Open(r, new Payload.Files([p]));
        File.Delete(p); // the sharing program cleans up
        Assert.Equal("weg.bin (97,7 KB) senden", m.Title);
        Assert.False(m.CanSend);
        m.Targets.Rows.Single().Selected = true;
        Assert.True(m.CanSend);
        var closed = false;
        m.Closed += () => closed = true;
        m.Send();
        Assert.True(closed);
        await r.Until(() => r.Peer.State.Incoming.Count == 1, "offered");
        await r.Peer.AcceptAsync(r.Peer.State.Incoming[0].Id);
        await r.Until(() => r.Peer.State.Downloads.Count == 1 && r.Peer.State.Downloads[0].Phase == Phase.Done, "received");
        Assert.Equal(TempDir.Random(100_000), File.ReadAllBytes(r.Peer.State.Downloads[0].Refs[0]));
    }

    [Fact]
    public async Task SeveralFilesAndPreselection()
    {
        await using var r = await new HostRig().Start();
        var m = await Open(r, new Payload.Files([r.File("a", 1024), r.File("b", 1024), r.File("a", 1024)]), [r.Peer.Api.Account.DeviceId]);
        Assert.Equal("2 Dateien (2,0 KB) senden", m.Title);
        Assert.Equal("a\nb", m.Details);
        Assert.True(m.CanSend);
        var all = await Open(r, new Payload.Files([r.File("c", 1)]), all: true);
        Assert.True(all.Targets.All);
        m.Cancel();
        all.Cancel();
    }

    [Fact]
    public async Task TextIsEditable()
    {
        await using var r = await new HostRig().Start();
        var m = await Open(r, new Payload.Text("hallo"), all: true);
        Assert.True(m.IsText);
        Assert.Equal("Text/Link senden", m.Title);
        m.Text = "";
        Assert.False(m.CanSend);
        m.Text = new string('ä', 32769);
        Assert.False(m.CanSend);
        Assert.Equal("Zu lang (höchstens 64 KB)", m.Problem);
        m.Text = "https://example.org";
        Assert.True(m.CanSend);
        m.Send();
        await r.Until(() => r.Peer.State.Incoming.Any(t => t.Kind == Kind.Link && t.Broadcast), "offered");
    }

    [Fact]
    public async Task NotPairedKeepsThePayload()
    {
        await using var r = await new HostRig().Start(paired: false);
        var p = r.File("a", 10);
        var m = await Open(r, new Payload.Files([p]));
        File.Delete(p);
        Assert.True(m.NeedsPairing);
        Assert.False(m.CanSend);
        r.Host.SetAccount(await r.Go.AccountAsync("PC"));
        await Until(r, () => m.Targets.Rows.Count == 1 && !m.NeedsPairing, "devices after pairing");
        m.Targets.All = true;
        m.Send();
        await r.Until(() => r.Peer.State.Incoming.Count == 1, "offered");
    }

    [Fact]
    public async Task WaitsForDevicesAfterColdStart()
    {
        await using var r = await new HostRig().Start(paired: false);
        r.Host.SetAccount(await r.Go.AccountAsync("PC"));
        var m = await Open(r, new Payload.Text("x"), [r.Peer.Api.Account.DeviceId]);
        await Until(r, () => m.Targets.Rows.Count == 1, "devices");
        Assert.True(m.CanSend); // the wish from before the list was there still counts
        m.Cancel();
    }

    [Fact]
    public async Task FoldersAreReportedOthersOffered()
    {
        await using var r = await new HostRig().Start();
        var m = await Open(r, new Payload.Files([r.Tmp.Path, r.File("ok.txt", 5), r.Tmp.File("fehlt")]));
        Assert.Equal("ok.txt (5 B) senden", m.Title);
        Assert.Contains("Ordner können nicht gesendet werden", m.Problem);
        Assert.Contains("„fehlt“ kann nicht gelesen werden", m.Problem);
        m.Targets.All = true;
        Assert.True(m.CanSend);
        m.Cancel();
    }

    [Fact]
    public async Task NothingUsableCannotBeSent()
    {
        await using var r = await new HostRig().Start();
        var m = await Open(r, new Payload.Files([r.Tmp.Path]), all: true);
        Assert.Equal("Nichts zu senden", m.Title);
        Assert.False(m.CanSend);
    }

    [Fact]
    public async Task CancelReleasesEverything()
    {
        await using var r = await new HostRig().Start();
        var keep = r.File("bleibt", 10);
        var temp = r.File("temp", 10);
        var a = await Open(r, new Payload.Files([keep]));
        var b = await Open(r, new Payload.Files([temp], Temporary: true));
        a.Cancel();
        b.Cancel();
        a.Cancel();
        using (new FileStream(keep, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
        Assert.False(File.Exists(temp));
    }

    [Fact]
    public async Task TemporaryFilesAreDeletedAfterTheOffer()
    {
        await using var r = await new HostRig().Start();
        var temp = r.File("temp.png", 10);
        var m = await Open(r, new Payload.Files([temp], Temporary: true), all: true);
        m.Send();
        await r.Until(() => r.Engine.State.Outgoing.Count == 1, "offered");
        Assert.True(File.Exists(temp));
        await r.Engine.CancelOutgoingAsync(r.Engine.State.Outgoing[0].Id);
        Assert.False(File.Exists(temp));
    }

    [Fact]
    public async Task FailedSendIsToldAndReleased()
    {
        await using var r = await new HostRig().Start();
        var temp = r.File("temp.png", 10);
        var m = await Open(r, new Payload.Files([temp], Temporary: true), all: true);
        r.Go.Stop();
        m.Send();
        await r.Until(() => r.Notifier.Has("message Keine Verbindung zum Server"), "told");
        Assert.False(File.Exists(temp));
    }

    [Fact]
    public async Task UnpairingWhileOpenDisablesSending()
    {
        await using var r = await new HostRig().Start();
        var m = await Open(r, new Payload.Text("x"), all: true);
        Assert.True(m.CanSend);
        r.Host.SetAccount(null);
        await Until(r, () => !m.CanSend && m.NeedsPairing, "disabled");
        m.Cancel();
    }
}
