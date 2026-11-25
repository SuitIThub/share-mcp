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
        _tray = new Tray(() => Handle(new Command.Show()), () => Handle(new Command.Show()), Quit);
        _host.StateChanged += () => Dispatcher.InvokeAsync(UpdateTray);
        UpdateTray();
        _ipc.Listen(c => Dispatcher.InvokeAsync(() => Handle(c)).Task);
        if (_args.Command is not null) Handle(_args.Command);
    }

    private void UpdateTray()
    {
        var s = _host.State;
        _tray.Update(s.Incoming.Count, s.Connected);
    }

    private void Handle(Command c) => Trace.TraceInformation("command " + c.GetType().Name);

    private void Quit()
    {
        if (_host.Engine?.Busy == true && MessageBox.Show("Es laufen noch Übertragungen. Wirklich beenden?", "Share", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
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
