using System.Diagnostics;
using System.IO;
using Share.Core;

namespace Share.App;

/// <summary>Windows notifications.</summary>
public interface INotifier
{
    /// <summary>False if the user has switched them off.</summary>
    bool Enabled { get; }
    void Offer(Transfer t, string sender);
    void RemoveOffer(string id);
    void Result(Download d);

    /// <summary>An accepted text or link, already in the clipboard.</summary>
    void Text(Transfer t, string sender);

    /// <summary>How one of our own offers ended.</summary>
    void Ended(Transfer t, Func<string, string> name);
    void Message(string title, string text);
}

public interface IDesktop
{
    void CopyText(string text);
    void KeepAwake(bool on);
}

public sealed record HostOptions(string DataDir, string DownloadsDir)
{
    public bool AllowLocalHttp { get; init; }
    public Func<EngineOptions> Engine { get; init; } = () => new EngineOptions();
}

/// <summary>The program without its windows: pairing, engine, what is announced where. Events come on any thread.</summary>
public sealed class AppHost : IDisposable
{
    private readonly HostOptions _o;
    private readonly INotifier _notifier;
    private readonly IDesktop _desktop;
    private readonly AccountStore _store;
    private readonly object _lock = new();

    /// <summary>offers that have had a notification; a dismissed one must not come back</summary>
    private readonly HashSet<string> _announced = [];
    private Account? _account;
    private Engine? _engine;
    private HashSet<string> _incoming = [];
    private int _visible;
    private bool _awake;
    private bool _disposed;

    public AppHost(HostOptions options, INotifier notifier, IDesktop desktop)
    {
        (_o, _notifier, _desktop) = (options, notifier, desktop);
        _store = new AccountStore(Path.Combine(options.DataDir, "account.bin"));
        Use(_store.Load());
    }

    public bool AllowLocalHttp => _o.AllowLocalHttp;
    public bool NotificationsEnabled => _notifier.Enabled;

    /// <summary>Null while the device is not paired.</summary>
    public Engine? Engine
    {
        get
        {
            lock (_lock) return _engine;
        }
    }

    public string DeviceName
    {
        get
        {
            lock (_lock) return _account?.Name ?? "";
        }
    }

    public EngineState State => Engine?.State ?? new EngineState();

    public event Action? EngineChanged;
    public event Action? StateChanged;

    /// <summary>A message for the visible window.</summary>
    public event Action<string, string>? Message;

    /// <summary>Pairs (or with null: unpairs) this device.</summary>
    public void SetAccount(Account? a)
    {
        if (a is not null) _store.Save(a);
        else _store.Clear();
        Use(a);
        EngineChanged?.Invoke();
        StateChanged?.Invoke();
    }

    private void Use(Account? a)
    {
        Engine? old;
        Engine? now = null;
        HashSet<string> gone;
        lock (_lock)
        {
            old = _engine;
            gone = [.. _announced];
            _announced.Clear();
            _incoming = [];
            _account = a;
            if (a is not null && !_disposed)
            {
                now = new Engine(new Api(a), new DiskDownloads(_o.DownloadsDir, Path.Combine(_o.DataDir, "downloads.json")), _o.Engine());
                now.StateChanged += () => OnState(now);
                now.Notified += n => OnNotice(now, n);
            }
            _engine = now;
        }
        old?.Dispose();
        foreach (var id in gone) _notifier.RemoveOffer(id);
        now?.Start();
        UpdateAwake();
    }

    private void OnState(Engine e)
    {
        List<string> left;
        lock (_lock)
        {
            if (e != _engine) return;
            var now = e.State.Incoming.Select(t => t.Id).ToHashSet();
            left = [.. _incoming.Except(now)];
            _incoming = now;
        }
        foreach (var id in left) _notifier.RemoveOffer(id);
        UpdateAwake();
        StateChanged?.Invoke();
    }

    private void UpdateAwake()
    {
        lock (_lock)
        {
            var busy = !_disposed && _engine?.Busy == true;
            if (busy == _awake) return;
            _awake = busy;
        }
        _desktop.KeepAwake(_awake);
    }

    private void OnNotice(Engine e, Notice n)
    {
        if (e != Engine) return;
        switch (n)
        {
            case Notice.OfferArrived a:
                if (!UiVisible) Announce(e, a.Transfer);
                break;
            case Notice.OfferGone g:
                _notifier.RemoveOffer(g.Id);
                break;
            case Notice.DownloadDone d:
                if (!UiVisible) _notifier.Result(d.Download);
                break;
            case Notice.DownloadFailed d:
                if (!UiVisible) _notifier.Result(d.Download);
                break;
            case Notice.OfferEnded o:
                if (!UiVisible) _notifier.Ended(o.Transfer, e.DeviceName);
                break;
            case Notice.TextReceived t:
                _desktop.CopyText(t.Transfer.Text);
                if (UiVisible) Tell("In die Zwischenablage kopiert");
                else _notifier.Text(t.Transfer, e.DeviceName(t.Transfer.SenderId));
                break;
            case Notice.Unavailable u:
                _notifier.RemoveOffer(u.Id);
                Tell("Angebot nicht mehr verfügbar");
                break;
        }
    }

    private void Announce(Engine e, Transfer t)
    {
        lock (_lock)
            if (!_announced.Add(t.Id))
                return;
        _notifier.Offer(t, e.DeviceName(t.SenderId));
    }

    public bool UiVisible => Volatile.Read(ref _visible) > 0;

    /// <summary>A window that shows offers itself is visible until disposed.</summary>
    public IDisposable UiShown()
    {
        Interlocked.Increment(ref _visible);
        return new Shown(this);
    }

    private sealed class Shown(AppHost host) : IDisposable
    {
        private int _open = 1;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _open, 0) == 1 && Interlocked.Decrement(ref host._visible) == 0) host.AnnounceUnseen();
        }
    }

    /// <summary>Offers that arrived while a window was open, but maybe nobody looked.</summary>
    private void AnnounceUnseen()
    {
        if (Engine is not { } e) return;
        foreach (var t in e.State.Incoming) Announce(e, t);
    }

    /// <summary>Tells the user something, wherever they are.</summary>
    public void Tell(string title, string text = "")
    {
        if (UiVisible) Message?.Invoke(title, text);
        else _notifier.Message(title, text);
    }

    public void CopyText(string text) => _desktop.CopyText(text);

    /// <summary>Runs something in the background and tells the user if it fails.</summary>
    public void Run(Func<Engine, Task> f)
    {
        if (Engine is not { } e) return;
        _ = Task.Run(async () =>
        {
            try
            {
                await f(e).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                TellError(ex);
            }
        });
    }

    public void TellError(Exception ex)
    {
        switch (ex)
        {
            case OperationCanceledException:
                break;
            case SourceException:
                Tell(ex.Message);
                break;
            case ApiException:
                Tell("Server lehnt ab", ex.Message);
                break;
            case IOException:
                Tell("Keine Verbindung zum Server");
                break;
            default:
                Trace.TraceError(ex.ToString());
                Tell("Unerwarteter Fehler", ex.Message);
                break;
        }
    }

    /// <summary>Renames this device on the server; throws IOException.</summary>
    public async Task RenameAsync(string name)
    {
        if (Engine is not { } e) return;
        await e.Api.UpdateMeAsync(name: name).ConfigureAwait(false);
        lock (_lock)
        {
            if (_account is null || e != _engine) return;
            _account = _account with { Name = name };
            _store.Save(_account);
        }
        StateChanged?.Invoke();
    }

    /// <summary>Ends the pairing, even if the server cannot be told.</summary>
    public async Task UnpairAsync()
    {
        if (Engine is not { } e) return;
        var told = true;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await e.Api.DeleteMeAsync(cts.Token).ConfigureAwait(false);
        }
        catch (ApiException ex) when (ex.Status == 401)
        {
            // removed on the server already
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException)
        {
            told = false;
        }
        SetAccount(null);
        if (!told) Tell("Lokal abgemeldet", "Der Server war nicht erreichbar. Entferne das Gerät dort mit „share-server devices rm“.");
    }

    public void Dispose()
    {
        Engine? e;
        lock (_lock)
        {
            _disposed = true;
            e = _engine;
            _engine = null;
        }
        e?.Dispose();
        UpdateAwake();
    }
}
