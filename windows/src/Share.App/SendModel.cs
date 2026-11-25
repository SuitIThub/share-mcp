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
    private readonly CancellationTokenSource _cancel = new();
    private bool _done, _sending;
    private string _error = "";

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

    /// <summary>Opens all files at once, so whoever handed them over may clean up as soon as this returns.</summary>
    public static SendModel Open(AppHost host, Action<Action> post, Payload p, IReadOnlyList<string> targets, bool all)
    {
        var (files, problems) = OpenFiles(host, p);
        return new SendModel(host, post, p, files, problems, targets, all);
    }

    /// <summary>The same without blocking the caller, who must wait for it.</summary>
    public static async Task<SendModel> OpenAsync(AppHost host, Action<Action> post, Payload p, IReadOnlyList<string> targets, bool all)
    {
        var (files, problems) = await Task.Run(() => OpenFiles(host, p)).ConfigureAwait(true);
        return new SendModel(host, post, p, files, problems, targets, all);
    }

    private static (List<DiskFile> Files, List<string> Problems) OpenFiles(AppHost host, Payload p)
    {
        var files = new List<DiskFile>();
        var problems = new List<string>();
        foreach (var path in (p as Payload.Files)?.Paths.Distinct(StringComparer.OrdinalIgnoreCase) ?? [])
        {
            try
            {
                files.Add(DiskFile.Open(path, host.IsTemporary(path)));
            }
            catch (SourceException e)
            {
                problems.Add(e.Message);
            }
        }
        return (files, problems);
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
    public string Problem => IsText && TooLong ? "Zu lang (höchstens 64 KB)" : string.Join("\n", _error.Length > 0 ? _problems.Append(_error) : _problems);

    /// <summary>Waiting for the server to take the offer.</summary>
    public bool IsSending
    {
        get => _sending;
        private set
        {
            if (Set(ref _sending, value)) RaiseSend();
        }
    }

    private string Error
    {
        set
        {
            _error = value;
            Raise(nameof(Problem));
        }
    }
    public bool NeedsPairing { get => _needsPairing; private set => Set(ref _needsPairing, value); }
    public bool Connecting { get => _connected; private set => Set(ref _connected, value); }
    public bool NoDevices { get => _noDevices; private set => Set(ref _noDevices, value); }

    /// <summary>The target may still be missing from the list right after a cold start.</summary>
    public bool CanSend => !_done && !IsSending && !NeedsPairing && Targets.CanSend && (IsText ? !string.IsNullOrWhiteSpace(_text) && !TooLong : HasFiles);

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

    /// <summary>The window stays until the server has taken the offer, so nothing is lost if it does not.</summary>
    public async Task Send()
    {
        if (!CanSend || _host.Engine is not { } e) return;
        var (ids, all, text) = (Targets.SelectedIds, Targets.All, _text);
        Error = "";
        IsSending = true;
        try
        {
            var t = await Task.Run(() => IsText ? Sending.TextAsync(e, text, ids, all, _cancel.Token) : e.OfferFilesAsync(_files, ids, all, keepOnFailure: true, ct: _cancel.Token)).ConfigureAwait(false);
            _post(() => Sent(e, t));
        }
        catch (Exception ex)
        {
            _post(() => Failed(ex));
        }
    }

    /// <summary>From here on the engine releases the files.</summary>
    private void Sent(Engine e, Transfer t)
    {
        if (_done) _host.Run(_ => e.CancelOutgoingAsync(t.Id)); // cancelled a moment too late
        else End();
    }

    private void Failed(Exception ex)
    {
        IsSending = false;
        if (_done)
        {
            Release();
            return;
        }
        var (title, text) = AppHost.Describe(ex);
        Error = text.Length == 0 ? title : $"{title}: {text}";
    }

    public void Cancel()
    {
        if (_done) return;
        End();
        if (IsSending) _cancel.Cancel(); // how that ends decides who releases the files
        else Release();
    }

    private void Release()
    {
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
