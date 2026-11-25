using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Text.RegularExpressions;

namespace Share.Core;

/// <summary>Minimal HTTPS server for running offers: GET /t/{id}/ping and GET /t/{id}/files/{n}, one request per connection.</summary>
public sealed partial class LocalEndpoint(Identity identity, int preferredPort, Func<string, Task<Offer?>> lookup, TimeSpan? headerTimeout = null) : IDisposable
{
    private const int MaxConnections = 32;
    private const int MaxHeader = 16 * 1024;

    private static readonly Dictionary<int, string> Reasons = new()
    {
        [200] = "OK", [204] = "No Content", [206] = "Partial Content", [401] = "Unauthorized", [404] = "Not Found", [405] = "Method Not Allowed",
        [409] = "Conflict", [410] = "Gone", [416] = "Range Not Satisfiable", [431] = "Request Header Fields Too Large",
    };

    private readonly TimeSpan _headerTimeout = headerTimeout ?? TimeSpan.FromSeconds(10);
    private readonly object _lock = new();
    private Run? _run;

    private sealed class Run(Socket listener)
    {
        public Socket Listener { get; } = listener;

        /// <summary>sockets and the offer they are serving ("" until authorised)</summary>
        public ConcurrentDictionary<Socket, string> Sockets { get; } = new();
    }

    public bool Running
    {
        get
        {
            lock (_lock) return _run is not null;
        }
    }

    /// <summary>Starts listening (no-op if running) and returns the actual port. Throws IOException.</summary>
    public int Start()
    {
        lock (_lock)
        {
            if (_run is null)
            {
                _run = new Run(Listen());
                _ = AcceptLoop(_run);
            }
            return ((IPEndPoint)_run.Listener.LocalEndPoint!).Port;
        }
    }

    private Socket Listen()
    {
        foreach (var port in new[] { preferredPort, 0 })
        {
            var s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { ExclusiveAddressUse = true };
            try
            {
                s.Bind(new IPEndPoint(IPAddress.Any, port));
                s.Listen(64);
                return s;
            }
            catch (SocketException e)
            {
                s.Dispose();
                if (port == 0) throw new IOException("cannot listen", e);
            }
        }
        throw new IOException("cannot listen");
    }

    public void Stop()
    {
        Run? r;
        lock (_lock)
        {
            r = _run;
            _run = null;
        }
        if (r is null) return;
        r.Listener.Dispose();
        foreach (var s in r.Sockets.Keys) s.Dispose();
    }

    public void Dispose() => Stop();

    /// <summary>Ends running downloads of one offer.</summary>
    public void CloseConnections(string transferId)
    {
        Run? r;
        lock (_lock) r = _run;
        if (r is null) return;
        foreach (var (s, id) in r.Sockets)
            if (id == transferId)
                s.Dispose();
    }

    private async Task AcceptLoop(Run r)
    {
        while (true)
        {
            Socket s;
            try
            {
                s = await r.Listener.AcceptAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException)
            {
                lock (_lock)
                    if (_run != r)
                        return;
                await Task.Delay(50).ConfigureAwait(false);
                continue;
            }
            if (r.Sockets.Count >= MaxConnections)
            {
                s.Dispose();
                continue;
            }
            r.Sockets[s] = "";
            lock (_lock)
                if (_run != r)
                    s.Dispose(); // lost the race against Stop
            _ = Task.Run(() => Connection(r, s));
        }
    }

    private async Task Connection(Run r, Socket s)
    {
        // hard deadline for handshake and request; disposing the socket cannot block
        using var deadline = new Timer(_ => s.Dispose(), null, _headerTimeout, Timeout.InfiniteTimeSpan);
        try
        {
            s.NoDelay = true;
            await using var tls = new SslStream(new NetworkStream(s, ownsSocket: false));
            await tls.AuthenticateAsServerAsync(Pinning.ServerOptions(identity)).ConfigureAwait(false);
            var req = await ReadRequest(tls).ConfigureAwait(false);
            if (req is null) await Respond(tls, 431).ConfigureAwait(false);
            else await Handle(r, s, req, tls, deadline).ConfigureAwait(false);
            await tls.FlushAsync().ConfigureAwait(false);
            await tls.ShutdownAsync().ConfigureAwait(false);
            s.Shutdown(SocketShutdown.Send);
        }
        catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException or AuthenticationException or InvalidOperationException or OperationCanceledException)
        {
            // peer went away, junk, or closed by us
        }
        finally
        {
            r.Sockets.TryRemove(s, out _);
            s.Dispose();
        }
    }

    private sealed record Request(string Method, string Path, Dictionary<string, string> Headers);

    /// <summary>null: header too large.</summary>
    private static async Task<Request?> ReadRequest(Stream input)
    {
        var buf = new byte[MaxHeader];
        var n = 0;
        while (true)
        {
            if (n == buf.Length) return null;
            var k = await input.ReadAsync(buf.AsMemory(n)).ConfigureAwait(false);
            if (k == 0) throw new IOException("closed");
            var from = Math.Max(0, n - 3);
            n += k;
            var end = buf.AsSpan(from, n - from).IndexOf("\r\n\r\n"u8);
            if (end < 0) continue;
            var lines = Encoding.Latin1.GetString(buf, 0, from + end).Split("\r\n");
            var parts = lines[0].Split(' ');
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var l in lines.Skip(1))
                if (l.IndexOf(':') is > 0 and var i)
                    headers[l[..i].Trim()] = l[(i + 1)..].Trim();
            return new Request(parts[0], parts.Length > 1 ? parts[1] : "", headers);
        }
    }

    private async Task Handle(Run r, Socket s, Request req, Stream o, Timer deadline)
    {
        if (req.Method != "GET")
        {
            await Respond(o, 405).ConfigureAwait(false);
            return;
        }
        var m = PathPattern().Match(req.Path);
        var offer = m.Success ? await lookup(m.Groups["id"].Value).ConfigureAwait(false) : null;
        if (offer is not { Transfer.Kind: Kind.Files })
        {
            await Respond(o, 404).ConfigureAwait(false);
            return;
        }
        var id = m.Groups["id"].Value;
        var auth = req.Headers.GetValueOrDefault("Authorization", "");
        if (!auth.StartsWith("Bearer ", StringComparison.Ordinal) || AccessToken.Verify(offer.Secret, id, auth["Bearer ".Length..]) is null)
        {
            await Respond(o, 401).ConfigureAwait(false);
            return;
        }
        deadline.Change(Timeout.Infinite, Timeout.Infinite);
        r.Sockets[s] = id;
        if (!m.Groups["n"].Success)
        {
            await Respond(o, 204).ConfigureAwait(false);
            return;
        }
        if (!int.TryParse(m.Groups["n"].Value, System.Globalization.NumberStyles.None, null, out var n) || n >= offer.Sources.Count)
        {
            await Respond(o, 404).ConfigureAwait(false);
            return;
        }
        await SendFile(offer.Sources[n], req.Headers.GetValueOrDefault("Range"), o).ConfigureAwait(false);
    }

    private static async Task SendFile(IFileSource src, string? range, Stream o)
    {
        if (!src.Unchanged())
        {
            await Respond(o, 409).ConfigureAwait(false);
            return;
        }
        var size = src.Item.Size;
        long offset = 0;
        if (range is not null && !(RangePattern().Match(range) is { Success: true } rm && long.TryParse(rm.Groups[1].ValueSpan, out offset) && offset < size))
        {
            await Respond(o, 416, $"Content-Range: bytes */{size}\r\n").ConfigureAwait(false);
            return;
        }
        Stream input;
        try
        {
            input = src.OpenAt(offset);
        }
        catch (IOException)
        {
            await Respond(o, 410).ConfigureAwait(false);
            return;
        }
        await using (input)
        {
            var mime = new string([.. src.Item.Mime.Where(c => c is > ' ' and < (char)127)]);
            var extra = $"Content-Type: {(mime.Length == 0 ? "application/octet-stream" : mime)}\r\nAccept-Ranges: bytes\r\n";
            if (range is null) await Respond(o, 200, extra, size).ConfigureAwait(false);
            else await Respond(o, 206, extra + $"Content-Range: bytes {offset}-{size - 1}/{size}\r\n", size - offset).ConfigureAwait(false);
            var buf = new byte[Limits.Buffer];
            for (var left = size - offset; left > 0;)
            {
                var k = await input.ReadAsync(buf.AsMemory(0, (int)Math.Min(left, buf.Length))).ConfigureAwait(false);
                // checked after reading: whatever was read before a change is still the old content
                if (k == 0 || !src.Unchanged()) throw new IOException("source changed");
                await o.WriteAsync(buf.AsMemory(0, k)).ConfigureAwait(false);
                left -= k;
            }
        }
    }

    private static ValueTask Respond(Stream o, int code, string extra = "", long length = 0)
    {
        var len = code == 204 ? "" : $"Content-Length: {length}\r\n";
        return o.WriteAsync(Encoding.Latin1.GetBytes($"HTTP/1.1 {code} {Reasons[code]}\r\n{len}{extra}Connection: close\r\n\r\n"));
    }

    [GeneratedRegex("^/t/(?<id>[^/]+)/(ping|files/(?<n>[^/]+))$", RegexOptions.CultureInvariant)]
    private static partial Regex PathPattern();

    [GeneratedRegex("^bytes=([0-9]+)-$", RegexOptions.CultureInvariant)]
    private static partial Regex RangePattern();
}
