using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using AmpzDesktopBooster.Persistence;

namespace AmpzDesktopBooster.Services.Tasks;

/// <summary>
/// Config del subsistema de tareas: una LISTA de cuentas (cada una con su Kind + credenciales).
/// Persiste en %APPDATA%\AmpzDesktopBooster\tasks.json — mismo patrón que UsageSettings.
///
/// Por qué LISTA y no "el provider activo": un consultor trabaja con varios gestores a la vez
/// (dos Trellos distintos, un JIRA, un Vikunja). El fetch las pide TODAS en paralelo y agrega.
/// Apagar una cuenta = Enabled=false (sin perder credenciales).
///
/// NOTA de seguridad: los tokens van en TEXTO PLANO (decisión del proyecto: consistencia con el
/// resto de las configs, que también son JSON plano). Si algún día el perfil se sincroniza o se
/// comparte, conviene cifrarlos con DPAPI (System.Security.Cryptography.ProtectedData); ese cambio
/// quedaría aislado en el get/set del Token de cada bloque.
///
/// Migración: el formato viejo era { Provider:"vikunja", Vikunja:{...}, Jira:{...}, Trello:{...} }
/// con UN solo provider activo. Si al cargar veo Accounts vacío y Provider != "none", migro el
/// bloque viejo a una sola TaskAccount. Los campos legacy quedan deserializables (sin uso runtime)
/// para no romper a quien tenga el JSON viejo.
/// </summary>
public sealed class TasksSettings
{
    /// <summary>Cuentas configuradas. El fetch usa las que tengan Enabled=true.</summary>
    public List<TaskAccount> Accounts { get; set; } = new();

    // ---- Campos LEGACY (formato viejo, single-provider) ----
    // Se conservan para deserializar archivos viejos y migrarlos. NO se usan en runtime tras Load.
    // Los seguimos serializando con su nombre original para no romper rollback a una versión vieja.
    public string Provider { get; set; } = "none";
    public VikunjaSettings Vikunja { get; set; } = new();
    public JiraSettings Jira { get; set; } = new();
    public TrelloSettings Trello { get; set; } = new();

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
                if (loaded is not null)
                {
                    loaded.MigrateLegacyIfNeeded();
                    return loaded;
                }
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

    /// <summary>
    /// Si el JSON viene del formato viejo (Provider != "none" y Accounts vacío), arma UNA TaskAccount
    /// equivalente y la mete en Accounts. Idempotente: si ya hay cuentas, no hace nada.
    /// </summary>
    private void MigrateLegacyIfNeeded()
    {
        if (Accounts.Count > 0) return;
        if (string.IsNullOrEmpty(Provider) || Provider == "none") return;

        var acct = new TaskAccount
        {
            Kind = Provider,
            DisplayName = Provider switch
            {
                "vikunja" => "Vikunja",
                "jira"    => "JIRA",
                "trello"  => "Trello",
                _         => Provider,
            },
            Enabled = true,
        };
        switch (Provider)
        {
            case "vikunja": acct.Vikunja = Vikunja; break;
            case "jira":    acct.Jira    = Jira;    break;
            case "trello":  acct.Trello  = Trello;  break;
        }
        Accounts.Add(acct);
        // Limpiamos el legacy para no duplicar credenciales en el próximo Save.
        Provider = "none";
        Vikunja = new();
        Jira = new();
        Trello = new();
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

/// <summary>
/// Credenciales de Trello. La API REST usa autenticación por query-string: ApiKey + Token.
/// - ApiKey: se obtiene creando un Power-Up en https://trello.com/power-ups/admin (campo "API key").
/// - Token: se genera a partir de la API key (link "Token" al lado de la key) y autoriza al usuario.
/// No hace falta username: el endpoint GET /1/members/me/cards usa el miembro dueño del token, así que
/// "lo mío" ya queda filtrado por la propia credencial.
///
/// IgnoredListsRaw: tokens EXTRA (uno por línea o separados por coma) que el usuario quiere filtrar
/// además de los defaults hardcoded. Se ADITIONAN a la heurística (no la reemplazan) — los defaults
/// cubren done/cancelled/completado/etc., acá ponés cosas como "Hecho", "Validado", "En review",
/// lo que use tu kanban. Match por Contains case-insensitive sobre el nombre de la lista.
/// </summary>
public sealed class TrelloSettings
{
    public string ApiKey { get; set; } = "";
    public string Token { get; set; } = "";
    public string IgnoredListsRaw { get; set; } = "";

    /// <summary>
    /// Tokenes parseados de IgnoredListsRaw: splitea por coma O salto de línea, trimea, descarta
    /// vacíos. Cero alocs si está vacío — devuelve array vacío.
    /// </summary>
    public string[] GetIgnoredTokens()
    {
        if (string.IsNullOrWhiteSpace(IgnoredListsRaw)) return System.Array.Empty<string>();
        var parts = IgnoredListsRaw.Split(new[] { ',', '\n', '\r' },
            System.StringSplitOptions.RemoveEmptyEntries | System.StringSplitOptions.TrimEntries);
        return parts;
    }
}
