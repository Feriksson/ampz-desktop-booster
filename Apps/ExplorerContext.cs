using System;
using System.Collections.Generic;
using AmpzDesktopBooster.Interop;

namespace AmpzDesktopBooster.Apps;

/// <summary>
/// Lee el contexto del Explorer activo: carpetas SELECCIONADAS o, en su defecto, la carpeta
/// abierta. Es lo que alimenta "Abrir con" (Win+F2) y "WT acá" (Win+`). Usa Shell.Application
/// por COM — la misma vía que el legacy. Si el foreground no es un Explorer, cae al Escritorio.
/// </summary>
public static class ExplorerContext
{
    /// <summary>
    /// Targets a abrir: carpetas seleccionadas en el Explorer activo; si no hay selección, la
    /// carpeta abierta; si el foreground no es Explorer (o es una vista especial), el Escritorio.
    /// </summary>
    public static IReadOnlyList<string> GetTargetPaths()
    {
        var selected = GetSelectedFolders();
        if (selected.Count > 0)
            return selected;

        var current = GetCurrentFolder();
        if (string.IsNullOrEmpty(current) || current.Contains("::{")) // vista especial (Este equipo, etc.)
            current = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        return new[] { current };
    }

    /// <summary>
    /// La carpeta abierta en el Explorer en foreground, o "" si el foreground NO es un Explorer
    /// (o es una vista especial tipo "Este equipo"). A diferencia de <see cref="GetTargetPaths"/>,
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

    private static List<string> GetSelectedFolders()
    {
        var result = new List<string>();
        try
        {
            dynamic? w = ForegroundExplorer();
            if (w is null) return result;

            dynamic items = w.Document.SelectedItems();
            foreach (dynamic item in items)
            {
                try
                {
                    if ((bool)item.IsFolder)
                        result.Add((string)item.Path);
                }
                catch { }
            }
        }
        catch { }
        return result;
    }
}
