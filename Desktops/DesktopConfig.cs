using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using AmpzDesktopBooster.Persistence;

namespace AmpzDesktopBooster.Desktops;

/// <summary>
/// Qué escritorios virtuales gestiona la app, en orden (el orden = índice del desktop).
/// Se persiste en %APPDATA%\AmpzDesktopBooster\desktops.json y se edita desde la pestaña
/// DESKTOPS de la ventana de configuración.
///
/// Defaults = el set del legacy con el rename posterior: MAIN, CONSOLES (ex-MAILS), MISCS y DESK +1..+6.
/// </summary>
public sealed class DesktopConfig
{
    [JsonPropertyName("managed")]
    public List<string> Managed { get; set; } = DefaultManaged();

    /// <summary>Si true, al arrancar se crean/renombran los escritorios faltantes.</summary>
    [JsonPropertyName("autoCreate")]
    public bool AutoCreate { get; set; } = true;

    public static List<string> DefaultManaged() => new()
    {
        "MAIN", "CONSOLES", "MISCS",
        "DESK +1", "DESK +2", "DESK +3", "DESK +4", "DESK +5", "DESK +6",
    };

    // ── Persistencia ────────────────────────────────────────────────────────────

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
    private static string Path => System.IO.Path.Combine(AppPaths.DataDir, "desktops.json");

    public static DesktopConfig Load()
    {
        try
        {
            if (File.Exists(Path))
            {
                var loaded = JsonSerializer.Deserialize<DesktopConfig>(File.ReadAllText(Path));
                if (loaded is not null && loaded.Managed.Count > 0)
                    return loaded;
            }
        }
        catch { /* corrupto → defaults */ }
        return new DesktopConfig();
    }

    public void Save()
    {
        try { File.WriteAllText(Path, JsonSerializer.Serialize(this, JsonOpts)); }
        catch { /* disco/permisos → seguimos en memoria */ }
    }
}
