using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace AmpzDesktopBooster;

/// <summary>
/// Convierte un string vacío/null a Visibility.Collapsed (lo demás Visible). Útil en DataTemplates
/// donde un TextBlock dependiente de un campo opcional debe DESAPARECER del layout (no quedar como
/// hueco con padding) cuando ese campo no tiene valor — ej. el Stage del picker para JIRA, que es
/// null y no debería dejar un "(  )" colgando.
/// </summary>
public sealed class EmptyStringToCollapsedConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => string.IsNullOrEmpty(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
