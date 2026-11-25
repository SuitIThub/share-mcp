using System.Diagnostics;
using System.Windows;

namespace Share.App;

/// <summary>Ties host, tray icon and windows together.</summary>
public sealed class ShareApplication : Application
{
    private readonly StartArgs _args;
    private readonly Ipc _ipc;
    private readonly string _data;
    private AppHost _host = null!;
    private Tray _tray = null!;
    private MainWindow _main = null!;
    private PairWindow? _pair;

    public ShareApplication(StartArgs args, Ipc ipc, string data)
    {
        (_args, _ipc, _data) = (args, ipc, data);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        ThemeMode = ThemeMode.System;
        DispatcherUnhandledException += (_, e) =>
        {
            Trace.TraceError(e.Exception.ToString());
            _host?.TellError(e.Exception);
            e.Handled = true;
        };
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _host = new AppHost(new HostOptions(_data, Core.DiskDownloads.DefaultDirectory) { AllowLocalHttp = _args.AllowLocalHttp }, new SilentNotifier(), new Desktop(Dispatcher));
        _tray = new Tray(() => Handle(new Command.Show()), SendFiles, Quit);
        _host.StateChanged += () => Dispatcher.InvokeAsync(UpdateTray);
        UpdateTray();
        _main = new MainWindow(_host, new MainModel(_host, a => Dispatcher.InvokeAsync(a), new Results(_host), Path.Combine(_data, "temp")));
        _main.Model.PayloadOffered += async p => await OfferAsync(p);
        _main.SettingsWanted += Settings;
        _main.PairingWanted += () => Pair(null);
        _main.Hidden += OnHidden;
        _ipc.Listen(c => Dispatcher.InvokeAsync(() => HandleAsync(c)).Task.Unwrap());
        if (_args.Command is not null) Handle(_args.Command);
    }

    private void UpdateTray()
    {
        var s = _host.State;
        _tray.Update(s.Incoming.Count, s.Connected);
    }

    private async Task HandleAsync(Command c)
    {
        Trace.TraceInformation("command " + c.GetType().Name);
        switch (c)
        {
            case Command.Show:
                _main.BringUp();
                break;
            case Command.Send f:
                await OfferAsync(new Payload.Files(f.Paths, f.Temporary));
                break;
            case Command.SendText t:
                await OfferAsync(new Payload.Text(t.Text));
                break;
            case Command.Pair p:
                Pair(p.Link);
                break;
            case Command.Accept a:
                _host.Run(e => e.AcceptAsync(a.Id));
                break;
            case Command.Decline d:
                _host.Run(e => e.DeclineAsync(d.Id));
                break;
        }
    }

    private async void Handle(Command c) => await HandleAsync(c);

    /// <summary>Asks for targets; every payload gets its own window. Completes once all files are open.</summary>
    private async Task OfferAsync(Payload p)
    {
        var picked = _main.IsVisible ? _main.Model.Targets : new TargetList();
        var m = await SendModel.OpenAsync(_host, a => Dispatcher.InvokeAsync(a), p, picked.SelectedIds, picked.All);
        var w = new SendWindow(m);
        w.PairingWanted += () => Pair(null);
        w.Show();
        w.Topmost = true; // from the share menu there is no focus to inherit
        w.Topmost = false;
    }

    /// <summary>A link never replaces an existing pairing.</summary>
    private void Pair(string? link)
    {
        if (_host.Engine is { State.Unauthorised: false })
        {
            _main.BringUp();
            if (link is not null) _host.Tell("Dieses Gerät ist schon gekoppelt");
            return;
        }
        if (_pair is null)
        {
            _pair = new PairWindow(new PairModel(_host));
            _pair.Closed += (_, _) => _pair = null;
            _pair.Show();
        }
        if (link is not null) _pair.Fill(link);
        _pair.Activate();
    }

    private void Settings()
    {
        var w = new SettingsWindow(new SettingsModel(_host, new Autostart())) { Owner = _main };
        w.ShowDialog();
    }

    private async void SendFiles()
    {
        if (App.MainWindow.PickFiles(null) is { Count: > 0 } paths) await OfferAsync(new Payload.Files(paths));
    }

    /// <summary>Once, so nobody wonders where the window went.</summary>
    private void OnHidden()
    {
        var marker = Path.Combine(_data, "hinted");
        if (File.Exists(marker)) return;
        File.WriteAllText(marker, "");
        _host.Tell("Share läuft weiter", "Du findest es im Infobereich neben der Uhr. Beenden kannst du es dort mit einem Rechtsklick.");
    }

    private void Quit()
    {
        if (_host.Engine?.Busy == true && MessageBox.Show("Es laufen noch Übertragungen. Wirklich beenden?", "Share", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _main.ReallyClose = true;
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray.Dispose();
        _host.Dispose();
        base.OnExit(e);
    }
}

/// <summary>Until notifications are wired up.</summary>
internal sealed class SilentNotifier : INotifier
{
    public bool Enabled => false;
    public void Offer(Core.Transfer t, string sender) { }
    public void RemoveOffer(string id) { }
    public void Result(Core.Download d) { }
    public void Text(Core.Transfer t, string sender) { }
    public void Ended(Core.Transfer t, Func<string, string> name) { }
    public void Message(string title, string text) { }
}
