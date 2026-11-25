using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;

namespace Share.App;

public partial class SettingsWindow : Window
{
    private readonly SettingsModel _model;
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(1) };

    public SettingsWindow(SettingsModel model)
    {
        _model = model;
        DataContext = model;
        InitializeComponent();
        _tick.Tick += (_, _) => _model.Tick(DateTimeOffset.UtcNow);
        Loaded += async (_, _) =>
        {
            _tick.Start();
            await _model.LoadAsync();
        };
        Closed += (_, _) => _tick.Stop();
    }

    private async void OnRename(object sender, RoutedEventArgs e) => await _model.RenameAsync();
    private async void OnAdd(object sender, RoutedEventArgs e) => await _model.AddDeviceAsync();
    private async void OnAutostart(object sender, RoutedEventArgs e) => await _model.SetAutostartAsync(!_model.Autostart);

    private void OnNotifications(object sender, RoutedEventArgs e) => Process.Start(new ProcessStartInfo("ms-settings:notifications") { UseShellExecute = true })?.Dispose();

    private async void OnUnpair(object sender, RoutedEventArgs e)
    {
        var a = MessageBox.Show(this, "Laufende Übertragungen enden. Zum Wiederverwenden muss das Gerät neu gekoppelt werden.", "Gerät abmelden?", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (a != MessageBoxResult.OK) return;
        IsEnabled = false;
        await _model.UnpairAsync();
        Close();
    }
}
