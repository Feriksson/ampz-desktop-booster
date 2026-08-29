using System;
using System.Globalization;
using System.Windows.Data;

namespace AmpzDesktopBooster;

/// <summary>
/// Pasa un string a MAYÚSCULAS para mostrar. Existe porque WPF no tiene un equivalente a
/// text-transform de CSS: TextBlock no expone CharacterCasing (sólo lo tiene TextBox, y para ENTRADA).
///
/// Se usa en los divisores de grupo del picker de tareas, donde el texto es el nombre CRUDO del
/// estado que puso el usuario en su gestor ("In progress", "Haciendo"). Las mayúsculas son sólo
/// tipografía — el nombre no se toca ni se normaliza — y son lo que hace que la banda se lea como
/// encabezado de sección y no como una fila más, que es justo lo que el divisor viene a evitar.
///
/// Usa la cultura que le pasa el binding (ToUpper(culture)), no ToUpperInvariant: hay idiomas donde
/// el mapeo a mayúscula depende de la cultura (el clásico es la i sin punto del turco).
/// </summary>
public sealed class UpperCaseConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => (value as string)?.ToUpper(culture ?? CultureInfo.CurrentCulture) ?? "";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
