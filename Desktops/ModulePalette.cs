using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Media;

namespace AmpzDesktopBooster.Desktops;

/// <summary>
/// Snapshot inmutable del módulo activo de un desk, tal como lo consume la UI (barra, overlay,
/// pickers). Viaja como VALOR para que la UI no tenga que conocer el ProjectStore ni re-resolverlo.
/// </summary>
public readonly record struct DeskModule(string Name, string Color)
{
    public static readonly DeskModule None = new("", "");

    public bool IsSet => !string.IsNullOrEmpty(Name);

    /// <summary>Color parseado listo para pintar. Sin color propio cae al dorado de DESK +N.</summary>
    public Color Accent => ModulePalette.Parse(Color);
}

/// <summary>
/// Los colores con los que se identifican los módulos de un proyecto.
///
/// Por qué existe: el problema que originó los módulos NO era no poder anotarlos, era ERRARLE al
/// módulo al cambiar de pantalla. El texto obliga a LEER; el color se percibe de reflejo, con la
/// visión periférica. Por eso cada módulo arranca ya con un color asignado — cero fricción, la
/// señal existe desde el minuto uno aunque el usuario nunca abra el selector de color.
///
/// La paleta esquiva a propósito el dorado de DESK +N y el verde de MAIN (ver <see cref="DeskPalette"/>):
/// un módulo NUNCA se debe confundir con un tipo de desk.
/// </summary>
public static class ModulePalette
{
    /// <summary>Colores disponibles, en el orden en que se auto-asignan a los módulos nuevos.</summary>
    public static readonly string[] Colors =
    {
        "#4FC3F7", // celeste
        "#FF8A65", // coral
        "#BA68C8", // violeta
        "#4DB6AC", // teal
        "#F06292", // rosa
        "#9CCC65", // lima
        "#7986CB", // índigo
        "#FFB300", // ámbar
    };

    /// <summary>Fallback: el dorado de DESK +N. Un módulo sin color se ve como se veía antes.</summary>
    public static readonly Color Fallback = Color.FromRgb(0xFF, 0xD7, 0x00);

    /// <summary>
    /// "#RRGGBB" → Color. Cualquier basura (string vacío, hex inválido, valor de una versión futura)
    /// cae al <see cref="Fallback"/>: un color corrupto en el JSON NO puede tumbar la barra ni el
    /// overlay, que se pintan en el camino crítico de cada cambio de desk.
    /// </summary>
    public static Color Parse(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return Fallback;
        try
        {
            var obj = ColorConverter.ConvertFromString(hex.Trim());
            return obj is Color c ? c : Fallback;
        }
        catch { return Fallback; }
    }

    /// <summary>
    /// Primer color de la paleta que NINGÚN módulo del proyecto esté usando ya — así dos módulos
    /// del mismo cliente nunca nacen del mismo color (que es justo la confusión que veníamos a
    /// resolver). Si ya se agotó la paleta, cicla por posición: repetir es mejor que quedarse sin.
    /// </summary>
    public static string NextFree(IEnumerable<string> used)
    {
        var taken = new HashSet<string>(used.Where(c => !string.IsNullOrWhiteSpace(c)),
                                        StringComparer.OrdinalIgnoreCase);
        foreach (var c in Colors)
            if (!taken.Contains(c))
                return c;
        return Colors[taken.Count % Colors.Length];
    }

    /// <summary>Siguiente color de la paleta (cicla). Lo usa el F3 del picker para cambiar el color.</summary>
    public static string Next(string? current)
    {
        int i = Array.FindIndex(Colors, c => string.Equals(c, current, StringComparison.OrdinalIgnoreCase));
        return Colors[(i + 1) % Colors.Length]; // i == -1 (sin color) → arranca en el primero
    }
}
