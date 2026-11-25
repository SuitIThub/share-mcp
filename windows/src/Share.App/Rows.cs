using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Share.Core;

namespace Share.App;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }

    protected void Raise(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class RelayCommand(Action run) : ICommand
{
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => run();

    public event EventHandler? CanExecuteChanged
    {
        add { }
        remove { }
    }
}

internal static class Rows
{
    /// <summary>Brings the rows in line with the items without replacing rows that stay, so focus and selection survive.</summary>
    public static void Sync<TItem, TRow>(ObservableCollection<TRow> rows, IReadOnlyList<TItem> items, Func<TItem, string> key, Func<TRow, string> rowKey, Func<TItem, TRow> create, Action<TRow, TItem> update)
    {
        var wanted = items.Select(key).ToHashSet();
        for (var i = rows.Count - 1; i >= 0; i--)
            if (!wanted.Contains(rowKey(rows[i])))
                rows.RemoveAt(i);
        for (var i = 0; i < items.Count; i++)
        {
            var k = key(items[i]);
            var at = -1;
            for (var j = i; j < rows.Count && at < 0; j++)
                if (rowKey(rows[j]) == k)
                    at = j;
            if (at < 0) rows.Insert(i, create(items[i]));
            else if (at != i) rows.Move(at, i);
            update(rows[i], items[i]);
        }
    }
}

/// <summary>An offer waiting for our answer.</summary>
public sealed class IncomingRow(string key, ICommand accept, ICommand decline) : Observable
{
    private string _title = "", _body = "", _remaining = "", _details = "";

    public string Key { get; } = key;
    public ICommand Accept { get; } = accept;
    public ICommand Decline { get; } = decline;
    public string Title { get => _title; private set => Set(ref _title, value); }
    public string Body { get => _body; private set => Set(ref _body, value); }
    public string Remaining { get => _remaining; private set => Set(ref _remaining, value); }
    public string Details { get => _details; private set => Set(ref _details, value); }

    internal void Update(Transfer t, EngineState s)
    {
        Title = Format.OfferTitle(t, s.NameOf(t.SenderId));
        Body = Format.OfferBody(t);
        Details = Format.Details(t);
        Remaining = Format.Remaining(t.ExpiresAt, DateTimeOffset.UtcNow);
    }
}

/// <summary>Anything running or finished: own offers, downloads, accepted texts.</summary>
public sealed class RunningRow(string key, ICommand cancel, ICommand dismiss, ICommand open, ICommand copy) : Observable
{
    private string _title = "", _text = "";
    private int? _progress;
    private bool _canCancel, _canDismiss, _canCopy, _failed;
    private string? _openLabel;

    public string Key { get; } = key;
    public ICommand Cancel { get; } = cancel;
    public ICommand Dismiss { get; } = dismiss;
    public ICommand Open { get; } = open;
    public ICommand Copy { get; } = copy;
    public string Title { get => _title; internal set => Set(ref _title, value); }
    public string Text { get => _text; internal set => Set(ref _text, value); }

    /// <summary>Null: no progress bar.</summary>
    public int? Progress { get => _progress; internal set => Set(ref _progress, value); }
    public bool CanCancel { get => _canCancel; internal set => Set(ref _canCancel, value); }
    public bool CanDismiss { get => _canDismiss; internal set => Set(ref _canDismiss, value); }
    public bool CanCopy { get => _canCopy; internal set => Set(ref _canCopy, value); }
    public bool Failed { get => _failed; internal set => Set(ref _failed, value); }

    /// <summary>Null: nothing to open.</summary>
    public string? OpenLabel { get => _openLabel; internal set => Set(ref _openLabel, value); }
}

/// <summary>A device to send to.</summary>
public sealed class TargetRow(string key, Action selectionChanged) : Observable
{
    private string _name = "";
    private bool _online, _selected;

    public string Key { get; } = key;
    public string Name { get => _name; internal set => Set(ref _name, value); }
    public bool Online { get => _online; internal set => Set(ref _online, value); }

    public bool Selected
    {
        get => _selected;
        set
        {
            if (Set(ref _selected, value)) selectionChanged();
        }
    }
}

/// <summary>The devices with check boxes, and "all".</summary>
public sealed class TargetList : Observable
{
    private bool _all;

    public ObservableCollection<TargetRow> Rows { get; } = [];
    public bool CanSend => All || Rows.Any(r => r.Selected);
    public IReadOnlyList<string> SelectedIds => [.. Rows.Where(r => r.Selected).Select(r => r.Key)];

    public bool All
    {
        get => _all;
        set
        {
            if (!Set(ref _all, value)) return;
            if (value)
                foreach (var r in Rows)
                    r.Selected = false;
            Raise(nameof(CanSend));
        }
    }

    internal void Select(IReadOnlyList<string> ids, bool all)
    {
        All = all;
        foreach (var r in Rows.Where(r => ids.Contains(r.Key))) r.Selected = true;
    }

    internal void Update(IReadOnlyList<Device> devices)
    {
        App.Rows.Sync(Rows, devices, d => d.Id, r => r.Key, d => new TargetRow(d.Id, SelectionChanged), (r, d) =>
        {
            r.Name = d.Name;
            r.Online = d.Online;
        });
        Raise(nameof(CanSend));
    }

    private void SelectionChanged()
    {
        if (Rows.Any(r => r.Selected)) All = false;
        Raise(nameof(CanSend));
    }
}
