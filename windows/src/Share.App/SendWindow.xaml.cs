using System.Windows;

namespace Share.App;

public partial class SendWindow : Window
{
    private readonly SendModel _model;

    public SendWindow(SendModel model)
    {
        _model = model;
        DataContext = model;
        InitializeComponent();
        model.Closed += Close;
        Loaded += (_, _) =>
        {
            Activate();
            if (model.IsText) Content_.Focus();
        };
        // closing the window any other way is a cancel, too
        Closed += (_, _) => model.Cancel();
    }

    public event Action? PairingWanted;

    private void OnSend(object sender, RoutedEventArgs e) => _model.Send();
    private void OnCancel(object sender, RoutedEventArgs e) => _model.Cancel();
    private void OnPair(object sender, RoutedEventArgs e) => PairingWanted?.Invoke();
}
