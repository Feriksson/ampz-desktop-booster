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
/// Adapter REST de Trello. A diferencia de JIRA (stub), este SÍ hace fetch real — la API de Trello es
/// la más simple de las tres: autenticación por query-string (key + token) y un único endpoint para
/// "mis tarjetas".
///
/// Detalles del contrato de Trello (API v1, estable hace años):
/// - Endpoint: GET https://api.trello.com/1/members/me/cards — devuelve las tarjetas ASIGNADAS al
///   miembro dueño del token, de TODOS sus tableros. Por eso no necesitamos username: el token ya
///   identifica al usuario (al revés que Vikunja, donde el tk_ no resuelve el usuario).
/// - Auth: ?key={ApiKey}&token={Token} en la query. No hay header Bearer; va todo en la URL.
/// - Por defecto trae solo tarjetas VISIBLES (no archivadas) → ya son "abiertas". No hace falta filtro
///   server-side como el "done = false" de Vikunja.
/// - Pedimos fields acotados para no traer el JSON gigante de cada tarjeta (Trello manda TODO si no se
///   limita): name, due, dueComplete, shortUrl, idShort, idBoard.
/// - El nombre del tablero NO viene en este endpoint (solo idBoard). Para no encadenar un segundo
///   request por tablero, dejamos Project=null en v1 — igual criterio que Vikunja, que también lo deja
///   null. Si más adelante se quiere el nombre, se resuelve con un GET /1/members/me/boards y un mapa.
/// </summary>
public sealed class TrelloTaskProvider : ITaskProvider
{
    public string Id => "trello";
    public string DisplayName => "Trello";

    private readonly TrelloSettings _settings;

    // Un solo HttpClient para toda la vida del proceso (crear uno por request agota los sockets) —
    // mismo criterio que VikunjaTaskProvider.
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    public TrelloTaskProvider(TrelloSettings settings) => _settings = settings;

    public async Task<TaskFetchResult> GetOpenTasksAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_settings.ApiKey))
            return TaskFetchResult.Failed(Id, "Falta la API key de Trello.");
        if (string.IsNullOrWhiteSpace(_settings.Token))
            return TaskFetchResult.Failed(Id, "Falta el token de Trello.");

        string key = Uri.EscapeDataString(_settings.ApiKey.Trim());
        string token = Uri.EscapeDataString(_settings.Token.Trim());
        // fields acotado: sin esto Trello devuelve TODO el objeto tarjeta (decenas de campos por card).
        string url = "https://api.trello.com/1/members/me/cards" +
                     "?fields=name,due,dueComplete,shortUrl,idShort,idBoard" +
                     $"&key={key}&token={token}";

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);

            if (resp.StatusCode == HttpStatusCode.Unauthorized || resp.StatusCode == HttpStatusCode.Forbidden)
                return TaskFetchResult.Failed(Id, "Credenciales rechazadas (401/403). Revisá la API key y el token.");

            if (!resp.IsSuccessStatusCode)
                return TaskFetchResult.Failed(Id, $"La API respondió {(int)resp.StatusCode}.");

            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return Parse(json);
        }
        catch (OperationCanceledException)
        {
            throw; // cancelación real (no es fallo de la API): que la propague el llamador
        }
        catch (Exception ex)
        {
            return TaskFetchResult.Failed(Id, "No pude conectar: " + ex.Message);
        }
    }

    /// <summary>
    /// Mapea el array JSON de /members/me/cards a TaskItem. Tolerante: cualquier campo faltante o de
    /// tipo inesperado cae a un default razonable en vez de tirar (igual que el Parse de Vikunja).
    /// </summary>
    private TaskFetchResult Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);

        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            return TaskFetchResult.Failed(Id, "Respuesta inesperada de la API (no es una lista de tarjetas).");

        var items = new List<TaskItem>();

        foreach (var el in doc.RootElement.EnumerateArray())
        {
            string id = el.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String
                ? idEl.GetString()! : "";
            string title = el.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
                ? n.GetString()! : "(sin título)";

            // idShort es el número visible de la tarjeta dentro de su tablero (ej. "#42"). Lo usamos
            // como identifier corto, análogo al "VKJ-123" de Vikunja.
            string identifier = el.TryGetProperty("idShort", out var sh) && sh.ValueKind == JsonValueKind.Number
                ? "#" + sh.GetInt64() : "";

            // dueComplete = la tarjeta tiene el vencimiento marcado como cumplido (sigue visible, pero
            // ya está "hecha"). El picker lo muestra como Done.
            bool done = el.TryGetProperty("dueComplete", out var dc) && dc.ValueKind == JsonValueKind.True;

            // Trello no tiene prioridad numérica → 0, como hace Vikunja cuando no hay priority.
            int priority = 0;

            // due es ISO8601 o null (literal JSON null) cuando la tarjeta no tiene vencimiento.
            DateTimeOffset? due = null;
            if (el.TryGetProperty("due", out var dd) && dd.ValueKind == JsonValueKind.String &&
                DateTimeOffset.TryParse(dd.GetString(), out var parsed))
            {
                due = parsed;
            }

            string itemUrl = el.TryGetProperty("shortUrl", out var su) && su.ValueKind == JsonValueKind.String
                ? su.GetString()! : "";

            // Project=null en v1: el nombre del tablero no viene en este endpoint (ver comentario de clase).
            items.Add(new TaskItem(id, title, identifier, done, due, priority, null, itemUrl));
        }

        return TaskFetchResult.Success(Id, items);
    }
}
