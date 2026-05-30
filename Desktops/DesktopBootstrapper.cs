using System;
using System.Threading;
using AmpzDesktopBooster.Interop;

namespace AmpzDesktopBooster.Desktops;

/// <summary>
/// Asegura que existan los escritorios gestionados con sus nombres, al arrancar.
///
/// Cómo: si faltan escritorios (hay menos que los gestionados), los CREA con el export
/// nativo CreateDesktop de la DLL — que crea al final SIN cambiar el foco (bootstrap
/// silencioso, sin parpadeo). Después renombra los primeros N por índice con SetDesktopName.
///
/// Modelo: la app "posee" el layout — los primeros N escritorios pasan a ser, en orden,
/// el set gestionado. Renombrar NO mueve ventanas; sólo cambia la etiqueta del desktop.
/// Si ya tenés los 9 escritorios bien nombrados, esto es un no-op total (no crea ni renombra).
/// </summary>
public static class DesktopBootstrapper
{
    /// <returns>Cantidad de escritorios creados (0 si no hizo falta crear ninguno).</returns>
    public static int Ensure(DesktopConfig config, DesktopService desktops)
    {
        var wanted = config.Managed;
        if (wanted.Count == 0)
            return 0;

        int created = 0;
        int need = wanted.Count - desktops.Count;

        for (int i = 0; i < need; i++)
        {
            VirtualDesktopAccessor.CreateDesktop(); // crea al final, NO cambia el foco
            Thread.Sleep(60);                        // dar tiempo a que el shell lo registre
            created++;
        }

        // Renombrar índice por índice sólo si difiere (evita writes innecesarios).
        for (int i = 0; i < wanted.Count && i < desktops.Count; i++)
        {
            if (!string.Equals(desktops.GetName(i), wanted[i], StringComparison.OrdinalIgnoreCase))
                desktops.SetName(i, wanted[i]);
        }

        return created;
    }
}
