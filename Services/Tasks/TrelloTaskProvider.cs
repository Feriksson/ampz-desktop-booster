using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AmpzDesktopBooster.Services.Tasks;

/// <summary>
/// Adapter REST de Trello. A diferencia de JIRA (stub), este SÍ hace fetch real.
///
/// Detalles del contrato de Trello (API v1, estable hace años):
/// - Auth: ?key={ApiKey}&token={Token} en la query. No hay header Bearer; va todo en la URL.
/// - GET /1/members/me/cards — devuelve las tarjetas ASIGNADAS al miembro dueño del token, de TODOS
///   sus tableros. Por defecto trae sólo las VISIBLES (archivadas excluidas server-side).
/// - GET /1/members/me/boards?fields=id,name&lists=open&list_fields=id,name — devuelve los tableros
///   del usuario (id+name) CON sus listas abiertas embebidas. Esa única respuesta alimenta DOS mapas:
///   idBoard → boardName (TaskItem.Project) y idList → listName (TaskItem.Stage).
///
/// Por qué DOS calls y no inline expansion: probé `&list=true&list_fields=name` en /members/me/cards
/// y Trello lo IGNORA en silencio en ese endpoint — la respuesta no trae la lista embebida (no está
/// en los params documentados ahí). Los boards SÍ soportan expandir lists nested, así que pivotamos
/// por ese camino. Las dos calls van en PARALELO (Task.WhenAll), así que latencia ≈ a una sola.
///
/// FILTRADO DE "DONE" (heurística, client-side, dos vías que se suman):
///   1. dueComplete=true (botón "Marcar como completa" sobre el due date): señal NATIVA de Trello.
///   2. Nombre de la lista padre matchea tokens terminales (defaults hardcoded + extras por cuenta
///      desde TrelloSettings.IgnoredListsRaw). Match por Contains case-insensitive, así "Done — Q1"
///      o "Completadas (archivo)" también caen.
/// Las cards filtradas NO se devuelven (no se marcan como done) — el picker NO debería verlas.
/// </summary>
public sealed class TrelloTaskProvider : ITaskProvider
{
    public string Id => "trello";
    public string DisplayName => "Trello";

    private readonly TaskAccount _account;
    private readonly TrelloSettings _settings;

    // Un solo HttpClient para toda la vida del proceso (crear uno por request agota los sockets) —
    // mismo criterio que VikunjaTaskProvider.
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    public TrelloTaskProvider(TaskAccount account, TrelloSettings settings)
    {
        _account = account;
        _settings = settings;
    }

    public async Task<TaskFetchResult> GetOpenTasksAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_settings.ApiKey))
            return TaskFetchResult.Failed(Id, "Falta la API key de Trello.");
        if (string.IsNullOrWhiteSpace(_settings.Token))
            return TaskFetchResult.Failed(Id, "Falta el token de Trello.");

        string key = Uri.EscapeDataString(_settings.ApiKey.Trim());
        string token = Uri.EscapeDataString(_settings.Token.Trim());

        // fields acotado: sin esto Trello devuelve TODO el objeto card. Incluimos idList SÍ o SÍ
        // (es lo que cruzamos con el mapa de listas para saber en qué columna está la card).
        // 'desc' es el body de la card (markdown plano) — lo mostramos en el panel de detalle.
        string cardsUrl = "https://api.trello.com/1/members/me/cards" +
                          "?fields=name,desc,due,dueComplete,shortUrl,idShort,idBoard,idList" +
                          $"&key={key}&token={token}";

        // Boards CON listas abiertas embebidas. Pedimos id+name del board (Project en TaskItem) y
        // id+name de cada lista (Stage en TaskItem). Un solo request alimenta DOS mapas.
        string boardsUrl = "https://api.trello.com/1/members/me/boards" +
                           "?fields=id,name&lists=open&list_fields=id,name" +
                           $"&key={key}&token={token}";

        try
        {
            // Dos calls en paralelo: latencia total ≈ la de la más lenta.
            var cardsTask = FetchJsonAsync(cardsUrl, ct);
            var boardsTask = FetchJsonAsync(boardsUrl, ct);
            await Task.WhenAll(cardsTask, boardsTask).ConfigureAwait(false);

            var cardsResult = cardsTask.Result;
            if (cardsResult.Error != null) return TaskFetchResult.Failed(Id, cardsResult.Error);

            // Si fallan los boards igual seguimos, sólo perdemos el filtro por nombre de lista Y la
            // resolución de board name (Project). El dueComplete sigue funcionando. Mejor traer algo
            // que romper el picker entero.
            var (listMap, boardMap) = boardsTask.Result.Error == null
                ? BuildMaps(boardsTask.Result.Json!)
                : (new Dictionary<string, string>(), new Dictionary<string, string>());

            return Parse(cardsResult.Json!, listMap, boardMap);
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
    /// Hace UN GET y devuelve (json, error). NUNCA tira: errores HTTP / red se aplanan a Error.
    /// El llamador decide si la falla aborta o degrada (ver GetOpenTasksAsync con boards).
    /// </summary>
    private async Task<(string? Json, string? Error)> FetchJsonAsync(string url, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);

            if (resp.StatusCode == HttpStatusCode.Unauthorized || resp.StatusCode == HttpStatusCode.Forbidden)
                return (null, "Credenciales rechazadas (401/403). Revisá la API key y el token.");
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

    /// <summary>
    /// Construye DOS mapas en una pasada sobre /members/me/boards con lists embebidas:
    /// idList → listName (Stage) y idBoard → boardName (Project). Tolerante: cualquier elemento
    /// malformado se saltea sin tirar.
    /// </summary>
    private static (Dictionary<string, string> listMap, Dictionary<string, string> boardMap) BuildMaps(string json)
    {
        var listMap  = new Dictionary<string, string>(StringComparer.Ordinal);
        var boardMap = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return (listMap, boardMap);

            foreach (var board in doc.RootElement.EnumerateArray())
            {
                string bid = board.TryGetProperty("id", out var bidEl) && bidEl.ValueKind == JsonValueKind.String
                    ? bidEl.GetString()! : "";
                string bname = board.TryGetProperty("name", out var bnEl) && bnEl.ValueKind == JsonValueKind.String
                    ? bnEl.GetString()! : "";
                if (!string.IsNullOrEmpty(bid) && !string.IsNullOrEmpty(bname)) boardMap[bid] = bname;

                if (!board.TryGetProperty("lists", out var listsEl) ||
                    listsEl.ValueKind != JsonValueKind.Array) continue;

                foreach (var list in listsEl.EnumerateArray())
                {
                    string lid = list.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String
                        ? idEl.GetString()! : "";
                    string lname = list.TryGetProperty("name", out var nEl) && nEl.ValueKind == JsonValueKind.String
                        ? nEl.GetString()! : "";
                    if (!string.IsNullOrEmpty(lid)) listMap[lid] = lname;
                }
            }
        }
        catch
        {
            // JSON corrupto → mapas vacíos. El picker degrada al filtro dueComplete solamente.
        }
        return (listMap, boardMap);
    }

    /// <summary>
    /// Mapea el array JSON de /members/me/cards a TaskItem. Tolerante: cualquier campo faltante o de
    /// tipo inesperado cae a un default razonable en vez de tirar.
    /// </summary>
    private TaskFetchResult Parse(string json, Dictionary<string, string> listMap, Dictionary<string, string> boardMap)
    {
        using var doc = JsonDocument.Parse(json);

        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            return TaskFetchResult.Failed(Id, "Respuesta inesperada de la API (no es una lista de tarjetas).");

        var items = new List<TaskItem>();
        var userTokens = _settings.GetIgnoredTokens();

        foreach (var el in doc.RootElement.EnumerateArray())
        {
            string id = el.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String
                ? idEl.GetString()! : "";
            string title = el.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
                ? n.GetString()! : "(sin título)";

            string identifier = el.TryGetProperty("idShort", out var sh) && sh.ValueKind == JsonValueKind.Number
                ? "#" + sh.GetInt64() : "";

            // dueComplete = la card tiene el vencimiento marcado como cumplido. Señal nativa de
            // Trello → la filtramos del picker.
            bool done = el.TryGetProperty("dueComplete", out var dc) && dc.ValueKind == JsonValueKind.True;
            if (done) continue;

            // Cruza idList contra el mapa para obtener el nombre. Si el board no estaba en la 2da
            // call (raro), listName queda vacío y el filtro por nombre no aplica para esta card.
            string idList = el.TryGetProperty("idList", out var ilEl) && ilEl.ValueKind == JsonValueKind.String
                ? ilEl.GetString()! : "";
            string listName = (idList != "" && listMap.TryGetValue(idList, out var nm)) ? nm : "";
            if (IsTerminalListName(listName, userTokens)) continue;

            int priority = 0; // Trello no tiene prioridad numérica

            DateTimeOffset? due = null;
            if (el.TryGetProperty("due", out var dd) && dd.ValueKind == JsonValueKind.String &&
                DateTimeOffset.TryParse(dd.GetString(), out var parsed))
            {
                due = parsed;
            }

            string itemUrl = el.TryGetProperty("shortUrl", out var su) && su.ValueKind == JsonValueKind.String
                ? su.GetString()! : "";

            // Project = nombre del board (resuelto del mapa idBoard → name).
            // Stage   = nombre de la lista padre (la columna del kanban).
            // done=false fijo: las dueComplete ya quedaron filtradas arriba.
            string idBoard = el.TryGetProperty("idBoard", out var ibEl) && ibEl.ValueKind == JsonValueKind.String
                ? ibEl.GetString()! : "";
            string? projectName = (idBoard != "" && boardMap.TryGetValue(idBoard, out var bn)) ? bn : null;

            // Description = body de la card. Trello lo guarda como Markdown plano (no HTML), así que
            // mostrarlo crudo es legible — bullets *, headers #, links [a](b) quedan claros.
            string? description = null;
            if (el.TryGetProperty("desc", out var descEl) && descEl.ValueKind == JsonValueKind.String)
            {
                var raw = descEl.GetString();
                if (!string.IsNullOrWhiteSpace(raw)) description = raw.Trim();
            }

            items.Add(new TaskItem(id, title, identifier, false, due, priority, projectName, itemUrl,
                _account.Id, _account.DisplayName, string.IsNullOrEmpty(listName) ? null : listName, description));
        }

        return TaskFetchResult.Success(Id, items);
    }

    /// <summary>
    /// Tokens de cierre habituales en kanbans (ES + EN). Match por Contains case-insensitive, así
    /// "Done — Q1 2026" o "Completadas (archivo)" también caen. La lista por-cuenta del usuario se
    /// SUMA a esta — no la reemplaza.
    /// </summary>
    private static readonly string[] TerminalListTokens =
    {
        // EN
        "done", "complete", "completed", "cancelled", "canceled", "closed", "archived", "archive",
        // ES
        "terminado", "terminada", "terminadas", "completado", "completada", "completadas", "completa",
        "cancelado", "cancelada", "canceladas", "archivado", "archivada", "archivadas",
        "finalizado", "finalizada", "finalizadas", "cerrado", "cerrada", "cerradas"
    };

    private static bool IsTerminalListName(string name, string[] userExtra)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        foreach (var t in TerminalListTokens)
            if (name.Contains(t, StringComparison.OrdinalIgnoreCase)) return true;
        foreach (var t in userExtra)
            if (!string.IsNullOrWhiteSpace(t) && name.Contains(t, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}
