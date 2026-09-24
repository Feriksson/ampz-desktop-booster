using System.Security.Principal;

namespace AmpzDesktopBooster.Interop;

/// <summary>
/// ¿Este proceso corre elevado (token de administrador)? Centralizado acá porque lo necesitan dos
/// consumidores sin relación entre sí: <see cref="Services.AutoStartService"/> (crear la tarea
/// programada con RunLevel Highest requiere admin) y <see cref="UnelevatedLauncher"/> (decidir si
/// hay que des-elevar a los hijos). Un solo lugar evita que definan el criterio cada uno por su lado.
/// </summary>
internal static class ElevationHelper
{
    /// <summary>true si el proceso actual corre con el token elevado (Administrador).</summary>
    public static bool IsElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            // Si no podemos preguntar, asumimos que NO estamos elevados: es el camino más
            // conservador (el que menos sorprende — nunca de-elevamos algo que ya corría normal).
            return false;
        }
    }
}
