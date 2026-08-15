using System.Collections.Generic;
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
    /// <summary>El comando usa <c>{ip}</c> pero no hay IP de LAN para poner en su lugar.</summary>
    NoNetwork,
    /// <summary>El comando usa <c>{port}</c> pero el servicio no declara puerto.</summary>
    NoPortToken,
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
        var result = Resolve(service, out var job);
        if (result != LaunchResult.Ok) return result;

        Shell.RunInDir(job.WorkingDir, job.Command);
        return LaunchResult.Ok;
    }

    /// <summary>
    /// Lanza VARIOS servicios de una, todos en la MISMA ventana de terminal (una pestaña cada uno).
    /// Devuelve cuántos se pudieron resolver — los que no (directorio inexistente, {ip} sin red)
    /// quedan afuera en silencio, igual que antes: en un arranque grupal el que falla no puede
    /// frenar a los demás ni tapar la pantalla con un modal por cada uno.
    ///
    /// POR QUÉ EXISTE Y NO ES UN <c>foreach</c> DE <see cref="Launch"/> — que es lo que era, y estaba
    /// MAL: <see cref="Shell"/> decide "ventana nueva vs pestaña" preguntando si YA hay una ventana de
    /// Windows Terminal en el escritorio actual, y wt.exe crea sus ventanas de forma ASÍNCRONA (vía su
    /// proceso monarca). En un lazo sincrónico las N preguntas se hacen antes de que NINGUNA de las
    /// ventanas exista, así que las N contestaban "no hay" y abrían N ventanas. Lanzando uno por uno a
    /// mano el bug no aparece porque entre clic y clic la ventana alcanza a nacer.
    ///
    /// La cura NO es esperar entre lanzadas —eso sería arreglar una carrera con timing, que en este
    /// repo ya sabemos que no se sostiene— sino que deje de haber N decisiones independientes: se arma
    /// UN solo comando de terminal con todas las pestañas. Ver <see cref="Shell.RunMany"/>.
    /// </summary>
    public static int LaunchMany(IEnumerable<ServiceEntry> services)
    {
        var jobs = new List<ShellJob>();
        foreach (var s in services)
            if (Resolve(s, out var job) == LaunchResult.Ok)
                jobs.Add(job);

        if (jobs.Count == 0) return 0;
        Shell.RunMany(jobs);
        return jobs.Count;
    }

    /// <summary>
    /// Valida el servicio y expande sus tokens, SIN lanzar nada. Separado de <see cref="Launch"/>
    /// para que el arranque grupal pueda resolver todo primero y recién después disparar una sola vez.
    /// </summary>
    private static LaunchResult Resolve(ServiceEntry service, out ShellJob job)
    {
        job = default;

        string command = service.Command.Trim();
        if (command == "") return LaunchResult.NoCommand;

        string dir = service.WorkDir.Trim();
        if (dir == "") return LaunchResult.NoWorkDir;
        if (!Directory.Exists(dir)) return LaunchResult.WorkDirMissing;

        // Los tokens se resuelven ACÁ, en cada lanzada, y nunca se persiste el valor: ese es todo el
        // punto de {ip} —la IP de LAN la rota el DHCP y cambia al saltar de WiFi a cable—. Ver
        // CommandTokens. Sólo escaneamos las interfaces de red si el comando de verdad los usa.
        string? ip = CommandTokens.HasTokens(command) ? Services.LocalIp.Get() : null;
        var token = CommandTokens.Expand(command, service.Port, ip, out string expanded);
        if (token == TokenResult.NoNetwork) return LaunchResult.NoNetwork;
        if (token == TokenResult.NoPort) return LaunchResult.NoPortToken;

        job = new ShellJob(dir, expanded);
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
