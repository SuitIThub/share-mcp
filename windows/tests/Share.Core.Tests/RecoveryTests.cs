using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace Share.Core.Tests;

/// <summary>Single failing calls while the event connection stays up.</summary>
public sealed class RecoveryTests : IDisposable
{
    private readonly TempDir _tmp = new();
    private readonly Transfer _offer = new("t1", "peer", Kind.Text, DateTimeOffset.UtcNow.AddMinutes(5)) { Text = "x", CreatedAt = DateTimeOffset.UtcNow, Deliveries = [new("me", Status.Waiting)] };
    private readonly Transfer _own = new("o1", "me", Kind.Text, DateTimeOffset.UtcNow.AddMinutes(5)) { Text = "y", CreatedAt = DateTimeOffset.UtcNow, Deliveries = [new("peer", Status.Waiting)] };
    private readonly Dictionary<string, int> _failures = [];

    public void Dispose() => _tmp.Dispose();

    private static string Json(object o) => JsonSerializer.Serialize(o, o.GetType(), ShareJson.Options);

    private static async Task Hold(WebSocket ws)
    {
        var buf = new byte[1024];
        while (ws.State == WebSocketState.Open) await ws.ReceiveAsync(buf, default);
    }

    private Func<WebSocket, Task> _socket = Hold;

    private Resp Answer(Req q)
    {
        if (q.Path == "/v1/events") return new Resp(101) { WebSocket = _socket };
        lock (_failures)
            if (_failures.TryGetValue(q.Path, out var n) && n > 0)
            {
                _failures[q.Path] = n - 1;
                return new Resp(502, "bad gateway");
            }
        return (q.Method, q.Path) switch
        {
            ("GET", "/v1/devices") => new Resp(200, "[]"),
            ("GET", "/v1/transfers?role=incoming") => new Resp(200, Json(new List<Transfer> { _offer })),
            ("GET", "/v1/transfers?role=outgoing") => new Resp(200, "[]"),
            ("GET", "/v1/transfers/t1") => new Resp(200, Json(_offer)),
            ("GET", "/v1/transfers/o1") => new Resp(200, Json(_own with { Closed = true })),
            ("POST", "/v1/transfers") => new Resp(201, Json(new CreateTransferResponse(_own, ""))),
            _ => new Resp(404),
        };
    }

    private Engine Engine(string url) =>
        TestEngine.Create(new Api(new Account(url, "me", "tok", "PC", Identity.Generate("PC"))), new DiskDownloads(_tmp.File("dl"), _tmp.File("s.json")));

    [Theory]
    [InlineData("/v1/devices")]
    [InlineData("/v1/transfers?role=incoming")]
    [InlineData("/v1/transfers/t1")]
    public async Task FailedRefreshIsRepeatedWhileConnected(string path)
    {
        _failures[path] = 2;
        using var s = new ScriptedServer(Answer);
        using var e = Engine(s.Url);
        e.Start();
        await Eventually.True(() => e.State.Incoming.Count == 1, "offer shown after a failing call", 8);
        Assert.Single(s.Seen, q => q.Path == "/v1/events");
    }

    [Fact]
    public async Task FailedResyncIsRepeatedWhileConnected()
    {
        _failures["/v1/transfers?role=outgoing"] = 0;
        var drop = new TaskCompletionSource();
        _socket = ws => drop.Task;
        using var s = new ScriptedServer(Answer);
        using var e = Engine(s.Url);
        e.Start();
        await Eventually.True(() => e.State.Connected, "connected");
        await Task.Delay(300); // the first resync is over
        await e.OfferTextAsync(Kind.Text, "y", ["peer"], false);
        Assert.Single(e.State.Outgoing);
        lock (_failures) _failures["/v1/transfers?role=outgoing"] = 2;
        _socket = Hold;
        drop.SetResult(); // meanwhile the server closes the offer
        await Eventually.True(() => e.State is { Outgoing.Count: 0, Sent.Count: 1 }, "closed offer leaves the list", 8);
    }

    [Fact]
    public async Task RejectedTokenIsNotRepeated()
    {
        using var s = new ScriptedServer(q => q.Path == "/v1/events" ? new Resp(101) { WebSocket = Hold } : new Resp(401));
        using var e = Engine(s.Url);
        e.Start();
        await Eventually.True(() => e.State.Unauthorised, "unauthorised");
        await Task.Delay(500);
        Assert.InRange(s.Seen.Count(q => q.Path == "/v1/devices"), 1, 2);
    }

    /// <summary>After waking up, pooled connections may be dead without anybody having noticed.</summary>
    [Fact]
    public async Task ReconnectForgetsPooledConnections()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var connections = 0;
        _ = Task.Run(async () =>
        {
            while (true)
            {
                var c = await l.AcceptTcpClientAsync();
                var first = Interlocked.Increment(ref connections) == 1;
                _ = Task.Run(async () =>
                {
                    var st = c.GetStream();
                    var buf = new byte[8192];
                    while (await st.ReadAsync(buf) > 0)
                    {
                        await st.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 2\r\n\r\n[]"));
                        if (first) await Task.Delay(-1); // dead from now on
                    }
                });
            }
        });
        try
        {
            using var e = Engine($"http://127.0.0.1:{((IPEndPoint)l.LocalEndpoint).Port}");
            await e.Api.DevicesAsync();
            e.Reconnect();
            await e.Api.DevicesAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(2, connections);
        }
        finally
        {
            l.Stop();
        }
    }

    /// <summary>An error answer whose body never comes is a failed attempt, not our own cancelling.</summary>
    [Fact]
    public async Task StalledErrorAnswerIsAnIoError()
    {
        using var s = new ScriptedServer(_ => new Resp(502, new string('x', 100)) { Throttle = TimeSpan.FromMilliseconds(200) });
        using var api = new Api(new Account(s.Url, "me", "tok", "PC", Identity.Generate("PC")));
        await Assert.ThrowsAnyAsync<IOException>(() => api.RelayDownloadAsync("t1", 0, 0, 10, TimeSpan.FromMilliseconds(500), default));
    }

    [Fact]
    public async Task OwnCancellingOfAStalledErrorAnswerStaysACancellation()
    {
        using var s = new ScriptedServer(_ => new Resp(502, new string('x', 100)) { Throttle = TimeSpan.FromMilliseconds(200) });
        using var api = new Api(new Account(s.Url, "me", "tok", "PC", Identity.Generate("PC")));
        using var cts = new CancellationTokenSource(500);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => api.RelayDownloadAsync("t1", 0, 0, 10, TimeSpan.FromSeconds(30), cts.Token));
    }

    private sealed class ForgetfulStore(IDownloadStore inner) : IDownloadStore
    {
        public IPart Part(string id, int n, FileItem item) => inner.Part(id, n, item);
        public void Forget(string id) => throw new StorageBusyException("busy");
        public void Retain(IReadOnlySet<string> ids) => inner.Retain(ids);
    }

    /// <summary>Housekeeping that fails must not turn a complete download into a failed one.</summary>
    [Fact]
    public async Task DeliveredDownloadIsDoneEvenIfTheStateFileIsBusy()
    {
        var item = new FileItem("a.txt", 3);
        var t = new Transfer("f1", "peer", Kind.Files, DateTimeOffset.UtcNow.AddMinutes(5)) { Files = [item] };
        using var s = new ScriptedServer(q => q.Path switch
        {
            "/v1/transfers/f1/accept" => new Resp(200, "{}"),
            "/v1/transfers/f1/files/0" => new Resp(200, "abc"),
            _ => new Resp(204),
        });
        using var api = new Api(new Account(s.Url, "me", "tok", "PC", Identity.Generate("PC")));
        var refs = await new Receiver(api, new ForgetfulStore(new DiskDownloads(_tmp.File("dl"), _tmp.File("s.json")))).DownloadAsync(t, (_, _) => { });
        Assert.Equal("abc", File.ReadAllText(refs.Single()));
    }
}
