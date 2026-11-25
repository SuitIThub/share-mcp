using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Windows;
using Microsoft.Toolkit.Uwp.Notifications;
using Microsoft.Win32;
using Windows.ApplicationModel.Activation;

namespace Share.App;

/// <summary>Ties host, tray icon and windows together.</summary>
public sealed class ShareApplication : Application
{
    private readonly StartArgs _args;
    private readonly ShareTargetActivatedEventArgs? _share;
    private readonly Ipc _ipc;
    private readonly string _data;
    private AppHost _host = null!;
    private Tray _tray = null!;
    private MainWindow _main = null!;
    private Results _results = null!;
    private PairWindow? _pair;

    public ShareApplication(StartArgs args, ShareTargetActivatedEventArgs? share, Ipc ipc, string data)
    {
        (_args, _share, _ipc, _data) = (args, share, ipc, data);
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
        _host = new AppHost(new HostOptions(_data, Core.DiskDownloads.DefaultDirectory) { AllowLocalHttp = _args.AllowLocalHttp }, Notifier(), new Desktop(Dispatcher));
        _results = new Results(_host);
        _tray = new Tray(() => Handle(new Command.Show()), SendFiles, Quit);
        _host.StateChanged += () => Dispatcher.InvokeAsync(UpdateTray);
        UpdateTray();
        _main = new MainWindow(_host, new MainModel(_host, a => Dispatcher.InvokeAsync(a), _results));
        _main.Model.PayloadOffered += async p => await OfferAsync(p);
        _main.SettingsWanted += Settings;
        _main.PairingWanted += () => Pair(null);
        _main.Hidden += OnHidden;
        _ipc.Listen(c => Dispatcher.InvokeAsync(() => HandleAsync(c)).Task.Unwrap());
        SystemEvents.PowerModeChanged += OnPower;
        NetworkChange.NetworkAvailabilityChanged += OnNetwork;
        if (_args.Command is not null) Handle(_args.Command);
        if (_share is not null) _ = Task.Run(() => ShareTarget.HandleAsync(new WindowsShareData(_share.ShareOperation), _host.TempDir, async c =>
        {
            await (await Dispatcher.InvokeAsync(() => HandleAsync(c)));
            return true;
        }));
    }

    /// <summary>With its own data folder (tests) the program stays out of the user's notifications.</summary>
    private INotifier Notifier()
    {
        if (_args.DataDir is not null) return new SilentNotifier();
        ToastNotificationManagerCompat.OnActivated += e =>
        {
            // comes on a worker thread, possibly before start-up has finished
            if (ToastArgs.Parse(e.Argument) is { } c) Dispatcher.InvokeAsync(() => Handle(c));
        };
        return new ToastNotifier(new WindowsToasts());
    }

    private void OnPower(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume) _host.Engine?.Reconnect();
    }

    private void OnNetwork(object? sender, NetworkAvailabilityEventArgs e)
    {
        if (e.IsAvailable) _host.Engine?.Reconnect();
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
                await OfferAsync(new Payload.Files(f.Paths));
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
            case Command.OpenDownload o:
                if (_host.State.Downloads.FirstOrDefault(x => x.Transfer.Id == o.Id) is { Phase: Core.Phase.Done } done) _results.Open(done);
                else _main.BringUp();
                break;
            case Command.OpenLink l:
                _results.OpenLink(l.Url);
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
        SystemEvents.PowerModeChanged -= OnPower; // static events would keep us alive
        NetworkChange.NetworkAvailabilityChanged -= OnNetwork;
        _tray.Dispose();
        _host.Dispose();
        base.OnExit(e);
    }
}

/// <summary>For tests of the whole program.</summary>
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
