using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AmpzDesktopBooster.Services.Tasks;

/// <summary>
/// Adapter REST de ClickUp (API v2). Hace fetch REAL, como Trello y Vikunja (JIRA sigue stub).
///
/// Detalles del contrato de ClickUp que NO son obvios y explican la forma de este adapter:
/// - Auth: header `Authorization: pk_...` CRUDO — sin "Bearer". Por eso va con
///   TryAddWithoutValidation: HttpClient rechaza un valor de Authorization sin esquema.
/// - GET /v2/user devuelve QUIÉN sos. Hace falta SÍ o SÍ: el endpoint de tareas filtra por
///   `assignees[]={userId}` y no existe un "currentUser()" como el JQL de JIRA. A diferencia de
///   Vikunja (donde el username lo tipea el usuario porque el token no puede leer /user), acá el
///   token personal SÍ puede → lo auto-detectamos y le ahorramos un campo al usuario.
/// - Las tareas se piden POR WORKSPACE (team), no globalmente: GET /v2/team/{id}/task. Si el usuario
///   no fijó uno, listamos sus workspaces con GET /v2/team y los recorremos TODOS en paralelo —
///   mismo criterio que Trello, que trae las cards de todos los tableros.
/// - PAGINACIÓN: la respuesta trae `last_page` (bool) y páginas de 100. Vikunja y Trello devuelven
///   todo de una; ClickUp no. Paginamos hasta MaxPages para no colgar el picker si alguien tiene
///   miles de tareas asignadas — 500 tareas abiertas ya es una lista que nadie va a scrollear.
///
/// FILTRADO DE "DONE": acá NO hace falta heurística por nombre como en Trello. ClickUp tipa el
/// estado (`status.type`): "open" | "custom" | "closed" | "done". Descartamos closed/done, que es la
/// señal NATIVA, y `include_closed=false` ya lo filtra server-side (el chequeo local es la red por
/// si el workspace tiene un tipo raro). IgnoredStatusesRaw suma los estados propios que el usuario
/// considere terminales (ej. "En review", "Bloqueado") — mismo espíritu que las listas de Trello.
/// </summary>
public sealed class ClickUpTaskProvider : ITaskProvider
{
    public string Id => "clickup";
    public string DisplayName => "ClickUp";

    private readonly TaskAccount _account;
    private readonly ClickUpSettings _settings;

    // Un solo HttpClient para toda la vida del proceso (uno por request agota los sockets) —
    // mismo criterio que Vikunja y Trello.
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    private const string ApiBase = "https://api.clickup.com/api/v2";

    /// <summary>Tope de páginas por workspace (100 tareas c/u). Ver nota de paginación arriba.</summary>
    private const int MaxPages = 5;

    public ClickUpTaskProvider(TaskAccount account, ClickUpSettings settings)
    {
        _account = account;
        _settings = settings;
    }

    public async Task<TaskFetchResult> GetOpenTasksAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_settings.Token))
            return TaskFetchResult.Failed(Id, "Falta el API token de ClickUp (pk_...).");

        try
        {
            // 1) Quién soy. Sin el userId no podemos filtrar "lo mío" — si esto falla, abortamos.
            var me = await FetchJsonAsync($"{ApiBase}/user", ct).ConfigureAwait(false);
            if (me.Error != null) return TaskFetchResult.Failed(Id, me.Error);

            string userId = ParseUserId(me.Json!);
            if (string.IsNullOrEmpty(userId))
                return TaskFetchResult.Failed(Id, "No pude identificar tu usuario de ClickUp (respuesta inesperada de /user).");

            // 2) En qué workspaces buscar. Fijado a mano > todos los del usuario.
            IReadOnlyList<string> teamIds;
            if (!string.IsNullOrWhiteSpace(_settings.WorkspaceId))
            {
                teamIds = new[] { _settings.WorkspaceId.Trim() };
            }
            else
            {
                var teams = await FetchJsonAsync($"{ApiBase}/team", ct).ConfigureAwait(false);
                if (teams.Error != null) return TaskFetchResult.Failed(Id, teams.Error);
                teamIds = ParseTeamIds(teams.Json!);
                if (teamIds.Count == 0)
                    return TaskFetchResult.Failed(Id, "El token no tiene acceso a ningún workspace de ClickUp.");
            }

            // 3) Tareas de cada workspace EN PARALELO: la latencia total es la del más lento, no la suma.
            var perTeam = await Task.WhenAll(
                teamIds.Select(t => FetchTeamTasksAsync(t, userId, ct))).ConfigureAwait(false);

            // Un workspace que falle NO tumba al resto — mismo criterio que TasksService con las
            // cuentas. Sólo reportamos error si fallaron TODOS y no trajimos nada.
            var items = new List<TaskItem>();
            string? firstError = null;
            foreach (var (list, error) in perTeam)
            {
                if (error != null) { firstError ??= error; continue; }
                items.AddRange(list);
            }
            if (items.Count == 0 && firstError != null)
                return TaskFetchResult.Failed(Id, firstError);

            return TaskFetchResult.Success(Id, items);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return TaskFetchResult.Failed(Id, "No pude conectar: " + ex.Message);
        }
    }

    /// <summary>
    /// Trae TODAS las páginas de tareas abiertas asignadas al usuario en un workspace. Devuelve
    /// (items, error): el error es de este workspace solo, el llamador decide si degrada.
    /// </summary>
    private async Task<(List<TaskItem> Items, string? Error)> FetchTeamTasksAsync(
        string teamId, string userId, CancellationToken ct)
    {
        var items = new List<TaskItem>();
        string team = Uri.EscapeDataString(teamId);
        string uid = Uri.EscapeDataString(userId);
        var extraTokens = _settings.GetIgnoredTokens();

        for (int page = 0; page < MaxPages; page++)
        {
            // assignees%5B%5D = "assignees[]" — los corchetes van escapados para que ningún proxy
            // los reinterprete. subtasks=true: una subtarea asignada a vos es trabajo tuyo igual.
            string url = $"{ApiBase}/team/{team}/task" +
                         $"?assignees%5B%5D={uid}&include_closed=false&subtasks=true&page={page}";

            var res = await FetchJsonAsync(url, ct).ConfigureAwait(false);
            if (res.Error != null) return (items, res.Error);

            bool lastPage;
            try
            {
                lastPage = ParsePage(res.Json!, extraTokens, items);
            }
            catch
            {
                // JSON corrupto en una página → cortamos y devolvemos lo que ya juntamos.
                break;
            }
            if (lastPage) break;
        }

        return (items, null);
    }

    /// <summary>
    /// Parsea UNA página de /team/{id}/task, agrega a <paramref name="into"/> y devuelve si es la
    /// última. Tolerante: campo faltante o de tipo inesperado da un default razonable, nunca tira.
    /// </summary>
    private bool ParsePage(string json, string[] extraTokens, List<TaskItem> into)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("tasks", out var tasks) || tasks.ValueKind != JsonValueKind.Array)
            return true; // sin array de tareas no hay nada más que pedir

        foreach (var el in tasks.EnumerateArray())
        {
            string id = Str(el, "id");
            if (id.Length == 0) continue;

            string title = Str(el, "name");
            if (title.Length == 0) title = "(sin título)";

            // custom_id es el código lindo tipo "GEO-42" cuando el workspace lo tiene activado;
            // si no, ClickUp no expone un número corto → queda vacío y la UI muestra sólo el título.
            string identifier = Str(el, "custom_id");

            // Estado: nombre para mostrar (Stage) + tipo para decidir si está terminado.
            string stage = "", statusType = "";
            if (el.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.Object)
            {
                stage = Str(st, "status");
                statusType = Str(st, "type");
            }
            if (statusType.Equals("closed", StringComparison.OrdinalIgnoreCase) ||
                statusType.Equals("done", StringComparison.OrdinalIgnoreCase)) continue;
            if (IsIgnoredStatus(stage, extraTokens)) continue;

            // due_date viene como epoch en MILISEGUNDOS y ClickUp lo manda como STRING (no number),
            // así que aceptamos las dos formas antes de dar el dato por perdido.
            DateTimeOffset? due = ParseEpochMs(el, "due_date");

            // ClickUp prioriza al revés que Vikunja: acá 1=urgent … 4=low. Invertimos a
            // "más alto = más urgente" para que TaskItem.Priority signifique lo mismo venga de donde venga.
            int priority = 0;
            if (el.TryGetProperty("priority", out var pr) && pr.ValueKind == JsonValueKind.Object)
            {
                string pid = Str(pr, "id");
                if (int.TryParse(pid, out int p) && p >= 1 && p <= 4) priority = 5 - p;
            }

            string url = Str(el, "url");

            // Project = la LISTA (es lo que el usuario nombra "dónde vive la tarea"). Si viniera sin
            // lista, caemos al folder (que la API v2 llama "project", nombre heredado) y al space.
            string? project = NestedName(el, "list") ?? NestedName(el, "project") ?? NestedName(el, "space");

            // text_content es la descripción en texto plano; description trae markdown. Preferimos
            // el plano: el panel de detalle lo muestra crudo.
            string? description = null;
            var raw = Str(el, "text_content");
            if (string.IsNullOrWhiteSpace(raw)) raw = Str(el, "description");
            if (!string.IsNullOrWhiteSpace(raw)) description = raw.Trim();

            into.Add(new TaskItem(id, title, identifier, false, due, priority, project, url,
                _account.Id, _account.DisplayName,
                string.IsNullOrEmpty(stage) ? null : stage, description));
        }

        // last_page ausente = asumimos última página (mejor cortar que loopear al pedo).
        return !root.TryGetProperty("last_page", out var lp) || lp.ValueKind != JsonValueKind.False;
    }

    /// <summary>Hace UN GET autenticado y devuelve (json, error). NUNCA tira.</summary>
    private async Task<(string? Json, string? Error)> FetchJsonAsync(string url, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            // El token va CRUDO en Authorization, sin esquema — HttpClient lo rechazaría validado.
            req.Headers.TryAddWithoutValidation("Authorization", _settings.Token.Trim());
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);

            if (resp.StatusCode == HttpStatusCode.Unauthorized || resp.StatusCode == HttpStatusCode.Forbidden)
                return (null, "Credenciales rechazadas (401/403). Revisá el API token de ClickUp.");
            if ((int)resp.StatusCode == 429)
                return (null, "ClickUp está limitando las consultas (429). Probá de nuevo en un rato.");
            if (!resp.IsSuccessStatusCode)
                return (null, $"La API respondió {(int)resp.StatusCode}.");

            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return (body, null);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return (null, "Falla de red: " + ex.Message);
        }
    }

    /// <summary>Saca user.id de /v2/user. El id llega como número; lo normalizamos a string.</summary>
    private static string ParseUserId(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("user", out var user) ||
                user.ValueKind != JsonValueKind.Object) return "";
            if (!user.TryGetProperty("id", out var id)) return "";
            return id.ValueKind switch
            {
                JsonValueKind.Number => id.GetRawText(),
                JsonValueKind.String => id.GetString() ?? "",
                _ => "",
            };
        }
        catch { return ""; }
    }

    /// <summary>Saca los ids de workspace de /v2/team. Tolerante: elemento raro se saltea.</summary>
    private static List<string> ParseTeamIds(string json)
    {
        var ids = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("teams", out var teams) ||
                teams.ValueKind != JsonValueKind.Array) return ids;

            foreach (var t in teams.EnumerateArray())
            {
                if (!t.TryGetProperty("id", out var id)) continue;
                string s = id.ValueKind switch
                {
                    JsonValueKind.String => id.GetString() ?? "",
                    JsonValueKind.Number => id.GetRawText(),
                    _ => "",
                };
                if (s.Length > 0) ids.Add(s);
            }
        }
        catch { /* JSON corrupto → lista vacía, el llamador avisa */ }
        return ids;
    }

    /// <summary>Lee prop.name de un sub-objeto (list / project / space). null si no está o viene vacío.</summary>
    private static string? NestedName(JsonElement el, string prop)
    {
        if (!el.TryGetProperty(prop, out var o) || o.ValueKind != JsonValueKind.Object) return null;
        var name = Str(o, "name");
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    /// <summary>String de una propiedad; "" si falta, es null o no es string.</summary>
    private static string Str(JsonElement el, string prop)
        => el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? "" : "";

    /// <summary>Epoch en milisegundos, venga como string (lo habitual en ClickUp) o como número.</summary>
    private static DateTimeOffset? ParseEpochMs(JsonElement el, string prop)
    {
        if (!el.TryGetProperty(prop, out var v)) return null;
        long ms;
        if (v.ValueKind == JsonValueKind.String)
        {
            if (!long.TryParse(v.GetString(), out ms)) return null;
        }
        else if (v.ValueKind == JsonValueKind.Number)
        {
            if (!v.TryGetInt64(out ms)) return null;
        }
        else return null;

        try { return DateTimeOffset.FromUnixTimeMilliseconds(ms).ToLocalTime(); }
        catch { return null; }
    }

    /// <summary>
    /// Estados EXTRA que el usuario marcó como terminales para su workspace. No hay defaults
    /// hardcoded como en Trello: acá el `status.type` de ClickUp ya cubre el cierre nativo, esto es
    /// sólo para los estados propios (ej. "En review") que el usuario no quiere ver en el picker.
    /// </summary>
    private static bool IsIgnoredStatus(string status, string[] extra)
    {
        if (string.IsNullOrWhiteSpace(status) || extra.Length == 0) return false;
        foreach (var t in extra)
            if (!string.IsNullOrWhiteSpace(t) && status.Contains(t, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }
}
