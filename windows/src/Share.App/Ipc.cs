using System.Buffers.Binary;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;

namespace Share.App;

/// <summary>What another start of the program (or a click) wants from the running one.</summary>
public abstract record Command
{
    public sealed record Show : Command;

    /// <summary>Presents files for sending; nothing is sent without a click.</summary>
    public sealed record Send(IReadOnlyList<string> Paths) : Command;
    public sealed record SendText(string Text) : Command;

    /// <summary>Opens the pairing window with the fields filled in.</summary>
    public sealed record Pair(string Link) : Command;

    /// <summary>Only from our own notifications, never over the pipe.</summary>
    public sealed record Accept(string Id) : Command;

    /// <inheritdoc cref="Accept"/>
    public sealed record Decline(string Id) : Command;

    /// <inheritdoc cref="Accept"/>
    public sealed record OpenDownload(string Id) : Command;

    /// <inheritdoc cref="Accept"/>
    public sealed record OpenLink(string Url) : Command;

    internal byte[]? ToJson() => this switch
    {
        Show => JsonSerializer.SerializeToUtf8Bytes(new { cmd = "show" }),
        Send s => JsonSerializer.SerializeToUtf8Bytes(new { cmd = "send", paths = s.Paths }),
        SendText t => JsonSerializer.SerializeToUtf8Bytes(new { cmd = "text", text = t.Text }),
        Pair p => JsonSerializer.SerializeToUtf8Bytes(new { cmd = "pair", link = p.Link }),
        _ => null,
    };

    internal static Command? FromJson(ReadOnlyMemory<byte> json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var o = doc.RootElement;
            return o.GetProperty("cmd").GetString() switch
            {
                "show" => new Show(),
                "send" => new Send([.. o.GetProperty("paths").EnumerateArray().Select(e => e.GetString()!)]),
                "text" => new SendText(o.GetProperty("text").GetString()!),
                "pair" => new Pair(o.GetProperty("link").GetString()!),
                _ => null,
            };
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            return null;
        }
    }
}

/// <summary>One running instance per user (and data folder); later starts hand their command over and exit.</summary>
public sealed class Ipc : IDisposable
{
    private const int MaxMessage = 1 << 20;
    private readonly string _name;
    private readonly Mutex _mutex;
    private readonly CancellationTokenSource _stop = new();

    public Ipc(string name)
    {
        _name = name;
        _mutex = new Mutex(false, "Local\\" + name, out var created);
        IsFirst = created;
    }

    /// <summary>The name exists as long as any handle does, so a second instance must dispose or exit.</summary>
    public bool IsFirst { get; }

    /// <summary>Hands a command to the first instance and waits until it has been dealt with.</summary>
    public bool TrySend(Command c, TimeSpan? timeout = null)
    {
        if (c.ToJson() is not { } json || json.Length > MaxMessage) return false;
        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(10));
        try
        {
            return Send(json, timeout ?? TimeSpan.FromSeconds(10), cts.Token);
        }
        catch (Exception e) when (e is IOException or TimeoutException or OperationCanceledException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private bool Send(byte[] json, TimeSpan timeout, CancellationToken ct)
    {
        using var p = new NamedPipeClientStream(".", _name, PipeDirection.InOut, PipeOptions.CurrentUserOnly | PipeOptions.Asynchronous);
        p.Connect((int)timeout.TotalMilliseconds);
        return Exchange().GetAwaiter().GetResult();

        async Task<bool> Exchange()
        {
            var frame = new byte[4 + json.Length];
            BinaryPrimitives.WriteInt32LittleEndian(frame, json.Length);
            json.CopyTo(frame, 4);
            await p.WriteAsync(frame, ct).ConfigureAwait(false);
            var answer = new byte[1];
            return await p.ReadAsync(answer, ct).ConfigureAwait(false) == 1 && answer[0] == 1;
        }
    }

    public void Listen(Func<Command, Task> handler)
    {
        for (var i = 0; i < 4; i++) _ = Task.Run(() => ServeAsync(handler));
    }

    private async Task ServeAsync(Func<Command, Task> handler)
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                await using var p = new NamedPipeServerStream(_name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.CurrentUserOnly | PipeOptions.Asynchronous);
                await p.WaitForConnectionAsync(_stop.Token).ConfigureAwait(false);
                var c = await ReadAsync(p).ConfigureAwait(false);
                if (c is null) continue;
                var ok = true;
                try
                {
                    await handler(c).ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    System.Diagnostics.Trace.TraceError("command failed: " + e);
                    ok = false;
                }
                await p.WriteAsync(new[] { ok ? (byte)1 : (byte)0 }, _stop.Token).ConfigureAwait(false);
            }
            catch (Exception e) when (e is IOException or OperationCanceledException or UnauthorizedAccessException)
            {
                // the other side hung up
                if (!_stop.IsCancellationRequested) await Task.Delay(20).ConfigureAwait(false);
            }
        }
    }

    private async Task<Command?> ReadAsync(Stream p)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        cts.CancelAfter(TimeSpan.FromSeconds(2));
        var head = new byte[4];
        await p.ReadExactlyAsync(head, cts.Token).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32LittleEndian(head);
        if (length is <= 0 or > MaxMessage) return null;
        var json = new byte[length];
        await p.ReadExactlyAsync(json, cts.Token).ConfigureAwait(false);
        return Command.FromJson(json);
    }

    public void Dispose()
    {
        _stop.Cancel();
        _mutex.Dispose();
    }
}
