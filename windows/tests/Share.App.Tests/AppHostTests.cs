using System.Collections.Concurrent;

namespace Share.App.Tests;

public sealed class FakeNotifier : INotifier
{
    public readonly ConcurrentQueue<string> Calls = new();
    public bool Enabled { get; set; } = true;
    public void Offer(Transfer t, string sender) => Calls.Enqueue($"offer {t.Id} {sender}");
    public void RemoveOffer(string id) => Calls.Enqueue("remove " + id);
    public void Result(Download d) => Calls.Enqueue($"result {d.Phase} {d.Error}");
    public void Text(Transfer t, string sender) => Calls.Enqueue($"text {t.Text} {sender}");
    public void Ended(Transfer t, Func<string, string> name) => Calls.Enqueue($"ended {string.Join(",", t.Deliveries.Select(d => name(d.DeviceId) + "=" + d.Status))}");
    public void Message(string title, string text) => Calls.Enqueue($"message {title}|{text}");
    public bool Has(string start) => Calls.Any(c => c.StartsWith(start, StringComparison.Ordinal));
}

public sealed class FakeDesktop : IDesktop
{
    public readonly ConcurrentQueue<string> Copied = new();
    public volatile bool Awake;
    public void CopyText(string text) => Copied.Enqueue(text);
    public Action<bool>? Waking;

    public void KeepAwake(bool on)
    {
        Waking?.Invoke(on);
        Awake = on;
    }
}

/// <summary>The program without windows, against a real server, plus a second device.</summary>
public sealed class HostRig : IAsyncDisposable
{
    public readonly GoServer Go = new();
    public readonly TempDir Tmp = new();
    public readonly FakeNotifier Notifier = new();
    public readonly FakeDesktop Desktop = new();
    public readonly AppHost Host;
    public Engine Peer = null!;

    public HostRig() => Host = new AppHost(new HostOptions(Tmp.File("data"), Tmp.File("dl")) { AllowLocalHttp = true, Engine = Options }, Notifier, Desktop);

    public static EngineOptions Options() => new()
    {
        Port = 0, Candidates = p => [$"127.0.0.1:{p}"], DeviceRefresh = TimeSpan.FromMilliseconds(300), Tick = TimeSpan.FromMilliseconds(50),
        Events = new EventOptions { BackoffBase = TimeSpan.FromMilliseconds(20) },
        Receiver = new ReceiverOptions { ProbeTimeout = TimeSpan.FromMilliseconds(500), BackoffBase = TimeSpan.FromMilliseconds(20) },
    };

    public Engine Engine => Host.Engine!;
    public string Me => Host.DeviceName;

    public async Task<HostRig> Start(bool paired = true)
    {
        Peer = new Engine(new Api(await Go.AccountAsync("Peer")), new DiskDownloads(Tmp.File("peer"), Tmp.File("peer.json")), Options());
        Peer.Start();
        await Until(() => Peer.State.Connected, "peer");
        if (paired)
        {
            Host.SetAccount(await Go.AccountAsync("PC"));
            await Until(() => Engine.State is { Connected: true, Devices.Count: > 0 } && Peer.State.Devices.Count > 0, "host");
        }
        return this;
    }

    public Task Until(Func<bool> c, string what, int seconds = 15) => Eventually.True(c, $"{what}; {string.Join(" / ", Notifier.Calls)}", seconds);

    public string File(string name, int size) => Tmp.File(name, TempDir.Random(size));

    public ValueTask DisposeAsync()
    {
        Host.Dispose();
        Peer?.Dispose();
        Go.Dispose();
        Tmp.Dispose();
        return ValueTask.CompletedTask;
    }
}

public sealed class AppHostTests
{
    private static IFileSource Slow(HostRig r, string name, int size) => new SlowSource(DiskFile.Open(r.File(name, size)), 50_000, 20);

    [Fact]
    public async Task PairingSurvivesARestart()
    {
        await using var r = await new HostRig().Start();
        using var again = new AppHost(new HostOptions(r.Tmp.File("data"), r.Tmp.File("dl")) { Engine = HostRig.Options }, new FakeNotifier(), new FakeDesktop());
        Assert.Equal("PC", again.DeviceName);
        Assert.NotNull(again.Engine);
    }

    [Fact]
    public async Task UnpairedHasNoEngine()
    {
        await using var r = await new HostRig().Start(paired: false);
        Assert.Null(r.Host.Engine);
        Assert.Equal("", r.Host.DeviceName);
        r.Host.Run(_ => throw new InvalidOperationException()); // nothing to run on
    }

    [Fact]
    public async Task OffersAreAnnouncedOnlyWhileHidden()
    {
        await using var r = await new HostRig().Start();
        var t1 = await r.Peer.OfferTextAsync(Kind.Text, "hallo", ["PC"], false);
        await r.Until(() => r.Notifier.Has($"offer {t1.Id} Peer"), "announced");
        await r.Peer.CancelOutgoingAsync(t1.Id);
        await r.Until(() => r.Notifier.Has("remove " + t1.Id), "removed");
        Transfer t2;
        using (r.Host.UiShown())
        {
            t2 = await r.Peer.OfferTextAsync(Kind.Text, "hallo", ["PC"], false);
            await r.Until(() => r.Engine.State.Incoming.Any(x => x.Id == t2.Id), "arrived");
            await Task.Delay(200);
            Assert.False(r.Notifier.Has("offer " + t2.Id));
        }
        await r.Until(() => r.Notifier.Has("offer " + t2.Id), "announced when the window hides");
        r.Host.UiShown().Dispose();
        await Task.Delay(200);
        Assert.Single(r.Notifier.Calls, c => c.StartsWith("offer " + t2.Id)); // a dismissed one must not come back
    }

    [Fact]
    public async Task AnsweringRemovesTheNotification()
    {
        await using var r = await new HostRig().Start();
        var t = await r.Peer.OfferTextAsync(Kind.Text, "hallo", ["PC"], false);
        await r.Until(() => r.Notifier.Has("offer " + t.Id), "announced");
        await r.Engine.DeclineAsync(t.Id);
        await r.Until(() => r.Notifier.Has("remove " + t.Id), "removed");
    }

    [Fact]
    public async Task AcceptedTextIsCopiedAndAnnounced()
    {
        await using var r = await new HostRig().Start();
        var t = await r.Peer.OfferTextAsync(Kind.Text, "geheim 123", ["PC"], false);
        await r.Until(() => r.Engine.State.Incoming.Count > 0, "arrived");
        r.Host.Run(e => e.AcceptAsync(t.Id));
        await r.Until(() => r.Desktop.Copied.Contains("geheim 123") && r.Notifier.Has("text geheim 123 Peer"), "copied");
    }

    [Fact]
    public async Task ResultsAreAnnounced()
    {
        await using var r = await new HostRig().Start();
        var t = await r.Peer.OfferFilesAsync([DiskFile.Open(r.File("a.bin", 1000))], ["PC"], false);
        await r.Until(() => r.Engine.State.Incoming.Count > 0, "arrived");
        r.Host.Run(e => e.AcceptAsync(t.Id));
        await r.Until(() => r.Notifier.Has("result Done"), "done");
        Assert.Equal(TempDir.Random(1000), File.ReadAllBytes(r.Tmp.File("dl\\a.bin")));
        var o = await r.Engine.OfferTextAsync(Kind.Text, "x", ["Peer"], false);
        await r.Until(() => r.Peer.State.Incoming.Count > 0, "seen");
        await r.Peer.DeclineAsync(o.Id);
        await r.Until(() => r.Notifier.Has("ended Peer=Declined"), "ended");
    }

    [Fact]
    public async Task StaleAnswersAreHarmless()
    {
        await using var r = await new HostRig().Start();
        var t = await r.Peer.OfferTextAsync(Kind.Text, "x", ["PC"], false);
        await r.Until(() => r.Engine.State.Incoming.Count > 0, "arrived");
        await r.Peer.CancelOutgoingAsync(t.Id);
        for (var i = 0; i < 2; i++)
        {
            r.Host.Run(e => e.AcceptAsync(t.Id));
            r.Host.Run(e => e.DeclineAsync(t.Id));
            r.Host.Run(e => e.AcceptAsync("unknown"));
        }
        await r.Until(() => r.Notifier.Has("message Angebot nicht mehr verfügbar"), "told");
    }

    [Fact]
    public async Task ErrorsAreTold()
    {
        await using var r = await new HostRig().Start();
        r.Host.Run(_ => throw new ApiException(400, "unknown target"));
        r.Host.Run(_ => throw new SourceException("„x“ kann nicht gelesen werden"));
        r.Host.Run(_ => throw new IOException("x"));
        r.Host.Run(_ => throw new InvalidOperationException("bug"));
        await r.Until(() => r.Notifier.Calls.Count(c => c.StartsWith("message")) == 4, "told");
        Assert.True(r.Notifier.Has("message Server lehnt ab|unknown target"));
        Assert.True(r.Notifier.Has("message „x“ kann nicht gelesen werden|"));
        Assert.True(r.Notifier.Has("message Keine Verbindung zum Server|"));
        Assert.True(r.Notifier.Has("message Unerwarteter Fehler|bug"));
    }

    [Fact]
    public async Task VisibleWindowShowsMessagesItself()
    {
        await using var r = await new HostRig().Start();
        var got = new ConcurrentQueue<string>();
        r.Host.Message += (a, b) => got.Enqueue(a + "|" + b);
        using (r.Host.UiShown()) r.Host.Tell("Hallo", "Welt");
        Assert.Equal(["Hallo|Welt"], got);
        Assert.False(r.Notifier.Has("message"));
        r.Host.Tell("Hallo");
        Assert.True(r.Notifier.Has("message Hallo|"));
    }

    /// <summary>A window behind another one is no reason to keep quiet.</summary>
    [Fact]
    public async Task UnattendedWindowGetsNotificationsToo()
    {
        await using var r = await new HostRig().Start();
        var got = new ConcurrentQueue<string>();
        r.Host.Message += (a, b) => got.Enqueue(a + "|" + b);
        r.Host.WindowOpen = true;
        var t = await r.Peer.OfferTextAsync(Kind.Text, "hallo", ["PC"], false);
        await r.Until(() => r.Notifier.Has("offer " + t.Id), "announced");
        r.Host.Run(e => e.AcceptAsync(t.Id));
        await r.Until(() => r.Notifier.Has("text hallo") && got.Contains("In die Zwischenablage kopiert|"), "both");
        r.Host.Tell("Hallo");
        Assert.True(r.Notifier.Has("message Hallo|"));
        Assert.Contains("Hallo|", got);
    }

    /// <summary>Without notifications the message has to wait in the window.</summary>
    [Fact]
    public async Task MessagesAreNotLostWithoutNotifications()
    {
        await using var r = await new HostRig().Start();
        r.Notifier.Enabled = false;
        var got = new ConcurrentQueue<string>();
        r.Host.Message += (a, b) => got.Enqueue(a + "|" + b);
        r.Host.Tell("Hallo");
        Assert.Equal(["Hallo|"], got);
    }

    /// <summary>The last word on sleeping must be the true one.</summary>
    [Fact]
    public async Task RacingSleepUpdatesKeepTheirOrder()
    {
        await using var r = await new HostRig().Start();
        var entered = new TaskCompletionSource();
        r.Desktop.Waking = on =>
        {
            if (!on) return;
            entered.TrySetResult();
            Thread.Sleep(400); // the other update overtakes
        };
        var offering = Task.Run(() => r.Engine.OfferFilesAsync([DiskFile.Open(r.File("a", 10))], ["Peer"], false));
        await entered.Task;
        await Task.Run(() => r.Host.SetAccount(null));
        await offering;
        Assert.False(r.Desktop.Awake);
    }

    [Fact]
    public async Task SleepIsPreventedOnlyWhileBusy()
    {
        await using var r = await new HostRig().Start();
        Assert.False(r.Desktop.Awake);
        await r.Engine.OfferTextAsync(Kind.Text, "x", ["Peer"], false);
        Assert.False(r.Desktop.Awake);
        var t = await r.Engine.OfferFilesAsync([DiskFile.Open(r.File("a", 10))], ["Peer"], false);
        await r.Until(() => r.Desktop.Awake, "offering");
        await r.Engine.CancelOutgoingAsync(t.Id);
        await r.Until(() => !r.Desktop.Awake, "idle");
        var d = await r.Peer.OfferFilesAsync([Slow(r, "b", 2_000_000)], ["PC"], false);
        r.Host.Run(e => e.AcceptAsync(d.Id));
        await r.Until(() => r.Desktop.Awake, "downloading");
        r.Host.SetAccount(null);
        await r.Until(() => !r.Desktop.Awake, "unpaired");
    }

    [Fact]
    public async Task RenameReachesTheServerAndSurvives()
    {
        await using var r = await new HostRig().Start();
        await r.Host.RenameAsync("Neuer PC");
        Assert.Equal("Neuer PC", r.Host.DeviceName);
        Assert.Contains(await r.Peer.Api.DevicesAsync(), d => d.Name == "Neuer PC");
        Assert.Equal("Neuer PC", new AccountStore(r.Tmp.File("data\\account.bin")).Load()!.Name);
        await Assert.ThrowsAsync<ApiException>(() => r.Host.RenameAsync("peer"));
        Assert.Equal("Neuer PC", r.Host.DeviceName);
    }

    [Fact]
    public async Task UnpairRemovesTheDevice()
    {
        await using var r = await new HostRig().Start();
        var t = await r.Peer.OfferTextAsync(Kind.Text, "x", ["PC"], false);
        await r.Until(() => r.Notifier.Has("offer " + t.Id), "announced");
        await r.Host.UnpairAsync();
        Assert.Null(r.Host.Engine);
        Assert.DoesNotContain(await r.Peer.Api.DevicesAsync(), d => d.Name == "PC");
        Assert.True(r.Notifier.Has("remove " + t.Id)); // nothing to answer any more
        Assert.False(r.Notifier.Has("message"));
    }

    [Fact]
    public async Task UnpairWithoutServer()
    {
        await using var r = await new HostRig().Start();
        r.Go.Stop();
        await r.Host.UnpairAsync();
        Assert.Null(r.Host.Engine);
        Assert.True(r.Notifier.Has("message Lokal abgemeldet|"));
    }

    [Fact]
    public async Task UnpairOfARevokedDeviceIsQuiet()
    {
        await using var r = await new HostRig().Start();
        using (var api = new Api(r.Engine.Api.Account)) await api.DeleteMeAsync();
        await r.Host.UnpairAsync();
        Assert.Null(r.Host.Engine);
        Assert.False(r.Notifier.Has("message"));
    }

    [Fact]
    public async Task LeftOverTemporaryFilesAreRemovedAtStart()
    {
        await using var r = await new HostRig().Start(paired: false);
        Directory.CreateDirectory(r.Host.TempDir);
        File.WriteAllText(Path.Combine(r.Host.TempDir, "alt.png"), "x");
        using var again = new AppHost(new HostOptions(r.Tmp.File("data"), r.Tmp.File("dl")), new FakeNotifier(), new FakeDesktop());
        Assert.False(File.Exists(Path.Combine(r.Host.TempDir, "alt.png")));
    }

    [Fact]
    public async Task EngineChangesAreAnnounced()
    {
        await using var r = await new HostRig().Start(paired: false);
        var n = 0;
        r.Host.EngineChanged += () => n++;
        r.Host.SetAccount(await r.Go.AccountAsync("PC"));
        r.Host.SetAccount(null);
        Assert.Equal(2, n);
    }
}
