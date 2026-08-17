using System;
using System.Windows.Media;

namespace AmpzDesktopBooster.Desktops;

/// <summary>
/// Color del desktop en overlay, barra y pickers. Cada color es un par (activo brillante / inactivo
/// apagado) — el apagado se deriva del activo, así un color elegido a mano trae su versión tenue sola.
///
/// ⚠ ORDEN DE RESOLUCIÓN (importa): color PROPIO del catálogo → color por ROL → legado por nombre.
/// Antes el color salía SÓLO del nombre ("MAIN" → verde) y renombrar el desk te lo apagaba a blanco
/// sin decirte nada. El legado quedó de última red para desks NO gestionados (creados a mano desde
/// Windows), que nunca pasaron por nuestro catálogo.
/// </summary>
public static class DeskPalette
{
    public readonly record struct Pair(Color Active, Color Inactive);

    /// <summary>
    /// Colores ofrecidos en la config para pintar un desk a mano. Arrancan por los tres de ROL (para
    /// que "poner el verde de siempre" sea un click y no un hex a mano) y siguen con la paleta de
    /// contextos, que ya está elegida para distinguirse entre sí sobre fondo oscuro.
    /// </summary>
    public static readonly string[] Presets =
    {
        "#2ECC40", // verde  (rol Principal)
        "#FFD700", // dorado (rol Espacio)
        "#AAAAAA", // gris   (rol Fijo)
        "#4682B4", // steel blue (el histórico de CONSOLES)
        "#4FC3F7", "#FF8A65", "#BA68C8", "#4DB6AC",
        "#F06292", "#9CCC65", "#7986CB", "#FFB300",
    };

    public static Pair For(string name)
    {
        // 1) Color propio elegido en la config → manda sobre todo lo demás.
        string custom = DeskCatalog.ColorOf(name);
        if (custom != "" && TryParseHex(custom, out var c))
            return new(c, Dim(c));

        // 2) Por ROL — sobrevive a cualquier renombre.
        var entry = DeskCatalog.Config?.ByName(name);
        if (entry is not null)
            return entry.DeskRole switch
            {
                DeskRole.Main  => new(Rgb(0x2E, 0xCC, 0x40), Rgb(0x1A, 0x7A, 0x20)), // verde
                DeskRole.Space => new(Rgb(0xFF, 0xD7, 0x00), Rgb(0x7A, 0x60, 0x00)), // dorado
                _              => new(Rgb(0xAA, 0xAA, 0xAA), Rgb(0x55, 0x55, 0x55)), // gris
            };

        // 3) Legado por nombre — sólo para desks fuera del catálogo.
        if (Contains(name, "MAIN"))     return new(Rgb(0x2E, 0xCC, 0x40), Rgb(0x1A, 0x7A, 0x20));
        if (Contains(name, "CONSOLES")) return new(Rgb(0x46, 0x82, 0xB4), Rgb(0x23, 0x40, 0x60));
        if (Contains(name, "MISCS"))    return new(Rgb(0xAA, 0xAA, 0xAA), Rgb(0x55, 0x55, 0x55));
        if (Contains(name, "DESK +"))   return new(Rgb(0xFF, 0xD7, 0x00), Rgb(0x7A, 0x60, 0x00));
        return new(Rgb(0xFF, 0xFF, 0xFF), Rgb(0x44, 0x44, 0x44));
    }

    /// <summary>Versión apagada de un color: mismo tono al 45%. Es el "inactivo" de cualquier par.</summary>
    private static Color Dim(Color c) =>
        Color.FromRgb((byte)(c.R * 0.45), (byte)(c.G * 0.45), (byte)(c.B * 0.45));

    /// <summary>"#RRGGBB" → Color. false si el string está mal escrito (config editada a mano).</summary>
    public static bool TryParseHex(string hex, out Color color)
    {
        color = Colors.White;
        if (hex.Length != 7 || hex[0] != '#') return false;
        try
        {
            color = Rgb(Convert.ToByte(hex.Substring(1, 2), 16),
                        Convert.ToByte(hex.Substring(3, 2), 16),
                        Convert.ToByte(hex.Substring(5, 2), 16));
            return true;
        }
        catch { return false; }
    }

    private static bool Contains(string haystack, string needle) =>
        haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);

    private static Color Rgb(byte r, byte g, byte b) => Color.FromRgb(r, g, b);
}
