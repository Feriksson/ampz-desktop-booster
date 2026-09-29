using System;
using System.Collections.Generic;
using System.Linq;
using AmpzDesktopBooster.Apps;
using AmpzDesktopBooster.Hotkeys;

namespace AmpzDesktopBooster.Desktops;

/// <summary>Qué pasó al pedirle al launcher que abra un espacio+contexto.</summary>
public enum LauncherOpenResult
{
    /// <summary>Se creó un escritorio dinámico nuevo, se asignó y se saltó ahí.</summary>
    Created,
    /// <summary>Ese espacio+contexto YA estaba abierto en otro desk dinámico → se saltó ahí en vez de duplicar.</summary>
    SwitchedExisting,
    /// <summary>No queda ninguna tecla 1..9 libre (todas las toma el catálogo fijo u otro desk dinámico vivo).</summary>
    NoFreeKey,
}

/// <summary>
/// Orquesta el ciclo de vida de un escritorio DINÁMICO — la lógica no-UI detrás de Win+NumpadEnter.
/// Separado de <see cref="Hotkeys.HotkeyRouter"/> (que sólo cablea el atajo y las ventanas) para que
/// se pueda razonar y testear como una secuencia de pasos de dominio, igual que
/// <see cref="DesktopBootstrapper"/> con los fijos.
///
/// PARADIGMA: un desk de ESPACIO ya no es una entrada fija y siempre-viva del catálogo ("DESK +N").
/// Nace cuando el usuario confirma espacio+contexto en el launcher y muere con el re-press sobre ese
/// mismo desk — ver CLAUDE.md, sección del cambio de paradigma.
/// </summary>
public static class DeskLauncher
{
    /// <summary>
    /// Abre (o encuentra) el desk de <paramref name="project"/>+<paramref name="module"/>.
    ///
    /// Dedupe primero: un mismo par espacio+contexto no puede vivir en dos desks a la vez — si ya
    /// está abierto, SALTAMOS ahí en vez de crear un duplicado confuso.
    ///
    /// Si hay que crear: la tecla es la MÁS BAJA libre entre 1..9, descontando las que ya usa el
    /// catálogo de FIJOS (Main/Fixed) y las que ya tiene otro desk dinámico vivo — nunca se pisan dos
    /// destinos con la misma tecla (mismo principio que <see cref="DesktopConfig.ClaimKey"/>).
    /// Sin tecla libre, NO se crea nada (se devuelve <see cref="LauncherOpenResult.NoFreeKey"/>): un
    /// desk sin atajo de navegación sería un callejón sin salida hasta el DeskPicker.
    ///
    /// Tras crear, asienta la sesión (espacio+contexto del desk YA es éste, no el de origen) y salta
    /// ahí. NADA MÁS: antes además auto-lanzaba los servicios "levantar todo" del scope, y se RETIRÓ
    /// a pedido del usuario — abrir un escritorio no tiene que disparar procesos por su cuenta. Para
    /// levantar lo básico está el re-press de Win+Numpad+ (o Ctrl+Enter en Servicios).
    /// </summary>
    public static LauncherOpenResult Open(DesktopService desktops, ProjectStore projects,
        DesktopConfig catalog, DynamicDeskStore dynamic, string project, string module, out int deskIndex)
    {
        deskIndex = -1;

        var existing = dynamic.FindOpenAssignment(project, module);
        if (existing is { } found)
        {
            deskIndex = found.Index;
            desktops.GoTo(found.Index);
            return LauncherOpenResult.SwitchedExisting;
        }

        var taken = new HashSet<NumpadKey>(catalog.Managed
            .Where(d => d.ShortcutKey != NumpadKey.None)
            .Select(d => d.ShortcutKey));
        taken.UnionWith(dynamic.UsedKeys());

        NumpadKey freeKey = DesktopConfig.AssignableKeys.FirstOrDefault(k => !taken.Contains(k), NumpadKey.None);
        if (freeKey == NumpadKey.None)
            return LauncherOpenResult.NoFreeKey;

        var (idx, id) = desktops.CreateDesktopTracked();
        string name = module == "" ? project : $"{project} / {module}";
        desktops.SetName(idx, name);

        dynamic.Register(id, project, module, freeKey);
        projects.AssignDeskSession(idx, project, module);
        deskIndex = idx;
        desktops.GoTo(idx);

        return LauncherOpenResult.Created;
    }

    /// <summary>
    /// Cierra el desk dinámico en <paramref name="deskIndex"/>: sus ventanas pasan al desk REFUGIO
    /// (rol Main) y se saca del registro. Los servicios que haya lanzado NO se matan — sólo se
    /// cierra el desk. false si ese índice no es (o ya no es) un desk dinámico vivo, o si el refugio
    /// no se pudo resolver (catálogo mal armado, sin ningún desk Main).
    /// </summary>
    public static bool Close(DesktopService desktops, DynamicDeskStore dynamic, int deskIndex)
    {
        var entry = dynamic.GetByIndex(deskIndex);
        if (entry is null) return false;

        int fallback = desktops.FindByNameFragment(DeskCatalog.FallbackDeskName);
        if (fallback < 0 || fallback == deskIndex) return false;

        var id = desktops.IdOf(deskIndex);
        desktops.RemoveDesktopAt(deskIndex, fallback);
        dynamic.Unregister(id);
        return true;
    }

    /// <summary>
    /// Cierra TODOS los escritorios dinámicos vivos (sus ventanas pasan al Main). Los dinámicos son
    /// TEMPORALES por diseño: viven lo que vive la app. Se usa al SALIR (App.OnExit) y al ARRANCAR,
    /// para barrer los que quedaron de una sesión que terminó mal (crash, kill, apagado) — sin eso,
    /// se acumularían escritorios huérfanos reteniendo teclas del numpad sesión tras sesión.
    /// De MAYOR a menor índice: cerrar uno corre hacia abajo el índice de los que están después, así
    /// que empezando por el último los que faltan siguen siendo válidos.
    /// </summary>
    /// <returns>Cuántos se cerraron (los que no se pudieron — p.ej. sin desk Main — quedan registrados).</returns>
    public static int CloseAll(DesktopService desktops, DynamicDeskStore dynamic)
    {
        int closed = 0;
        foreach (var (idx, _) in dynamic.LiveEntries().OrderByDescending(t => t.Index).ToList())
            if (Close(desktops, dynamic, idx))
                closed++;
        return closed;
    }
}
