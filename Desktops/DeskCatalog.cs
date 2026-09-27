using System;

namespace AmpzDesktopBooster.Desktops;

/// <summary>
/// Punto ÚNICO donde se pregunta "¿qué rol tiene el desk que se llama así?".
///
/// ⚠ POR QUÉ ES ESTÁTICO (no lo "arregles" enhebrando DesktopConfig por seis constructores).
/// La pregunta la hacen capas que no se conocen entre sí y que reciben el desk como un simple
/// string: <see cref="ProjectStore"/> (scope de variables/notas/servicios), <see cref="DeskPalette"/>
/// (color), <see cref="RestrictionStore"/> (qué se puede proteger), <see cref="WindowGovernor"/>
/// (a dónde rebotar), la barra (panel dual) y el router de hotkeys (setter de espacio). Antes cada
/// una tenía su propio <c>name.Contains("DESK +")</c> — seis copias de la misma regla, y renombrar
/// un desk las desincronizaba a todas de golpe y en silencio.
///
/// La inyección estática es el MISMO patrón que ya usa <c>Apps.Shell.Desktops</c>: App la setea una
/// vez en el arranque, antes de montar nada que la consulte.
///
/// Sin catálogo (o con un desk que no está gestionado — creado a mano desde Windows) cae al criterio
/// LEGADO por nombre, que es como se comportaba la app entera hasta acá: nunca devuelve basura.
/// </summary>
public static class DeskCatalog
{
    /// <summary>Lo inyecta App.OnStartup apenas carga la config, antes de la barra y los hooks.</summary>
    public static DesktopConfig? Config { get; set; }

    /// <summary>
    /// Lo inyecta App.OnStartup con <c>DynamicDeskStore.IsDynamicIndex</c>: dado el ÍNDICE actual de
    /// un desk, dice si es uno de los escritorios DINÁMICOS que crea el launcher (Win+NumpadEnter).
    ///
    /// ⚠ POR QUÉ POR ÍNDICE Y NO POR NOMBRE. Desde la reforma de escritorios dinámicos, "Espacio" ya
    /// no es una entrada fija del catálogo (ver DesktopConfig.DefaultManaged) — es un desk que el
    /// launcher crea con el nombre "Espacio" o "Espacio / Contexto" y registra en DynamicDeskStore
    /// por su GUID. El nombre es arbitrario (lo tipeó el usuario), así que no sirve para reconocerlo;
    /// el índice actual sí, resolviendo el GUID vivo en ese índice contra el registro.
    /// </summary>
    public static Func<int, bool>? DynamicIndexProbe { get; set; }

    /// <summary>
    /// Rol del desk. Con <paramref name="deskIdx"/> ≥ 0 se consulta PRIMERO si es un desk DINÁMICO
    /// (siempre Espacio) — el catálogo por nombre ya no tiene entradas de ese rol, así que sin este
    /// chequeo un desk creado por el launcher caería al legado y se comportaría como Fijo.
    /// Fallback legado si no está en el catálogo ni es dinámico.
    /// </summary>
    public static DeskRole RoleOf(string deskName, int deskIdx = -1)
    {
        if (deskIdx >= 0 && DynamicIndexProbe?.Invoke(deskIdx) == true) return DeskRole.Space;

        var entry = Config?.ByName(deskName);
        if (entry is not null) return entry.DeskRole;

        // Legado: así se decidía antes en cada capa. Un desk fuera del catálogo se sigue comportando
        // como siempre — la reforma no puede cambiarle el sentido a lo que no gestionamos. El
        // fragmento "DESK +" ya no lo genera nadie nuevo, pero se deja por compatibilidad con desks
        // que alguien haya renombrado a mano imitando el viejo esquema.
        if (deskName.Contains("DESK +", StringComparison.OrdinalIgnoreCase)) return DeskRole.Space;
        if (deskName.Contains("MAIN", StringComparison.OrdinalIgnoreCase))   return DeskRole.Main;
        return DeskRole.Fixed;
    }

    /// <summary>¿Es un desk de ESPACIO? (acepta espacio + contexto y usa scope propio).</summary>
    public static bool IsSpace(string deskName, int deskIdx = -1) => RoleOf(deskName, deskIdx) == DeskRole.Space;

    /// <summary>Color propio del desk en "#RRGGBB", o "" si no le pusieron uno (lo decide el rol).</summary>
    public static string ColorOf(string deskName) => Config?.ByName(deskName)?.Color ?? "";

    /// <summary>
    /// Nombre del desk REFUGIO (a donde el governor manda lo no permitido). Sin catálogo cae al
    /// literal "MAIN", que es a donde apuntaba el código antes de esta reforma.
    /// </summary>
    public static string FallbackDeskName
    {
        get
        {
            string name = Config?.FallbackDeskName ?? "";
            return name == "" ? "MAIN" : name;
        }
    }
}
