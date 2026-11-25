using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;

namespace Share.Core.Tests;

public sealed class LocalEndpointTests : IDisposable
{
    private static readonly Identity Id = Identity.Generate("a");
    private static readonly byte[] Secret = [.. Enumerable.Repeat((byte)7, 32)];
    private readonly string _token = AccessToken.Create(Secret, "t1", "d1");
    private readonly TempDir _tmp = new();
    private readonly HttpClient _http = new(Pinning.CreateHandler(Id.Fingerprint)) { Timeout = TimeSpan.FromSeconds(60) };
    private readonly Dictionary<string, Offer> _offers = [];
    private readonly LocalEndpoint _ep;
    private readonly int _port;

    public LocalEndpointTests()
    {
        _ep = new LocalEndpoint(Id, 0, id => Task.FromResult(_offers.GetValueOrDefault(id)), TimeSpan.FromMilliseconds(800));
        _port = _ep.Start();
    }

    public void Dispose()
    {
        _ep.Dispose();
        _http.Dispose();
        foreach (var s in _offers.Values.SelectMany(o => o.Sources)) s.Release();
        _tmp.Dispose();
    }

    private void Offer(params IFileSource[] sources) =>
        _offers["t1"] = new Offer(new Transfer("t1", "s", Kind.Files, DateTimeOffset.Now) { Files = [.. sources.Select(s => s.Item)] }, Secret, sources);

    private DiskFile Source(int size, string name = "f.bin") => DiskFile.Open(_tmp.File(name, TempDir.Random(size)));

    private async Task<HttpResponseMessage> Get(string path, string? range = null, string? token = "", HttpMethod? method = null)
    {
        var r = new HttpRequestMessage(method ?? HttpMethod.Get, $"https://127.0.0.1:{_port}{path}");
        if (token is not null) r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token == "" ? _token : token);
        if (range is not null) r.Headers.TryAddWithoutValidation("Range", range);
        return await _http.SendAsync(r, HttpCompletionOption.ResponseHeadersRead);
    }

    private async Task<int> Code(string path, string? range = null, string? token = "")
    {
        using var r = await Get(path, range, token);
        return (int)r.StatusCode;
    }

    [Fact]
    public async Task PingAndFullDownload()
    {
        Offer(Source(3 * 1024 * 1024));
        Assert.True(_ep.Running);
        Assert.Equal(204, await Code("/t/t1/ping"));
        using var r = await Get("/t/t1/files/0");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal(3 * 1024 * 1024, r.Content.Headers.ContentLength);
        Assert.Equal(TempDir.Random(3 * 1024 * 1024), await r.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task RangeResume()
    {
        Offer(Source(5000));
        using (var r = await Get("/t/t1/files/0", "bytes=1000-"))
        {
            Assert.Equal(206, (int)r.StatusCode);
            Assert.Equal("bytes 1000-4999/5000", r.Content.Headers.ContentRange!.ToString());
            Assert.Equal(TempDir.Random(5000)[1000..], await r.Content.ReadAsByteArrayAsync());
        }
        Assert.Equal(206, await Code("/t/t1/files/0", "bytes=4999-"));
        foreach (var bad in new[] { "bytes=5000-", "bytes=1-2", "bytes=-5", "bytes=x-", "bytes=99999999999999999999-", "bytes=1-,3-" })
        {
            using var r = await Get("/t/t1/files/0", bad);
            Assert.Equal(416, (int)r.StatusCode);
            Assert.Equal("bytes */5000", r.Content.Headers.ContentRange!.ToString());
        }
    }

    [Fact]
    public async Task AuthRules()
    {
        Offer(Source(10));
        foreach (var p in new[] { "/t/t1/ping", "/t/t1/files/0" })
        {
            Assert.Equal(401, await Code(p, token: null));
            Assert.Equal(401, await Code(p, token: "d1.00"));
            Assert.Equal(401, await Code(p, token: AccessToken.Create(Secret, "t2", "d1")));
            Assert.Equal(401, await Code(p, token: AccessToken.Create(new byte[32], "t1", "d1")));
        }
        Assert.Equal(404, await Code("/t/t2/ping"));
        foreach (var n in new[] { "9", "-1", "x", "", "99999999999", "+0" }) Assert.Equal(404, await Code("/t/t1/files/" + n));
    }

    [Fact]
    public async Task TextOffersAreNotServed()
    {
        _offers["t1"] = new Offer(new Transfer("t1", "s", Kind.Text, DateTimeOffset.Now) { Text = "x" }, Secret, []);
        Assert.Equal(404, await Code("/t/t1/ping"));
    }

    [Fact]
    public async Task ChangedFileIs409()
    {
        Offer(Source(10));
        File.WriteAllBytes(_tmp.File("f.bin"), new byte[11]);
        Assert.Equal(409, await Code("/t/t1/files/0"));
    }

    [Fact]
    public async Task UnreadableFileIs410()
    {
        Offer(new Probe(Source(10)) { Open = _ => throw new IOException("gone") });
        Assert.Equal(410, await Code("/t/t1/files/0"));
    }

    [Fact]
    public async Task EmptyFile()
    {
        Offer(Source(0));
        using var r = await Get("/t/t1/files/0");
        Assert.Equal(200, (int)r.StatusCode);
        Assert.Equal(0, r.Content.Headers.ContentLength);
    }

    [Fact]
    public async Task OtherMethodsAndPaths()
    {
        Offer(Source(10));
        using (var post = await Get("/t/t1/ping", method: HttpMethod.Post)) Assert.Equal(405, (int)post.StatusCode);
        foreach (var p in new[] { "/", "/t/t1", "/t/t1/files", "/t/t1/files/0/x", "/t//ping", "/v1/devices" }) Assert.Equal(404, await Code(p));
        Assert.StartsWith("HTTP/1.1 404", await Raw("GET /t/../x HTTP/1.1\r\n\r\n"));
    }

    private async Task<SslStream> Tls()
    {
        var c = new TcpClient("127.0.0.1", _port);
        var s = new SslStream(c.GetStream(), false, (_, _, _, _) => true);
        await s.AuthenticateAsClientAsync("x");
        return s;
    }

    /// <summary>Sends raw bytes over TLS and returns what comes back until the peer closes.</summary>
    private async Task<string> Raw(string text)
    {
        try
        {
            await using var s = await Tls();
            await s.WriteAsync(Encoding.Latin1.GetBytes(text));
            using var m = new MemoryStream();
            await s.CopyToAsync(m).WaitAsync(TimeSpan.FromSeconds(5));
            return Encoding.Latin1.GetString(m.ToArray());
        }
        catch (IOException)
        {
            return "";
        }
    }

    private static async Task<bool> ClosedWithin(Stream s, int ms)
    {
        try
        {
            return await s.ReadAsync(new byte[1]).AsTask().WaitAsync(TimeSpan.FromMilliseconds(ms)) == 0;
        }
        catch (TimeoutException)
        {
            return false;
        }
        catch (IOException)
        {
            return true;
        }
    }

    [Fact]
    public async Task AbuseDoesNotKillServer()
    {
        Offer(Source(10));
        using (var c = new TcpClient("127.0.0.1", _port)) await c.GetStream().WriteAsync(Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\n\r\n"));
        foreach (var junk in new[] { "\r\n\r\n", "GET\r\n\r\n", "GET /t/t1/ping\r\n\r\n", "\0\u0001\r\n\r\n", "GET /t/t1/ping HTTP/1.1\r\nno colon\r\n\r\n" }) await Raw(junk);
        var big = await Raw("GET /t/t1/ping HTTP/1.1\r\nX: " + new string('a', 100_000) + "\r\n\r\n");
        Assert.True(big == "" || big.StartsWith("HTTP/1.1 431"), big);
        using (var c = new TcpClient("127.0.0.1", _port)) Assert.True(await ClosedWithin(c.GetStream(), 5000), "silent tcp");
        await using (var s = await Tls()) Assert.True(await ClosedWithin(s, 5000), "silent tls");
        // dripping bytes must not extend the deadline
        await using (var s = await Tls())
        {
            var sw = Stopwatch.StartNew();
            try
            {
                while (sw.ElapsedMilliseconds < 8000)
                {
                    await s.WriteAsync(new[] { (byte)'a' });
                    await Task.Delay(50);
                }
            }
            catch (IOException)
            {
                // closed by the server
            }
            Assert.True(sw.ElapsedMilliseconds < 6000);
        }
        Assert.Equal(204, await Code("/t/t1/ping"));
    }

    [Fact]
    public async Task ConnectionLimitRecovers()
    {
        Offer(Source(10));
        var silent = Enumerable.Range(0, 60).Select(_ => new TcpClient("127.0.0.1", _port)).ToList();
        await Task.Delay(1500);
        silent.ForEach(c => c.Dispose());
        Assert.Equal(204, await Code("/t/t1/ping"));
    }

    [Fact]
    public async Task SlowDownloadOutlivesHeaderTimeout()
    {
        Offer(new SlowSource(Source(20_000), 1000, 80));
        using var r = await Get("/t/t1/files/0");
        Assert.Equal(TempDir.Random(20_000), await r.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public void FallsBackToFreePort()
    {
        var busy = new TcpListener(IPAddress.Any, 0) { ExclusiveAddressUse = true };
        busy.Start();
        var port = ((IPEndPoint)busy.LocalEndpoint).Port;
        using var e = new LocalEndpoint(Id, port, _ => Task.FromResult<Offer?>(null));
        var p = e.Start();
        Assert.NotEqual(port, p);
        Assert.True(p > 0);
        busy.Stop();
    }

    [Fact]
    public async Task StartIsIdempotentAndCloseStops()
    {
        Assert.Equal(_port, _ep.Start());
        _ep.Stop();
        Assert.False(_ep.Running);
        await Assert.ThrowsAsync<HttpRequestException>(() => Code("/t/t1/ping"));
        Assert.True(_ep.Start() > 0);
        Assert.True(_ep.Running);
    }

    [Fact]
    public async Task FourParallelDownloads()
    {
        Offer(Source(8 * 1024 * 1024));
        var want = TempDir.Random(8 * 1024 * 1024);
        var got = await Task.WhenAll(Enumerable.Range(0, 4).Select(async _ =>
        {
            using var r = await Get("/t/t1/files/0");
            return await r.Content.ReadAsByteArrayAsync();
        }));
        Assert.All(got, g => Assert.True(want.AsSpan().SequenceEqual(g)));
    }

    [Fact]
    public async Task ShorterSourceBreaksTheConnection()
    {
        Offer(new Probe(Source(5000)) { Open = _ => new MemoryStream(new byte[100]) });
        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            using var r = await Get("/t/t1/files/0");
            await r.Content.ReadAsByteArrayAsync();
        });
    }

    /// <summary>A build overwrites the offered file while somebody downloads it.</summary>
    [Fact]
    public async Task FileRewrittenDuringDownloadNeverMixesBytes()
    {
        var p = _tmp.File("f.bin", Enumerable.Repeat((byte)'A', 4_000_000).ToArray());
        Offer(new SlowSource(DiskFile.Open(p), 100_000, 20));
        using var r = await Get("/t/t1/files/0");
        await using var s = await r.Content.ReadAsStreamAsync();
        var buf = new byte[50_000];
        await s.ReadExactlyAsync(buf);
        await File.WriteAllBytesAsync(p, Enumerable.Repeat((byte)'B', 4_000_000).ToArray());
        using var m = new MemoryStream();
        try
        {
            await s.CopyToAsync(m);
            Assert.DoesNotContain((byte)'B', m.ToArray());
            Assert.Fail("the file could not stay readable as it was");
        }
        catch (Exception e) when (e is IOException or HttpRequestException)
        {
            Assert.DoesNotContain((byte)'B', m.ToArray());
        }
        Assert.Equal(409, await Code("/t/t1/files/0"));
    }

    [Fact]
    public async Task CurlCompatible()
    {
        Offer(Source(400_000));
        var part = _tmp.File("teil", TempDir.Random(400_000)[..123_456]);
        using var p = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "curl.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true,
            ArgumentList = { "-k", "-sS", "-f", "-C", "-", "-H", "Authorization: Bearer " + _token, "-o", part, $"https://127.0.0.1:{_port}/t/t1/files/0" },
        })!;
        var err = await p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        Assert.True(p.ExitCode == 0, err);
        Assert.Equal(TempDir.Random(400_000), File.ReadAllBytes(part));
    }

    private async Task AssertEnds(Action end)
    {
        Offer(new SlowSource(Source(50 * 1024 * 1024), 1000, 20));
        using var r = await Get("/t/t1/files/0");
        await using var s = await r.Content.ReadAsStreamAsync();
        await s.ReadExactlyAsync(new byte[1]);
        end();
        await Assert.ThrowsAnyAsync<IOException>(() => s.CopyToAsync(Stream.Null).WaitAsync(TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public Task StopEndsRunningDownloads() => AssertEnds(_ep.Stop);

    [Fact]
    public Task CloseConnectionsEndsDownloadsOfThatOffer() => AssertEnds(() => _ep.CloseConnections("t1"));
}
