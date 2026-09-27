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
    /// Tras crear, asienta la sesión (espacio+contexto del desk YA es éste, no el de origen), salta
    /// ahí y dispara el auto-arranque de servicios del scope (misma herencia contexto→espacio→global
    /// que "levantar lo básico" de la ventana de Servicios, sin abrir ninguna ventana).
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

        LaunchAutoStartServices(projects, name, idx);

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
    /// Dispara "levantar lo básico" del scope recién creado, con la MISMA herencia de tres niveles
    /// (contexto → espacio → global) y la misma regla de "la global no se mete si estás en un
    /// espacio/contexto" que <c>ServicesWindow.LaunchMissing</c> — ver el comentario de esa clase.
    /// Sin ventana: el launcher no abre Servicios, sólo dispara el efecto.
    /// </summary>
    private static void LaunchAutoStartServices(ProjectStore projects, string deskName, int deskIdx)
    {
        var pool = projects.ResolveServicePoolWithGlobal(deskName, deskIdx, out var globalPool, out var parentPool);
        var scoped = globalPool is not null
            ? new ServicePool?[] { pool, parentPool }
            : new ServicePool?[] { pool, parentPool, globalPool };
        ServiceLauncher.LaunchGroupMissing(scoped);
    }
}
