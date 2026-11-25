using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security;

namespace Share.Core;

/// <summary>The offer expired, was withdrawn or is otherwise over.</summary>
public sealed class OfferEndedException(string message) : IOException(message);

/// <summary>The download cannot succeed; the message is shown to the user.</summary>
public sealed class DownloadFailedException(string message, Exception? inner = null) : IOException(message, inner);

public sealed record ReceiverOptions
{
    public TimeSpan ProbeTimeout { get; init; } = TimeSpan.FromMilliseconds(1500);
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan BackoffBase { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Without any progress and contact to the server, this long after the offer's expiry.</summary>
    public TimeSpan GiveUpAfter { get; init; } = TimeSpan.FromMinutes(10);
}

/// <summary>Accepts a file offer and downloads it: LAN first, relay otherwise, resuming where it stopped.</summary>
public sealed class Receiver(Api api, IDownloadStore store, ReceiverOptions? options = null)
{
    private readonly Api _api = api;
    private readonly IDownloadStore _store = store;
    private readonly ReceiverOptions _o = options ?? new();

    /// <summary>Returns references to all files. Cancelling keeps the partial state for a later call.</summary>
    /// <param name="onProgress">gets the total received bytes and the current path</param>
    public Task<IReadOnlyList<string>> DownloadAsync(Transfer t, Action<long, DataPath> onProgress, CancellationToken ct = default) => Task.Run(async () =>
    {
        try
        {
            var accepted = await AcceptAsync(t, ct).ConfigureAwait(false);
            using var run = new Run(this, t, accepted, onProgress);
            var refs = await run.AllAsync(ct).ConfigureAwait(false);
            // without the server's answer the finished markers stay, so a later call only reports
            if (await run.Reporter.DeliveredAsync(ct).ConfigureAwait(false)) _store.Forget(t.Id);
            return refs;
        }
        catch (OfferEndedException)
        {
            _store.Forget(t.Id);
            throw;
        }
        catch (FatalException e)
        {
            await AbortAsync(t.Id).ConfigureAwait(false);
            throw new DownloadFailedException(e.Message, e.InnerException);
        }
    }, CancellationToken.None);

    /// <summary>Ends an accepted download for good: removes partial files and tells the server.</summary>
    public async Task AbortAsync(string id)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            _store.Forget(id);
            await _api.FailAsync(id, cts.Token).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or UnauthorizedAccessException)
        {
            // the server notices by itself
        }
    }

    private sealed class FatalException(string message, Exception? inner = null) : Exception(message, inner);

    private async Task<AcceptResponse> AcceptAsync(Transfer t, CancellationToken ct)
    {
        var retry = new Backoff(_o, t);
        while (true)
        {
            try
            {
                return await _api.AcceptAsync(t.Id, ct).ConfigureAwait(false);
            }
            catch (IOException e)
            {
                switch ((e as ApiException)?.Status)
                {
                    case 404 or 409 or 410: throw new OfferEndedException(e.Message);
                    case 401: throw new DownloadFailedException("Dieses Gerät ist nicht mehr gekoppelt", e);
                }
                await retry.PauseAsync(e, ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Pauses between attempts; gives up when nothing has moved for long after the offer expired.</summary>
    private sealed class Backoff(ReceiverOptions o, Transfer t)
    {
        private TimeSpan _pause = o.BackoffBase;
        private long _contact = Stopwatch.GetTimestamp();

        public void Progressed()
        {
            _pause = o.BackoffBase;
            Contact();
        }

        public void Contact() => _contact = Stopwatch.GetTimestamp();

        public async Task PauseAsync(IOException cause, CancellationToken ct)
        {
            if (Stopwatch.GetElapsedTime(_contact) > o.GiveUpAfter && DateTimeOffset.UtcNow > t.ExpiresAt) throw new DownloadFailedException("Server nicht erreichbar", cause);
            await Task.Delay(_pause, ct).ConfigureAwait(false);
            _pause = _pause * 2 < o.BackoffBase * 16 ? _pause * 2 : o.BackoffBase * 16;
        }
    }

    private sealed class Run : IDisposable
    {
        private readonly Receiver _r;
        private readonly Transfer _t;
        private readonly AcceptResponse _acc;
        private readonly Action<long, DataPath> _onProgress;
        private readonly HttpClient? _lan;

        public Run(Receiver r, Transfer t, AcceptResponse acc, Action<long, DataPath> onProgress)
        {
            (_r, _t, _acc, _onProgress) = (r, t, acc, onProgress);
            Reporter = new Reporter(r._api, t.Id, r._o);
            if (acc.LanCandidates.Count == 0 || acc.CertFingerprint.Length == 0) return;
            var h = Pinning.CreateHandler(acc.CertFingerprint);
            h.ConnectTimeout = r._o.ProbeTimeout;
            _lan = new HttpClient(h) { Timeout = Timeout.InfiniteTimeSpan };
        }

        public Reporter Reporter { get; }

        public void Dispose()
        {
            Reporter.Dispose();
            _lan?.Dispose();
        }

        public async Task<IReadOnlyList<string>> AllAsync(CancellationToken ct)
        {
            var refs = new List<string>();
            long done = 0;
            for (var n = 0; n < _t.Files.Count; n++)
            {
                refs.Add(await FileAsync(n, done, ct).ConfigureAwait(false));
                done += _t.Files[n].Size;
            }
            return refs;
        }

        private async Task<string> FileAsync(int n, long before, CancellationToken ct)
        {
            var item = _t.Files[n];
            var part = Local(() => _r._store.Part(_t.Id, n, item));
            var retry = new Backoff(_r._o, _t);
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                long have = 0;
                try
                {
                    if (Local(() => part.FinishedRef) is { } done) return done;
                    have = Local(part.Size);
                    if (have > item.Size)
                    {
                        Local(() =>
                        {
                            part.Reset();
                            return 0;
                        });
                        have = 0;
                    }
                    if (have == item.Size) return Local(part.Finish);
                    await FetchAsync(n, item, part, have, before, ct).ConfigureAwait(false);
                    if (SizeOf(part) > have) retry.Progressed();
                }
                catch (IOException e) when (e is not (OfferEndedException or DownloadFailedException))
                {
                    ct.ThrowIfCancellationRequested();
                    if (SizeOf(part) > have) retry.Progressed();
                    if (await CheckAliveAsync(ct).ConfigureAwait(false)) retry.Contact();
                    await retry.PauseAsync(e, ct).ConfigureAwait(false);
                }
            }
        }

        /// <summary>For the back-off only, so a busy storage does not matter.</summary>
        private static long SizeOf(IPart part)
        {
            try
            {
                return part.Size();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return 0;
            }
        }

        /// <summary>Local storage errors are final (partial files are removed), unless the storage is just busy.</summary>
        private static T Local<T>(Func<T> f)
        {
            try
            {
                return f();
            }
            catch (Exception e) when (IsStorageError(e))
            {
                throw new FatalException("Speichern fehlgeschlagen (Speicher voll?)", e);
            }
        }

        private static bool IsStorageError(Exception e) =>
            e is (IOException and not StorageBusyException) or UnauthorizedAccessException or NotSupportedException or ArgumentException or SecurityException;

        /// <summary>True: the server says the delivery goes on. False: no contact. Throws when it is over.</summary>
        private async Task<bool> CheckAliveAsync(CancellationToken ct)
        {
            Transfer now;
            try
            {
                now = await _r._api.TransferAsync(_t.Id, ct).ConfigureAwait(false);
            }
            catch (IOException e)
            {
                switch ((e as ApiException)?.Status)
                {
                    case 404: throw new OfferEndedException("unknown offer");
                    case 401: throw new DownloadFailedException("Dieses Gerät ist nicht mehr gekoppelt", e);
                    default: return false;
                }
            }
            if (now.Closed) throw new OfferEndedException("offer closed");
            if (now.Deliveries.FirstOrDefault(d => d.Status.IsFinal()) is { } d) throw new OfferEndedException($"delivery {d.Status}");
            return true;
        }

        /// <summary>Base URL of the first LAN candidate that answers, or null.</summary>
        private async Task<string?> ProbeAsync(CancellationToken ct)
        {
            if (_lan is null) return null;
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_r._o.ProbeTimeout);
            var probes = _acc.LanCandidates.Select(c => ProbeOneAsync($"https://{c}/t/{_t.Id}", cts.Token)).ToList();
            string? winner = null;
            while (probes.Count > 0 && winner is null)
            {
                var first = await Task.WhenAny(probes).ConfigureAwait(false);
                probes.Remove(first);
                winner = await first.ConfigureAwait(false);
            }
            cts.Cancel();
            ct.ThrowIfCancellationRequested();
            return winner;
        }

        private async Task<string?> ProbeOneAsync(string baseUrl, CancellationToken ct)
        {
            try
            {
                using var req = LanRequest(baseUrl + "/ping");
                using var resp = await _lan!.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                return (int)resp.StatusCode == 204 ? baseUrl : null;
            }
            catch (Exception e) when (e is HttpRequestException or OperationCanceledException or IOException or UriFormatException or InvalidOperationException or NotSupportedException)
            {
                return null; // unreachable, somebody else, or a malformed candidate
            }
        }

        private HttpRequestMessage LanRequest(string url)
        {
            var r = new HttpRequestMessage(HttpMethod.Get, url);
            r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _acc.AccessToken);
            return r;
        }

        private async Task FetchAsync(int n, FileItem item, IPart part, long have, long before, CancellationToken ct)
        {
            var lan = await ProbeAsync(ct).ConfigureAwait(false);
            var path = lan is null ? DataPath.Relay : DataPath.Lan;
            HttpResponseMessage resp;
            try
            {
                resp = lan is null
                    ? await _r._api.RelayDownloadAsync(_t.Id, n, have, item.Size, _r._o.IdleTimeout, ct).ConfigureAwait(false)
                    : await Http.OpenAsync(_lan!, LanRequest($"{lan}/files/{n}"), have, item.Size, _r._o.IdleTimeout, ct).ConfigureAwait(false);
            }
            catch (ApiException e) when (lan is not null && e.Status is (409 or 410))
            {
                throw new FatalException(e.Status == 409 ? "Datei wurde beim Sender geändert" : "Datei ist beim Sender nicht mehr lesbar");
            }
            using var _ = resp;
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
            await using var input = await Http.Guard(() => resp.Content.ReadAsStreamAsync(idle.Token), ct).ConfigureAwait(false);
            var o = Local(() => part.Append(have));
            try
            {
                var buf = new byte[Limits.Buffer];
                for (var got = have; got < item.Size;)
                {
                    idle.CancelAfter(_r._o.IdleTimeout);
                    var max = (int)Math.Min(buf.Length, item.Size - got);
                    var k = await Http.Guard(() => input.ReadAsync(buf.AsMemory(0, max), idle.Token).AsTask(), ct).ConfigureAwait(false);
                    if (k == 0) throw new IOException("stream ended early");
                    await WriteAsync(o, buf.AsMemory(0, k)).ConfigureAwait(false);
                    got += k;
                    Reporter.Update(before + got, path);
                    _onProgress(before + got, path);
                }
            }
            finally
            {
                await CloseAsync(o).ConfigureAwait(false);
            }
        }

        private static async Task WriteAsync(Stream o, ReadOnlyMemory<byte> data)
        {
            try
            {
                await o.WriteAsync(data, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception e) when (IsStorageError(e))
            {
                throw new FatalException("Speichern fehlgeschlagen (Speicher voll?)", e);
            }
        }

        private static async Task CloseAsync(Stream o)
        {
            try
            {
                await o.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception e) when (IsStorageError(e))
            {
                // the size check decides what is left
            }
        }
    }

    /// <summary>Reports progress to the server at most once per second, never slowing the download.</summary>
    private sealed class Reporter : IDisposable
    {
        private readonly Api _api;
        private readonly string _id;
        private readonly ReceiverOptions _o;
        private readonly CancellationTokenSource _stop = new();
        private readonly SemaphoreSlim _changed = new(0, 1);
        private readonly object _lock = new();
        private (long Bytes, DataPath Path)? _latest;
        private DataPath? _sentPath;

        public Reporter(Api api, string id, ReceiverOptions o)
        {
            (_api, _id, _o) = (api, id, o);
            _ = LoopAsync();
        }

        public void Update(long bytes, DataPath path)
        {
            lock (_lock)
            {
                _latest = (bytes, path);
                if (_changed.CurrentCount == 0) _changed.Release();
            }
        }

        private async Task LoopAsync()
        {
            try
            {
                while (true)
                {
                    await _changed.WaitAsync(_stop.Token).ConfigureAwait(false);
                    await SendAsync(_stop.Token).ConfigureAwait(false);
                    await Task.Delay(1000, _stop.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
            }
        }

        private async Task SendAsync(CancellationToken ct)
        {
            (long Bytes, DataPath Path) l;
            lock (_lock)
            {
                if (_latest is not { } x) return;
                l = x;
            }
            try
            {
                await _api.ProgressAsync(_id, l.Bytes, l.Path, ct).ConfigureAwait(false);
                lock (_lock) _sentPath = l.Path;
            }
            catch (IOException)
            {
                // purely informational
            }
        }

        /// <summary>Best effort: the files are complete, whatever the server says. False if it could not be told.</summary>
        public async Task<bool> DeliveredAsync(CancellationToken ct)
        {
            _stop.Cancel();
            bool pathKnown;
            lock (_lock) pathKnown = _latest is null || _sentPath == _latest?.Path;
            if (!pathKnown)
            {
                // the server fills in the bytes on delivery; only the path must have been reported once
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(2));
                try
                {
                    await SendAsync(cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                }
            }
            var pause = _o.BackoffBase;
            for (var i = 0; i < 5; i++)
            {
                try
                {
                    await _api.DeliveredAsync(_id, ct).ConfigureAwait(false);
                    return true;
                }
                catch (IOException e)
                {
                    if (e is ApiException { Status: < 500 }) return true;
                    await Task.Delay(pause, ct).ConfigureAwait(false);
                    pause *= 2;
                }
            }
            return false;
        }

        public void Dispose() => _stop.Cancel();
    }
}
