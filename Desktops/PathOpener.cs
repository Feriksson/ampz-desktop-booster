using System;
using System.Diagnostics;
using System.IO;
using AmpzDesktopBooster.Apps;
using AmpzDesktopBooster.Interop;

namespace AmpzDesktopBooster.Desktops;

/// <summary>
/// Abre una variable (path o URL) o la manda a Claude CLI. Centraliza las acciones del
/// Paths Manager (Win+Numpad*) para no repetir lógica en la ventana.
///
/// NOTA: el legacy ruteaba las URLs por el "browser shim" (para abrirlas en el monitor/desktop
/// actual). Ese shim se porta en la Fase 5; por ahora abrimos con el handler default del SO.
/// </summary>
public static class PathOpener
{
    public enum Result { Opened, NotFound, Error }

    /// <summary>
    /// Abre la variable: si es URL → browser; si es path → explorer/asociación del SO.
    /// <paramref name="targetMonitor"/> es el HMONITOR donde está el usuario (el de la ventana de
    /// Variables); SÓLO se usa para carpetas, para decidir EN QUÉ pantalla mostrarlas (ver
    /// <see cref="OpenFolderHere"/>). <c>Zero</c> (default) → comportamiento simple sin ventaneo.
    /// </summary>
    public static Result Open(string value, IntPtr targetMonitor = default)
    {
        value = value.Trim();
        if (value == "")
            return Result.NotFound;

        try
        {
            if (UrlHelper.IsUrl(value))
            {
                var url = UrlHelper.Normalize(value);
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                return Result.Opened;
            }

            // Path de filesystem: validar existencia antes de abrir (como el legacy).
            if (Directory.Exists(value))
            {
                OpenFolderHere(value, targetMonitor);
                return Result.Opened;
            }
            if (File.Exists(value))
            {
                // Un archivo abre una app/pestaña de ventana NO identificable de antemano: no podemos
                // garantizar foco ni monitor, así que no lo intentamos (sería humo). Abrir y listo.
                Process.Start(new ProcessStartInfo(value) { UseShellExecute = true });
                return Result.Opened;
            }
            return Result.NotFound;
        }
        catch
        {
            return Result.Error;
        }
    }

    /// <summary>
    /// Abre la CARPETA <paramref name="dir"/> trayéndola al monitor donde está el usuario
    /// (<paramref name="targetMonitor"/>), sin catapultarlo a otra pantalla. Las ventanas de Explorer
    /// son identificables (clase <c>CabinetWClass</c> + título = nombre de la carpeta), filtradas al
    /// escritorio virtual actual para no agarrar una carpeta homónima de OTRO desk.
    ///
    /// La regla (decidida con el usuario):
    ///   1. Si esa carpeta YA está abierta en MI monitor → sólo la traigo al frente. Cubre el
    ///      "estaba atrás y no venía al frente" SIN duplicar ventanas.
    ///   2. Si sólo existe en OTRO monitor (mismo desk virtual) o no existe → abro una NUEVA con
    ///      <c>explorer /n</c> (fuerza ventana nueva en vez de REACTIVAR la que vive en otra pantalla,
    ///      que era justo el "salto de monitor" no deseado) y, cuando aparece, la muevo a MI monitor y
    ///      le doy foco. El foco viene a vos, no vos al foco.
    ///
    /// Por qué forzamos el foreground: la ventana la crea explorer.exe (ya vivo), NO nosotros, y
    /// venimos de un hotkey global → el anti-robo de foco de Windows la dejaría atrás.
    /// </summary>
    private static void OpenFolderHere(string dir, IntPtr targetMonitor)
    {
        string leaf = Path.GetFileName(dir.TrimEnd('\\', '/', ' '));
        int desk = Shell.Desktops?.Current ?? -1;

        // Raíz de unidad (ej. C:\): el título no es un nombre de carpeta identificable; y sin monitor
        // objetivo no hay a dónde traerla. En ambos casos caemos al comportamiento simple: abrir.
        if (leaf == "" || targetMonitor == IntPtr.Zero)
        {
            Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
            return;
        }

        // OJO (cazado con el log): Windows 11 NO titula la ventana de Explorer con el nombre pelado de
        // la carpeta — le agrega el sufijo localizado " - Explorador de archivos" (" - File Explorer").
        // El match exacto del legacy fallaba SIEMPRE acá. Aceptamos el título pelado (Win10/configs sin
        // sufijo) O que EMPIECE con "{leaf} - " (Win11). El separador " - " evita falsos positivos con
        // carpetas de nombre similar, y es agnóstico al idioma del sufijo.
        bool TitleMatches(string t) =>
            t.Equals(leaf, StringComparison.OrdinalIgnoreCase)
            || t.StartsWith(leaf + " - ", StringComparison.OrdinalIgnoreCase);

        bool IsThisFolder(IntPtr h) =>
            WindowMethods.ClassOf(h) == "CabinetWClass"
            && TitleMatches(WindowMethods.TextOf(h))
            && (desk < 0 || VirtualDesktopAccessor.GetWindowDesktopNumber(h) == desk);

        var existing = WindowMethods.FindAllVisible(IsThisFolder);

        // 1) ¿Ya hay una de esta carpeta en MI monitor? → traerla al frente y chau (no duplicamos).
        foreach (var h in existing)
            if (WindowMethods.MonitorOf(h) == targetMonitor)
            {
                WindowMethods.ForceForeground(h, preserveMaximized: true);
                return;
            }

        // 2) No hay ninguna en mi monitor (sólo en otro, o ninguna) → abrir una NUEVA y traerla acá.
        //    explorer /n fuerza ventana nueva; sin /n, Explorer reactivaría la del otro monitor → salto.
        var seen = new HashSet<IntPtr>(existing);
        Process.Start("explorer.exe", $"/n,\"{dir}\"");
        WindowFocuser.FocusWhenReady(
            match: h => IsThisFolder(h) && !seen.Contains(h), // la NUEVA, no las que ya estaban
            onReady: h =>
            {
                WindowMethods.MoveToMonitor(h, targetMonitor); // a mi pantalla...
                WindowMethods.ForceForeground(h, preserveMaximized: true); // ...y al frente
            });
    }

    /// <summary>
    /// Abre el directorio en Claude CLI: shell preferido (pwsh → powershell) parado en el dir,
    /// corriendo claude en bypass. Sólo aplica a paths de filesystem (con URLs no tiene sentido).
    /// El ventaneo (ventana nueva vs pestaña, por escritorio) y el quoting del comando los maneja
    /// <see cref="Shell.RunInDir"/> — ver allí el porqué de wt.exe + -EncodedCommand.
    /// </summary>
    public static Result OpenInClaude(string value)
    {
        value = value.Trim();
        if (value == "" || UrlHelper.IsUrl(value))
            return Result.NotFound;
        if (!Directory.Exists(value))
            return Result.NotFound;

        var claude = AppDetector.InPath("claude.exe")
                     ?? AppDetector.InPath("claude.cmd")
                     ?? AppDetector.FirstExisting(@"%USERPROFILE%\.local\bin\claude.exe");
        if (claude is null)
            return Result.NotFound;

        try
        {
            Shell.RunInDir(value, $"& '{claude}' --permission-mode bypassPermissions");
            return Result.Opened;
        }
        catch
        {
            return Result.Error;
        }
    }
}
