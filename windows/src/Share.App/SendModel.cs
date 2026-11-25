using Share.Core;

namespace Share.App;

/// <summary>The little window that asks where something should go. Members belong to the UI thread.</summary>
public sealed class SendModel : Observable
{
    private readonly AppHost _host;
    private readonly Action<Action> _post;
    private readonly IReadOnlyList<DiskFile> _files;
    private readonly IReadOnlyList<string> _problems;
    private IReadOnlyList<string> _wanted;
    private readonly bool _wantedAll;
    private string _text;
    private bool _needsPairing, _connected, _noDevices;
    private bool _done;

    private SendModel(AppHost host, Action<Action> post, Payload p, IReadOnlyList<DiskFile> files, IReadOnlyList<string> problems, IReadOnlyList<string> targets, bool all)
    {
        (_host, _post, _files, _problems, _wanted, _wantedAll) = (host, post, files, problems, targets, all);
        _text = (p as Payload.Text)?.Content ?? "";
        IsText = p is Payload.Text;
        Targets.PropertyChanged += (_, _) => RaiseSend();
        host.StateChanged += OnChange;
        host.EngineChanged += OnChange;
        Targets.All = all;
        Refresh();
    }

    /// <summary>
    /// Opens all files at once, so whoever handed them over may clean up as soon as this returns.
    /// </summary>
    public static async Task<SendModel> OpenAsync(AppHost host, Action<Action> post, Payload p, IReadOnlyList<string> targets, bool all)
    {
        var files = new List<DiskFile>();
        var problems = new List<string>();
        if (p is Payload.Files f)
        {
            await Task.Run(() =>
            {
                foreach (var path in f.Paths.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    try
                    {
                        files.Add(DiskFile.Open(path, f.Temporary));
                    }
                    catch (SourceException e)
                    {
                        problems.Add(e.Message);
                    }
                }
            }).ConfigureAwait(true);
        }
        return new SendModel(host, post, p, files, problems, targets, all);
    }

    public TargetList Targets { get; } = new();
    public bool IsText { get; }
    public bool HasFiles => _files.Count > 0;

    public string Title => IsText ? "Text/Link senden"
        : _files.Count == 0 ? "Nichts zu senden"
        : _files.Count == 1 ? $"{_files[0].Item.Name} ({Format.Size(_files[0].Item.Size)}) senden"
        : $"{_files.Count} Dateien ({Format.Size(_files.Sum(f => f.Item.Size))}) senden";

    public string Details => string.Join("\n", _files.Select(f => f.Item.Name));

    public string Text
    {
        get => _text;
        set
        {
            if (!Set(ref _text, value)) return;
            RaiseSend();
            Raise(nameof(Problem));
        }
    }

    private bool TooLong => System.Text.Encoding.UTF8.GetByteCount(_text) > Limits.MaxTextBytes;
    public string Problem => IsText && TooLong ? "Zu lang (höchstens 64 KB)" : string.Join("\n", _problems);
    public bool NeedsPairing { get => _needsPairing; private set => Set(ref _needsPairing, value); }
    public bool Connecting { get => _connected; private set => Set(ref _connected, value); }
    public bool NoDevices { get => _noDevices; private set => Set(ref _noDevices, value); }

    /// <summary>The target may still be missing from the list right after a cold start.</summary>
    public bool CanSend => !_done && !NeedsPairing && Targets.CanSend && (IsText ? !string.IsNullOrWhiteSpace(_text) && !TooLong : HasFiles);

    public event Action? Closed;

    private void OnChange() => _post(Refresh);

    private void Refresh()
    {
        if (_done) return;
        var e = _host.Engine;
        var s = e?.State ?? new EngineState();
        NeedsPairing = e is null || s.Unauthorised;
        Connecting = e is not null && !s.Connected && s.Devices.Count == 0 && !s.Unauthorised;
        NoDevices = s.Connected && s.Devices.Count == 0;
        Targets.Update(s.Devices);
        if (_wanted.Count > 0 && Targets.Rows.Any(r => _wanted.Contains(r.Key)))
        {
            if (!_wantedAll) Targets.Select(_wanted, false);
            _wanted = [];
        }
        RaiseSend();
    }

    private void RaiseSend() => Raise(nameof(CanSend));

    public void Send()
    {
        if (!CanSend) return;
        var (ids, all, text) = (Targets.SelectedIds, Targets.All, _text);
        End();
        // from here on the engine releases the files, whatever happens
        _host.Run(e => IsText ? Sending.TextAsync(e, text, ids, all) : e.OfferFilesAsync(_files, ids, all));
    }

    public void Cancel()
    {
        if (_done) return;
        End();
        foreach (var f in _files) f.Release();
    }

    private void End()
    {
        _done = true;
        _host.StateChanged -= OnChange;
        _host.EngineChanged -= OnChange;
        RaiseSend();
        Closed?.Invoke();
    }
}
