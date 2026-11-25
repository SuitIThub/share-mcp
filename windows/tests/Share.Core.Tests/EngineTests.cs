using System.Collections.Concurrent;
using System.Diagnostics;

namespace Share.Core.Tests;

public static class TestEngine
{
    public static Engine Create(Api api, IDownloadStore store, TimeSpan? deviceRefresh = null, Func<DateTimeOffset>? now = null) => new(api, store, new EngineOptions
    {
        Port = 0, Candidates = p => [$"127.0.0.1:{p}"], DeviceRefresh = deviceRefresh ?? TimeSpan.FromHours(1), Tick = TimeSpan.FromMilliseconds(50), Now = now ?? (() => DateTimeOffset.UtcNow),
        Events = new EventOptions { BackoffBase = TimeSpan.FromMilliseconds(20) },
        Receiver = new ReceiverOptions { ProbeTimeout = TimeSpan.FromMilliseconds(500), IdleTimeout = TimeSpan.FromSeconds(5), BackoffBase = TimeSpan.FromMilliseconds(20) },
    });
}

public sealed class EngineTests : IAsyncLifetime
{
    private readonly GoServer _go = new();
    private readonly TempDir _tmp = new();
    private readonly ConcurrentQueue<Notice> _notices = new();
    private Func<DateTimeOffset> _clock = () => DateTimeOffset.UtcNow;
    private Api _a = null!; // the other device, driven by hand
    private Account _acc = null!;
    private Engine _e = null!;
    private Engine? _other;

    public async Task InitializeAsync()
    {
        _a = new Api(await _go.AccountAsync("PC"));
        _acc = await _go.AccountAsync("Laptop");
        _e = New();
    }

    public Task DisposeAsync()
    {
        _e.Dispose();
        _other?.Dispose();
        _go.Dispose();
        _tmp.Dispose();
        return Task.CompletedTask;
    }

    private string Dl => _tmp.File("dl");
    private DiskDownloads Store() => new(Dl, _tmp.File("state.json"));
    private string[] Files() => Directory.Exists(Dl) ? [.. Directory.GetFiles(Dl).Select(f => Path.GetFileName(f)!).Order(StringComparer.Ordinal)] : [];

    private Engine New(IDownloadStore? store = null, TimeSpan? deviceRefresh = null)
    {
        var e = TestEngine.Create(new Api(_acc), store ?? Store(), deviceRefresh, () => _clock());
        e.Notified += _notices.Enqueue;
        return e;
    }

    private Task Until(Func<bool> c, string what, int seconds = 15) => Eventually.True(c, $"{what}; {_e.State} {string.Join(",", _notices)}", seconds);

    private async Task Connected()
    {
        _e.Start();
        await Until(() => _e.State.Connected && _e.State.Devices.Count > 0, "connected");
    }

    private async Task<Transfer> OfferText(string text = "hi", Kind kind = Kind.Text) => (await _a.CreateTransferAsync(new(kind, false) { Text = text, Targets = ["Laptop"] })).Transfer;

    private Probe Source(int size, string name = "f.bin") => new(DiskFile.Open(_tmp.File(name, TempDir.Random(size))));

    /// <summary>A second engine on the PC account that can serve files.</summary>
    private async Task<Engine> Pc()
    {
        if (_other is null)
        {
            _other = TestEngine.Create(_a, new DiskDownloads(_tmp.File("pc"), _tmp.File("pc.json")));
            _other.Start();
            using var api = new Api(_acc);
            await Eventually.True(async () => (await api.DevicesAsync()).Single(d => d.Name == "PC").Online, "pc online");
        }
        return _other;
    }

    private async Task<Status> Status(Transfer t) => (await _a.TransferAsync(t.Id)).Deliveries.Single().Status;
    private Download? Single => _e.State.Downloads.Count == 1 ? _e.State.Downloads[0] : null;
    private bool Has<T>(Func<T, bool> f) where T : Notice => _notices.OfType<T>().Any(f);

    [Fact]
    public async Task DevicesExcludeSelfAndAgents()
    {
        using (var p = Process.Start(new ProcessStartInfo(GoServer.ShareExe) { ArgumentList = { "-config", _tmp.File("cli.json"), "pair", "-name", "Bot", _go.Url, _go.PairCode() }, CreateNoWindow = true })!)
        {
            await p.WaitForExitAsync();
            Assert.Equal(0, p.ExitCode);
        }
        await Connected();
        Assert.Equal(["PC"], _e.State.Devices.Select(d => d.Name));
        Assert.Equal("Bot", _e.DeviceName((await _a.DevicesAsync()).Single(d => d.Name == "Bot").Id));
        Assert.Equal("Unbekanntes Gerät", _e.DeviceName("nope"));
    }

    [Fact]
    public async Task NewDevicesAndOnlineStateAppear()
    {
        _e.Dispose();
        _e = New(deviceRefresh: TimeSpan.FromMilliseconds(100));
        await Connected();
        Assert.False(_e.State.Devices.Single().Online);
        await Pc();
        await Until(() => _e.State.Devices.Single(d => d.Name == "PC").Online, "online");
        await _go.AccountAsync("Desk");
        await Until(() => _e.State.Devices.Select(d => d.Name).SequenceEqual(["Desk", "PC"]), "new device");
    }

    [Fact]
    public async Task UnknownReceiversGetNames()
    {
        await Connected();
        var late = new Api(await _go.AccountAsync("Late"));
        await _e.OfferTextAsync(Kind.Text, "x", [], true);
        await Eventually.True(async () => (await late.IncomingAsync()).Count > 0, "late sees it");
        await late.DeclineAsync((await late.IncomingAsync()).Single().Id);
        await Until(() => _e.DeviceName(late.Account.DeviceId) == "Late", "name known");
    }

    [Fact]
    public async Task IncomingAppearsAndDisappears()
    {
        await Connected();
        var t = await OfferText();
        await Until(() => _e.State.Incoming.Select(x => x.Id).SequenceEqual([t.Id]), "arrived");
        await Until(() => Has<Notice.OfferArrived>(n => n.Transfer.Id == t.Id), "notice");
        await _a.CancelAsync(t.Id);
        await Until(() => _e.State.Incoming.Count == 0 && _notices.Contains(new Notice.OfferGone(t.Id)), "gone");
    }

    [Fact]
    public async Task OffersFromBeforeConnectingAppear()
    {
        var t = await OfferText();
        await Connected();
        await Until(() => _e.State.Incoming.Select(x => x.Id).SequenceEqual([t.Id]), "arrived");
        await _e.RefreshAsync();
        Assert.Single(_notices.OfType<Notice.OfferArrived>());
    }

    [Fact]
    public async Task AcceptTextYieldsTextReceived()
    {
        await Connected();
        var t = await OfferText("hällo");
        await Until(() => _e.State.Incoming.Count > 0, "arrived");
        await Task.WhenAll(_e.AcceptAsync(t.Id), _e.AcceptAsync(t.Id));
        await Until(() => _notices.OfType<Notice.TextReceived>().Select(n => n.Transfer.Text).SequenceEqual(["hällo"]), "text");
        Assert.Empty(_e.State.Incoming);
        Assert.Equal(Core.Status.Delivered, await Status(t));
        Assert.Equal(["hällo"], _e.State.Received.Select(r => r.Text));
        _e.Dismiss(t.Id);
        Assert.Empty(_e.State.Received);
    }

    [Fact]
    public async Task AcceptWithoutConnectionToEventsWorks()
    {
        var t = await OfferText("x");
        await _e.AcceptAsync(t.Id);
        Assert.Contains(_notices, n => n is Notice.TextReceived);
        Assert.Equal(Core.Status.Delivered, await Status(t));
    }

    [Fact]
    public async Task AcceptFilesRunsToDone()
    {
        await Connected();
        var pc = await Pc();
        var t = await pc.OfferFilesAsync([new SlowSource(Source(2_000_000), 100_000, 10)], ["Laptop"], false);
        await Until(() => _e.State.Incoming.Count > 0, "arrived");
        Assert.False(_e.Busy);
        await _e.AcceptAsync(t.Id);
        await Until(() => Single is { Phase: Phase.Running, Bytes: > 0, Path: DataPath.Lan }, "running");
        Assert.True(_e.Busy);
        await Until(() => Single!.Phase == Phase.Done, "done");
        Assert.Equal(2_000_000, Single!.Bytes);
        Assert.Equal(TempDir.Random(2_000_000), File.ReadAllBytes(Single!.Refs.Single()));
        Assert.False(_e.Busy);
        await Until(() => _notices.OfType<Notice.DownloadDone>().Count() == 1, "notice");
        await Until(() => pc.State.Outgoing.Count == 0, "sender closed");
        _e.Dismiss(t.Id);
        Assert.Empty(_e.State.Downloads);
    }

    [Fact]
    public async Task DoubleAcceptStartsOneDownload()
    {
        await Connected();
        var f = Source(500_000);
        var t = await (await Pc()).OfferFilesAsync([f], ["Laptop"], false);
        await Until(() => _e.State.Incoming.Count > 0, "arrived");
        await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => Task.Run(() => _e.AcceptAsync(t.Id))));
        await Until(() => Single!.Phase == Phase.Done, "done");
        await _e.AcceptAsync(t.Id);
        await Task.Delay(300);
        Assert.Single(f.Opened);
        Assert.Equal(["f.bin"], Files());
        Assert.DoesNotContain(_notices, n => n is Notice.Unavailable);
    }

    [Fact]
    public async Task AcceptAfterWithdrawIsUnavailable()
    {
        var text = await OfferText();
        var files = (await _a.CreateTransferAsync(new(Kind.Files, false) { Files = [new FileItem("x", 5)], Targets = ["Laptop"] })).Transfer;
        await _a.CancelAsync(text.Id);
        await _a.CancelAsync(files.Id);
        foreach (var id in new[] { text.Id, files.Id, "unknown" })
        {
            await _e.AcceptAsync(id);
            await Until(() => _notices.Contains(new Notice.Unavailable(id)), "unavailable " + id);
        }
        Assert.Empty(_e.State.Downloads);
    }

    [Fact]
    public async Task AcceptOfExpiredKnownOfferIsUnavailable()
    {
        await Connected();
        var pc = await Pc();
        var t = await pc.OfferFilesAsync([Source(5)], ["Laptop"], false);
        await Until(() => _e.State.Incoming.Count > 0, "arrived");
        _go.Stop(); // the engine does not learn about the withdrawal ...
        pc.Dispose();
        _go.Restart();
        using (var api = new Api(_a.Account)) await api.CancelAsync(t.Id);
        await _e.AcceptAsync(t.Id);
        await Until(() => _notices.Contains(new Notice.Unavailable(t.Id)) || _notices.Contains(new Notice.OfferGone(t.Id)), "unavailable");
        await Until(() => _e.State.Incoming.Count == 0 && _e.State.Downloads.Count == 0, "gone");
        Assert.DoesNotContain(_notices, n => n is Notice.DownloadFailed);
    }

    [Fact]
    public async Task DeclineAfterWithdrawIsUnavailable()
    {
        var t = await OfferText();
        await _a.CancelAsync(t.Id);
        await _e.DeclineAsync(t.Id);
        await _e.DeclineAsync("unknown");
        Assert.Contains(new Notice.Unavailable(t.Id), _notices);
        Assert.Contains(new Notice.Unavailable("unknown"), _notices);
    }

    [Fact]
    public async Task DeclineRemovesAndReports()
    {
        await Connected();
        var t = await OfferText();
        await Until(() => _e.State.Incoming.Count > 0, "arrived");
        await _e.DeclineAsync(t.Id);
        Assert.Empty(_e.State.Incoming);
        Assert.Equal(Core.Status.Declined, await Status(t));
    }

    [Fact]
    public async Task AnsweringWithoutServerIsAnIoErrorAndKeepsTheOffer()
    {
        await Connected();
        var t = await OfferText();
        await Until(() => _e.State.Incoming.Count > 0, "arrived");
        _go.Stop();
        await Assert.ThrowsAnyAsync<IOException>(() => _e.AcceptAsync(t.Id));
        await Assert.ThrowsAnyAsync<IOException>(() => _e.DeclineAsync(t.Id));
        Assert.Single(_e.State.Incoming);
    }

    [Fact]
    public async Task ResumesConfirmedDownloadsOnConnect()
    {
        var t = await (await Pc()).OfferFilesAsync([Source(1000)], ["Laptop"], false);
        using (var api = new Api(_acc)) await api.AcceptAsync(t.Id); // confirmed by a process that has died since
        await Connected();
        await Until(() => Single?.Phase == Phase.Done, "done");
        Assert.Equal(TempDir.Random(1000), File.ReadAllBytes(Single!.Refs.Single()));
    }

    [Fact]
    public async Task WaitingOffersAreNeverAutoAccepted()
    {
        var t = await (await Pc()).OfferFilesAsync([Source(10)], ["Laptop"], false);
        await Connected();
        await _e.RefreshAsync();
        await Task.Delay(500);
        Assert.Equal(Core.Status.Waiting, await Status(t));
        Assert.Empty(_e.State.Downloads);
        Assert.Empty(Files());
    }

    [Fact]
    public async Task CancelDownloadReportsFailedAndDiscards()
    {
        await Connected();
        var t = await (await Pc()).OfferFilesAsync([new SlowSource(Source(5_000_000), 50_000, 20)], ["Laptop"], false);
        await _e.AcceptAsync(t.Id);
        await Until(() => Single?.Bytes > 100_000, "running");
        _e.CancelDownload(t.Id);
        await Eventually.True(async () => await Status(t) == Core.Status.Failed, "failed");
        await Until(() => Files().Length == 0 && _e.State.Downloads.Count == 0, "clean");
        await _e.RefreshAsync();
        Assert.Empty(_e.State.Downloads);
    }

    [Fact]
    public async Task OfflineCancelIsNotResumedLater()
    {
        await Connected();
        var t = await (await Pc()).OfferFilesAsync([new SlowSource(Source(5_000_000), 50_000, 20)], ["Laptop"], false);
        await _e.AcceptAsync(t.Id);
        await Until(() => Single?.Bytes > 100_000, "running");
        _go.Stop();
        _e.CancelDownload(t.Id);
        await Task.Delay(200);
        _go.Restart();
        await Eventually.True(async () =>
        {
            try
            {
                return await Status(t) == Core.Status.Failed;
            }
            catch (IOException)
            {
                return false;
            }
        }, "failed on server", 30);
        Assert.Empty(_e.State.Downloads);
    }

    [Fact]
    public async Task FailedDownloadIsShown()
    {
        await Connected();
        var t = await (await Pc()).OfferFilesAsync([Source(1000)], ["Laptop"], false);
        File.WriteAllBytes(_tmp.File("f.bin"), new byte[3]);
        await _e.AcceptAsync(t.Id);
        await Until(() => Single?.Phase == Phase.Failed, "failed");
        Assert.Contains("geändert", Single!.Error);
        Assert.Contains(_notices, n => n is Notice.DownloadFailed);
        Assert.False(_e.Busy);
    }

    [Fact]
    public async Task WithdrawnWhileDownloadingIsShownAsFailed()
    {
        await Connected();
        var pc = await Pc();
        var t = await pc.OfferFilesAsync([new SlowSource(Source(5_000_000), 50_000, 20)], ["Laptop"], false);
        await _e.AcceptAsync(t.Id);
        await Until(() => Single?.Bytes > 100_000, "running");
        await pc.CancelOutgoingAsync(t.Id);
        await Until(() => Single?.Phase == Phase.Failed, "failed");
    }

    private sealed class BrokenStore : IDownloadStore
    {
        public IPart Part(string id, int n, FileItem item) => throw new InvalidOperationException("broken");
        public void Forget(string id) => throw new InvalidOperationException("broken");
        public void Retain(IReadOnlySet<string> ids) => throw new InvalidOperationException("broken");
    }

    [Fact]
    public async Task CrashingStoreFailsTheDownloadOnly()
    {
        _e.Dispose();
        _e = New(new BrokenStore());
        await Connected();
        var t = await (await Pc()).OfferFilesAsync([Source(10)], ["Laptop"], false);
        await _e.AcceptAsync(t.Id);
        await Until(() => Single?.Phase == Phase.Failed, "failed");
        await Eventually.True(async () => await Status(t) == Core.Status.Failed, "reported");
    }

    [Fact]
    public async Task UnauthorisedIsVisible()
    {
        await Connected();
        Assert.False(_e.State.Unauthorised);
        using (var api = new Api(_acc)) await api.DeleteMeAsync();
        _go.Restart();
        await Until(() => _e.State is { Unauthorised: true, Connected: false }, "unauthorised");
    }

    [Fact]
    public async Task StaleParts()
    {
        var item = new FileItem("x", 100);
        using (var s = Store().Part("old", 0, item).Append(0)) s.WriteByte(1);
        _go.Stop();
        await Assert.ThrowsAnyAsync<IOException>(() => _e.RefreshAsync());
        Assert.Single(Files());
        _go.Restart();
        var t = (await _a.CreateTransferAsync(new(Kind.Files, false) { Files = [item], Targets = ["Laptop"] })).Transfer;
        using (var api = new Api(_acc)) await api.AcceptAsync(t.Id);
        using (var s = Store().Part(t.Id, 0, item).Append(0)) s.WriteByte(1);
        await _e.RefreshAsync();
        Assert.Contains(t.Id[..8], Assert.Single(Files()));
    }

    [Fact]
    public async Task ExpiredOffersLeaveIncomingWithoutServer()
    {
        await Connected();
        var t = await OfferText();
        await Until(() => _e.State.Incoming.Count > 0, "arrived");
        _go.Stop();
        _clock = () => t.ExpiresAt.AddSeconds(1);
        await Until(() => _e.State.Incoming.Count == 0 && _notices.Contains(new Notice.OfferGone(t.Id)), "gone");
    }

    [Fact]
    public async Task OwnOffersEndLocallyWhenServerStaysAway()
    {
        await Connected();
        var f = Source(10);
        var t = await _e.OfferFilesAsync([f], ["PC"], false);
        _go.Stop();
        await Until(() => !_e.State.Connected, "disconnected");
        await Task.Delay(200);
        Assert.Single(_e.State.Outgoing);
        _clock = () => t.ExpiresAt.AddMinutes(11);
        await Until(() => _e.State.Outgoing.Count == 0 && f.Released == 1, "ended");
    }

    [Fact]
    public async Task OutcomeOfOwnOffersStaysVisibleUntilDismissed()
    {
        await Connected();
        var pc = await Pc();
        var t1 = await _e.OfferFilesAsync([Source(1000)], ["PC"], false);
        await Until(() => pc.State.Incoming.Count > 0, "seen");
        await pc.AcceptAsync(t1.Id);
        await Until(() => _e.State.Sent.Count == 1 && _e.State.Sent[0].Deliveries.Single().Status == Core.Status.Delivered, "delivered");
        Assert.Empty(_e.State.Outgoing);
        Assert.Equal(t1.Id, _notices.OfType<Notice.OfferEnded>().Single().Transfer.Id);
        Assert.False(_e.Busy);
        var t2 = await _e.OfferTextAsync(Kind.Text, "x", ["PC"], false);
        await Until(() => pc.State.Incoming.Count > 0, "seen");
        await pc.DeclineAsync(t2.Id);
        await Until(() => _e.State.Sent.Select(s => s.Id).SequenceEqual([t1.Id, t2.Id]) && _e.State.Sent[1].Deliveries.Single().Status == Core.Status.Declined, "declined");
        _e.Dismiss(t1.Id);
        Assert.Equal([t2.Id], _e.State.Sent.Select(s => s.Id));
    }

    [Fact]
    public async Task CancelledOwnOffersLeaveNoRow()
    {
        await Connected();
        var t = await _e.OfferTextAsync(Kind.Text, "x", ["PC"], false);
        Assert.Single(_e.State.Outgoing);
        await _e.CancelOutgoingAsync(t.Id);
        await Task.Delay(300);
        Assert.Empty(_e.State.Sent);
        Assert.Empty(_e.State.Outgoing);
    }

    [Fact]
    public async Task OffersTheServerCancelledWhileOfflineAreReported()
    {
        await Connected();
        var t = await _e.OfferFilesAsync([Source(10)], ["PC"], false);
        _go.Stop();
        await Until(() => !_e.State.Connected, "disconnected");
        _go.Restart();
        await _a.DeclineAsync(t.Id); // ends it before (or while) the engine is back
        await Until(() => _e.State.Sent.Count == 1 && _e.State.Sent[0].Id == t.Id, "reported");
    }

    [Fact]
    public async Task DisposingLeavesNothingRunning()
    {
        await Connected();
        await _e.OfferFilesAsync([Source(10)], ["PC"], false);
        var t = await (await Pc()).OfferFilesAsync([new SlowSource(Source(5_000_000, "slow"), 50_000, 20)], ["Laptop"], false);
        await _e.AcceptAsync(t.Id);
        await Until(() => _e.State.Downloads.Count > 0 && _e.State.Outgoing.Count > 0, "busy");
        _e.Dispose();
        Assert.False(_e.Busy);
        Assert.Empty(_e.State.Downloads);
        Assert.Empty(_e.State.Outgoing);
        await Eventually.True(async () => !(await _a.DevicesAsync()).Single(d => d.Name == "Laptop").Online, "offline");
    }

    [Fact]
    public async Task OwnOffersAreNotIncoming()
    {
        await Connected();
        await _e.OfferTextAsync(Kind.Link, "https://x", [], true);
        await Task.Delay(300);
        Assert.Empty(_e.State.Incoming);
        Assert.Single(_e.State.Outgoing);
    }

    [Fact]
    public async Task ThrowingListenersDoNotStopTheEngine()
    {
        _e.StateChanged += () => throw new InvalidOperationException("listener");
        _e.Notified += _ => throw new InvalidOperationException("listener");
        await Connected();
        var t = await OfferText();
        await Until(() => _e.State.Incoming.Count > 0, "arrived");
        await _a.CancelAsync(t.Id);
        await Until(() => _e.State.Incoming.Count == 0, "gone");
    }
}
