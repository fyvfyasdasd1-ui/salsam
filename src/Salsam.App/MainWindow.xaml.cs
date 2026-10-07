using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Salsam.App;
public sealed class SectionVisibility : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => Equals(value, parameter) ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
public partial class MainWindow : Window
{
    private readonly MainViewModel model = new();
    public MainWindow()
    {
        InitializeComponent(); DataContext = model;
        Loaded += async (_, _) => await model.Refresh();
        Closed += (_, _) => model.Stop();
    }
}
