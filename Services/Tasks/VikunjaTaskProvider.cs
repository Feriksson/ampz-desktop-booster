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
/// Adapter REST de Vikunja (v2.x). Pega a GET {BaseUrl}/api/v1/tasks con el API token tk_.
///
/// Detalles VERIFICADOS contra una instancia v2.3.0 (la integración nació probando con Postman):
/// - El endpoint es /api/v1/tasks. El viejo /api/v1/tasks/all quedó DEPRECADO en v2.x → tira
///   HTTP 400 {"code":2004,"message":"Invalid model provided"}.
/// - El header Accept: application/json es OBLIGATORIO; sin él, la auth con API token FALLA.
/// - El API token tk_ NO accede a /api/v1/user → el username lo provee el usuario (settings).
/// - "Mis tareas abiertas" = filter "done = false && assignees = {username}". /tasks a secas trae
///   TODO lo VISIBLE (todos los proyectos con acceso), no solo lo asignado.
/// </summary>
public sealed class VikunjaTaskProvider : ITaskProvider
{
    public string Id => "vikunja";
    public string DisplayName => "Vikunja";

    private readonly VikunjaSettings _settings;

    // Un solo HttpClient para toda la vida del proceso: crear uno por request agota los sockets.
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    public VikunjaTaskProvider(VikunjaSettings settings) => _settings = settings;

    public async Task<TaskFetchResult> GetOpenTasksAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_settings.BaseUrl))
            return TaskFetchResult.Failed(Id, "Falta la URL de Vikunja.");
        if (string.IsNullOrWhiteSpace(_settings.Token))
            return TaskFetchResult.Failed(Id, "Falta el API token (tk_...).");

        // "done = false" siempre; sumamos "assignees = user" solo si hay username (si no, traería
        // TODO lo visible en vez de solo lo tuyo).
        string filter = "done = false";
        if (!string.IsNullOrWhiteSpace(_settings.Username))
            filter += $" && assignees = {_settings.Username.Trim()}";

        string baseUrl = _settings.BaseUrl.TrimEnd('/');
        string url = $"{baseUrl}/api/v1/tasks?filter={Uri.EscapeDataString(filter)}&sort_by=due_date&order_by=asc";

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _settings.Token.Trim());
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json")); // OBLIGATORIO

            using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);

            if (resp.StatusCode == HttpStatusCode.Unauthorized || resp.StatusCode == HttpStatusCode.Forbidden)
                return TaskFetchResult.Failed(Id, "Token rechazado (401/403). Revisá el API token y la URL.");

            if (!resp.IsSuccessStatusCode)
                return TaskFetchResult.Failed(Id, $"La API respondió {(int)resp.StatusCode}. Revisá la URL base.");

            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return Parse(json);
        }
        catch (OperationCanceledException)
        {
            throw; // cancelación real (no es un fallo de la API): que la propague el llamador
        }
        catch (Exception ex)
        {
            return TaskFetchResult.Failed(Id, "No pude conectar: " + ex.Message);
        }
    }

    /// <summary>
    /// Mapea el array JSON de /api/v1/tasks a TaskItem. Tolerante: cualquier campo faltante o de tipo
    /// inesperado cae a un default razonable en vez de tirar.
    /// </summary>
    private TaskFetchResult Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);

        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            return TaskFetchResult.Failed(Id, "Respuesta inesperada de la API (no es una lista de tareas).");

        string baseUrl = _settings.BaseUrl.TrimEnd('/');
        var items = new List<TaskItem>();

        foreach (var el in doc.RootElement.EnumerateArray())
        {
            long id = el.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.Number
                ? idEl.GetInt64() : 0;
            string title = el.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String
                ? t.GetString()! : "(sin título)";
            string identifier = el.TryGetProperty("identifier", out var idn) && idn.ValueKind == JsonValueKind.String
                ? idn.GetString()! : "";
            bool done = el.TryGetProperty("done", out var d) && d.ValueKind == JsonValueKind.True;
            int priority = el.TryGetProperty("priority", out var p) && p.ValueKind == JsonValueKind.Number
                ? p.GetInt32() : 0;

            // Vikunja usa "0001-01-01T00:00:00Z" como "sin fecha" → lo tratamos como null (Year > 1).
            DateTimeOffset? due = null;
            if (el.TryGetProperty("due_date", out var dd) && dd.ValueKind == JsonValueKind.String &&
                DateTimeOffset.TryParse(dd.GetString(), out var parsed) && parsed.Year > 1)
            {
                due = parsed;
            }

            string itemUrl = $"{baseUrl}/tasks/{id}";
            items.Add(new TaskItem(id.ToString(), title, identifier, done, due, priority, null, itemUrl));
        }

        return TaskFetchResult.Success(Id, items);
    }
}
