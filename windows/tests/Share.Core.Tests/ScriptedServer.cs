using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;

namespace Share.Core.Tests;

public sealed record Req(string Method, string Path, Dictionary<string, string> Headers);

public sealed record Resp(int Code, string Body = "")
{
    public Dictionary<string, string> Headers { get; init; } = [];
    public TimeSpan Delay { get; init; }

    /// <summary>One byte per this time.</summary>
    public TimeSpan Throttle { get; init; }

    /// <summary>Claimed length if it differs from the body (the connection is closed early).</summary>
    public int? DeclaredLength { get; init; }
    public bool Chunked { get; init; }

    /// <summary>Answers the request as a websocket.</summary>
    public Func<WebSocket, Task>? WebSocket { get; init; }
}

/// <summary>A little HTTP/1.1 server whose answers are scripted; one request per connection.</summary>
public sealed class ScriptedServer : IDisposable
{
    private readonly TcpListener _l = new(IPAddress.Loopback, 0);
    private readonly Func<Req, Resp> _handler;
    public readonly ConcurrentQueue<Req> Seen = new();

    public ScriptedServer(Func<Req, Resp> handler)
    {
        _handler = handler;
        _l.Start();
        _ = Loop();
    }

    public string Url => $"http://127.0.0.1:{((IPEndPoint)_l.LocalEndpoint).Port}";

    private async Task Loop()
    {
        while (true)
        {
            TcpClient c;
            try
            {
                c = await _l.AcceptTcpClientAsync();
            }
            catch (Exception)
            {
                return;
            }
            _ = Task.Run(async () =>
            {
                using (c)
                {
                    try
                    {
                        await Serve(c.GetStream());
                    }
                    catch (Exception)
                    {
                        // the client hung up
                    }
                }
            });
        }
    }

    private async Task Serve(NetworkStream s)
    {
        var head = new List<byte>();
        var one = new byte[1];
        while (!(head.Count >= 4 && head[^4] == '\r' && head[^3] == '\n' && head[^2] == '\r' && head[^1] == '\n'))
        {
            if (await s.ReadAsync(one) == 0) return;
            head.Add(one[0]);
        }
        var lines = Encoding.ASCII.GetString([.. head]).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        var first = lines[0].Split(' ');
        var headers = lines.Skip(1).ToDictionary(l => l[..l.IndexOf(':')].ToLowerInvariant(), l => l[(l.IndexOf(':') + 1)..].Trim());
        if (headers.TryGetValue("content-length", out var cl))
        {
            var body = new byte[int.Parse(cl)];
            await s.ReadExactlyAsync(body);
        }
        var req = new Req(first[0], first[1], headers);
        Seen.Enqueue(req);
        var r = _handler(req);
        await Task.Delay(r.Delay);
        if (r.WebSocket is not null)
        {
            var key = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(headers["sec-websocket-key"] + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
            await s.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {key}\r\n\r\n"));
            using var ws = WebSocket.CreateFromStream(s, new WebSocketCreationOptions { IsServer = true });
            await r.WebSocket(ws);
            return;
        }
        var bytes = Encoding.UTF8.GetBytes(r.Body);
        var h = new StringBuilder($"HTTP/1.1 {r.Code} X\r\nConnection: close\r\n");
        foreach (var (k, v) in r.Headers) h.Append($"{k}: {v}\r\n");
        if (r.Chunked) h.Append("Transfer-Encoding: chunked\r\n");
        else if (r.Code != 204) h.Append($"Content-Length: {r.DeclaredLength ?? bytes.Length}\r\n");
        await s.WriteAsync(Encoding.ASCII.GetBytes(h.Append("\r\n").ToString()));
        if (r.Chunked)
        {
            await s.WriteAsync(Encoding.ASCII.GetBytes($"{bytes.Length:x}\r\n{r.Body}\r\n0\r\n\r\n"));
        }
        else if (r.Throttle > TimeSpan.Zero)
        {
            foreach (var b in bytes)
            {
                await s.WriteAsync(new[] { b });
                await Task.Delay(r.Throttle);
            }
        }
        else
        {
            await s.WriteAsync(bytes);
        }
    }

    public void Dispose() => _l.Dispose();
}
