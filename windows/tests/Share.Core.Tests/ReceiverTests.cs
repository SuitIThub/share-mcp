using System.Diagnostics;

namespace Share.Core.Tests;

public sealed class ReceiverTests : IAsyncLifetime
{
    private readonly GoServer _go = new();
    private readonly TempDir _tmp = new();
    private CancellationTokenSource _events = new();
    private Api _a = null!, _b = null!;
    private Sender _s = null!;
    private Func<int, IReadOnlyList<string>> _lan = p => [$"127.0.0.1:{p}"];

    public async Task InitializeAsync()
    {
        _a = new Api(await _go.AccountAsync("PC"));
        _b = new Api(await _go.AccountAsync("Laptop"));
        _s = new Sender(_a, 0, p => _lan(p));
        await Connect();
    }

    public Task DisposeAsync()
    {
        _s.Dispose();
        _events.Cancel();
        _go.Dispose();
        _tmp.Dispose();
        return Task.CompletedTask;
    }

    private async Task Connect()
    {
        _events = new CancellationTokenSource();
        var ct = _events.Token;
        _ = Task.Run(async () =>
        {
            await foreach (var e in _a.EventsAsync(new EventOptions { BackoffBase = TimeSpan.FromMilliseconds(20) }, ct)) _s.OnEvent(e);
        });
        await Eventually.True(async () => (await _b.DevicesAsync()).Single(d => d.Name == "PC").Online, "online");
    }

    private string Dl => _tmp.File("dl");
    private DiskDownloads Store() => new(Dl, _tmp.File("state.json"));
    private string[] Files() => Directory.Exists(Dl) ? [.. Directory.GetFiles(Dl).Select(f => Path.GetFileName(f)!).Order(StringComparer.Ordinal)] : [];

    private Receiver NewReceiver(IDownloadStore? store = null) => new(_b, store ?? Store(), new ReceiverOptions
    {
        ProbeTimeout = TimeSpan.FromMilliseconds(500), IdleTimeout = TimeSpan.FromSeconds(5), BackoffBase = TimeSpan.FromMilliseconds(20),
    });

    private Probe Source(int size, string name = "f.bin") => new(DiskFile.Open(_tmp.File(name, TempDir.Random(size, name.Length))));
    private static byte[] Bytes(int size, string name = "f.bin") => TempDir.Random(size, name.Length);
    private Task<Transfer> Offer(params IFileSource[] src) => _s.OfferFilesAsync(src, ["Laptop"], false);
    private async Task<Delivery> Delivery(Transfer t) => (await _b.TransferAsync(t.Id)).Deliveries.Single();
    private static void Nothing(long n, DataPath p) { }

    [Fact]
    public async Task LanDownloadIsIdenticalAndReportsLan()
    {
        var t = await Offer(Source(3_000_000, "a.bin"), Source(10, "bb.bin"));
        var seen = new List<(long N, DataPath P)>();
        var refs = await NewReceiver().DownloadAsync(t, (n, p) =>
        {
            lock (seen) seen.Add((n, p));
        });
        Assert.Equal(Bytes(3_000_000, "a.bin"), File.ReadAllBytes(refs[0]));
        Assert.Equal(Bytes(10, "bb.bin"), File.ReadAllBytes(refs[1]));
        Assert.Equal(3_000_010, seen[^1].N);
        Assert.All(seen, s => Assert.Equal(DataPath.Lan, s.P));
        Assert.Equal(seen.Select(s => s.N).Order(), seen.Select(s => s.N));
        var d = await Delivery(t);
        Assert.Equal(Status.Delivered, d.Status);
        Assert.Equal(DataPath.Lan, d.Path);
        Assert.Equal(["a.bin", "bb.bin"], Files());
    }

    [Fact]
    public async Task FallsBackToRelayWhenProbeFails()
    {
        _lan = _ => ["192.0.2.1:1", "127.0.0.1:1"];
        var t = await Offer(Source(1_000_000));
        var sw = Stopwatch.StartNew();
        var refs = await NewReceiver().DownloadAsync(t, (_, p) => Assert.Equal(DataPath.Relay, p));
        Assert.True(sw.Elapsed.TotalSeconds < 3.5);
        Assert.Equal(Bytes(1_000_000), File.ReadAllBytes(refs.Single()));
        Assert.Equal(DataPath.Relay, (await Delivery(t)).Path);
    }

    [Fact]
    public async Task OneReachableCandidateIsEnough()
    {
        _lan = p => ["192.0.2.1:1", $"127.0.0.1:{p}", "not an address", "", "127.0.0.1"];
        var t = await Offer(Source(1000));
        await NewReceiver().DownloadAsync(t, (_, p) => Assert.Equal(DataPath.Lan, p));
    }

    [Fact]
    public async Task WrongFingerprintNeverSendsTokenToLan()
    {
        await using var evil = new TlsEcho(Identity.Generate("evil"));
        _lan = _ => [$"127.0.0.1:{evil.Port}"];
        var refs = await NewReceiver().DownloadAsync(await Offer(Source(1000)), (_, p) => Assert.Equal(DataPath.Relay, p));
        Assert.Equal(Bytes(1000), File.ReadAllBytes(refs.Single()));
        Assert.Equal(0, evil.Requests);
    }

    /// <summary>Starts a download, waits until at least this many bytes arrived, then kills it.</summary>
    private async Task InterruptAfter(Transfer t, long bytes)
    {
        using var cts = new CancellationTokenSource();
        long got = 0;
        var dl = NewReceiver().DownloadAsync(t, (n, _) => Volatile.Write(ref got, n), cts.Token);
        await Eventually.True(() => Volatile.Read(ref got) >= bytes, "bytes");
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => dl);
    }

    [Fact]
    public async Task ResumesAfterNewInstance()
    {
        var f = Source(4_000_000);
        var t = await Offer(new SlowSource(f, 100_000, 10));
        await InterruptAfter(t, 2_000_000);
        var have = Store().Part(t.Id, 0, f.Item).Size();
        Assert.InRange(have, 1_500_000, 3_999_999);
        var refs = await NewReceiver(Store()).DownloadAsync(await _b.TransferAsync(t.Id), (n, _) => Assert.True(n >= have));
        Assert.Equal(have, f.Opened[^1]);
        Assert.Equal(Bytes(4_000_000), File.ReadAllBytes(refs.Single()));
    }

    [Fact]
    public async Task FinishedFilesAreNotReloaded()
    {
        var f1 = Source(1000, "a.bin");
        var t = await Offer(f1, new SlowSource(Source(2_000_000, "bb.bin"), 50_000, 10));
        await InterruptAfter(t, 500_000);
        Assert.Single(f1.Opened);
        var refs = await NewReceiver(Store()).DownloadAsync(t, (n, _) => Assert.True(n > 1000));
        Assert.Single(f1.Opened);
        Assert.Equal(Bytes(1000, "a.bin"), File.ReadAllBytes(refs[0]));
        Assert.Equal(Bytes(2_000_000, "bb.bin"), File.ReadAllBytes(refs[1]));
        Assert.Equal(["a.bin", "bb.bin"], Files());
    }

    [Fact]
    public async Task EndpointLostMidFileContinuesViaRelay()
    {
        var t = await Offer(new SlowSource(Source(6_000_000), 100_000, 10));
        var paths = new List<DataPath>();
        var refs = await NewReceiver().DownloadAsync(t, (n, p) =>
        {
            if (paths.Count == 0 || paths[^1] != p) paths.Add(p);
            if (n > 1_000_000 && p == DataPath.Lan) _s.StopEndpointForTest();
        }).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(Bytes(6_000_000), File.ReadAllBytes(refs.Single()));
        Assert.Equal([DataPath.Lan, DataPath.Relay], paths);
        Assert.Equal(Status.Delivered, (await Delivery(t)).Status);
    }

    [Fact]
    public async Task ChangedSourceFailsAndReports()
    {
        var t = await Offer(Source(10, "a"), Source(1000, "bb"));
        File.WriteAllBytes(_tmp.File("bb"), new byte[5]);
        var e = await Assert.ThrowsAsync<DownloadFailedException>(() => NewReceiver().DownloadAsync(t, Nothing));
        Assert.Contains("geändert", e.Message);
        Assert.Equal(Status.Failed, (await Delivery(t)).Status);
        Assert.Equal(["a"], Files()); // the complete file stays
    }

    private sealed class WrapStore(IDownloadStore inner, Func<IPart, IPart> wrap) : IDownloadStore
    {
        public IPart Part(string id, int n, FileItem item) => wrap(inner.Part(id, n, item));
        public void Forget(string id) => inner.Forget(id);
        public void Retain(IReadOnlySet<string> ids) => inner.Retain(ids);
    }

    private sealed class WrapPart(IPart inner) : IPart
    {
        public Func<long>? SizeF;
        public Func<long, Stream>? AppendF;
        public string? FinishedRef => inner.FinishedRef;
        public long Size() => SizeF is null ? inner.Size() : SizeF();
        public Stream Append(long offset) => AppendF is null ? inner.Append(offset) : AppendF(offset);
        public void Reset() => inner.Reset();
        public string Finish() => inner.Finish();
    }

    /// <summary>A disk with room for only so many bytes.</summary>
    private sealed class FullStream(Stream inner, long room) : Stream
    {
        public override void Write(byte[] buffer, int offset, int count)
        {
            if (inner.Position + count > room) throw new IOException("There is not enough space on the disk");
            inner.Write(buffer, offset, count);
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            if (inner.Position + buffer.Length > room) throw new IOException("There is not enough space on the disk");
            return inner.WriteAsync(buffer, ct);
        }

        protected override void Dispose(bool disposing) => inner.Dispose();
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    [Fact]
    public async Task WriteErrorFailsAndLeavesNothing()
    {
        var t = await Offer(Source(1_000_000));
        var full = new WrapStore(Store(), p => new WrapPart(p) { AppendF = o => new FullStream(p.Append(o), 400_000) });
        var e = await Assert.ThrowsAsync<DownloadFailedException>(() => NewReceiver(full).DownloadAsync(t, Nothing));
        Assert.Contains("peicher", e.Message);
        Assert.Equal(Status.Failed, (await Delivery(t)).Status);
        Assert.Empty(Files());
    }

    [Fact]
    public async Task UnexpectedStorageErrorsFailToo()
    {
        var t = await Offer(Source(1000));
        var broken = new WrapStore(Store(), _ => throw new UnauthorizedAccessException("no"));
        await Assert.ThrowsAsync<DownloadFailedException>(() => NewReceiver(broken).DownloadAsync(t, Nothing));
        Assert.Equal(Status.Failed, (await Delivery(t)).Status);
    }

    [Fact]
    public async Task CancelledOfferStopsRetrying()
    {
        var t = await Offer(new SlowSource(Source(5_000_000), 50_000, 20));
        var dl = NewReceiver().DownloadAsync(t, (n, p) =>
        {
            if (n > 200_000) _ = _s.CancelAsync(t.Id);
        });
        await Assert.ThrowsAsync<OfferEndedException>(() => dl.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Empty(Files());
    }

    [Fact]
    public async Task ExpiredOrUnknownOffersEnd()
    {
        var t = await Offer(Source(10));
        await _s.CancelAsync(t.Id);
        foreach (var x in new[] { t, t with { Id = "nope" } }) await Assert.ThrowsAsync<OfferEndedException>(() => NewReceiver().DownloadAsync(x, Nothing));
    }

    [Fact]
    public async Task SenderGoneRetriesWhileAlive()
    {
        _lan = _ => [];
        var t = await Offer(Source(1000));
        _events.Cancel(); // sender offline: relay answers 502
        await Eventually.True(async () => !(await _b.DevicesAsync()).Single(d => d.Name == "PC").Online, "offline");
        var dl = NewReceiver().DownloadAsync(t, Nothing);
        await Task.Delay(500);
        Assert.False(dl.IsCompleted);
        await Connect();
        Assert.Equal(Bytes(1000), File.ReadAllBytes((await dl.WaitAsync(TimeSpan.FromSeconds(10))).Single()));
    }

    [Fact]
    public async Task ServerOutageDuringDownloadIsSurvived()
    {
        var t = await Offer(new SlowSource(Source(4_000_000), 100_000, 20));
        long got = 0;
        var dl = NewReceiver().DownloadAsync(t, (n, _) => Volatile.Write(ref got, n));
        await Eventually.True(() => Volatile.Read(ref got) > 500_000, "running");
        _go.Restart();
        Assert.Equal(Bytes(4_000_000), File.ReadAllBytes((await dl.WaitAsync(TimeSpan.FromSeconds(30))).Single()));
        Assert.Equal(Status.Delivered, (await Delivery(t)).Status);
    }

    private sealed class Named(IFileSource inner, string name) : IFileSource
    {
        public FileItem Item => inner.Item with { Name = name };
        public bool Unchanged() => inner.Unchanged();
        public Stream OpenAt(long offset) => inner.OpenAt(offset);
        public void Release() => inner.Release();
    }

    [Fact]
    public async Task EmptyAndHostileNames()
    {
        await NewReceiver().DownloadAsync(await Offer(Source(0, "empty.txt"), new Named(Source(1, "x"), "../evil"), new Named(Source(2, "yy"), "evil")), Nothing);
        Assert.Equal(["empty.txt", "evil", "evil (1)"], Files());
    }

    [Fact]
    public async Task CancelKeepsPartForResume()
    {
        var t = await Offer(new SlowSource(Source(3_000_000), 50_000, 10));
        var sw = Stopwatch.StartNew();
        await InterruptAfter(t, 300_000);
        Assert.True(sw.Elapsed.TotalSeconds < 5);
        Assert.Single(Files());
        Assert.Equal(Status.Downloading, (await Delivery(t)).Status);
    }

    [Fact]
    public async Task OversizedPartStartsOver()
    {
        var f = Source(1000);
        var t = await Offer(f);
        using (var s = Store().Part(t.Id, 0, f.Item).Append(0)) s.Write(new byte[2000]);
        Assert.Equal(Bytes(1000), File.ReadAllBytes((await NewReceiver().DownloadAsync(t, Nothing)).Single()));
    }

    [Fact]
    public async Task BusyStorageIsRetriedAndNeverMixesOffsets()
    {
        var f = Source(500_000);
        var t = await Offer(f);
        using (var s = Store().Part(t.Id, 0, f.Item).Append(0)) s.Write(Bytes(500_000), 0, 200_000);
        int sizes = 0, appends = 0;
        var flaky = new WrapStore(Store(), p => new WrapPart(p)
        {
            SizeF = () => sizes++ switch { 0 => throw new StorageBusyException("busy"), 1 => 0, _ => p.Size() },
            AppendF = o => appends++ == 1 ? throw new StorageBusyException("busy") : p.Append(o),
        });
        var refs = await NewReceiver(flaky).DownloadAsync(t, Nothing).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(Bytes(500_000), File.ReadAllBytes(refs.Single()));
    }

    // --- scripted server ---

    private async Task Scripted(Func<string, Resp> handler, Func<Receiver, Transfer, ScriptedServer, Task> test)
    {
        using var srv = new ScriptedServer(r => handler(r.Path.Replace("/v1/transfers/t1", "")));
        using var api = new Api(_b.Account with { ServerUrl = srv.Url });
        var t = new Transfer("t1", "s", Kind.Files, DateTimeOffset.UtcNow.AddMinutes(10)) { Files = [new FileItem("f", 5)] };
        await test(new Receiver(api, Store(), new ReceiverOptions
        {
            ProbeTimeout = TimeSpan.FromMilliseconds(100), IdleTimeout = TimeSpan.FromSeconds(2), BackoffBase = TimeSpan.FromMilliseconds(10), GiveUpAfter = TimeSpan.FromMilliseconds(300),
        }), t, srv);
    }

    private const string Alive = """{"id":"t1","sender_id":"s","kind":"files","expires_at":"2099-01-01T00:00:00Z","deliveries":[{"device_id":"x","status":"accepted"}]}""";

    private static async Task<string> Text(Receiver r, Transfer t) => File.ReadAllText((await r.DownloadAsync(t, Nothing).WaitAsync(TimeSpan.FromSeconds(15))).Single());
    private static int Count(ScriptedServer s, string end) => s.Seen.Count(r => r.Path.EndsWith(end, StringComparison.Ordinal));

    [Fact]
    public Task LostDeliveredResponseIsRetried()
    {
        var n = 0;
        return Scripted(p => p switch { "/accept" => new(200, "{}"), "/files/0" => new(200, "hello"), "/delivered" => n++ == 0 ? new(500) : new(204), _ => new(204) }, async (r, t, s) =>
        {
            Assert.Equal("hello", await Text(r, t));
            Assert.Equal(2, Count(s, "/delivered"));
        });
    }

    [Fact]
    public Task CompleteFilesCountAsSuccessEvenIfServerMovedOn() =>
        Scripted(p => p switch { "/accept" => new(200, "{}"), "/files/0" => new(200, "hello"), "/delivered" => new(409), _ => new(204) }, async (r, t, s) =>
        {
            Assert.Equal("hello", await Text(r, t));
            Assert.Equal(1, Count(s, "/delivered"));
        });

    [Fact]
    public Task ProxyErrorsOnRelayAreRetriedWhileAlive()
    {
        var n = 0;
        return Scripted(p => p switch
        {
            "/accept" => new(200, "{}"), "" => new(200, Alive),
            "/files/0" => n++ switch { 0 => new(404), 1 => new(403), 2 => new(503), _ => new(200, "hello") }, _ => new(204),
        }, async (r, t, _) => Assert.Equal("hello", await Text(r, t)));
    }

    /// <summary>e.g. a maintenance or login page answered with 200 by a proxy.</summary>
    [Fact]
    public Task AnswersOfTheWrongLengthAreNeverStored()
    {
        var n = 0;
        return Scripted(p => p switch
        {
            "/accept" => new(200, "{}"), "" => new(200, Alive), "/login" => new(200, "login"),
            "/files/0" => n++ switch { 0 => new(200, "<html>maintenance</html>"), 1 => new(200, "hi"), 2 => new(200, "<htm>") { Chunked = true }, 3 => new(302) { Headers = { ["Location"] = "/login" } }, _ => new(200, "hello") },
            _ => new(204),
        }, async (r, t, _) =>
        {
            Assert.Equal("hello", await Text(r, t));
            Assert.Equal(5, n);
        });
    }

    [Fact]
    public Task PartialAnswerFromTheWrongOffsetIsNotStored()
    {
        var n = 0;
        return Scripted(p => p switch
        {
            "/accept" => new(200, "{}"), "" => new(200, Alive),
            "/files/0" => n++ == 0 ? new(206, "xxx") { Headers = { ["Content-Range"] = "bytes 0-2/5" } } : new(206, "llo") { Headers = { ["Content-Range"] = "bytes 2-4/5" } },
            _ => new(204),
        }, async (r, t, _) =>
        {
            using (var s = Store().Part("t1", 0, t.Files[0]).Append(0)) s.Write("he"u8);
            Assert.Equal("hello", await Text(r, t));
        });
    }

    [Fact]
    public Task UnreachableServerLongAfterExpiryGivesUpButKeepsThePart() =>
        Scripted(p => p switch { "/accept" => new(200, "{}"), "/files/0" => new(200, "hel") { DeclaredLength = 5 }, _ => new(503) }, async (r, t, _) =>
        {
            await Assert.ThrowsAsync<DownloadFailedException>(() => r.DownloadAsync(t with { ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1) }, Nothing).WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Single(Files());
        });

    [Fact]
    public Task RevokedTokenEndsAtOnce() => Scripted(_ => new(401), async (r, t, s) =>
    {
        await Assert.ThrowsAsync<DownloadFailedException>(() => r.DownloadAsync(t, Nothing));
        Assert.Single(s.Seen);
    });

    [Fact]
    public Task SilentStreamIsAbandonedAndResumed()
    {
        var n = 0;
        return Scripted(p => p switch
        {
            "/accept" => new(200, "{}"), "" => new(200, Alive),
            "/files/0" => n++ == 0 ? new(200, "hello") { Throttle = TimeSpan.FromSeconds(4) } : new(206, "ello") { Headers = { ["Content-Range"] = "bytes 1-4/5" } },
            _ => new(204),
        }, async (r, t, _) => Assert.Equal("hello", await Text(r, t)));
    }

    [Fact]
    public Task ProgressIsReportedAtMostOncePerSecond()
    {
        var at = new List<long>();
        return Scripted(p =>
        {
            if (p == "/progress")
                lock (at)
                    at.Add(Stopwatch.GetTimestamp());
            return p switch { "/accept" => new(200, "{}"), "/files/0" => new(200, "hello") { Throttle = TimeSpan.FromMilliseconds(500) }, _ => new(204) };
        }, async (r, t, s) =>
        {
            await Text(r, t);
            Assert.InRange(at.Count, 2, 5);
            for (var i = 1; i < at.Count - 1; i++) Assert.True(Stopwatch.GetElapsedTime(at[i - 1], at[i]).TotalSeconds > 0.9);
            Assert.EndsWith("/delivered", s.Seen.Last().Path);
        });
    }

    [Fact]
    public Task SlowProgressReportsDoNotSlowTheDownload() =>
        Scripted(p => p switch { "/accept" => new(200, "{}"), "/files/0" => new(200, "hello"), "/progress" => new(204) { Delay = TimeSpan.FromSeconds(4) }, _ => new(204) }, async (r, t, _) =>
        {
            var sw = Stopwatch.StartNew();
            await Text(r, t);
            Assert.True(sw.Elapsed.TotalSeconds < 3.5);
        });
}
