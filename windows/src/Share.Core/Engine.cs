namespace Share.Core;

public enum Phase { Running, Done, Failed }

/// <summary>An accepted file offer.</summary>
public sealed record Download(Transfer Transfer)
{
    public Phase Phase { get; init; } = Phase.Running;
    public long Bytes { get; init; }
    public DataPath? Path { get; init; }
    public IReadOnlyList<string> Refs { get; init; } = [];
    public string? Error { get; init; }
}

public sealed record EngineState
{
    public bool Connected { get; init; }

    /// <summary>The token was revoked; the device has to be paired again.</summary>
    public bool Unauthorised { get; init; }

    /// <summary>Possible targets: without this device and without agents.</summary>
    public IReadOnlyList<Device> Devices { get; init; } = [];

    /// <summary>Names of all known devices by ID, for display.</summary>
    public IReadOnlyDictionary<string, string> Names { get; init; } = new Dictionary<string, string>();

    /// <summary>Offers waiting for our answer.</summary>
    public IReadOnlyList<Transfer> Incoming { get; init; } = [];
    public IReadOnlyList<Transfer> Outgoing { get; init; } = [];
    public IReadOnlyList<Download> Downloads { get; init; } = [];

    /// <summary>Own offers that have ended, with their outcome, until dismissed.</summary>
    public IReadOnlyList<Transfer> Sent { get; init; } = [];

    /// <summary>Accepted texts and links, until dismissed.</summary>
    public IReadOnlyList<Transfer> Received { get; init; } = [];

    public string NameOf(string id) => Names.GetValueOrDefault(id, "Unbekanntes Gerät");
}

public abstract record Notice
{
    public sealed record OfferArrived(Transfer Transfer) : Notice;
    public sealed record OfferGone(string Id) : Notice;
    public sealed record DownloadDone(Download Download) : Notice;
    public sealed record DownloadFailed(Download Download) : Notice;

    /// <summary>One of our offers ended (not by cancelling it here); carries the final status per receiver.</summary>
    public sealed record OfferEnded(Transfer Transfer) : Notice;

    /// <summary>An accepted text or link.</summary>
    public sealed record TextReceived(Transfer Transfer) : Notice;

    /// <summary>The user answered an offer that no longer exists.</summary>
    public sealed record Unavailable(string Id) : Notice;
}

public sealed record EngineOptions
{
    public int Port { get; init; } = Limits.DefaultPort;
    public Func<int, IReadOnlyList<string>>? Candidates { get; init; }
    public EventOptions Events { get; init; } = new();
    public ReceiverOptions Receiver { get; init; } = new();
    public TimeSpan Tick { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>How often the device list is reloaded.</summary>
    public TimeSpan DeviceRefresh { get; init; } = TimeSpan.FromSeconds(15);
    public Func<DateTimeOffset> Now { get; init; } = () => DateTimeOffset.UtcNow;
}

/// <summary>Everything the program does, as observable state. Safe to call from any thread; events come on any thread.</summary>
public sealed class Engine : IDisposable
{
    /// <summary>Finished entries are not a history.</summary>
    private const int Keep = 20;

    private readonly IDownloadStore _store;
    private readonly EngineOptions _o;
    private readonly Sender _sender;
    private readonly Receiver _receiver;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _refreshing = new(1, 1);
    private readonly object _lock = new();
    private readonly Dictionary<string, (CancellationTokenSource Cts, Task Task)> _jobs = []; // running downloads
    private readonly HashSet<string> _answering = []; // offers being accepted
    private readonly HashSet<string> _aborted = []; // downloads the user cancelled; never resumed
    private readonly HashSet<string> _completed = []; // downloads that are complete here, whatever the server thinks
    private EngineState _state = new();
    private bool _started;
    private bool _disposed;

    public Engine(Api api, IDownloadStore store, EngineOptions? options = null)
    {
        Api = api;
        _store = store;
        _o = options ?? new();
        _sender = new Sender(api, _o.Port, _o.Candidates, t => _ = OfferEndedAsync(t));
        _sender.Changed += () => Update(s => s with { Outgoing = [.. _sender.Offers.Values.OrderBy(t => t.CreatedAt)] });
        _receiver = new Receiver(api, store, _o.Receiver);
    }

    public Api Api { get; }
    public EngineState State => Volatile.Read(ref _state);

    /// <summary>Own file offers or running downloads: the computer must not sleep.</summary>
    public bool Busy => _sender.Offers.Values.Any(t => t.Kind == Kind.Files) || State.Downloads.Any(d => d.Phase == Phase.Running);

    public event Action? StateChanged;
    public event Action<Notice>? Notified;

    public string DeviceName(string id) => State.NameOf(id);

    /// <summary>Connects and stays connected.</summary>
    public void Start()
    {
        lock (_lock)
        {
            if (_started || _disposed) return;
            _started = true;
        }
        _ = Task.Run(ListenAsync);
        _ = Every(_o.DeviceRefresh, async () =>
        {
            if (State.Connected) await Quietly(LoadDevicesAsync).ConfigureAwait(false);
        });
        _ = Every(_o.Tick, () =>
        {
            Expire();
            return Task.CompletedTask;
        });
    }

    /// <summary>Reconnects without waiting, e.g. after waking up.</summary>
    public void Reconnect() => _o.Events.Reconnect();

    private async Task Every(TimeSpan interval, Func<Task> f)
    {
        try
        {
            while (true)
            {
                await Task.Delay(interval, _stop.Token).ConfigureAwait(false);
                await f().ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task ListenAsync()
    {
        try
        {
            await foreach (var e in Api.EventsAsync(_o.Events, _stop.Token).ConfigureAwait(false)) OnEvent(e);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            Update(s => s with { Connected = false });
        }
    }

    private void OnEvent(ServerEvent e)
    {
        _sender.OnEvent(e);
        switch (e)
        {
            case ServerEvent.Connected:
                Update(s => s with { Connected = true, Unauthorised = false });
                _ = Quietly(RefreshAsync);
                break;
            case ServerEvent.Disconnected:
                Update(s => s with { Connected = false });
                break;
            case ServerEvent.Unauthorised:
                Update(s => s with { Connected = false, Unauthorised = true });
                break;
            case ServerEvent.OfferNew n:
                LearnName(n.Transfer.SenderId);
                Arrive(n.Transfer);
                break;
            case ServerEvent.OfferRemoved r:
                Leave(r.Id);
                break;
            case ServerEvent.DeliveryUpdated d:
                LearnName(d.Update.Delivery.DeviceId);
                break;
        }
    }

    private void Update(Func<EngineState, EngineState> f)
    {
        lock (_lock)
        {
            if (_disposed) return;
            Volatile.Write(ref _state, f(_state));
        }
        Raise(StateChanged, l => ((Action)l)());
    }

    private void Notify(Notice n) => Raise(Notified, l => ((Action<Notice>)l)(n));

    /// <summary>A listener that throws must not take the engine down.</summary>
    private static void Raise(Delegate? listeners, Action<Delegate> call)
    {
        foreach (var l in listeners?.GetInvocationList() ?? [])
        {
            try
            {
                call(l);
            }
            catch (Exception e)
            {
                System.Diagnostics.Trace.TraceError("listener failed: " + e);
            }
        }
    }

    private static async Task Quietly(Func<Task> f)
    {
        try
        {
            await f().ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or OperationCanceledException)
        {
            // connection lost again
        }
    }

    private void Arrive(Transfer t)
    {
        if (t.ExpiresAt <= _o.Now() || t.SenderId == Api.Account.DeviceId) return;
        var added = false;
        Update(s =>
        {
            added = s.Incoming.All(x => x.Id != t.Id) && s.Downloads.All(d => d.Transfer.Id != t.Id);
            return added ? s with { Incoming = [.. s.Incoming.Append(t).OrderBy(x => x.CreatedAt)] } : s;
        });
        if (added) Notify(new Notice.OfferArrived(t));
    }

    /// <summary>Takes an offer off the list without a notice.</summary>
    private void Take(string id) => Update(s => s with { Incoming = [.. s.Incoming.Where(t => t.Id != id)] });

    private void Leave(string id)
    {
        Take(id);
        Notify(new Notice.OfferGone(id));
    }

    private void LearnName(string id)
    {
        if (!State.Names.ContainsKey(id)) _ = Quietly(LoadDevicesAsync);
    }

    private async Task LoadDevicesAsync()
    {
        var all = await Api.DevicesAsync(_stop.Token).ConfigureAwait(false);
        Update(s => s with
        {
            Devices = [.. all.Where(d => d.Id != Api.Account.DeviceId && d.Type != DeviceType.Agent).OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase)],
            Names = all.ToDictionary(d => d.Id, d => d.Name),
        });
    }

    /// <summary>Loads devices and open offers; resumes downloads the user confirmed earlier. Throws IOException.</summary>
    public async Task RefreshAsync()
    {
        await _refreshing.WaitAsync(_stop.Token).ConfigureAwait(false);
        try
        {
            await LoadDevicesAsync().ConfigureAwait(false);
            var before = State.Incoming.Select(t => t.Id).ToList();
            List<Transfer> waiting = [], confirmed = [];
            foreach (var t in await Api.IncomingAsync(_stop.Token).ConfigureAwait(false))
            {
                switch (await OwnStatusAsync(t.Id).ConfigureAwait(false))
                {
                    case Status.Waiting:
                        waiting.Add(t);
                        break;
                    case Status.Accepted or Status.Downloading when t.Kind == Kind.Files:
                        confirmed.Add(t);
                        break;
                }
            }
            // only those known before loading: an offer arriving meanwhile is not in the list yet
            foreach (var id in before.Where(id => waiting.All(t => t.Id != id) && State.Incoming.Any(t => t.Id == id))) Leave(id);
            waiting.ForEach(Arrive);
            foreach (var t in confirmed)
            {
                bool aborted, completed;
                lock (_lock) (aborted, completed) = (_aborted.Contains(t.Id), _completed.Contains(t.Id));
                if (aborted) await Quietly(() => Api.FailAsync(t.Id, _stop.Token)).ConfigureAwait(false);
                else if (completed) await Quietly(() => Api.DeliveredAsync(t.Id, _stop.Token)).ConfigureAwait(false);
                else StartDownload(t);
            }
            RetainParts(confirmed);
        }
        catch (ApiException e) when (e.Status == 401)
        {
            Update(s => s with { Unauthorised = true });
            throw;
        }
        finally
        {
            _refreshing.Release();
        }
    }

    private void RetainParts(List<Transfer> confirmed)
    {
        lock (_lock)
        {
            try
            {
                _store.Retain(confirmed.Select(t => t.Id).Concat(_jobs.Keys).ToHashSet());
            }
            catch (Exception e)
            {
                System.Diagnostics.Trace.TraceError("cannot clean up parts: " + e.Message); // only housekeeping
            }
        }
    }

    private async Task<Status?> OwnStatusAsync(string id)
    {
        try
        {
            var t = await Api.TransferAsync(id, _stop.Token).ConfigureAwait(false);
            return t.Deliveries.FirstOrDefault(d => d.DeviceId == Api.Account.DeviceId)?.Status;
        }
        catch (ApiException e) when (e.Status is 403 or 404)
        {
            return null;
        }
    }

    /// <summary>An offer that waits for our answer, or null if there is none (any more). Throws IOException.</summary>
    private async Task<Transfer?> OfferDetailsAsync(string id)
    {
        Transfer t;
        try
        {
            t = await Api.TransferAsync(id, _stop.Token).ConfigureAwait(false);
        }
        catch (ApiException e) when (e.Status is 403 or 404)
        {
            return null;
        }
        var own = t.Deliveries.FirstOrDefault(d => d.DeviceId == Api.Account.DeviceId);
        return own?.Status == Status.Waiting && !t.Closed && t.ExpiresAt > _o.Now() ? t : null;
    }

    public Task<Transfer> OfferFilesAsync(IReadOnlyList<IFileSource> sources, IReadOnlyList<string> targets, bool broadcast, string message = "", int minutes = 10) =>
        _sender.OfferFilesAsync(sources, targets, broadcast, message, minutes);

    public Task<Transfer> OfferTextAsync(Kind kind, string text, IReadOnlyList<string> targets, bool broadcast) => _sender.OfferTextAsync(kind, text, targets, broadcast);

    public Task CancelOutgoingAsync(string id) => _sender.CancelAsync(id);

    private async Task OfferEndedAsync(Transfer last)
    {
        var t = last;
        try
        {
            t = await Api.TransferAsync(last.Id, _stop.Token).ConfigureAwait(false); // the closing event may overtake the last status
        }
        catch (Exception e) when (e is IOException or OperationCanceledException)
        {
            // what we know has to do
        }
        if (t.Deliveries.Any(d => !State.Names.ContainsKey(d.DeviceId))) await Quietly(LoadDevicesAsync).ConfigureAwait(false);
        Update(s => s with { Sent = [.. s.Sent.Where(x => x.Id != t.Id).Append(t).OrderBy(x => x.CreatedAt).TakeLast(Keep)] });
        Notify(new Notice.OfferEnded(t));
    }

    /// <summary>Accepts an offer. Files download in the background; throws IOException if the server cannot be reached.</summary>
    public async Task AcceptAsync(string id)
    {
        lock (_lock)
            if (_jobs.ContainsKey(id) || _completed.Contains(id) || !_answering.Add(id))
                return;
        try
        {
            var t = State.Incoming.FirstOrDefault(x => x.Id == id) ?? await OfferDetailsAsync(id).ConfigureAwait(false);
            if (t is null)
            {
                // answered before (e.g. by a process that has died): carry on instead of complaining
                if (await OwnStatusAsync(id).ConfigureAwait(false) is Status.Accepted or Status.Downloading) await RefreshAsync().ConfigureAwait(false);
                else Unavailable(id);
                return;
            }
            if (t.Kind == Kind.Files)
            {
                StartDownload(t);
                return;
            }
            await Api.AcceptAsync(id, _stop.Token).ConfigureAwait(false);
            Take(id);
            Update(s => s with { Received = [.. s.Received.Append(t).TakeLast(Keep)] });
            Notify(new Notice.TextReceived(t));
        }
        catch (ApiException e) when (e.Gone)
        {
            Unavailable(id);
        }
        finally
        {
            lock (_lock) _answering.Remove(id);
        }
    }

    public async Task DeclineAsync(string id)
    {
        try
        {
            await Api.DeclineAsync(id, _stop.Token).ConfigureAwait(false);
            Take(id);
        }
        catch (ApiException e) when (e.Gone)
        {
            Unavailable(id);
        }
    }

    private void Unavailable(string id)
    {
        Take(id);
        Notify(new Notice.Unavailable(id));
    }

    private void StartDownload(Transfer t)
    {
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_lock)
        {
            if (_disposed || _jobs.ContainsKey(t.Id) || _completed.Contains(t.Id) || _aborted.Contains(t.Id)) return;
            var cts = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            _jobs[t.Id] = (cts, RunDownloadAsync(t, start.Task, cts.Token));
        }
        Update(s => s with { Incoming = [.. s.Incoming.Where(x => x.Id != t.Id)], Downloads = [.. s.Downloads.Where(d => d.Transfer.Id != t.Id), new Download(t)] });
        start.SetResult();
    }

    private async Task RunDownloadAsync(Transfer t, Task start, CancellationToken ct)
    {
        await start.ConfigureAwait(false);
        long shown = 0;
        DataPath? shownPath = null;
        try
        {
            var refs = await _receiver.DownloadAsync(t, (bytes, path) =>
            {
                // a fast LAN download reports hundreds of times per second
                var now = Environment.TickCount64;
                if (path == shownPath && now - shown <= 100 && bytes != t.TotalSize) return;
                (shown, shownPath) = (now, path);
                UpdateDownload(t.Id, d => d with { Bytes = bytes, Path = path });
            }, ct).ConfigureAwait(false);
            lock (_lock) _completed.Add(t.Id);
            Done(t.Id, d => d with { Phase = Phase.Done, Bytes = t.TotalSize, Refs = refs }, d => new Notice.DownloadDone(d));
        }
        catch (OperationCanceledException)
        {
            lock (_lock) _jobs.Remove(t.Id);
        }
        catch (OfferEndedException)
        {
            if (State.Downloads.FirstOrDefault(d => d.Transfer.Id == t.Id) is { Bytes: > 0 })
            {
                Done(t.Id, d => d with { Phase = Phase.Failed, Error = "Angebot beendet" }, d => new Notice.DownloadFailed(d));
                return;
            }
            lock (_lock) _jobs.Remove(t.Id);
            Update(s => s with { Downloads = [.. s.Downloads.Where(d => d.Transfer.Id != t.Id)] });
            Unavailable(t.Id);
        }
        catch (DownloadFailedException e)
        {
            Done(t.Id, d => d with { Phase = Phase.Failed, Error = e.Message }, d => new Notice.DownloadFailed(d));
        }
        catch (Exception e)
        {
            // whatever it was, one download must not take the program down
            System.Diagnostics.Trace.TraceError("download failed: " + e);
            try
            {
                await _receiver.AbortAsync(t.Id).ConfigureAwait(false);
            }
            catch (Exception)
            {
                await Quietly(() => Api.FailAsync(t.Id)).ConfigureAwait(false);
            }
            Done(t.Id, d => d with { Phase = Phase.Failed, Error = "Speichern fehlgeschlagen" }, d => new Notice.DownloadFailed(d));
        }
    }

    private void UpdateDownload(string id, Func<Download, Download> f) => Update(s => s with { Downloads = [.. s.Downloads.Select(d => d.Transfer.Id == id ? f(d) : d)] });

    private void Done(string id, Func<Download, Download> f, Func<Download, Notice> notice)
    {
        lock (_lock) _jobs.Remove(id);
        UpdateDownload(id, f);
        if (State.Downloads.FirstOrDefault(d => d.Transfer.Id == id) is { } d) Notify(notice(d));
    }

    /// <summary>Aborts a running download for good.</summary>
    public void CancelDownload(string id)
    {
        (CancellationTokenSource Cts, Task Task) job;
        lock (_lock)
        {
            if (!_jobs.TryGetValue(id, out job)) return;
            _aborted.Add(id);
        }
        job.Cts.Cancel();
        _ = Task.Run(async () =>
        {
            await job.Task.ConfigureAwait(false);
            Update(s => s with { Downloads = [.. s.Downloads.Where(d => d.Transfer.Id != id)] });
            await _receiver.AbortAsync(id).ConfigureAwait(false);
        });
    }

    /// <summary>Removes a finished entry from the lists.</summary>
    public void Dismiss(string id) => Update(s => s with
    {
        Downloads = [.. s.Downloads.Where(d => d.Transfer.Id != id || d.Phase == Phase.Running)],
        Sent = [.. s.Sent.Where(t => t.Id != id)],
        Received = [.. s.Received.Where(t => t.Id != id)],
    });

    private void Expire()
    {
        var now = _o.Now();
        foreach (var t in State.Incoming.Where(t => t.ExpiresAt <= now)) Leave(t.Id);
        if (State.Connected) return;
        // normally the server ends offers; without it they must not keep the computer awake for ever
        foreach (var t in _sender.Offers.Values.Where(t => t.ExpiresAt < now.AddMinutes(-10))) _sender.Drop(t.Id);
    }

    /// <summary>Simulates losing the local path (tests).</summary>
    internal void LoseLanForTest() => _sender.StopEndpointForTest();

    /// <summary>Ends everything locally. Running downloads keep their partial state.</summary>
    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            Volatile.Write(ref _state, new EngineState());
        }
        _stop.Cancel();
        _sender.Dispose();
        Api.Dispose();
        Raise(StateChanged, l => ((Action)l)());
    }
}
