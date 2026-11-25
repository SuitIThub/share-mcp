using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Share.Core;

namespace Share.App;

/// <summary>Something to be sent, before targets are chosen.</summary>
public abstract record Payload
{
    public sealed record Files(IReadOnlyList<string> Paths) : Payload;
    public sealed record Text(string Content) : Payload;
}

/// <summary>Opens results outside the program.</summary>
public interface IOpener
{
    void Open(Download d);
    void OpenLink(string url);
}

/// <summary>The main window's content. All members belong to the UI thread; changes come in through post.</summary>
public sealed class MainModel : Observable
{
    private readonly AppHost _host;
    private readonly Action<Action> _post;
    private readonly IOpener _opener;
    private int _pending;
    private string _status = "", _message = "";
    private bool _noIncoming = true, _noDevices = true, _noRunning = true, _needsPairing;

    public MainModel(AppHost host, Action<Action> post, IOpener opener)
    {
        (_host, _post, _opener) = (host, post, opener);
        CloseMessage = new RelayCommand(() => Message = "");
        host.StateChanged += Invalidate;
        host.EngineChanged += Invalidate;
        host.Message += (title, text) => post(() => Message = text.Length == 0 ? title : $"{title}: {text}");
    }

    public ObservableCollection<IncomingRow> Incoming { get; } = [];
    public ObservableCollection<RunningRow> Running { get; } = [];
    public TargetList Targets { get; } = new();
    public ICommand CloseMessage { get; }
    public string Status { get => _status; private set => Set(ref _status, value); }
    public bool NoIncoming { get => _noIncoming; private set => Set(ref _noIncoming, value); }
    public bool NoDevices { get => _noDevices; private set => Set(ref _noDevices, value); }
    public bool NoRunning { get => _noRunning; private set => Set(ref _noRunning, value); }
    public bool NeedsPairing { get => _needsPairing; private set => Set(ref _needsPairing, value); }

    public string Message
    {
        get => _message;
        private set
        {
            if (Set(ref _message, value)) Raise(nameof(HasMessage));
        }
    }

    public bool HasMessage => Message.Length > 0;

    /// <summary>Dropped or pasted things: the user still has to choose targets and click.</summary>
    public event Action<Payload>? PayloadOffered;

    /// <summary>Many changes in a row cause one update.</summary>
    private void Invalidate()
    {
        if (Interlocked.Exchange(ref _pending, 1) == 1) return;
        _post(() =>
        {
            Interlocked.Exchange(ref _pending, 0);
            Refresh();
        });
    }

    /// <summary>Also called every second while visible, for the remaining times.</summary>
    public void Refresh()
    {
        var e = _host.Engine;
        var s = e?.State ?? new EngineState();
        NeedsPairing = e is null || s.Unauthorised;
        Status = e is null ? "nicht gekoppelt" : s.Unauthorised ? "nicht mehr gekoppelt" : s.Connected ? "verbunden" : "verbinde …";
        Rows.Sync(Incoming, s.Incoming, t => t.Id, r => r.Key, t => new IncomingRow(t.Id, Do(x => x.AcceptAsync(t.Id)), Do(x => x.DeclineAsync(t.Id))), (r, t) => r.Update(t, s));
        NoIncoming = Incoming.Count == 0;
        Rows.Sync(Running, Items(s), i => i.Key, r => r.Key, NewRow, (r, i) => Update(r, i, s));
        NoRunning = Running.Count == 0;
        Targets.Update(s.Devices);
        NoDevices = Targets.Rows.Count == 0;
    }

    private sealed record Item(string Key, Transfer Transfer, Download? Download = null);

    private static List<Item> Items(EngineState s) =>
    [
        .. s.Outgoing.Select(t => new Item("out:" + t.Id, t)), .. s.Downloads.Select(d => new Item("dl:" + d.Transfer.Id, d.Transfer, d)),
        .. s.Received.Select(t => new Item("rcv:" + t.Id, t)), .. s.Sent.Select(t => new Item("sent:" + t.Id, t)),
    ];

    private RelayCommand Do(Func<Engine, Task> f) => new(() => _host.Run(f));

    private RunningRow NewRow(Item i)
    {
        var id = i.Transfer.Id;
        return new RunningRow(
            i.Key,
            i.Download is null ? Do(e => e.CancelOutgoingAsync(id)) : new RelayCommand(() => _host.Engine?.CancelDownload(id)),
            new RelayCommand(() => _host.Engine?.Dismiss(id)),
            new RelayCommand(() =>
            {
                if (i.Download is null) _opener.OpenLink(i.Transfer.Text);
                else if (_host.State.Downloads.FirstOrDefault(d => d.Transfer.Id == id) is { Phase: Phase.Done } d) _opener.Open(d);
            }),
            new RelayCommand(() => _host.CopyText(i.Transfer.Text)));
    }

    private static void Update(RunningRow r, Item i, EngineState s)
    {
        var t = i.Transfer;
        switch (i.Key[..i.Key.IndexOf(':')])
        {
            case "out":
                r.Title = "↑ " + Format.ShortName(t);
                r.Text = string.Join("\n", t.Deliveries.Select(d => Receiver(d, t, s)).Append(t.ExpiresAt > DateTimeOffset.UtcNow ? Format.Remaining(t.ExpiresAt, DateTimeOffset.UtcNow) : "wird übertragen"));
                r.CanCancel = true;
                break;
            case "sent":
                r.Title = "↑ " + Format.ShortName(t);
                r.Text = string.Join("\n", t.Deliveries.Select(d => Receiver(d, t, s)));
                r.CanDismiss = true;
                break;
            case "rcv":
                r.Title = "↓ " + Format.OfferTitle(t, s.NameOf(t.SenderId));
                r.Text = t.Text;
                r.OpenLabel = Format.IsWebLink(t.Text) ? "Öffnen" : null;
                r.CanCopy = r.CanDismiss = true;
                break;
            default:
                Update(r, i.Download!, s);
                break;
        }
    }

    private static string Receiver(Delivery d, Transfer t, EngineState s) =>
        $"{s.NameOf(d.DeviceId)}: {Format.Status(d)}{(d.Status == Core.Status.Downloading && t.Kind == Kind.Files ? $" {Format.Percent(d.Bytes, t.TotalSize)} %" : "")}";

    private static void Update(RunningRow r, Download d, EngineState s)
    {
        var percent = Format.Percent(d.Bytes, d.Transfer.TotalSize);
        r.Title = $"↓ {Format.ShortName(d.Transfer)} ({s.NameOf(d.Transfer.SenderId)})";
        r.Text = d.Phase switch
        {
            Phase.Running => d.Path is null ? $"{percent} %" : $"{percent} % · {Format.Path(d.Path)}",
            Phase.Done => "Im Download-Ordner",
            _ => d.Error ?? "fehlgeschlagen",
        };
        r.Progress = d.Phase == Phase.Running ? percent : null;
        r.CanCancel = d.Phase == Phase.Running;
        r.CanDismiss = d.Phase != Phase.Running;
        r.Failed = d.Phase == Phase.Failed;
        r.OpenLabel = d.Phase == Phase.Done ? Results.IsDangerous(d) || d.Refs.Count > 1 ? "Im Ordner zeigen" : "Öffnen" : null;
    }

    public void SendText(string text)
    {
        var (ids, all) = (Targets.SelectedIds, Targets.All);
        _host.Run(e => Sending.TextAsync(e, text, ids, all));
    }

    public void SendFiles(IReadOnlyList<string> paths)
    {
        var (ids, all) = (Targets.SelectedIds, Targets.All);
        _host.Run(e => Sending.FilesAsync(_host, e, paths, ids, all));
    }

    internal void Offer(Payload p) => PayloadOffered?.Invoke(p);

    /// <summary>Takes files, text or a picture; false if there is nothing usable.</summary>
    public bool Paste(IDataObject data)
    {
        if (data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files)
        {
            PayloadOffered?.Invoke(new Payload.Files(files));
            return true;
        }
        if (data.GetData(DataFormats.UnicodeText) is string text && !string.IsNullOrWhiteSpace(text))
        {
            PayloadOffered?.Invoke(new Payload.Text(text));
            return true;
        }
        if (data.GetData(DataFormats.Bitmap) is BitmapSource image)
        {
            PayloadOffered?.Invoke(new Payload.Files([SaveImage(image)]));
            return true;
        }
        return false;
    }

    private string SaveImage(BitmapSource image)
    {
        Directory.CreateDirectory(_host.TempDir);
        var path = Path.Combine(_host.TempDir, $"Bild {DateTime.Now:yyyy-MM-dd HH-mm-ss-fff}.png");
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(image));
        using var f = File.Create(path);
        png.Save(f);
        return path;
    }
}

public static class Sending
{
    public static Task TextAsync(Engine e, string text, IReadOnlyList<string> targets, bool all)
    {
        var kind = Format.KindOf(text);
        return e.OfferTextAsync(kind, kind == Kind.Link ? text.Trim() : text, targets, all);
    }

    /// <summary>Opens what can be opened; folders and unreadable files are told and left out.</summary>
    public static IReadOnlyList<DiskFile> Open(AppHost host, IReadOnlyList<string> paths)
    {
        var files = new List<DiskFile>();
        foreach (var p in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                files.Add(DiskFile.Open(p, host.IsTemporary(p)));
            }
            catch (SourceException ex)
            {
                host.Tell(ex.Message);
            }
        }
        return files;
    }

    public static async Task FilesAsync(AppHost host, Engine e, IReadOnlyList<string> paths, IReadOnlyList<string> targets, bool all)
    {
        var files = Open(host, paths);
        if (files.Count > 0) await e.OfferFilesAsync(files, targets, all).ConfigureAwait(false);
    }
}
