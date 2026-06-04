using System.IO;
using System.Text.Json;
using AmpzDesktopBooster.Persistence;

namespace AmpzDesktopBooster.Services.Tasks;

/// <summary>
/// Config del proveedor de tareas: cuál está activo y las credenciales de cada uno.
/// Persiste en %APPDATA%\AmpzDesktopBooster\tasks.json — mismo patrón que UsageSettings.
///
/// Guardamos los bloques de Vikunja Y JIRA aunque solo uno esté activo, para poder cambiar de
/// proveedor sin perder lo configurado. Provider="none" = integración apagada.
///
/// NOTA de seguridad: los tokens van en TEXTO PLANO (decisión del proyecto: consistencia con el
/// resto de las configs, que también son JSON plano). Si algún día el perfil se sincroniza o se
/// comparte, conviene cifrarlos con DPAPI (System.Security.Cryptography.ProtectedData); ese cambio
/// quedaría aislado acá, en el get/set del Token de cada bloque.
/// </summary>
public sealed class TasksSettings
{
    /// <summary>Id del provider activo: "none" (default), "vikunja" o "jira".</summary>
    public string Provider { get; set; } = "none";

    public VikunjaSettings Vikunja { get; set; } = new();
    public JiraSettings Jira { get; set; } = new();

    // ---- Persistencia (idéntico patrón a UsageSettings) ----

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private static string SettingsPath => Path.Combine(AppPaths.DataDir, "tasks.json");

    public static TasksSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var loaded = JsonSerializer.Deserialize<TasksSettings>(File.ReadAllText(SettingsPath));
                if (loaded is not null) return loaded;
            }
        }
        catch
        {
            // archivo corrupto o ilegible → defaults, no crasheamos
        }
        return new TasksSettings();
    }

    public void Save()
    {
        try
        {
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, JsonOpts));
        }
        catch
        {
            // sin permisos / disco lleno → seguimos en memoria
        }
    }
}

/// <summary>
/// Credenciales de Vikunja. BaseUrl sin slash final (ej. https://projects.blaster.com.ar).
/// Username es obligatorio para filtrar "lo mío": el API token tk_ no puede leer /api/v1/user, así
/// que no podemos auto-detectarlo → lo setea el usuario. Token = el API token (tk_...).
/// </summary>
public sealed class VikunjaSettings
{
    public string BaseUrl { get; set; } = "";
    public string Username { get; set; } = "";
    public string Token { get; set; } = "";
}

/// <summary>
/// Credenciales de JIRA. Cloud: BaseUrl (https://tuorg.atlassian.net) + Email + Token (API token,
/// auth Basic email:token). Server/DC usaría un PAT; se afina cuando se implemente el adapter real.
/// </summary>
public sealed class JiraSettings
{
    public string BaseUrl { get; set; } = "";
    public string Email { get; set; } = "";
    public string Token { get; set; } = "";
}
