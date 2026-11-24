using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace FluxTube.UI;

public sealed class VisibleIfConverter : IValueConverter
{
    public static bool Truthy(object? v) => v switch
    {
        null => false,
        bool b => b,
        string s => s.Length > 0,
        double d => d > 0.001,
        int i => i != 0,
        _ => true,
    };

    public object Convert(object value, Type targetType, object parameter, string language)
        => Truthy(value) ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
}

public sealed class VisibleIfNotConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => VisibleIfConverter.Truthy(value) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
}
