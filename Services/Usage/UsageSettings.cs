using System.IO;
using System.Text.Json;
using AmpzDesktopBooster.Persistence;

namespace AmpzDesktopBooster.Services.Usage;

/// <summary>
/// Config del panel de uso: qué provider mostrar, override de plan, y cada cuánto refrescar.
/// Persiste en %APPDATA%\AmpzDesktopBooster\usage.json — mismo patrón que WidgetSettings.
///
/// El plan se auto-detecta del provider (la credencial ya lo trae); PlanOverride es el escape
/// para fijarlo a mano o para providers que no lo informen. Provider queda preparado para el
/// día que haya más de uno: hoy el único valor real es "claude".
/// </summary>
public sealed class UsageSettings
{
    /// <summary>Id del provider activo. Hoy: "claude".</summary>
    public string Provider { get; set; } = "claude";

    /// <summary>Override del plan (vacío = auto-detectar desde el provider).</summary>
    public string PlanOverride { get; set; } = "";

    /// <summary>Cada cuántos segundos refrescar el uso. El endpoint tiene rate limit (429 si abusás),
    /// y el uso no cambia tan rápido → 3 minutos es de sobra y no nos cortan.</summary>
    public int RefreshSeconds { get; set; } = 180;

    // ---- Persistencia (idéntico patrón a WidgetSettings) ----

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private static string SettingsPath => Path.Combine(AppPaths.DataDir, "usage.json");

    public static UsageSettings Load()
    {
        try
        {
            // Lectura a prueba de archivo roto (.bak / aparta el dañado) — ver SafeFile.
            var loaded = SafeFile.LoadJson(SettingsPath, json => JsonSerializer.Deserialize<UsageSettings>(json));
            if (loaded is not null) return loaded;
        }
        catch
        {
            // archivo corrupto o ilegible → defaults, no crasheamos
        }
        return new UsageSettings();
    }

    public void Save()
    {
        try
        {
            SafeFile.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, JsonOpts));
        }
        catch
        {
            // sin permisos / disco lleno → seguimos en memoria
        }
    }
}
