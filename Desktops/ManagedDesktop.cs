using System;
using System.Text.Json.Serialization;
using AmpzDesktopBooster.Hotkeys;

namespace AmpzDesktopBooster.Desktops;

/// <summary>
/// Qué ES un escritorio para la app, más allá de cómo se llama hoy.
///
/// ⚠ POR QUÉ EXISTE ESTE TIPO (no lo vuelvas a un string pelado).
/// Antes el catálogo era `List&lt;string&gt;` y el NOMBRE hacía de tres cosas a la vez: identificador,
/// ROL y etiqueta visible. Consecuencia: renombrar un desk desde la config lo rompía todo en silencio
/// — el atajo del numpad dejaba de funcionar (el mapa de teclas era un switch con literales
/// hardcodeados: "MAIN", "DESK +1"…), el setter de Espacios dejaba de abrir (pedía que el nombre
/// CONTUVIERA "DESK +"), el color se caía al blanco de fallback y el desk dejaba de ser protegible.
/// Ninguno de esos efectos era visible al renombrar: la app seguía andando, sólo que sorda.
///
/// La cura: la entrada del catálogo tiene IDENTIDAD PROPIA. El nombre pasa a ser SÓLO la etiqueta;
/// la tecla y el rol viven acá y sobreviven a cualquier renombre.
/// </summary>
public sealed class ManagedDesktop
{
    /// <summary>Etiqueta visible del escritorio. Es lo ÚNICO que cambia al renombrar.</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    /// <summary>
    /// Tecla del numpad que salta a este desk, serializada por el nombre del enum ("D1".."D9").
    /// Vacío = el desk no tiene atajo (existe, se ve en la barra y en el picker, pero no se navega
    /// con una tecla). Se guarda el NOMBRE del enum y no el scancode: el scancode es un detalle del
    /// decoder y cambiarlo no debería invalidar la config del usuario.
    /// </summary>
    [JsonPropertyName("key")]
    public string Key { get; set; } = "";

    /// <summary>Rol serializado: "main" | "space" | "fixed". Ver <see cref="DeskRole"/>.</summary>
    [JsonPropertyName("role")]
    public string Role { get; set; } = "fixed";

    /// <summary>
    /// Color propio en "#RRGGBB". Vacío = lo decide el rol (ver <see cref="DeskPalette"/>). Existe
    /// porque antes el color se deducía del nombre ("MAIN" → verde) y renombrar te lo apagaba.
    /// </summary>
    [JsonPropertyName("color")]
    public string Color { get; set; } = "";

    // ── Vistas tipadas sobre los campos serializados ────────────────────────────

    [JsonIgnore]
    public NumpadKey ShortcutKey
    {
        get => Enum.TryParse<NumpadKey>(Key, ignoreCase: true, out var k) ? k : NumpadKey.None;
        set => Key = value == NumpadKey.None ? "" : value.ToString();
    }

    [JsonIgnore]
    public DeskRole DeskRole
    {
        get => Role.ToLowerInvariant() switch
        {
            "main"  => DeskRole.Main,
            "space" => DeskRole.Space,
            _       => DeskRole.Fixed,
        };
        set => Role = value switch
        {
            DeskRole.Main  => "main",
            DeskRole.Space => "space",
            _              => "fixed",
        };
    }

    public ManagedDesktop Clone() => new() { Name = Name, Key = Key, Role = Role, Color = Color };
}

/// <summary>
/// El ROL de un escritorio: qué puede hacer, independiente de cómo se llame.
///
/// Antes esto se INFERÍA del nombre en seis lugares distintos (cada uno con su propio
/// <c>name.Contains("DESK +")</c>), lo que hacía que un renombre cambiara el comportamiento de la
/// app sin que nada lo avisara. Ahora es un dato explícito del catálogo, editable desde la config.
/// </summary>
public enum DeskRole
{
    /// <summary>
    /// El desk REFUGIO. Hay uno solo: es a donde el <see cref="WindowGovernor"/> manda las ventanas
    /// que no están permitidas en un desk protegido. Por eso NO puede protegerse a sí mismo (si no,
    /// una ventana rebotaría para siempre). Históricamente era el literal "MAIN".
    /// </summary>
    Main,

    /// <summary>
    /// Desk de ESPACIO: acepta espacio + contexto (Win+NumpadEnter / Win+NumpadDot), usa scope propio
    /// de variables, notas y servicios, y la barra le reserva el panel dual. Antes = "DESK +N".
    /// No se protege: el que rota de espacio necesita traer cualquier app.
    /// </summary>
    Space,

    /// <summary>
    /// Desk FIJO de propósito único (antes: CONSOLES, MISCS). No toma espacio — sus variables y notas
    /// son las globales — y ES protegible con whitelist. Es el rol por defecto de un desk nuevo.
    /// </summary>
    Fixed,
}
