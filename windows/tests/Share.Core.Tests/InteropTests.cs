using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;

namespace Share.Core.Tests;

/// <summary>The Windows client against the Go CLI, the Kotlin client and itself, through the real server.</summary>
public sealed class InteropTests : IDisposable
{
    private readonly GoServer _go = new();
    private readonly TempDir _tmp = new();
    private readonly List<Engine> _engines = [];
    private readonly ConcurrentQueue<Notice> _notices = new();

    public void Dispose()
    {
        _engines.ForEach(e => e.Dispose());
        _go.Dispose();
        _tmp.Dispose();
    }

    private async Task<Engine> NewEngine(string name, Account? account = null, bool lan = true)
    {
        var api = new Api(account ?? await _go.AccountAsync(name));
        var store = new DiskDownloads(_tmp.File("dl-" + name), _tmp.File(name + ".json"));
        var e = lan ? TestEngine.Create(api, store) : new Engine(api, store, new EngineOptions { Port = 0, Candidates = _ => [], Events = new EventOptions { BackoffBase = TimeSpan.FromMilliseconds(20) } });
        e.Notified += _notices.Enqueue;
        _engines.Add(e);
        e.Start();
        await Until(() => e.State.Connected, name + " connected");
        return e;
    }

    private Task Until(Func<bool> c, string what, int seconds = 30) => Eventually.True(c, $"{what}; {string.Join(" | ", _engines.Select(e => e.State))}", seconds);

    private byte[] Got(string engine, string file) => File.ReadAllBytes(Path.Combine(_tmp.File("dl-" + engine), file));
    private string Make(string name, int size) => _tmp.File(name, TempDir.Random(size, name.Length));
    private static byte[] Bytes(string name, int size) => TempDir.Random(size, name.Length);
    private IFileSource Source(string name, int size) => DiskFile.Open(Make(name, size));

    private async Task<Transfer> AcceptFirst(Engine e)
    {
        await Until(() => e.State.Incoming.Count > 0, "offer");
        var t = e.State.Incoming[0];
        await e.AcceptAsync(t.Id);
        return t;
    }

    private async Task<Download> Done(Engine e, int seconds = 60)
    {
        await Until(() => e.State.Downloads.Count == 1 && e.State.Downloads[0].Phase != Phase.Running, "done", seconds);
        Assert.Null(e.State.Downloads[0].Error);
        return e.State.Downloads[0];
    }

    private Process Cli(params string[] args)
    {
        var psi = new ProcessStartInfo(GoServer.ShareExe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        psi.ArgumentList.Add("-config");
        psi.ArgumentList.Add(_tmp.File("cli.json"));
        foreach (var a in args) psi.ArgumentList.Add(a);
        return Process.Start(psi)!;
    }

    private static async Task<string> Exit(Process p)
    {
        using (p)
        {
            var o = await (p.StandardOutput.ReadToEndAsync()).WaitAsync(TimeSpan.FromSeconds(60));
            await p.WaitForExitAsync();
            Assert.True(p.ExitCode == 0, o);
            return o;
        }
    }

    [Fact]
    public async Task GoCliSendsFilesOverLan()
    {
        var pc = await NewEngine("PC");
        await Exit(Cli("pair", "-name", "Bot", _go.Url, _go.PairCode()));
        var p = Cli("send", Make("a.bin", 20_000_000), Make("leer.txt", 0), Make("ümläut ß.txt", 1000), "--to", "pc");
        await AcceptFirst(pc);
        var d = await Done(pc);
        Assert.Equal("Bot", pc.DeviceName(d.Transfer.SenderId));
        Assert.Equal(DataPath.Lan, d.Path);
        Assert.Equal(Bytes("a.bin", 20_000_000), Got("PC", "a.bin"));
        Assert.Empty(Got("PC", "leer.txt"));
        Assert.Equal(Bytes("ümläut ß.txt", 1000), Got("PC", "ümläut ß.txt"));
        await Exit(p);
    }

    [Fact]
    public async Task GoCliSendsTextAndLink()
    {
        var pc = await NewEngine("PC");
        await Exit(Cli("pair", "-name", "Bot", _go.Url, _go.PairCode()));
        await Exit(Cli("text", "Hallo Welt", "--to", "PC"));
        await Exit(Cli("link", "https://example.org/a?b=c", "--all"));
        for (var n = 2; n > 0; n--)
        {
            await Until(() => pc.State.Incoming.Count == n, "offers");
            await AcceptFirst(pc);
        }
        Assert.Equal(new[] { (Kind.Link, "https://example.org/a?b=c"), (Kind.Text, "Hallo Welt") }, pc.State.Received.Select(r => (r.Kind, r.Text)).OrderBy(r => r.Kind == Kind.Text));
    }

    [Fact]
    public async Task BroadcastToTwoEngines()
    {
        var sender = await NewEngine("Desk");
        var x = await NewEngine("X");
        var y = await NewEngine("Y");
        var z = await NewEngine("Z");
        var late = await _go.AccountAsync("Late");
        var t = await sender.OfferFilesAsync([Source("b.bin", 8_000_000)], [], true);
        await Until(() => x.State.Incoming.Count + y.State.Incoming.Count + z.State.Incoming.Count == 3, "all see it");
        await z.DeclineAsync(t.Id);
        await Task.WhenAll(x.AcceptAsync(t.Id), y.AcceptAsync(t.Id));
        await Done(x);
        await Done(y);
        Assert.Equal(Bytes("b.bin", 8_000_000), Got("X", "b.bin"));
        Assert.Equal(Bytes("b.bin", 8_000_000), Got("Y", "b.bin"));
        await Until(() => sender.State.Outgoing.Single().Deliveries.ToDictionary(d => sender.DeviceName(d.DeviceId), d => d.Status) is var s
            && s.GetValueOrDefault("X") == Status.Delivered && s.GetValueOrDefault("Y") == Status.Delivered && s.GetValueOrDefault("Z") == Status.Declined && s["Late"] == Status.Waiting, "sender knows");
        var l = await NewEngine("Late", late);
        await Until(() => l.State.Incoming.Count == 1, "late sees it");
    }

    [Fact]
    public async Task SwitchesPathMidTransfer()
    {
        var sender = await NewEngine("Desk");
        var x = await NewEngine("X");
        await sender.OfferFilesAsync([new SlowSource(Source("c.bin", 64_000_000), 1_000_000, 15)], ["X"], false);
        await AcceptFirst(x);
        await Until(() => x.State.Downloads[0] is { Bytes: > 5_000_000, Path: DataPath.Lan }, "lan running");
        sender.LoseLanForTest();
        Assert.Equal(DataPath.Relay, (await Done(x)).Path);
        Assert.Equal(Bytes("c.bin", 64_000_000), Got("X", "c.bin"));
    }

    [Fact]
    public async Task ServerRestartMidLanDownload()
    {
        var sender = await NewEngine("Desk");
        var x = await NewEngine("X");
        await sender.OfferFilesAsync([new SlowSource(Source("e.bin", 20_000_000), 500_000, 50)], ["X"], false);
        await AcceptFirst(x);
        await Until(() => x.State.Downloads[0].Bytes > 2_000_000, "running");
        _go.Restart();
        Assert.Equal(DataPath.Lan, (await Done(x)).Path);
        Assert.Equal(Bytes("e.bin", 20_000_000), Got("X", "e.bin"));
        await Until(() => sender.State.Outgoing.Count == 0 && sender.State.Sent.Single().Deliveries.Single().Status == Status.Delivered, "sender knows");
    }

    [Fact]
    public async Task ServerRestartMidRelayDownload()
    {
        var sender = await NewEngine("Desk", lan: false);
        var x = await NewEngine("X");
        await sender.OfferFilesAsync([new SlowSource(Source("g.bin", 6_000_000), 200_000, 40)], ["X"], false);
        await AcceptFirst(x);
        await Until(() => x.State.Downloads[0] is { Bytes: > 1_000_000, Path: DataPath.Relay }, "running");
        _go.Restart();
        await Done(x);
        Assert.Equal(Bytes("g.bin", 6_000_000), Got("X", "g.bin"));
    }

    [Fact]
    public async Task SenderDropsConnectionBriefly()
    {
        var x = await NewEngine("X");
        var acc = await _go.AccountAsync("Desk");
        using var api = new Api(acc);
        using var desk = new Sender(api, 0, _ => []);
        CancellationTokenSource Listen()
        {
            var c = new CancellationTokenSource();
            _ = Task.Run(async () =>
            {
                await foreach (var e in api.EventsAsync(new EventOptions(), c.Token)) desk.OnEvent(e);
            });
            return c;
        }
        var events = Listen();
        await desk.OfferFilesAsync([new SlowSource(Source("d.bin", 3_000_000), 100_000, 30)], ["X"], false);
        await AcceptFirst(x);
        await Until(() => x.State.Downloads[0] is { Bytes: > 500_000, Path: DataPath.Relay }, "relay running");
        events.Cancel();
        await Task.Delay(5000);
        events = Listen();
        await Done(x);
        Assert.Equal(Bytes("d.bin", 3_000_000), Got("X", "d.bin"));
        events.Cancel();
    }

    [Fact]
    public async Task CancelWhileDownloading()
    {
        var sender = await NewEngine("Desk");
        var x = await NewEngine("X");
        var t = await sender.OfferFilesAsync([new SlowSource(Source("h.bin", 20_000_000), 200_000, 50)], ["X"], false);
        await AcceptFirst(x);
        await Until(() => x.State.Downloads[0].Bytes > 1_000_000, "running");
        await sender.CancelOutgoingAsync(t.Id);
        await Until(() => x.State.Downloads[0].Phase == Phase.Failed, "ended");
        Assert.Empty(Directory.GetFiles(_tmp.File("dl-X")));
    }

    // --- Kotlin ---

    private static string SourceDir([CallerFilePath] string path = "") => Path.GetDirectoryName(path)!;

    private sealed record Peer(Process Process, ConcurrentQueue<string> Lines);

    private Peer Kotlin(params string[] args)
    {
        var android = Path.GetFullPath(Path.Combine(SourceDir(), "..", "..", "..", "android"));
        var psi = new ProcessStartInfo(Path.Combine(android, "gradlew.bat")) { WorkingDirectory = android, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8 };
        psi.ArgumentList.Add("-q");
        psi.ArgumentList.Add("--console=plain");
        psi.ArgumentList.Add(":core:peer");
        psi.Environment["PEER_ARGS"] = string.Join("|", args); // the command line of a .bat could not carry them
        if (Environment.GetEnvironmentVariable("JAVA_HOME_17") is { } java) psi.Environment["JAVA_HOME"] = java;
        var lines = new ConcurrentQueue<string>();
        var p = Process.Start(psi)!;
        p.OutputDataReceived += (_, e) => lines.Enqueue(e.Data ?? "EOF");
        p.ErrorDataReceived += (_, e) => lines.Enqueue(e.Data ?? "EOF");
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        return new Peer(p, lines);
    }

    private static async Task<string> Line(Peer p, string start)
    {
        await Eventually.True(() => p.Lines.Any(l => l.StartsWith(start, StringComparison.Ordinal)), "kotlin: " + start + "; " + string.Join("\n", p.Lines), 180);
        return p.Lines.First(l => l.StartsWith(start, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("lan")]
    [InlineData("relay")]
    public async Task WindowsReceivesFromKotlin(string mode)
    {
        var pc = await NewEngine("PC");
        var k = Kotlin(_go.Url, _go.PairCode(), "Kotlin", mode, "send", Make("kotlin.bin", 20_000_000), "PC");
        await Until(() => pc.State.Incoming.Count > 0, "offer", 180);
        await AcceptFirst(pc);
        var d = await Done(pc);
        Assert.Equal(mode, d.Path!.Value.ToString().ToLowerInvariant());
        Assert.Equal(Bytes("kotlin.bin", 20_000_000), Got("PC", "kotlin.bin"));
        Assert.Equal("ENDED DELIVERED " + mode.ToUpperInvariant(), await Line(k, "ENDED"));
        k.Process.Dispose();
    }

    [Theory]
    [InlineData("lan")]
    [InlineData("relay")]
    public async Task KotlinReceivesFromWindows(string mode)
    {
        var pc = await NewEngine("PC", lan: mode == "lan");
        var k = Kotlin(_go.Url, _go.PairCode(), "Kotlin", mode, "receive", _tmp.Path);
        await Line(k, "READY");
        await pc.OfferFilesAsync([Source("windows ü.bin", 20_000_000)], ["Kotlin"], false);
        Assert.StartsWith("ENDED DONE " + mode.ToUpperInvariant(), await Line(k, "ENDED"));
        Assert.Equal(Bytes("windows ü.bin", 20_000_000), File.ReadAllBytes(Path.Combine(_tmp.Path, "kotlin-Kotlin", "windows ü.bin")));
        await Until(() => pc.State.Sent.Count == 1 && pc.State.Sent[0].Deliveries.Single().Status == Status.Delivered, "sender knows");
        k.Process.Dispose();
    }
}
