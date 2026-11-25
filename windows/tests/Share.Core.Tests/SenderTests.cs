using System.Net.Sockets;

namespace Share.Core.Tests;

public sealed class SenderTests : IAsyncLifetime
{
    private readonly GoServer _go = new();
    private readonly TempDir _tmp = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly List<Transfer> _ended = [];
    private Api _a = null!, _b = null!;
    private Sender _s = null!;
    private int _lastPort;

    public async Task InitializeAsync()
    {
        _a = new Api(await _go.AccountAsync("PC"));
        _b = new Api(await _go.AccountAsync("Laptop"));
        _s = NewSender();
    }

    public Task DisposeAsync()
    {
        _s.Dispose();
        _cts.Cancel();
        _go.Dispose();
        _tmp.Dispose();
        return Task.CompletedTask;
    }

    private Sender NewSender() => new(_a, 0, p =>
    {
        _lastPort = p;
        return [$"127.0.0.1:{p}"];
    }, t =>
    {
        lock (_ended) _ended.Add(t);
    });

    private Probe Source(int size, string name = "f.bin") => new(DiskFile.Open(_tmp.File(name, TempDir.Random(size))));

    private static async Task<bool> PortOpen(int p)
    {
        using var c = new TcpClient();
        try
        {
            await c.ConnectAsync("127.0.0.1", p).WaitAsync(TimeSpan.FromMilliseconds(500));
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Connects the sender's events and waits until the server sees it online.</summary>
    private async Task Connect()
    {
        _ = Task.Run(async () =>
        {
            await foreach (var e in _a.EventsAsync(new EventOptions { BackoffBase = TimeSpan.FromMilliseconds(20) }, _cts.Token)) _s.OnEvent(e);
        });
        await Eventually.True(async () => (await _b.DevicesAsync()).Single(d => d.Name == "PC").Online, "online");
    }

    private async Task<Status> StatusAtB(string id) => (await _b.TransferAsync(id)).Deliveries.Single().Status;

    [Fact]
    public async Task EndpointRunsOnlyWhileFileOffersOpen()
    {
        await Connect();
        await _s.OfferTextAsync(Kind.Text, "hi", ["Laptop"], false);
        Assert.Equal(0, _lastPort);
        var t = await _s.OfferFilesAsync([Source(10)], ["Laptop"], false, "für dich");
        Assert.True(await PortOpen(_lastPort));
        Assert.Equal("für dich", (await _b.TransferAsync(t.Id)).Message);
        Assert.Equal([$"127.0.0.1:{_lastPort}"], (await _b.AcceptAsync(t.Id)).LanCandidates);
        Assert.Equal(2, _s.Offers.Count);
        await _s.CancelAsync(t.Id);
        Assert.False(await PortOpen(_lastPort));
        Assert.Single(_s.Offers);
        Assert.Equal(Status.Cancelled, await StatusAtB(t.Id));
    }

    [Fact]
    public async Task RelayRequestUploadsFromOffset()
    {
        await Connect();
        var f = Source(700_000);
        var t = await _s.OfferFilesAsync([Source(3, "x"), f], ["Laptop"], false);
        await _b.AcceptAsync(t.Id);
        using (var full = await _b.RelayDownloadAsync(t.Id, 1, 0, 700_000, TimeSpan.FromSeconds(30), default)) Assert.Equal(TempDir.Random(700_000), await full.Content.ReadAsByteArrayAsync());
        using (var rest = await _b.RelayDownloadAsync(t.Id, 1, 1000, 700_000, TimeSpan.FromSeconds(30), default)) Assert.Equal(TempDir.Random(700_000)[1000..], await rest.Content.ReadAsByteArrayAsync());
        Assert.Equal([0L, 1000L], f.Opened);
    }

    [Fact]
    public async Task ChangedFileIsNotUploaded()
    {
        await Connect();
        var f = Source(1000);
        var t = await _s.OfferFilesAsync([f], ["Laptop"], false);
        await _b.AcceptAsync(t.Id);
        File.WriteAllBytes(_tmp.File("f.bin"), new byte[999]);
        var e = await Assert.ThrowsAsync<ApiException>(() => _b.RelayDownloadAsync(t.Id, 0, 0, 1000, TimeSpan.FromSeconds(30), default));
        Assert.Equal(502, e.Status);
        Assert.Empty(f.Opened);
    }

    [Fact]
    public async Task FileChangedDuringRelayIsNeverMixed()
    {
        await Connect();
        var p = _tmp.File("f.bin", [.. Enumerable.Repeat((byte)'A', 4_000_000)]);
        var t = await _s.OfferFilesAsync([new SlowSource(DiskFile.Open(p), 100_000, 20)], ["Laptop"], false);
        await _b.AcceptAsync(t.Id);
        using var r = await _b.RelayDownloadAsync(t.Id, 0, 0, 4_000_000, TimeSpan.FromSeconds(30), default);
        await using var s = await r.Content.ReadAsStreamAsync();
        await s.ReadExactlyAsync(new byte[50_000]);
        await File.WriteAllBytesAsync(p, [.. Enumerable.Repeat((byte)'B', 4_000_000)]);
        using var m = new MemoryStream();
        await Assert.ThrowsAnyAsync<IOException>(() => s.CopyToAsync(m));
        Assert.DoesNotContain((byte)'B', m.ToArray());
    }

    [Fact]
    public async Task BogusRelayRequestsAreHarmless()
    {
        await Connect();
        var t = await _s.OfferFilesAsync([Source(1000)], ["Laptop"], false);
        foreach (var r in new[] { new RelayRequest("r", "unknown"), new("r", t.Id, 7), new("r", t.Id, -1), new("r", t.Id, 0, 5000), new("r", t.Id, 0, -1), new("gone", t.Id) })
            _s.OnEvent(new ServerEvent.Relay(r));
        await Task.Delay(300);
        await _b.AcceptAsync(t.Id);
        using var ok = await _b.RelayDownloadAsync(t.Id, 0, 0, 1000, TimeSpan.FromSeconds(30), default);
        Assert.Equal(1000, (await ok.Content.ReadAsByteArrayAsync()).Length);
    }

    [Fact]
    public async Task DeliveryUpdatesReachOffers()
    {
        await Connect();
        await _go.AccountAsync("Desk"); // second target keeps the offer open
        var t = await _s.OfferFilesAsync([Source(10)], ["Laptop", "Desk"], false);
        await _b.DeclineAsync(t.Id);
        await Eventually.True(() => _s.Offers[t.Id].Deliveries.Any(d => d.DeviceId == _b.Account.DeviceId && d.Status == Status.Declined), "declined");
        Assert.Equal(2, _s.Offers[t.Id].Deliveries.Count);
    }

    [Fact]
    public async Task LastDeclineClosesTheOffer()
    {
        await Connect();
        var f = Source(10);
        var t = await _s.OfferFilesAsync([f], ["Laptop"], false);
        await _b.DeclineAsync(t.Id);
        await Eventually.True(() => _s.Offers.Count == 0, "closed");
        await Eventually.True(() => f.Released == 1, "released");
        await Eventually.True(async () => !await PortOpen(_lastPort), "port closed");
        Assert.Equal([t.Id], _ended.Select(x => x.Id));
    }

    /// <summary>The receiver may answer before our create call has returned.</summary>
    [Fact]
    public async Task CloseBeforeCreateReturnsIsNotLost()
    {
        await Connect();
        _ = Task.Run(async () =>
        {
            await foreach (var e in _b.EventsAsync(new(), _cts.Token))
                if (e is ServerEvent.OfferNew n)
                    await _b.DeclineAsync(n.Transfer.Id);
        });
        await Eventually.True(async () => (await _a.DevicesAsync()).Single(d => d.Name == "Laptop").Online, "online");
        for (var i = 0; i < 40; i++) await _s.OfferTextAsync(Kind.Text, "x" + i, ["Laptop"], false);
        await Eventually.True(() => _s.Offers.Count == 0, "all closed");
    }

    [Fact]
    public async Task ClosedWhileDisconnectedIsDetectedOnReconnect()
    {
        var f = Source(10);
        var t = await _s.OfferFilesAsync([f], ["Laptop"], false);
        await _b.DeclineAsync(t.Id);
        Assert.Single(_s.Offers);
        _s.OnEvent(new ServerEvent.Connected());
        await Eventually.True(() => _s.Offers.Count == 0, "closed");
        await Eventually.True(async () => !await PortOpen(_lastPort), "port closed");
        Assert.Equal(1, f.Released);
        Assert.Single(_ended);
    }

    [Fact]
    public async Task ReconnectRefreshesDeliveries()
    {
        await _go.AccountAsync("Desk");
        var t = await _s.OfferFilesAsync([Source(10)], ["Laptop", "Desk"], false);
        await _b.DeclineAsync(t.Id);
        _s.OnEvent(new ServerEvent.Connected());
        await Eventually.True(() => _s.Offers[t.Id].Deliveries.Any(d => d.Status == Status.Declined), "declined");
    }

    [Fact]
    public async Task OrphanedFileOffersAreCancelledOnEveryConnect()
    {
        async Task<string> Orphan() => (await _a.CreateTransferAsync(new(Kind.Files, false) { Files = [new FileItem("x", 5)], Targets = ["Laptop"] })).Transfer.Id;
        var first = await Orphan();
        var text = (await _a.CreateTransferAsync(new(Kind.Text, false) { Text = "still there", Targets = ["Laptop"] })).Transfer.Id;
        _s.OnEvent(new ServerEvent.Connected());
        await Eventually.True(async () => await StatusAtB(first) == Status.Cancelled, "cancelled");
        Assert.Equal(Status.Waiting, await StatusAtB(text));
        await Eventually.True(() => _s.Offers.Keys.SequenceEqual([text]), "text adopted"); // texts survive a restart and stay cancellable
        var second = await Orphan();
        _s.OnEvent(new ServerEvent.Connected());
        await Eventually.True(async () => await StatusAtB(second) == Status.Cancelled, "cancelled again");
        await _s.CancelAsync(text);
        Assert.Equal(Status.Cancelled, await StatusAtB(text));
        Assert.Empty(_ended);
    }

    [Fact]
    public async Task OrphanCleanupNeverHitsOwnNewOffer()
    {
        for (var i = 0; i < 15; i++)
        {
            var name = "f" + i;
            var offer = Task.Run(() => _s.OfferFilesAsync([Source(10, name)], ["Laptop"], false));
            _ = Task.Run(() => _s.OnEvent(new ServerEvent.Connected()));
            _s.OnEvent(new ServerEvent.Connected());
            var t = await offer;
            await Task.Delay(50);
            Assert.Equal(Status.Waiting, await StatusAtB(t.Id));
            Assert.Contains(t.Id, _s.Offers.Keys);
        }
    }

    [Fact]
    public async Task TooLongTextFailsBeforeNetwork()
    {
        _go.Stop();
        foreach (var bad in new[] { new string('a', 65537), new string('ä', 32769), "", "  " })
            await Assert.ThrowsAsync<ArgumentException>(() => _s.OfferTextAsync(Kind.Text, bad, ["Laptop"], false));
        await Assert.ThrowsAsync<ArgumentException>(() => _s.OfferFilesAsync([], ["Laptop"], false));
    }

    [Fact]
    public async Task ReleaseCalledOnceOnEveryEnd()
    {
        await Connect();
        var f1 = Source(10, "1");
        var t1 = await _s.OfferFilesAsync([f1], ["Laptop"], false);
        await _s.CancelAsync(t1.Id);
        await _s.CancelAsync(t1.Id);
        _s.OnEvent(new ServerEvent.TransferClosed(t1.Id));
        Assert.Equal(1, f1.Released);

        var f2 = Source(10, "2");
        Assert.Equal(400, (await Assert.ThrowsAsync<ApiException>(() => _s.OfferFilesAsync([f2], ["Nobody"], false))).Status);
        Assert.Equal(1, f2.Released);
        await Eventually.True(async () => !await PortOpen(_lastPort), "endpoint closed");

        var f3 = Source(10, "3");
        await _s.OfferFilesAsync([f3], ["Laptop"], false);
        _s.Dispose();
        Assert.Equal(1, f3.Released);
        Assert.False(await PortOpen(_lastPort));
        Assert.Empty(_ended);

        var f4 = Source(10, "4");
        await Assert.ThrowsAnyAsync<IOException>(() => _s.OfferFilesAsync([f4], ["Laptop"], false));
        Assert.Equal(1, f4.Released);
    }

    [Fact]
    public async Task CancelWithoutNetworkStillEndsLocally()
    {
        var f = Source(10);
        var t = await _s.OfferFilesAsync([f], ["Laptop"], false);
        _go.Stop();
        await _s.CancelAsync(t.Id);
        Assert.Empty(_s.Offers);
        Assert.Equal(1, f.Released);
        // the server learns about it on reconnect
        _go.Restart();
        _s.OnEvent(new ServerEvent.Connected());
        await Eventually.True(async () => await StatusAtB(t.Id) == Status.Cancelled, "cancelled");
    }

    /// <summary>The offer exists on the server but we were told it failed: receivers must not wait for it.</summary>
    [Fact]
    public async Task LostAnswerOfCreateLeavesNoOrphan()
    {
        var real = _a.Account.ServerUrl;
        using var proxy = new LossyProxy(real);
        using var api = new Api(_a.Account with { ServerUrl = proxy.Url });
        using var s = new Sender(api, 0, p => []);
        proxy.DropNextAnswerTo = "POST /v1/transfers ";
        await Assert.ThrowsAnyAsync<IOException>(() => s.OfferFilesAsync([Source(10)], ["Laptop"], false));
        await Eventually.True(async () => (await _b.IncomingAsync()).Count == 0 && proxy.Dropped, "orphan cancelled");
    }

    [Fact]
    public async Task ChangesAreAnnounced()
    {
        var n = 0;
        _s.Changed += () => Interlocked.Increment(ref n);
        var t = await _s.OfferTextAsync(Kind.Text, "x", ["Laptop"], false);
        Assert.Equal(1, n);
        await _s.CancelAsync(t.Id);
        Assert.Equal(2, n);
    }
}
