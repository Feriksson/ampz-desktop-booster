using System;
using System.IO;

namespace AmpzDesktopBooster.Persistence;

/// <summary>
/// Rutas de datos del usuario. Todo va a %APPDATA%\AmpzDesktopBooster — NO junto al exe.
/// Es lo correcto para una app compartible: cada usuario tiene su config, y el exe queda
/// inmutable. El legacy guardaba junto al script (A_ScriptDir); acá lo modernizamos.
/// </summary>
public static class AppPaths
{
    public static string DataDir
    {
        get
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "AmpzDesktopBooster");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    /// <summary>Catálogo persistente de espacios: history, paths, notes, shared pools.</summary>
    public static string ProjectDataFile => Path.Combine(DataDir, "desk_project_data.json");

    /// <summary>INI con secciones varias (sugerencias de espacio, pins, restricciones, etc.).</summary>
    public static string SettingsIni => Path.Combine(DataDir, "settings.ini");

    /// <summary>Catálogo de puertos/servicios locales (título + puerto). Lista GLOBAL única.</summary>
    public static string PortsFile => Path.Combine(DataDir, "ports.json");

    /// <summary>
    /// Registro de escritorios DINÁMICOS (espacio+contexto creados por el launcher de
    /// Win+NumpadEnter), indexados por GUID del desktop — ver <see cref="Desktops.DynamicDeskStore"/>.
    /// Separado de <see cref="ProjectDataFile"/> a propósito: ESO es el catálogo durable de espacios
    /// (existen aunque ningún desk los tenga abiertos hoy); ESTO es la lista de qué desk vivo ES CADA
    /// asignación — vive y muere con la sesión de escritorios de Windows, no con el catálogo.
    /// </summary>
    public static string DynamicDesksFile => Path.Combine(DataDir, "dynamic_desks.json");

    /// <summary>
    /// Notas SUELTAS (muchas, con título y etiqueta opcional de espacio/contexto) — ver
    /// <see cref="Desktops.LooseNoteStore"/>. Archivo propio y no dentro de <see cref="ProjectDataFile"/>:
    /// se escribe en cada edición de una nota, y si se daña no arrastra el catálogo de espacios.
    /// </summary>
    public static string LooseNotesFile => Path.Combine(DataDir, "notes.json");

    /// <summary>
    /// Borra TODA la config del usuario: todos los archivos de DataDir (espacios, settings.ini,
    /// apps, atajos, desktops, widgets, uso). Operación DESTRUCTIVA — el caller confirma y luego
    /// reinicia la app para arrancar con defaults limpios. No toca nada fuera de DataDir (ni el
    /// crash-log, que vive junto al exe). try/catch por archivo: si uno está lockeado, seguimos con
    /// el resto (el relauncher del reinicio barre lo que quede una vez que el proceso cierra).
    /// </summary>
    public static void ResetAllData()
    {
        foreach (var file in Directory.EnumerateFiles(DataDir))
        {
            try { File.Delete(file); }
            catch { /* en uso: lo limpia el relauncher tras cerrar la app */ }
        }
    }
}
