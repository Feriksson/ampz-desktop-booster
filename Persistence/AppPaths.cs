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

    /// <summary>Catálogo persistente de proyectos: history, paths, notes, shared pools.</summary>
    public static string ProjectDataFile => Path.Combine(DataDir, "desk_project_data.json");

    /// <summary>INI con secciones varias (sugerencias de proyecto, pins, restricciones, etc.).</summary>
    public static string SettingsIni => Path.Combine(DataDir, "settings.ini");

    /// <summary>
    /// Borra TODA la config del usuario: todos los archivos de DataDir (proyectos, settings.ini,
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
