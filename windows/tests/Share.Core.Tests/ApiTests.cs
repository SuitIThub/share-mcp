using System.Diagnostics;

namespace Share.Core.Tests;

public sealed class ApiTests : IDisposable
{
    private readonly GoServer _go = new();

    public void Dispose() => _go.Dispose();

    private async Task<Api> New(string name) => new(await _go.AccountAsync(name));

    private static async Task<int> Status(Func<Task> f) => (await Assert.ThrowsAsync<ApiException>(f)).Status;

    [Fact]
    public async Task PairsAndListsDevices()
    {
        var a = await _go.AccountAsync("PC");
        var b = await _go.AccountAsync("Laptop");
        Assert.Equal("PC", a.Name);
        var devs = await new Api(a).DevicesAsync();
        Assert.Equal(["Laptop", "PC"], devs.Select(d => d.Name).Order());
        Assert.All(devs, d => Assert.Equal(DeviceType.Windows, d.Type));
        Assert.Equal(b.Identity.Fingerprint, devs.Single(d => d.Id == b.DeviceId).CertFingerprint);
    }

    [Fact]
    public async Task PairErrorsCarryStatus()
    {
        Assert.Equal(403, await Status(() => Pairing.PairAsync(_go.Url, "ZZZZZZ", "X", Identity.Generate("X"), allowInsecureLocal: true)));
        await _go.AccountAsync("PC");
        Assert.Equal(409, await Status(() => _go.AccountAsync("PC")));
    }

    [Fact]
    public void NormalisesServerUrl()
    {
        Assert.Equal("https://x.example", Pairing.NormalizeServerUrl(" x.example/ "));
        Assert.Equal("https://x.example", Pairing.NormalizeServerUrl("https://x.example/"));
        Assert.Equal("https://x.example:8443/share", Pairing.NormalizeServerUrl("HTTPS://user:pw@x.example:8443/share/?a=b#c"));
        Assert.Equal("http://127.0.0.1:8080", Pairing.NormalizeServerUrl("http://127.0.0.1:8080", allowInsecureLocal: true));
        foreach (var bad in new[] { "ftp://x", "", "http://x.example", "http://127.0.0.1:8080", "https://", "a b", "file:///c:/x" })
            Assert.Throws<ArgumentException>(() => Pairing.NormalizeServerUrl(bad));
        Assert.Throws<ArgumentException>(() => Pairing.NormalizeServerUrl("http://x.example", allowInsecureLocal: true));
    }

    [Fact]
    public async Task AccountNeverShowsItsToken()
    {
        var a = await _go.AccountAsync("PC");
        Assert.DoesNotContain(a.Token, a.ToString());
        Assert.DoesNotContain("PRIVATE", a.ToString());
    }

    [Fact]
    public async Task TextOfferLifecycle()
    {
        var a = await New("PC");
        var b = await New("Laptop");
        var t = (await a.CreateTransferAsync(new(Kind.Text, false) { Text = "hällo", Targets = ["Laptop"] })).Transfer;
        Assert.Equal("hällo", Assert.Single(await b.IncomingAsync()).Text);
        Assert.Empty((await b.AcceptAsync(t.Id)).LanCandidates);
        Assert.Equal(Core.Status.Delivered, Assert.Single((await a.TransferAsync(t.Id)).Deliveries).Status);
        Assert.Empty(await b.IncomingAsync());
    }

    [Fact]
    public async Task DeclineAndCancel()
    {
        var a = await New("PC");
        var b = await New("Laptop");
        var t = (await a.CreateTransferAsync(new(Kind.Text, true) { Text = "x" })).Transfer;
        Assert.Equal(t.Id, Assert.Single(await a.OutgoingAsync()).Id);
        await b.DeclineAsync(t.Id);
        Assert.Equal(409, await Status(() => b.AcceptAsync(t.Id)));
        await a.CancelAsync(t.Id);
        Assert.Empty(await a.OutgoingAsync());
    }

    [Fact]
    public async Task UnauthorisedIs401()
    {
        var a = await _go.AccountAsync("PC");
        Assert.Equal(401, await Status(() => new Api(a with { Token = "nope" }).DevicesAsync()));
    }

    [Fact]
    public async Task PatchAndDeleteMe()
    {
        var a = await New("PC");
        var code = await a.PairCodeAsync();
        Assert.Equal(6, code.Code.Length);
        Assert.True(code.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(9));
        var b = new Api(await Pairing.PairAsync(_go.Url, code.Code.ToLowerInvariant(), "Laptop", Identity.Generate("t"), allowInsecureLocal: true));
        await a.UpdateMeAsync(name: "PC 2");
        Assert.Equal(["Laptop", "PC 2"], (await b.DevicesAsync()).Select(d => d.Name).Order());
        Assert.Equal(409, await Status(() => a.UpdateMeAsync(name: "laptop")));
        await a.DeleteMeAsync();
        Assert.Equal(["Laptop"], (await b.DevicesAsync()).Select(d => d.Name));
        Assert.Equal(401, await Status(() => a.DevicesAsync()));
    }

    [Fact]
    public async Task ErrorMessageComesFromBody()
    {
        var acc = await _go.AccountAsync("PC");
        Assert.NotEmpty((await Assert.ThrowsAsync<ApiException>(() => new Api(acc).TransferAsync("nope"))).Message);
        Resp next = new(200);
        using var srv = new ScriptedServer(_ => next);
        var api = new Api(acc with { ServerUrl = srv.Url });
        next = new(502, "<html>Bad gateway</html>") { Headers = { ["Content-Type"] = "text/html" } };
        Assert.Equal("HTTP 502", (await Assert.ThrowsAsync<ApiException>(() => api.DevicesAsync())).Message);
        next = new(409, """{"error":"offer expired"}""");
        Assert.Equal("offer expired", (await Assert.ThrowsAsync<ApiException>(() => api.AcceptAsync("x"))).Message);
        // a captive portal answering 200 must be an ordinary IO error, never a crash
        foreach (var body in new[] { "<html>login page</html>", "", "null", "{}", "[{\"id\":5}]" })
        {
            next = new(200, body);
            await Assert.ThrowsAnyAsync<IOException>(() => api.DevicesAsync());
        }
    }

    [Fact]
    public async Task NetworkFailuresAreIoErrors()
    {
        var acc = await _go.AccountAsync("PC");
        _go.Stop();
        var e = await Assert.ThrowsAnyAsync<IOException>(() => new Api(acc).DevicesAsync());
        Assert.IsNotType<ApiException>(e);
        using var srv = new ScriptedServer(_ => new(200, "[") { DeclaredLength = 50 });
        await Assert.ThrowsAnyAsync<IOException>(() => new Api(acc with { ServerUrl = srv.Url }).DevicesAsync());
        await Assert.ThrowsAnyAsync<IOException>(() => new Api(acc with { ServerUrl = "https://does-not-exist.invalid" }).DevicesAsync());
        await Assert.ThrowsAnyAsync<IOException>(() => new Api(acc with { ServerUrl = srv.Url }).RelayUploadAsync("x", 5, () => throw new IOException("gone"), default));
    }

    [Fact]
    public async Task CancellingTheTokenCancelsTheCall()
    {
        var acc = await _go.AccountAsync("PC");
        using var srv = new ScriptedServer(_ => new(200, "[]") { Delay = TimeSpan.FromSeconds(20) });
        using var cts = new CancellationTokenSource(300);
        var sw = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new Api(acc with { ServerUrl = srv.Url }).DevicesAsync(cts.Token));
        Assert.True(sw.ElapsedMilliseconds < 5000);
    }

    [Fact]
    public async Task ManyRelayUploadsDoNotBlockOtherCalls()
    {
        var acc = await _go.AccountAsync("PC");
        using var srv = new ScriptedServer(r => r.Path.Contains("relay") ? new(200) { Delay = TimeSpan.FromSeconds(15) } : new(200, "[]"));
        var api = new Api(acc with { ServerUrl = srv.Url });
        using var cts = new CancellationTokenSource();
        var ups = Enumerable.Range(0, 40).Select(i => api.RelayUploadAsync("r" + i, 3, () => new MemoryStream(new byte[3]), cts.Token)).ToList();
        await Eventually.True(() => srv.Seen.Count == 40, "uploads running");
        Assert.Empty(await api.DevicesAsync().WaitAsync(TimeSpan.FromSeconds(3)));
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Task.WhenAll(ups));
    }

    [Fact]
    public async Task RelayUploadNeedsExactlyTheLength()
    {
        var acc = await _go.AccountAsync("PC");
        using var srv = new ScriptedServer(_ => new(200));
        var api = new Api(acc with { ServerUrl = srv.Url });
        await api.RelayUploadAsync("a", 3, () => new MemoryStream(new byte[10]), default);
        Assert.Equal("3", srv.Seen.Single().Headers["content-length"]);
        await Assert.ThrowsAnyAsync<IOException>(() => api.RelayUploadAsync("a", 30, () => new MemoryStream(new byte[10]), default));
    }

    [Fact]
    public async Task RelayDownloadAcceptsOnlyExactAnswers()
    {
        var acc = await _go.AccountAsync("PC");
        Resp next = new(200);
        using var srv = new ScriptedServer(_ => next);
        var api = new Api(acc with { ServerUrl = srv.Url });
        async Task Bad(long offset) => await Assert.ThrowsAnyAsync<IOException>(() => api.RelayDownloadAsync("t", 0, offset, 5, TimeSpan.FromSeconds(5), default));
        next = new(200, "hello");
        using (var ok = await api.RelayDownloadAsync("t", 0, 0, 5, TimeSpan.FromSeconds(5), default)) Assert.Equal("hello", await ok.Content.ReadAsStringAsync());
        await Bad(2); // 200 for a resumed download
        next = new(200, "<html>");
        await Bad(0);
        next = new(200, "hello") { Chunked = true };
        await Bad(0);
        next = new(302) { Headers = { ["Location"] = "/login" } };
        await Bad(0);
        next = new(206, "llo") { Headers = { ["Content-Range"] = "bytes 0-2/5" } };
        await Bad(2);
        next = new(206, "llo") { Headers = { ["Content-Range"] = "bytes 2-4/5" } };
        using (var ok = await api.RelayDownloadAsync("t", 0, 2, 5, TimeSpan.FromSeconds(5), default)) Assert.Equal("llo", await ok.Content.ReadAsStringAsync());
        Assert.Equal("bytes=2-", srv.Seen.Last().Headers["range"]);
        Assert.False(srv.Seen.Any(r => r.Path.Contains("login")));
        next = new(200, "x") { Delay = TimeSpan.FromSeconds(10) };
        await Assert.ThrowsAnyAsync<IOException>(() => api.RelayDownloadAsync("t", 0, 0, 1, TimeSpan.FromMilliseconds(300), default));
    }
}
