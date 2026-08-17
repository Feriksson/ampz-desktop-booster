using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using AmpzDesktopBooster.Hotkeys;
using AmpzDesktopBooster.Persistence;

namespace AmpzDesktopBooster.Desktops;

/// <summary>
/// Qué escritorios virtuales gestiona la app, en orden (el orden = índice del desktop).
/// Se persiste en %APPDATA%\AmpzDesktopBooster\desktops.json y se edita desde la pestaña
/// ESCRITORIOS de la ventana de configuración — que es la PRIMERA a propósito: es el cimiento del
/// que cuelga todo lo demás (espacios, atajos, protecciones, colores).
///
/// Cada entrada es un <see cref="ManagedDesktop"/> con identidad propia (nombre + tecla + rol + color).
/// Antes era un `List&lt;string&gt;` y el nombre hacía de identificador, rol y etiqueta al mismo tiempo:
/// renombrar rompía el atajo, el scope de espacios y el color, todo en silencio. Ver ManagedDesktop.
///
/// Defaults = el set del legacy con el rename posterior: MAIN, CONSOLES (ex-MAILS), MISCS y DESK +1..+6,
/// con el mapeo de numpad histórico (fila inferior 1-2-3 = los fijos, 4..9 = los de espacio).
/// </summary>
public sealed class DesktopConfig
{
    [JsonPropertyName("managed")]
    public List<ManagedDesktop> Managed { get; set; } = DefaultManaged();

    /// <summary>Si true, al arrancar se crean/renombran los escritorios faltantes.</summary>
    [JsonPropertyName("autoCreate")]
    public bool AutoCreate { get; set; } = true;

    public static List<ManagedDesktop> DefaultManaged() => new()
    {
        new() { Name = "MAIN",     Key = "D1", Role = "main"  },
        new() { Name = "CONSOLES", Key = "D2", Role = "fixed" },
        new() { Name = "MISCS",    Key = "D3", Role = "fixed" },
        new() { Name = "DESK +1",  Key = "D4", Role = "space" },
        new() { Name = "DESK +2",  Key = "D5", Role = "space" },
        new() { Name = "DESK +3",  Key = "D6", Role = "space" },
        new() { Name = "DESK +4",  Key = "D7", Role = "space" },
        new() { Name = "DESK +5",  Key = "D8", Role = "space" },
        new() { Name = "DESK +6",  Key = "D9", Role = "space" },
    };

    /// <summary>Teclas ofrecibles como atajo de navegación, en el orden del numpad físico.</summary>
    /// <remarks>
    /// Sólo los dígitos: el resto del numpad ya tiene dueño fijo en el router (Enter = setter de
    /// espacio, Dot = contexto, * = variables, / = notas, + = servicios, − = enviar ventana, 0 = tareas).
    /// Ofrecerlas acá dejaría al usuario pisar un atajo del sistema sin darse cuenta.
    /// </remarks>
    public static readonly NumpadKey[] AssignableKeys =
    {
        NumpadKey.D1, NumpadKey.D2, NumpadKey.D3,
        NumpadKey.D4, NumpadKey.D5, NumpadKey.D6,
        NumpadKey.D7, NumpadKey.D8, NumpadKey.D9,
    };

    // ── Consultas ───────────────────────────────────────────────────────────────

    /// <summary>Entrada dueña de esa tecla, o null si nadie la tiene asignada.</summary>
    public ManagedDesktop? ByKey(NumpadKey key) =>
        key == NumpadKey.None ? null : Managed.FirstOrDefault(d => d.ShortcutKey == key);

    /// <summary>Entrada con ese nombre EXACTO (case-insensitive), o null si no está gestionado.</summary>
    public ManagedDesktop? ByName(string name) =>
        name == "" ? null : Managed.FirstOrDefault(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Índice de la entrada en el catálogo (= índice del desktop real), o -1.</summary>
    public int IndexOfName(string name) =>
        Managed.FindIndex(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// El desk REFUGIO: el de rol Main, o el primero de la lista si nadie lo tiene (config a mano
    /// mal armada). "" sólo si el catálogo está vacío. Ver <see cref="DeskRole.Main"/>.
    /// </summary>
    [JsonIgnore] // es DERIVADA del rol: persistirla sería una segunda fuente de verdad del refugio
    public string FallbackDeskName =>
        (Managed.FirstOrDefault(d => d.DeskRole == DeskRole.Main) ?? Managed.FirstOrDefault())?.Name ?? "";

    /// <summary>
    /// Le saca la tecla a cualquier OTRA entrada que la tuviera: una tecla tiene un solo destino.
    /// Sin esto, dos desks con Numpad4 harían que el atajo salte al primero de la lista y el segundo
    /// pareciera roto — exactamente la clase de fallo mudo que esta reforma vino a matar.
    /// </summary>
    public void ClaimKey(ManagedDesktop owner, NumpadKey key)
    {
        owner.ShortcutKey = key;
        if (key == NumpadKey.None) return;
        foreach (var other in Managed)
            if (!ReferenceEquals(other, owner) && other.ShortcutKey == key)
                other.Key = "";
    }

    /// <summary>
    /// Deja UN solo desk con rol Main (es el refugio: dos refugios no significan nada). El elegido
    /// se queda; el resto pasa a Fixed.
    /// </summary>
    public void ClaimMain(ManagedDesktop owner)
    {
        foreach (var other in Managed)
            if (!ReferenceEquals(other, owner) && other.DeskRole == DeskRole.Main)
                other.DeskRole = DeskRole.Fixed;
    }

    // ── Persistencia ────────────────────────────────────────────────────────────

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
    private static string Path => System.IO.Path.Combine(AppPaths.DataDir, "desktops.json");

    public static DesktopConfig Load()
    {
        try
        {
            if (File.Exists(Path))
            {
                var loaded = Parse(File.ReadAllText(Path), out bool migrated);
                if (loaded is not null && loaded.Managed.Count > 0)
                {
                    // Persistimos la migración YA, no en el próximo "Guardar": mientras el archivo
                    // siga en formato viejo, cada arranque vuelve a adivinar atajos y roles. Con una
                    // escritura, lo adivinado pasa a ser un dato del usuario que él puede corregir.
                    if (migrated) loaded.Save();
                    return loaded;
                }
            }
        }
        catch { /* corrupto → defaults */ }
        return new DesktopConfig();
    }

    /// <summary>
    /// Parsea el JSON tolerando el formato VIEJO, donde "managed" era un array de strings pelados.
    /// Deserializar eso directo a List&lt;ManagedDesktop&gt; tira excepción → el usuario perdería su
    /// catálogo entero (nombres renombrados incluidos) y arrancaría con los defaults. Por eso miramos
    /// el shape del primer elemento antes de decidir.
    /// </summary>
    private static DesktopConfig? Parse(string json, out bool migrated)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        bool legacy = root.TryGetProperty("managed", out var managed)
                   && managed.ValueKind == JsonValueKind.Array
                   && managed.EnumerateArray().FirstOrDefault().ValueKind == JsonValueKind.String;
        migrated = legacy;

        if (!legacy)
            return JsonSerializer.Deserialize<DesktopConfig>(json);

        var cfg = new DesktopConfig { Managed = new List<ManagedDesktop>() };
        if (root.TryGetProperty("autoCreate", out var auto) && auto.ValueKind is JsonValueKind.True or JsonValueKind.False)
            cfg.AutoCreate = auto.GetBoolean();

        foreach (var el in managed.EnumerateArray())
        {
            string name = el.GetString() ?? "";
            if (name == "") continue;

            // El ROL sale del NOMBRE, que es exactamente lo que la app venía haciendo en runtime en
            // cada capa. El comportamiento post-migración queda IDÉNTICO al de antes: no adivinamos
            // nada nuevo. Si renombraste un "DESK +N" a algo propio, migra como Fijo — que es como se
            // venía comportando DE VERDAD (sin setter de espacio ni scope), no como creías. Se
            // corrige de un click en la config, que para eso ahora el rol es visible.
            var entry = new ManagedDesktop { Name = name };
            entry.DeskRole = name.Contains("DESK +", StringComparison.OrdinalIgnoreCase) ? DeskRole.Space
                           : name.Contains("MAIN", StringComparison.OrdinalIgnoreCase)   ? DeskRole.Main
                           : DeskRole.Fixed;
            cfg.Managed.Add(entry);
        }

        if (cfg.Managed.Count == 0) return null;
        AssignLegacyKeys(cfg.Managed);
        return cfg;
    }

    /// <summary>
    /// Mapa histórico tecla → fragmento de nombre: el switch hardcodeado que vivía en HotkeyRouter y
    /// que originó todo este lío. Sobrevive acá y SÓLO acá, como tabla de migración por única vez.
    /// </summary>
    private static readonly (NumpadKey Key, string Fragment)[] LegacyKeyMap =
    {
        (NumpadKey.D1, "MAIN"), (NumpadKey.D2, "CONSOLES"), (NumpadKey.D3, "MISCS"),
        (NumpadKey.D4, "DESK +1"), (NumpadKey.D5, "DESK +2"), (NumpadKey.D6, "DESK +3"),
        (NumpadKey.D7, "DESK +4"), (NumpadKey.D8, "DESK +5"), (NumpadKey.D9, "DESK +6"),
    };

    /// <summary>
    /// Reparte los atajos al migrar, PRESERVANDO la memoria muscular. Dos pasadas:
    ///
    ///  1. Por NOMBRE, con el mapa histórico. Cada desk que todavía se llame como antes se queda con
    ///     EXACTAMENTE la tecla que venías usando. Repartir por posición hubiera sido más simple, pero
    ///     a quien reordenó sus escritorios le cambiaría de golpe todos los atajos que hoy le andan
    ///     bien — arreglaríamos un atajo roto rompiendo ocho sanos. Eso no es una migración, es una
    ///     mudanza forzada.
    ///  2. Los que quedaron sin tecla (renombrados, o agregados por el usuario) toman, en orden, la
    ///     primera tecla LIBRE. Justamente un desk renombrado dejó su tecla vieja huérfana, así que
    ///     esta pasada le devuelve un atajo solo — que era el síntoma que destapó todo esto.
    /// </summary>
    private static void AssignLegacyKeys(List<ManagedDesktop> managed)
    {
        foreach (var (key, fragment) in LegacyKeyMap)
        {
            var match = managed.FirstOrDefault(d => d.ShortcutKey == NumpadKey.None &&
                d.Name.Contains(fragment, StringComparison.OrdinalIgnoreCase));
            if (match is not null) match.ShortcutKey = key;
        }

        var free = new Queue<NumpadKey>(AssignableKeys.Where(k => managed.All(d => d.ShortcutKey != k)));
        foreach (var d in managed)
        {
            if (d.ShortcutKey != NumpadKey.None || free.Count == 0) continue;
            d.ShortcutKey = free.Dequeue();
        }
    }

    public void Save()
    {
        try { File.WriteAllText(Path, JsonSerializer.Serialize(this, JsonOpts)); }
        catch { /* disco/permisos → seguimos en memoria */ }
    }
}
