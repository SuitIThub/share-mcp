using System.Windows;

namespace Share.App;

public partial class PairWindow : Window
{
    private readonly PairModel _model;

    public PairWindow(PairModel model)
    {
        _model = model;
        DataContext = model;
        InitializeComponent();
    }

    public void Fill(string link) => _model.Fill(link);

    private async void OnPair(object sender, RoutedEventArgs e)
    {
        if (await _model.PairAsync() && IsLoaded) Close();
    }
}
