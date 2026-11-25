using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace Share.App;

/// <summary>Check boxes for all devices and "all"; its DataContext is a TargetList.</summary>
public partial class TargetPicker : UserControl
{
    public TargetPicker() => InitializeComponent();
}

public sealed class NotNullToVisibility : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is null ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
