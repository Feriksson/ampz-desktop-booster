using System;
using AmpzDesktopBooster.Interop;

namespace AmpzDesktopBooster.Apps;

/// <summary>
/// Lee el contexto del Explorer activo: la carpeta abierta. Es lo que alimenta "Abrir con" (Win+F2)
/// y "WT acá" (Win+`). Usa Shell.Application por COM — la misma vía que el legacy. Si el foreground
/// no es un Explorer, cae al Escritorio.
///
/// NOTA: el legacy también consideraba las carpetas SELECCIONADAS (abría una entrada por cada
/// carpeta marcada). Eso se descartó a pedido: con varias carpetas seleccionadas terminaba abriendo
/// N terminales / N "Abrir con". Ahora SIEMPRE se trabaja sobre la ruta actual, una sola.
/// </summary>
public static class ExplorerContext
{
    /// <summary>
    /// La carpeta abierta del Explorer en foreground (ignora las carpetas seleccionadas a propósito),
    /// con fallback al Escritorio ante una vista especial (tipo "Este equipo") o un foreground que no
    /// sea Explorer. Alimenta "WT acá" (Win+`) y "Abrir con" (Win+F2): queremos UNA sola ruta, la
    /// actual, no una por cada carpeta marcada.
    /// </summary>
    public static string GetCurrentTargetPath()
    {
        var current = GetCurrentFolder();
        if (string.IsNullOrEmpty(current) || current.Contains("::{")) // vista especial (Este equipo, etc.)
            current = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        return current;
    }

    /// <summary>
    /// La carpeta abierta en el Explorer en foreground, o "" si el foreground NO es un Explorer
    /// (o es una vista especial tipo "Este equipo"). A diferencia de <see cref="GetCurrentTargetPath"/>,
    /// acá NO caemos al Escritorio: para las notas de carpeta, "no hay carpeta" tiene que ser
    /// distinguible (string vacío) para poder ocultar el panel.
    /// </summary>
    public static string GetActiveFolder()
    {
        var current = GetCurrentFolder();
        if (string.IsNullOrEmpty(current) || current.Contains("::{")) // vista especial → no es una carpeta real
            return "";
        return current;
    }

    private static dynamic? ForegroundExplorer()
    {
        try
        {
            IntPtr fg = WindowMethods.GetForegroundWindow();
            if (fg == IntPtr.Zero) return null;

            var t = Type.GetTypeFromProgID("Shell.Application");
            if (t is null) return null;
            dynamic shell = Activator.CreateInstance(t)!;

            foreach (dynamic w in shell.Windows())
            {
                try
                {
                    if ((IntPtr)(long)w.HWND == fg)
                        return w;
                }
                catch { /* ventana sin HWND legible → siguiente */ }
            }
        }
        catch { /* COM no disponible → sin contexto */ }
        return null;
    }

    private static string GetCurrentFolder()
    {
        try
        {
            dynamic? w = ForegroundExplorer();
            if (w is not null)
                return (string)w.Document.Folder.Self.Path;
        }
        catch { }
        return "";
    }
}
