using System.Net;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Share.Core;

public abstract record ServerEvent
{
    public sealed record Connected : ServerEvent;
    public sealed record Disconnected : ServerEvent;

    /// <summary>The token is no longer valid; nothing follows.</summary>
    public sealed record Unauthorised : ServerEvent;
    public sealed record OfferNew(Transfer Transfer) : ServerEvent;
    public sealed record OfferRemoved(string Id) : ServerEvent;
    public sealed record DeliveryUpdated(DeliveryUpdate Update) : ServerEvent;
    public sealed record TransferClosed(string Id) : ServerEvent;
    public sealed record Relay(RelayRequest Request) : ServerEvent;
}

public sealed class EventOptions
{
    private volatile TaskCompletionSource _retry = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TimeSpan BackoffBase { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Ping interval and how long an answer may take.</summary>
    public TimeSpan KeepAlive { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>Drops the current connection, if any, and connects anew without waiting, e.g. after waking up.</summary>
    public void Reconnect()
    {
        RetryNow();
        try
        {
            _connection?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // has just ended by itself
        }
    }

    private volatile CancellationTokenSource? _connection;

    internal CancellationTokenSource NewConnection(CancellationToken ct) => _connection = CancellationTokenSource.CreateLinkedTokenSource(ct);

    /// <summary>Ends the current pause, e.g. after waking up or when a network appears.</summary>
    public void RetryNow() => Interlocked.Exchange(ref _retry, new(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();

    internal Task RetrySignal => _retry.Task;

    /// <summary>Repeats a call that failed although the connection is up, with growing pauses, until it succeeds or is not wanted any more.</summary>
    internal async Task RepeatAsync(Func<Task> f, Func<bool> wanted, CancellationToken ct)
    {
        var pause = BackoffBase;
        try
        {
            while (wanted())
            {
                try
                {
                    await f().ConfigureAwait(false);
                    return;
                }
                catch (ApiException e) when (e.Status == 401)
                {
                    return;
                }
                catch (IOException)
                {
                    await Task.Delay(pause, ct).ConfigureAwait(false);
                    pause = pause * 2 < TimeSpan.FromSeconds(30) ? pause * 2 : TimeSpan.FromSeconds(30);
                }
            }
        }
        catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException)
        {
            // closing
        }
    }

    internal static async Task PauseAsync(TimeSpan t, Task retry, CancellationToken ct)
    {
        await Task.WhenAny(Task.Delay(t, ct), retry).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
    }
}

public static class EventStream
{
    private const int MaxMessage = 4 << 20;

    /// <summary>The server cancels our file offers after 30 s without connection, so pauses stay well below that.</summary>
    internal static TimeSpan NextPause(TimeSpan pause) => pause * 2 < TimeSpan.FromSeconds(5) ? pause * 2 : TimeSpan.FromSeconds(5);

    /// <summary>Event stream; reconnects with growing pauses until cancelled or the token is rejected.</summary>
    public static async IAsyncEnumerable<ServerEvent> EventsAsync(this Api api, EventOptions options, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var pause = options.BackoffBase;
        while (true)
        {
            using var ws = new ClientWebSocket();
            ws.Options.SetRequestHeader("Authorization", "Bearer " + api.Account.Token);
            ws.Options.KeepAliveInterval = options.KeepAlive;
            ws.Options.KeepAliveTimeout = options.KeepAlive;
            ws.Options.CollectHttpResponseDetails = true;
            using var connection = options.NewConnection(ct);
            var retry = options.RetrySignal; // taken early: a wish to reconnect during the connection must not be missed
            if (await ConnectAsync(ws, api.EventsUri, ct).ConfigureAwait(false))
            {
                pause = options.BackoffBase;
                yield return new ServerEvent.Connected();
                while (await ReceiveAsync(ws, connection.Token, ct).ConfigureAwait(false) is { } text)
                    if (Parse(text) is { } e)
                        yield return e;
                yield return new ServerEvent.Disconnected();
            }
            else if (ws.HttpStatusCode == HttpStatusCode.Unauthorized)
            {
                yield return new ServerEvent.Unauthorised();
                yield break;
            }
            await EventOptions.PauseAsync(pause, retry, ct).ConfigureAwait(false);
            pause = NextPause(pause);
        }
    }

    private static async Task<bool> ConnectAsync(ClientWebSocket ws, Uri uri, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            await ws.ConnectAsync(uri, cts.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception e) when (e is WebSocketException or HttpRequestException or IOException or OperationCanceledException)
        {
            ct.ThrowIfCancellationRequested();
            return false;
        }
    }

    /// <summary>The next text message, or null when the connection has ended.</summary>
    private static async Task<byte[]?> ReceiveAsync(ClientWebSocket ws, CancellationToken connection, CancellationToken ct)
    {
        var buf = new byte[16 * 1024];
        using var msg = new MemoryStream();
        try
        {
            while (true)
            {
                var r = await ws.ReceiveAsync(buf.AsMemory(), connection).ConfigureAwait(false);
                if (r.MessageType == WebSocketMessageType.Close) return null;
                msg.Write(buf, 0, r.Count);
                if (msg.Length > MaxMessage) return null;
                if (r.EndOfMessage) return r.MessageType == WebSocketMessageType.Text ? msg.ToArray() : [];
            }
        }
        catch (Exception e) when (e is WebSocketException or IOException or ObjectDisposedException or OperationCanceledException)
        {
            ct.ThrowIfCancellationRequested();
            return null;
        }
    }

    /// <summary>Unknown types and malformed messages yield null.</summary>
    private static ServerEvent? Parse(byte[] text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("type", out var type)) return null;
            var data = doc.RootElement.TryGetProperty("data", out var d) ? d : default;
            T Get<T>() => data.ValueKind == JsonValueKind.Object ? data.Deserialize<T>(ShareJson.Options)! : throw new JsonException();
            return (type.ValueKind == JsonValueKind.String ? type.GetString() : null) switch
            {
                "offer.new" => new ServerEvent.OfferNew(Get<Transfer>()),
                "offer.removed" => new ServerEvent.OfferRemoved(Get<TransferRef>().TransferId),
                "transfer.closed" => new ServerEvent.TransferClosed(Get<TransferRef>().TransferId),
                "delivery.updated" => new ServerEvent.DeliveryUpdated(Get<DeliveryUpdate>()),
                "relay.request" => new ServerEvent.Relay(Get<RelayRequest>()),
                _ => null,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
