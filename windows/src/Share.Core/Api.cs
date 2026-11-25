using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Share.Core;

/// <summary>A paired device.</summary>
public sealed record Account(string ServerUrl, string DeviceId, string Token, string Name, Identity Identity)
{
    // never leak the token through logs
    public override string ToString() => $"Account({ServerUrl}, {DeviceId}, {Name})";
}

/// <summary>The server answered with an error status.</summary>
public sealed class ApiException(int status, string message) : IOException(message)
{
    public int Status { get; } = status;

    /// <summary>The offer does not exist (for us) any more.</summary>
    public bool Gone => Status is 403 or 404 or 409 or 410;
}

public static class Pairing
{
    /// <summary>Adds https:// if missing and strips trailing slash, query and fragment; http only for local test servers.</summary>
    public static string NormalizeServerUrl(string input, bool allowInsecureLocal = false)
    {
        var raw = input.Trim();
        if (!raw.Contains("://")) raw = "https://" + raw;
        if (raw.Any(char.IsWhiteSpace) || !Uri.TryCreate(raw, UriKind.Absolute, out var u) || u.Host.Length == 0) throw new ArgumentException("invalid server url");
        var localTest = allowInsecureLocal && u.Scheme == "http" && (u.Host is "localhost" or "127.0.0.1");
        if (u.Scheme != "https" && !localTest)
            throw new ArgumentException("https required");
        return u.GetComponents(UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.UriEscaped).TrimEnd('/');
    }

    /// <summary>Registers this device with a pairing code.</summary>
    public static async Task<Account> PairAsync(string serverUrl, string code, string name, Identity identity, bool allowInsecureLocal = false, CancellationToken ct = default)
    {
        var url = NormalizeServerUrl(serverUrl, allowInsecureLocal);
        using var http = new Http(url, null);
        var r = await http.JsonAsync<PairResponse>("POST", "/v1/pair", new PairRequest(code.Trim().ToUpperInvariant(), name, DeviceType.Windows, identity.Fingerprint), ct).ConfigureAwait(false);
        return new Account(url, r.DeviceId, r.Token, name, identity);
    }
}

/// <summary>REST calls of docs/protocol.md. All network failures are IOExceptions.</summary>
public sealed class Api(Account account) : IDisposable
{
    private readonly Http _http = new(account.ServerUrl, account.Token);

    public Account Account { get; } = account;

    public Task<IReadOnlyList<Device>> DevicesAsync(CancellationToken ct = default) => _http.JsonAsync<IReadOnlyList<Device>>("GET", "/v1/devices", null, ct);
    public Task UpdateMeAsync(string? name = null, CancellationToken ct = default) => _http.SendAsync("PATCH", "/v1/devices/me", new UpdateDeviceRequest(name), ct);
    public Task DeleteMeAsync(CancellationToken ct = default) => _http.SendAsync("DELETE", "/v1/devices/me", null, ct);
    public Task<PairCodeResponse> PairCodeAsync(CancellationToken ct = default) => _http.JsonAsync<PairCodeResponse>("POST", "/v1/pair/codes", null, ct);
    public Task<CreateTransferResponse> CreateTransferAsync(CreateTransferRequest r, CancellationToken ct = default) => _http.JsonAsync<CreateTransferResponse>("POST", "/v1/transfers", r, ct);
    public Task<IReadOnlyList<Transfer>> IncomingAsync(CancellationToken ct = default) => _http.JsonAsync<IReadOnlyList<Transfer>>("GET", "/v1/transfers?role=incoming", null, ct);
    public Task<IReadOnlyList<Transfer>> OutgoingAsync(CancellationToken ct = default) => _http.JsonAsync<IReadOnlyList<Transfer>>("GET", "/v1/transfers?role=outgoing", null, ct);
    public Task<Transfer> TransferAsync(string id, CancellationToken ct = default) => _http.JsonAsync<Transfer>("GET", $"/v1/transfers/{id}", null, ct);
    public Task CancelAsync(string id, CancellationToken ct = default) => _http.SendAsync("DELETE", $"/v1/transfers/{id}", null, ct);
    public Task<AcceptResponse> AcceptAsync(string id, CancellationToken ct = default) => _http.JsonAsync<AcceptResponse>("POST", $"/v1/transfers/{id}/accept", null, ct);
    public Task DeclineAsync(string id, CancellationToken ct = default) => _http.SendAsync("POST", $"/v1/transfers/{id}/decline", null, ct);
    public Task ProgressAsync(string id, long bytes, DataPath path, CancellationToken ct = default) => _http.SendAsync("POST", $"/v1/transfers/{id}/progress", new ProgressRequest(bytes, path), ct);
    public Task DeliveredAsync(string id, CancellationToken ct = default) => _http.SendAsync("POST", $"/v1/transfers/{id}/delivered", null, ct);
    public Task FailAsync(string id, CancellationToken ct = default) => _http.SendAsync("POST", $"/v1/transfers/{id}/fail", null, ct);

    /// <summary>Opens the relay stream of file n from offset; the caller disposes the response.</summary>
    public Task<HttpResponseMessage> RelayDownloadAsync(string id, int n, long offset, long size, TimeSpan headerTimeout, CancellationToken ct) =>
        Http.OpenAsync(_http.Client, _http.Request("GET", $"/v1/transfers/{id}/files/{n}"), offset, size, headerTimeout, ct);

    /// <summary>Streams the rest of a file (exactly length bytes) to a waiting relay download.</summary>
    public async Task RelayUploadAsync(string relayId, long length, Func<Stream> open, CancellationToken ct)
    {
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var req = _http.Request("POST", $"/v1/relay/{relayId}");
        req.Content = new UploadContent(length, open, idle);
        using var resp = await Http.Guard(() => _http.Client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, idle.Token), ct).ConfigureAwait(false);
        await Http.CheckAsync(resp, ct).ConfigureAwait(false);
    }

    internal Uri EventsUri => new("ws" + Account.ServerUrl["http".Length..] + "/v1/events");

    public void Dispose() => _http.Dispose();

    private sealed class UploadContent : HttpContent
    {
        private readonly long _length;
        private readonly Func<Stream> _open;
        private readonly CancellationTokenSource _idle;

        public UploadContent(long length, Func<Stream> open, CancellationTokenSource idle)
        {
            (_length, _open, _idle) = (length, open, idle);
            Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        }

        protected override bool TryComputeLength(out long length)
        {
            length = _length;
            return true;
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => SerializeToStreamAsync(stream, context, default);

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken ct)
        {
            await using var input = _open();
            var buf = new byte[Limits.Buffer];
            for (var left = _length; left > 0;)
            {
                _idle.CancelAfter(TimeSpan.FromMinutes(1));
                var n = await input.ReadAsync(buf.AsMemory(0, (int)Math.Min(left, buf.Length)), ct).ConfigureAwait(false);
                if (n == 0) throw new IOException("file shorter than offered");
                await stream.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                left -= n;
            }
            _idle.CancelAfter(TimeSpan.FromMinutes(5)); // the answer comes only after the receiver has taken everything
        }
    }
}

internal sealed class Http(string baseUrl, string? token) : IDisposable
{
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(30);

    public HttpClient Client { get; } = new(new SocketsHttpHandler
    {
        ConnectTimeout = TimeSpan.FromSeconds(10),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        KeepAlivePingDelay = TimeSpan.FromSeconds(20),
        KeepAlivePingTimeout = TimeSpan.FromSeconds(15),
        EnableMultipleHttp2Connections = true,
        AllowAutoRedirect = false,
    })
    {
        Timeout = Timeout.InfiniteTimeSpan,
        DefaultRequestVersion = HttpVersion.Version20,
        DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
    };

    public HttpRequestMessage Request(string method, string path)
    {
        var r = new HttpRequestMessage(new HttpMethod(method), baseUrl + path) { Version = HttpVersion.Version20, VersionPolicy = HttpVersionPolicy.RequestVersionOrLower };
        if (token is not null) r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return r;
    }

    public async Task<string> SendAsync(string method, string path, object? body, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(CallTimeout);
        using var req = Request(method, path);
        if (body is not null) req.Content = new StringContent(JsonSerializer.Serialize(body, body.GetType(), ShareJson.Options), Encoding.UTF8, "application/json");
        return await Guard(async () =>
        {
            using var resp = await Client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
            await CheckAsync(resp, cts.Token).ConfigureAwait(false);
            return await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    public async Task<T> JsonAsync<T>(string method, string path, object? body, CancellationToken ct) where T : class
    {
        var s = await SendAsync(method, path, body, ct).ConfigureAwait(false);
        try
        {
            return JsonSerializer.Deserialize<T>(s, ShareJson.Options) ?? throw new IOException("unexpected answer from server");
        }
        catch (JsonException e) // e.g. a captive portal answering 200 with HTML
        {
            throw new IOException("unexpected answer from server", e);
        }
    }

    /// <summary>
    /// Opens a download. Accepts 200 for a full and 206 for a resumed one, and only if the answer covers exactly the missing
    /// bytes: a proxy's error or login page answered with 200 must never become part of a file.
    /// </summary>
    public static async Task<HttpResponseMessage> OpenAsync(HttpClient client, HttpRequestMessage req, long offset, long size, TimeSpan headerTimeout, CancellationToken ct)
    {
        using var _ = req;
        if (offset > 0) req.Headers.Range = new RangeHeaderValue(offset, null);
        req.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("identity"));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(headerTimeout);
        var resp = await Guard(() => client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token), ct).ConfigureAwait(false);
        try
        {
            await CheckAsync(resp, cts.Token).ConfigureAwait(false);
            var length = resp.Content.Headers.ContentLength;
            var ok = resp.StatusCode == (offset > 0 ? HttpStatusCode.PartialContent : HttpStatusCode.OK) && length == size - offset
                && (offset == 0 || resp.Content.Headers.ContentRange?.From == offset);
            if (!ok) throw new IOException($"unexpected answer {(int)resp.StatusCode} ({length} bytes, {resp.Content.Headers.ContentRange}) for offset {offset} of {size}");
            return resp;
        }
        catch
        {
            resp.Dispose();
            throw;
        }
    }

    /// <summary>Turns everything that is not our own cancellation into an IOException.</summary>
    public static async Task<T> Guard<T>(Func<Task<T>> f, CancellationToken ct)
    {
        try
        {
            return await f().ConfigureAwait(false);
        }
        catch (OperationCanceledException e) when (!ct.IsCancellationRequested)
        {
            throw new IOException("timeout", e);
        }
        catch (HttpRequestException e)
        {
            ct.ThrowIfCancellationRequested();
            throw new IOException(e.Message, e);
        }
        catch (ObjectDisposedException e)
        {
            throw new IOException("closed", e);
        }
    }

    /// <summary>Throws if the status is not 2xx.</summary>
    public static async Task CheckAsync(HttpResponseMessage r, CancellationToken ct)
    {
        if (r.IsSuccessStatusCode) return;
        string? msg = null;
        try
        {
            var text = await r.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String) msg = e.GetString();
        }
        catch (Exception e) when (e is JsonException or HttpRequestException or IOException)
        {
            // the status is all we have
        }
        throw new ApiException((int)r.StatusCode, string.IsNullOrWhiteSpace(msg) ? $"HTTP {(int)r.StatusCode}" : msg);
    }

    public void Dispose() => Client.Dispose();
}
