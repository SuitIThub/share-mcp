using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;

namespace Share.App;

public partial class MainWindow : Window
{
    private readonly AppHost _host;
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(1) };
    private IDisposable? _shown;

    public MainWindow(AppHost host, MainModel model)
    {
        _host = host;
        Model = model;
        DataContext = model;
        InitializeComponent();
        _tick.Tick += (_, _) => model.Refresh();
        IsVisibleChanged += (_, _) => UpdateShown();
        StateChanged += (_, _) => UpdateShown();
        Activated += (_, _) => UpdateShown();
        Deactivated += (_, _) => UpdateShown();
        DragOver += OnDragOver;
        Drop += (_, e) => Model.Paste(e.Data); // files are open before this returns; the source may then delete them
    }

    public MainModel Model { get; }
    public event Action? SettingsWanted;
    public event Action? PairingWanted;

    /// <summary>The window was closed into the notification area.</summary>
    public event Action? Hidden;

    /// <summary>Set before the program really ends.</summary>
    public bool ReallyClose { get; set; }

    private bool _locked;

    /// <summary>Nobody sees the window of a locked computer.</summary>
    public bool Locked
    {
        get => _locked;
        set
        {
            _locked = value;
            UpdateShown();
        }
    }

    internal static bool Attended(bool open, bool active, bool locked) => open && active && !locked;

    /// <summary>While it is being looked at, the window itself shows what happens; otherwise notifications do.</summary>
    private void UpdateShown()
    {
        var open = IsVisible && WindowState != WindowState.Minimized;
        _host.WindowOpen = open;
        if (open && !_tick.IsEnabled)
        {
            Model.Refresh();
            _tick.Start();
        }
        if (!open) _tick.Stop();
        var attended = Attended(open, IsActive, Locked);
        if (attended == (_shown is not null)) return;
        if (attended)
        {
            _shown = _host.UiShown();
        }
        else
        {
            _shown?.Dispose();
            _shown = null;
        }
    }

    public void BringUp()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        // a window that may not take the focus at least comes to the front
        Topmost = true;
        Topmost = false;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (ReallyClose) return;
        e.Cancel = true;
        Hide();
        Hidden?.Invoke();
    }

    private static void OnDragOver(object sender, DragEventArgs e)
    {
        var ok = e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetDataPresent(DataFormats.UnicodeText);
        e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnPaste(object sender, ExecutedRoutedEventArgs e)
    {
        try
        {
            if (Clipboard.GetDataObject() is { } data && Model.Paste(data)) return;
            _host.Tell("In der Zwischenablage ist nichts, was gesendet werden kann");
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            _host.Tell("Die Zwischenablage ist gerade belegt");
        }
    }

    private void OnFiles(object sender, RoutedEventArgs e)
    {
        if (PickFiles(this) is { Count: > 0 } paths) Model.SendFiles(paths);
    }

    internal static IReadOnlyList<string> PickFiles(Window? owner)
    {
        var d = new OpenFileDialog { Multiselect = true, Title = "Dateien senden" };
        return d.ShowDialog(owner) == true ? d.FileNames : [];
    }

    private void OnText(object sender, RoutedEventArgs e) => Model.Offer(new Payload.Text(""));
    private void OnSettings(object sender, RoutedEventArgs e) => SettingsWanted?.Invoke();
    private void OnPair(object sender, RoutedEventArgs e) => PairingWanted?.Invoke();
}
