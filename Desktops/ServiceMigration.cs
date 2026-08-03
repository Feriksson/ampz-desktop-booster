using System.IO;
using System.Linq;
using AmpzDesktopBooster.Persistence;

namespace AmpzDesktopBooster.Desktops;

/// <summary>
/// Migra el viejo catálogo GLOBAL de puertos (<c>ports.json</c>) a servicios del scope GLOBAL.
///
/// Por qué la migración es TRIVIAL y sin pérdida: una entrada vieja era <c>{Title, Port}</c>, o sea
/// un servicio que sabemos MIRAR pero no sabemos LEVANTAR. Eso es exactamente un
/// <see cref="ServiceEntry"/> con <c>Command</c> vacío — el caso degenerado del modelo nuevo. La
/// feature anterior no era inútil: era ésta sin el scope y sin el arranque.
///
/// IDEMPOTENCIA POR EL DISCO, no por una heurística: al terminar renombramos el archivo a
/// <c>ports.json.migrated</c>. Mientras exista <c>ports.json</c> hay algo que migrar; cuando no está,
/// no hay nada. Chequear "¿ya hay servicios globales?" habría sido frágil (borrás todos los servicios
/// y la migración vuelve a importar los puertos viejos como zombis). Y el archivo NO se borra: queda
/// ahí, legible, por si el usuario quiere volver atrás a mano.
/// </summary>
public static class ServiceMigration
{
    public static void MigratePortsIfNeeded(ProjectStore projects)
    {
        try
        {
            string path = AppPaths.PortsFile;
            if (!File.Exists(path)) return;

            var ports = PortStore.Load();
            var pool = projects.GetSharedServicePool();

            foreach (var e in ports.Entries)
            {
                // Guardia por si alguien corre esto dos veces con el archivo restaurado a mano: no
                // duplicamos una entrada de sólo-monitoreo que ya tenga el mismo puerto.
                if (pool.Entries.Any(s => s.Port == e.Port && s.Command == ""))
                    continue;
                pool.Add(e.Title, command: "", workDir: "", port: e.Port);
            }

            File.Move(path, path + ".migrated", overwrite: true);
        }
        catch
        {
            // Disco/permisos/JSON corrupto: arrancamos sin migrar. La persistencia NUNCA voltea la app
            // (y si el rename falló pero el import salió, la guardia de arriba evita duplicar mañana).
        }
    }
}
