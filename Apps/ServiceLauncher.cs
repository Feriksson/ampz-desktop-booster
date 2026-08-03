using System.IO;
using AmpzDesktopBooster.Persistence;

namespace AmpzDesktopBooster.Apps;

/// <summary>Por qué no se pudo lanzar un servicio — la ventana traduce el motivo a un mensaje que orienta.</summary>
public enum LaunchResult
{
    Ok,
    /// <summary>La entrada no tiene comando: es de SOLO MONITOREO (típicamente migrada del viejo ports.json).</summary>
    NoCommand,
    /// <summary>No tiene directorio de trabajo — un comando sin dónde correr no se puede lanzar.</summary>
    NoWorkDir,
    /// <summary>El directorio configurado ya no existe en disco (repo movido o worktree borrado).</summary>
    WorkDirMissing,
}

/// <summary>
/// Lanza un <see cref="ServiceEntry"/>. Es deliberadamente FLACO: todo el trabajo pesado ya lo hace
/// <see cref="Shell.RunInDir"/> —resolver un pwsh LANZABLE (no el del paquete MSIX, que muere en
/// hostfxr), pasar el comando por -EncodedCommand para que las comillas no las destroce wt.exe, y
/// decidir ventana nueva vs pestaña según el escritorio virtual actual—. Escribir un launcher propio
/// habría sido re-pelear las tres cosas.
///
/// El servicio corre en una TERMINAL VISIBLE a propósito, no oculto: los logs de un dev server son
/// justo lo que querés mirar cuando algo no levanta. Por eso tampoco capturamos su salida ni
/// guardamos su PID — ver el comentario de "estado" en ServicesWindow para el porqué de que el PID
/// no sirva acá (wt.exe delega en su proceso monarca: el PID que volvería NO es el del dev server).
/// </summary>
public static class ServiceLauncher
{
    public static LaunchResult Launch(ServiceEntry service)
    {
        string command = service.Command.Trim();
        if (command == "") return LaunchResult.NoCommand;

        string dir = service.WorkDir.Trim();
        if (dir == "") return LaunchResult.NoWorkDir;
        if (!Directory.Exists(dir)) return LaunchResult.WorkDirMissing;

        Shell.RunInDir(dir, command);
        return LaunchResult.Ok;
    }

    /// <summary>
    /// ¿Este servicio entra en el arranque grupal ("levantar lo básico")? Lo decide
    /// <see cref="ServiceEntry.AutoStartEffective"/> —NO el puerto— más lo obvio: sin comando no hay
    /// nada que lanzar. Ver <see cref="ServiceEntry.AutoStart"/> para por qué el puerto no alcanzaba
    /// (los workers de Laravel corren para siempre sin escuchar nada).
    /// </summary>
    public static bool IsGroupLaunchable(ServiceEntry s) =>
        s.AutoStartEffective && s.Command.Trim() != "";
}
