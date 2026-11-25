using System.Collections.Immutable;
using System.Text;

namespace Share.Core;

/// <summary>Offers files, texts and links; serves files from the local endpoint or uploads them to the relay.</summary>
public sealed class Sender : IDisposable
{
    private readonly Api _api;
    private readonly Func<int, IReadOnlyList<string>> _candidates;
    private readonly Action<Transfer>? _onEnded;
    private readonly LocalEndpoint _endpoint;
    private readonly CancellationTokenSource _closed = new();
    private readonly object _lock = new();

    /// <summary>Held while an offer is being created, so the orphan cleanup cannot mistake it for an orphan.</summary>
    private readonly SemaphoreSlim _creating = new(1, 1);

    private readonly Dictionary<string, Own> _own = [];
    private readonly List<ServerEvent> _early = []; // events for offers whose create call has not returned yet
    private ImmutableDictionary<string, Transfer> _offers = ImmutableDictionary<string, Transfer>.Empty;
    private int _pending;
    private int _pendingFiles;

    private sealed record Own(Offer Offer, CancellationTokenSource Relays);

    /// <param name="onEnded">An offer ended other than by our own cancelling; gets its last known state.</param>
    public Sender(Api api, int port = Limits.DefaultPort, Func<int, IReadOnlyList<string>>? candidates = null, Action<Transfer>? onEnded = null)
    {
        _api = api;
        _candidates = candidates ?? LanAddresses.Candidates;
        _onEnded = onEnded;
        _endpoint = new LocalEndpoint(api.Account.Identity, port, LookupAsync);
    }

    /// <summary>Our open offers with their current deliveries.</summary>
    public ImmutableDictionary<string, Transfer> Offers => Volatile.Read(ref _offers);

    public event Action? Changed;

    public async Task<Transfer> OfferFilesAsync(IReadOnlyList<IFileSource> sources, IReadOnlyList<string> targets, bool broadcast, string message = "", int minutes = 10, CancellationToken ct = default)
    {
        var registered = false;
        try
        {
            if (sources.Count == 0) throw new ArgumentException("no files");
            return await CreateAsync(true, async () =>
            {
                IReadOnlyList<string> lan = [];
                try
                {
                    lan = _candidates(_endpoint.Start());
                }
                catch (IOException)
                {
                    // relay only
                }
                var r = await _api.CreateTransferAsync(new(Kind.Files, broadcast)
                {
                    Message = message.Length == 0 ? null : message, Files = [.. sources.Select(s => s.Item)], Targets = targets,
                    Minutes = Math.Clamp(minutes, 1, 10), LanCandidates = lan,
                }, ct).ConfigureAwait(false);
                Register(r, sources);
                registered = true;
                return r.Transfer;
            }, ct).ConfigureAwait(false);
        }
        finally
        {
            if (!registered)
                foreach (var s in sources)
                    s.Release();
        }
    }

    public Task<Transfer> OfferTextAsync(Kind kind, string text, IReadOnlyList<string> targets, bool broadcast, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return Task.FromException<Transfer>(new ArgumentException("empty text"));
        if (Encoding.UTF8.GetByteCount(text) > Limits.MaxTextBytes) return Task.FromException<Transfer>(new ArgumentException("text too long"));
        return CreateAsync(false, async () =>
        {
            var r = await _api.CreateTransferAsync(new(kind, broadcast) { Text = text, Targets = targets }, ct).ConfigureAwait(false);
            Register(r, []);
            return r.Transfer;
        }, ct);
    }

    private async Task<Transfer> CreateAsync(bool files, Func<Task<Transfer>> create, CancellationToken ct)
    {
        lock (_lock)
        {
            _pending++;
            if (files) _pendingFiles++;
        }
        try
        {
            await _creating.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                return await create().ConfigureAwait(false);
            }
            finally
            {
                _creating.Release();
            }
        }
        catch (Exception e) when (e is OperationCanceledException or IOException and not ApiException)
        {
            // the server may have created the offer although we never got the answer
            _ = ResyncAsync();
            throw;
        }
        finally
        {
            lock (_lock)
            {
                _pending--;
                if (files) _pendingFiles--;
                if (_pending == 0) _early.Clear();
                StopEndpointIfIdle();
            }
        }
    }

    private void Register(CreateTransferResponse r, IReadOnlyList<IFileSource> sources)
    {
        var t = r.Transfer;
        List<ServerEvent> replay;
        lock (_lock)
        {
            if (_closed.IsCancellationRequested) throw new IOException("closed");
            _own[t.Id] = new Own(new Offer(t, Hex.TryParse(r.Secret) ?? [], sources), new CancellationTokenSource());
            _offers = _offers.SetItem(t.Id, t);
            replay = [.. _early.Where(e => IdOf(e) == t.Id)];
            _early.RemoveAll(replay.Contains);
        }
        Changed?.Invoke();
        foreach (var e in replay) Apply(e);
    }

    /// <summary>Withdraws an offer. Ends locally even without a connection; the server then learns about it on reconnect.</summary>
    public async Task CancelAsync(string id)
    {
        Remove(id);
        try
        {
            await _api.CancelAsync(id).ConfigureAwait(false);
        }
        catch (IOException)
        {
            // unknown/closed on the server, or no connection
        }
    }

    /// <summary>Ends an offer locally only.</summary>
    public void Drop(string id) => Ended(id);

    private void Ended(string id)
    {
        var last = Offers.GetValueOrDefault(id);
        if (Remove(id) && last is not null) _onEnded?.Invoke(last);
    }

    public void OnEvent(ServerEvent e)
    {
        switch (e)
        {
            case ServerEvent.Connected:
                _ = ResyncAsync();
                break;
            case ServerEvent.Relay r:
                _ = ServeRelayAsync(r.Request);
                break;
            case ServerEvent.DeliveryUpdated or ServerEvent.TransferClosed:
                Apply(e);
                break;
        }
    }

    private static string? IdOf(ServerEvent e) => e switch
    {
        ServerEvent.DeliveryUpdated d => d.Update.TransferId,
        ServerEvent.TransferClosed c => c.Id,
        _ => null,
    };

    private void Apply(ServerEvent e)
    {
        var id = IdOf(e);
        if (id is null) return;
        lock (_lock)
        {
            if (!_own.ContainsKey(id))
            {
                if (_pending > 0) _early.Add(e);
                return;
            }
            if (e is ServerEvent.DeliveryUpdated u && _offers.TryGetValue(id, out var t))
            {
                var d = u.Update.Delivery;
                var known = t.Deliveries.Any(x => x.DeviceId == d.DeviceId);
                _offers = _offers.SetItem(id, t with { Deliveries = known ? [.. t.Deliveries.Select(x => x.DeviceId == d.DeviceId ? d : x)] : [.. t.Deliveries, d] });
            }
        }
        if (e is ServerEvent.TransferClosed) Ended(id);
        else Changed?.Invoke();
    }

    /// <summary>Recovers what happened while disconnected.</summary>
    private async Task ResyncAsync()
    {
        try
        {
            await _creating.WaitAsync(_closed.Token).ConfigureAwait(false);
            try
            {
                string[] known;
                lock (_lock) known = [.. _own.Keys];
                var open = (await _api.OutgoingAsync(_closed.Token).ConfigureAwait(false)).ToDictionary(t => t.Id);
                foreach (var id in known)
                {
                    if (open.TryGetValue(id, out var t)) Refresh(t);
                    else Ended(id);
                }
                foreach (var t in open.Values.Where(t => !known.Contains(t.Id)))
                {
                    if (t.Kind == Kind.Files) await CancelOrphanAsync(t.Id).ConfigureAwait(false);
                    else Adopt(t);
                }
            }
            finally
            {
                _creating.Release();
            }
            Changed?.Invoke();
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException)
        {
            // connection lost again; the next Connected repeats this
        }
    }

    private void Refresh(Transfer t)
    {
        lock (_lock)
        {
            if (!_offers.TryGetValue(t.Id, out var old)) return;
            // an event may have overtaken this snapshot
            _offers = _offers.SetItem(t.Id, t with { Deliveries = [.. t.Deliveries.Select(d => old.Deliveries.FirstOrDefault(o => o.DeviceId == d.DeviceId) is { } o && o.UpdatedAt > d.UpdatedAt ? o : d)] });
        }
    }

    /// <summary>Left over from a killed process or an offline cancel: nobody can serve these files any more.</summary>
    private async Task CancelOrphanAsync(string id)
    {
        try
        {
            await _api.CancelAsync(id, _closed.Token).ConfigureAwait(false);
        }
        catch (IOException)
        {
            // the others still get their turn
        }
    }

    private void Adopt(Transfer t)
    {
        lock (_lock)
        {
            if (_closed.IsCancellationRequested) return;
            _own[t.Id] = new Own(new Offer(t, [], []), new CancellationTokenSource());
            _offers = _offers.SetItem(t.Id, t);
        }
    }

    /// <summary>Waits briefly for an offer that may still be registering: receivers hear about it before our create call returns.</summary>
    private async Task<Offer?> LookupAsync(string id)
    {
        for (var i = 0; i < 40; i++)
        {
            lock (_lock)
            {
                if (_own.TryGetValue(id, out var o)) return o.Offer;
                if (_pending == 0) return null;
            }
            await Task.Delay(50).ConfigureAwait(false);
        }
        return null;
    }

    private async Task ServeRelayAsync(RelayRequest r)
    {
        try
        {
            if (await LookupAsync(r.TransferId).ConfigureAwait(false) is not { } offer) return;
            CancellationToken ct;
            lock (_lock)
            {
                if (!_own.TryGetValue(r.TransferId, out var own)) return;
                ct = own.Relays.Token;
            }
            if (r.FileIndex < 0 || r.FileIndex >= offer.Sources.Count) return;
            var src = offer.Sources[r.FileIndex];
            if (r.Offset < 0 || r.Offset > src.Item.Size || !src.Unchanged()) return;
            await _api.RelayUploadAsync(r.RelayId, src.Item.Size - r.Offset, () => new Guarded(src, r.Offset), ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException)
        {
            // the receiver resumes
        }
    }

    /// <summary>Stops delivering as soon as the source has changed.</summary>
    private sealed class Guarded(IFileSource src, long offset) : Stream
    {
        private readonly Stream _inner = src.OpenAt(offset);

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            var n = await _inner.ReadAsync(buffer, ct).ConfigureAwait(false);
            return src.Unchanged() ? n : throw new IOException("source changed");
        }

        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        protected override void Dispose(bool disposing)
        {
            if (disposing) _inner.Dispose();
            base.Dispose(disposing);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>False if it was not open any more.</summary>
    private bool Remove(string id)
    {
        Own? own;
        lock (_lock)
        {
            if (!_own.Remove(id, out own)) return false;
            _offers = _offers.Remove(id);
            _endpoint.CloseConnections(id);
            StopEndpointIfIdle();
        }
        own.Relays.Cancel();
        foreach (var s in own.Offer.Sources) s.Release();
        Changed?.Invoke();
        return true;
    }

    private void StopEndpointIfIdle()
    {
        if (_pendingFiles == 0 && _own.Values.All(o => o.Offer.Transfer.Kind != Kind.Files)) _endpoint.Stop();
    }

    /// <summary>Simulates losing the local path (tests).</summary>
    internal void StopEndpointForTest() => _endpoint.Stop();

    /// <summary>Ends all offers locally.</summary>
    public void Dispose()
    {
        string[] ids;
        lock (_lock)
        {
            _closed.Cancel();
            ids = [.. _own.Keys];
        }
        foreach (var id in ids) Remove(id);
        _endpoint.Stop();
    }
}
