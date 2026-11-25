using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;

namespace Share.Core.Tests;

public sealed class EventsTests : IDisposable
{
    private readonly GoServer _go = new();
    private readonly CancellationTokenSource _cts = new();

    public void Dispose()
    {
        _cts.Cancel();
        _go.Dispose();
    }

    private ChannelReader<ServerEvent> Listen(Api api, EventOptions? o = null)
    {
        var ch = Channel.CreateUnbounded<ServerEvent>();
        _ = Task.Run(async () =>
        {
            try
            {
                await foreach (var e in api.EventsAsync(o ?? new(), _cts.Token)) ch.Writer.TryWrite(e);
                ch.Writer.Complete();
            }
            catch (Exception e)
            {
                ch.Writer.Complete(e);
            }
        });
        return ch.Reader;
    }

    private static async Task<ServerEvent> Next(ChannelReader<ServerEvent> r) => await r.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));

    private static EventOptions Fast => new() { BackoffBase = TimeSpan.FromMilliseconds(20) };

    private async Task Online(Api watcher, string name) =>
        await Eventually.True(async () => (await watcher.DevicesAsync()).Single(d => d.Name == name).Online, name + " online");

    [Fact]
    public async Task ReceivesOfferNew()
    {
        var a = new Api(await _go.AccountAsync("PC"));
        var b = new Api(await _go.AccountAsync("Laptop"));
        var ch = Listen(b);
        Assert.IsType<ServerEvent.Connected>(await Next(ch));
        await Online(a, "Laptop");
        var t = (await a.CreateTransferAsync(new(Kind.Text, false) { Text = "hi", Targets = ["Laptop"] })).Transfer;
        Assert.Equal("hi", Assert.IsType<ServerEvent.OfferNew>(await Next(ch)).Transfer.Text);
        await a.CancelAsync(t.Id);
        Assert.Equal(new ServerEvent.OfferRemoved(t.Id), await Next(ch));
    }

    [Fact]
    public async Task LongTextsArriveInOnePiece()
    {
        var a = new Api(await _go.AccountAsync("PC"));
        var b = new Api(await _go.AccountAsync("Laptop"));
        var ch = Listen(b);
        await Next(ch);
        await Online(a, "Laptop");
        var text = new string('ä', 32768);
        await a.CreateTransferAsync(new(Kind.Text, false) { Text = text, Targets = ["Laptop"] });
        Assert.Equal(text, Assert.IsType<ServerEvent.OfferNew>(await Next(ch)).Transfer.Text);
    }

    [Fact]
    public async Task SenderSeesDeliveryAndClose()
    {
        var a = new Api(await _go.AccountAsync("PC"));
        var b = new Api(await _go.AccountAsync("Laptop"));
        var ch = Listen(a);
        await Next(ch);
        await Online(b, "PC");
        var t = (await a.CreateTransferAsync(new(Kind.Text, false) { Text = "hi", Targets = ["Laptop"] })).Transfer;
        await b.DeclineAsync(t.Id);
        Assert.Equal(Status.Declined, Assert.IsType<ServerEvent.DeliveryUpdated>(await Next(ch)).Update.Delivery.Status);
        Assert.Equal(new ServerEvent.TransferClosed(t.Id), await Next(ch));
    }

    [Fact]
    public async Task ReconnectsAfterServerRestart()
    {
        var ch = Listen(new Api(await _go.AccountAsync("PC")), Fast);
        Assert.IsType<ServerEvent.Connected>(await Next(ch));
        _go.Stop();
        Assert.IsType<ServerEvent.Disconnected>(await Next(ch));
        _go.Restart();
        Assert.IsType<ServerEvent.Connected>(await Next(ch)); // failed attempts in between emit nothing
    }

    [Fact]
    public async Task RevokedTokenEndsFlow()
    {
        var acc = await _go.AccountAsync("PC");
        var ch = Listen(new Api(acc), Fast);
        Assert.IsType<ServerEvent.Connected>(await Next(ch));
        await new Api(acc).DeleteMeAsync();
        _go.Restart();
        Assert.IsType<ServerEvent.Disconnected>(await Next(ch));
        Assert.IsType<ServerEvent.Unauthorised>(await Next(ch));
        await ch.Completion.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void PausesStayShort()
    {
        var p = TimeSpan.FromSeconds(1);
        for (var i = 0; i < 20; i++)
        {
            Assert.True(p <= TimeSpan.FromSeconds(5));
            p = EventStream.NextPause(p);
        }
        Assert.Equal(TimeSpan.FromSeconds(5), p);
    }

    [Fact]
    public async Task NeverConnectedIsNotReportedAsDisconnected()
    {
        var acc = await _go.AccountAsync("PC");
        _go.Stop();
        var ch = Listen(new Api(acc), Fast);
        await Task.Delay(500);
        Assert.False(ch.TryRead(out _));
    }

    [Fact]
    public async Task CancellingEndsTheStream()
    {
        var ch = Listen(new Api(await _go.AccountAsync("PC")));
        await Next(ch);
        _cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ch.Completion.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    /// <summary>After sleep, a change of network or a router reboot the old connection just stays silent.</summary>
    [Fact]
    public async Task SilentConnectionIsDetected()
    {
        var acc = await _go.AccountAsync("PC");
        using var srv = new ScriptedServer(_ => new(200) { WebSocket = _ => Task.Delay(20_000) }); // never reads, never answers pings
        var ch = Listen(new Api(acc with { ServerUrl = srv.Url }), new EventOptions { BackoffBase = TimeSpan.FromMilliseconds(20), KeepAlive = TimeSpan.FromMilliseconds(500) });
        Assert.IsType<ServerEvent.Connected>(await Next(ch));
        Assert.IsType<ServerEvent.Disconnected>(await Next(ch));
    }

    [Fact]
    public async Task ReconnectCanBeForced()
    {
        var acc = await _go.AccountAsync("PC");
        var o = new EventOptions { BackoffBase = TimeSpan.FromSeconds(60) };
        _go.Stop();
        var ch = Listen(new Api(acc), o);
        await Task.Delay(4000); // Windows retries a refused local connection for about two seconds
        _go.Restart();
        await Task.Delay(300);
        Assert.False(ch.TryRead(out _));
        o.RetryNow();
        Assert.IsType<ServerEvent.Connected>(await Next(ch));
    }

    /// <summary>After waking up nobody should have to wait until the dead connection is noticed.</summary>
    [Fact]
    public async Task ReconnectDropsASilentConnection()
    {
        var acc = await _go.AccountAsync("PC");
        using var srv = new ScriptedServer(_ => new(200) { WebSocket = _ => Task.Delay(20_000) });
        var o = new EventOptions { BackoffBase = TimeSpan.FromSeconds(60), KeepAlive = TimeSpan.FromMinutes(5) };
        var ch = Listen(new Api(acc with { ServerUrl = srv.Url }), o);
        Assert.IsType<ServerEvent.Connected>(await Next(ch));
        o.Reconnect();
        Assert.IsType<ServerEvent.Disconnected>(await Next(ch));
        Assert.IsType<ServerEvent.Connected>(await Next(ch));
    }

    [Fact]
    public async Task UnknownOrBrokenMessagesAreIgnored()
    {
        var acc = await _go.AccountAsync("PC");
        var n = 0;
        using var srv = new ScriptedServer(_ => n++ > 0 ? new(401) : new(200)
        {
            WebSocket = async ws =>
            {
                foreach (var m in new[] { """{"type":"neu","data":1}""", "kaputt", """{"type":"offer.new","data":{"id":5}}""", """{"type":"offer.removed"}""", "null", "[]", """{"type":"offer.removed","data":{"transfer_id":"t9"}}""" })
                    await ws.SendAsync(Encoding.UTF8.GetBytes(m), WebSocketMessageType.Text, true, default);
                await ws.SendAsync(new byte[] { 1, 2 }, WebSocketMessageType.Binary, true, default);
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, default);
            },
        });
        var got = new List<ServerEvent>();
        await foreach (var e in new Api(acc with { ServerUrl = srv.Url }).EventsAsync(Fast, _cts.Token)) got.Add(e);
        Assert.Equal([new ServerEvent.Connected(), new ServerEvent.OfferRemoved("t9"), new ServerEvent.Disconnected(), new ServerEvent.Unauthorised()], got);
        Assert.Equal("Bearer " + acc.Token, srv.Seen.First().Headers["authorization"]);
    }
}
