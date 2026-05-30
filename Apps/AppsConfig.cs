using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using AmpzDesktopBooster.Persistence;

namespace AmpzDesktopBooster.Apps;

/// <summary>Una app definida por el usuario en la pestaña "Aplicaciones".</summary>
public sealed class UserApp
{
    [JsonPropertyName("name")]    public string Name { get; set; } = "";
    [JsonPropertyName("exePath")] public string ExePath { get; set; } = "";

    /// <summary>
    /// Argumentos. Si contiene "{path}", se reemplaza por cada target; si no, se pasa el path
    /// como argumento entre comillas. Ej.: "--folder \"{path}\"" o vacío.
    /// </summary>
    [JsonPropertyName("args")] public string Args { get; set; } = "";
}

/// <summary>
/// Apps definidas por el usuario para "Abrir con" — complementan a las auto-detectadas.
/// Persisten en %APPDATA%\AmpzDesktopBooster\apps.json y se editan en la pestaña "Aplicaciones".
/// </summary>
public sealed class AppsConfig
{
    [JsonPropertyName("apps")] public List<UserApp> Apps { get; set; } = new();

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
    private static string Path => System.IO.Path.Combine(AppPaths.DataDir, "apps.json");

    public static AppsConfig Load()
    {
        try
        {
            if (File.Exists(Path))
            {
                var loaded = JsonSerializer.Deserialize<AppsConfig>(File.ReadAllText(Path));
                if (loaded is not null) return loaded;
            }
        }
        catch { /* corrupto → vacío */ }
        return new AppsConfig();
    }

    public void Save()
    {
        try { File.WriteAllText(Path, JsonSerializer.Serialize(this, JsonOpts)); }
        catch { /* disco/permisos → en memoria */ }
    }
}
