using System.IO;
using System.Text.Json;
using AmpzDesktopBooster.Apps;
using AmpzDesktopBooster.Persistence;

namespace AmpzDesktopBooster.Services.Browser;

/// <summary>
/// Ajustes del "shim de navegador". Se PERSISTE en %APPDATA%\AmpzDesktopBooster\browser.json.
///
/// QUÉ resuelve la feature: cuando hacés click en un link desde cualquier app, Windows se lo pasa al
/// navegador YA abierto, que reusa su ventana existente (en OTRO escritorio virtual) y la trae al
/// frente → Windows te catapulta a ESE escritorio. Es comportamiento NATIVO del SO. El legacy AHK lo
/// resolvía con un "browser shim" (ver <see cref="Desktops.PathOpener"/>, nota de la "Fase 5"): la app
/// se mete en el medio como navegador y reenvía la URL al navegador real con --new-window → la ventana
/// nace en el desk ACTUAL, sin catapulteo.
///
/// Mismo patrón de configs del repo: Load() con try/catch → defaults si corrupto; Save() silencioso
/// → si el disco falla, seguimos en memoria. La persistencia NUNCA voltea la app.
/// </summary>
public sealed class BrowserSettings
{
    /// <summary>
    /// El shim está activo. Off = la app NO se registra como navegador candidato y, si está
    /// elegida en Windows, conviene que el usuario vuelva a su navegador real (lo avisa la UI).
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Ruta al navegador REAL al que reenviamos las URLs. Si está vacía, se autodetecta Brave en cada
    /// uso (ver <see cref="BrowserShim.ResolveBrowserPath"/>). El usuario puede fijar otro a mano.
    /// </summary>
    public string RealBrowserPath { get; set; } = "";

    // ── Persistencia ──

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private static string SettingsPath => Path.Combine(AppPaths.DataDir, "browser.json");

    public static BrowserSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var loaded = JsonSerializer.Deserialize<BrowserSettings>(File.ReadAllText(SettingsPath));
                if (loaded is not null) return loaded;
            }
        }
        catch { /* corrupto o ilegible → defaults */ }
        return new BrowserSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDir);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, JsonOpts));
        }
        catch { /* permisos/disco → seguimos en memoria */ }
    }

    /// <summary>
    /// El navegador real que usaríamos AHORA: el path fijado por el usuario si existe en disco, o el
    /// autodetectado. Conveniencia para la UI (mostrar qué navegador se va a usar).
    /// </summary>
    public string EffectiveBrowserPath()
    {
        if (!string.IsNullOrWhiteSpace(RealBrowserPath) && File.Exists(RealBrowserPath))
            return RealBrowserPath;
        return BrowserShim.ResolveBrowserPath(null) ?? "";
    }
}
