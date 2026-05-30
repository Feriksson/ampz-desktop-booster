using System;
using System.Diagnostics;
using System.IO;
using AmpzDesktopBooster.Apps;

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

    /// <summary>Abre la variable: si es URL → browser; si es path → explorer/asociación del SO.</summary>
    public static Result Open(string value)
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
            if (Directory.Exists(value) || File.Exists(value))
            {
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
