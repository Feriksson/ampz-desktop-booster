using System;
using System.Windows.Media;

namespace AmpzDesktopBooster.Desktops;

/// <summary>
/// El nombre del desktop define su color — exactamente como el overlay del legacy.
/// MAIN verde, MAILS steel blue, MISCS gris, DESK +N gold, y un fallback blanco.
/// Cada tipo tiene un par (activo brillante / inactivo apagado).
/// </summary>
public static class DeskPalette
{
    public readonly record struct Pair(Color Active, Color Inactive);

    public static Pair For(string name)
    {
        if (Contains(name, "MAIN"))    return new(Rgb(0x2E, 0xCC, 0x40), Rgb(0x1A, 0x7A, 0x20));
        if (Contains(name, "MAILS"))   return new(Rgb(0x46, 0x82, 0xB4), Rgb(0x23, 0x40, 0x60));
        if (Contains(name, "MISCS"))   return new(Rgb(0xAA, 0xAA, 0xAA), Rgb(0x55, 0x55, 0x55));
        if (Contains(name, "DESK +"))  return new(Rgb(0xFF, 0xD7, 0x00), Rgb(0x7A, 0x60, 0x00));
        return new(Rgb(0xFF, 0xFF, 0xFF), Rgb(0x44, 0x44, 0x44));
    }

    private static bool Contains(string haystack, string needle) =>
        haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);

    private static Color Rgb(byte r, byte g, byte b) => Color.FromRgb(r, g, b);
}
