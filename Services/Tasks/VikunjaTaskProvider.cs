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
/// Adapter REST de Vikunja (v2.x). Pega a GET {BaseUrl}/api/v1/tasks con el API token tk_.
///
/// Detalles VERIFICADOS contra una instancia v2.3.0 (la integración nació probando con Postman):
/// - El endpoint es /api/v1/tasks. El viejo /api/v1/tasks/all quedó DEPRECADO en v2.x → tira
///   HTTP 400 {"code":2004,"message":"Invalid model provided"}.
/// - El header Accept: application/json es OBLIGATORIO; sin él, la auth con API token FALLA.
/// - El API token tk_ NO accede a /api/v1/user → el username lo provee el usuario (settings).
/// - "Mis tareas abiertas" = filter "done = false && assignees = {username}". /tasks a secas trae
///   TODO lo VISIBLE (todos los proyectos con acceso), no solo lo asignado.
///
/// Project = nombre del PROYECTO (lo que en v1 era "list" — el contenedor de tareas). Resuelto con
/// /api/v1/projects (una sola call, paralela a la de tasks).
///
/// Stage = nombre del BUCKET kanban donde está la tarea (To-Do / Doing / Done, según cómo el usuario
/// arme su kanban). En v2.x los buckets cuelgan de las VIEWS del proyecto: cada proyecto tiene N
/// views (list, table, gantt, kanban), y cada kanban view tiene sus propios buckets. Para resolver
/// el nombre necesitamos:
///   1. GET /projects/{id}/views → array de views del proyecto.
///   2. Por cada view → GET /projects/{id}/views/{vid}/buckets → array de buckets con id+title.
/// Lo hacemos PARALELO por (proyecto x view) usando Task.WhenAll para que el costo en latencia sea
/// el de la cadena más lenta, no la suma. Si algún call falla, degradamos sin Stage para esa task.
/// </summary>
public sealed class VikunjaTaskProvider : ITaskProvider
{
    public string Id => "vikunja";
    public string DisplayName => "Vikunja";

    private readonly TaskAccount _account;
    private readonly VikunjaSettings _settings;

    // Un solo HttpClient para toda la vida del proceso: crear uno por request agota los sockets.
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    public VikunjaTaskProvider(TaskAccount account, VikunjaSettings settings)
    {
        _account = account;
        _settings = settings;
    }

    public async Task<TaskFetchResult> GetOpenTasksAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_settings.BaseUrl))
            return TaskFetchResult.Failed(Id, "Falta la URL de Vikunja.");
        if (string.IsNullOrWhiteSpace(_settings.Token))
            return TaskFetchResult.Failed(Id, "Falta el API token (tk_...).");

        string filter = "done = false";
        if (!string.IsNullOrWhiteSpace(_settings.Username))
            filter += $" && assignees = {_settings.Username.Trim()}";

        string baseUrl = _settings.BaseUrl.TrimEnd('/');
        string tasksUrl = $"{baseUrl}/api/v1/tasks?filter={Uri.EscapeDataString(filter)}&sort_by=due_date&order_by=asc";
        string projectsUrl = $"{baseUrl}/api/v1/projects";

        DebugLog("=== Nuevo fetch ===");
        DebugLog($"BaseUrl: {baseUrl}");

        try
        {
            // FASE 1: tasks + projects en paralelo.
            var tasksTask = FetchJsonAsync(tasksUrl, ct);
            var projectsTask = FetchJsonAsync(projectsUrl, ct);
            await Task.WhenAll(tasksTask, projectsTask).ConfigureAwait(false);

            var tasksResult = tasksTask.Result;
            if (tasksResult.Error != null) return TaskFetchResult.Failed(Id, tasksResult.Error);

            var projectMap = projectsTask.Result.Error == null
                ? BuildProjectMap(projectsTask.Result.Json!)
                : new Dictionary<long, string>();
            DebugLog($"projectMap entries: {projectMap.Count}");

            // FASE 2: buckets de los proyectos que aparecen en los tasks.
            var projectIds = ExtractProjectIds(tasksResult.Json!);
            DebugLog($"Distinct project_ids en tasks: [{string.Join(",", projectIds)}]");
            var bucketIdsInTasks = ExtractBucketIds(tasksResult.Json!);
            DebugLog($"Distinct bucket_ids en tasks: [{string.Join(",", bucketIdsInTasks)}]");

            var taskStageMap = await FetchTaskStageMapAsync(projectIds, baseUrl, ct).ConfigureAwait(false);
            DebugLog($"taskStageMap entries: {taskStageMap.Count}");
            foreach (var kv in taskStageMap) DebugLog($"  task {kv.Key} → '{kv.Value}'");

            return Parse(tasksResult.Json!, projectMap, taskStageMap);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return TaskFetchResult.Failed(Id, "No pude conectar: " + ex.Message);
        }
    }

    /// <summary>
    /// Logger de diagnóstico TEMPORAL para descular por qué Vikunja Stage no se resuelve en algunas
    /// versiones. Escribe append a %APPDATA%\AmpzDesktopBooster\vikunja-debug.log. Sacar cuando se
    /// confirme el fix definitivo.
    /// </summary>
    private static void DebugLog(string line)
    {
        try
        {
            string path = System.IO.Path.Combine(Persistence.AppPaths.DataDir, "vikunja-debug.log");
            System.IO.File.AppendAllText(path, $"[{DateTime.Now:HH:mm:ss}] {line}\n");
        }
        catch { /* nunca tumbamos el fetch por loguear */ }
    }

    /// <summary>Igual que ExtractProjectIds pero para bucket_id (para el diagnóstico de matching).</summary>
    private static HashSet<long> ExtractBucketIds(string tasksJson)
    {
        var ids = new HashSet<long>();
        try
        {
            using var doc = JsonDocument.Parse(tasksJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return ids;
            foreach (var t in doc.RootElement.EnumerateArray())
            {
                if (t.TryGetProperty("bucket_id", out var bidEl) && bidEl.ValueKind == JsonValueKind.Number)
                {
                    long bid = bidEl.GetInt64();
                    if (bid > 0) ids.Add(bid);
                }
            }
        }
        catch { }
        return ids;
    }

    /// <summary>
    /// Hace UN GET con auth Bearer + Accept JSON. NUNCA tira: errores HTTP/red se aplanan a Error.
    /// El llamador decide si la falla aborta o degrada.
    /// </summary>
    private async Task<(string? Json, string? Error)> FetchJsonAsync(string url, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _settings.Token.Trim());
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json")); // OBLIGATORIO con tk_
            using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);

            if (resp.StatusCode == HttpStatusCode.Unauthorized || resp.StatusCode == HttpStatusCode.Forbidden)
                return (null, "Token rechazado (401/403). Revisá el API token y la URL.");
            if (!resp.IsSuccessStatusCode)
                return (null, $"La API respondió {(int)resp.StatusCode}. Revisá la URL base.");

            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return (body, null);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return (null, "Falla de red: " + ex.Message);
        }
    }

    /// <summary>Mapa projectId → title a partir de /api/v1/projects.</summary>
    private static Dictionary<long, string> BuildProjectMap(string json)
    {
        var map = new Dictionary<long, string>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return map;
            foreach (var p in doc.RootElement.EnumerateArray())
            {
                long pid = p.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.Number
                    ? idEl.GetInt64() : 0;
                string title = p.TryGetProperty("title", out var tEl) && tEl.ValueKind == JsonValueKind.String
                    ? tEl.GetString()! : "";
                if (pid > 0 && !string.IsNullOrEmpty(title)) map[pid] = title;
            }
        }
        catch { }
        return map;
    }

    /// <summary>Saca los project_id distintos de un JSON array de tasks. Tolerante a malformados.</summary>
    private static HashSet<long> ExtractProjectIds(string tasksJson)
    {
        var ids = new HashSet<long>();
        try
        {
            using var doc = JsonDocument.Parse(tasksJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return ids;
            foreach (var t in doc.RootElement.EnumerateArray())
            {
                if (t.TryGetProperty("project_id", out var pidEl) && pidEl.ValueKind == JsonValueKind.Number)
                {
                    long pid = pidEl.GetInt64();
                    if (pid > 0) ids.Add(pid);
                }
            }
        }
        catch { }
        return ids;
    }

    /// <summary>
    /// Mapa taskId → bucketName. En Vikunja v2 la relación task↔bucket es POR-VIEW (un task puede
    /// estar en distintos buckets según la kanban view que mires), por eso el endpoint genérico
    /// /api/v1/tasks NO devuelve bucket_id. Para resolverlo:
    ///   1. Listar las views de cada proyecto (GET /projects/{pid}/views).
    ///   2. Filtrar las KANBAN (view_kind == "kanban"). Las list/gantt/table no tienen buckets.
    ///   3. Por cada kanban view, GET /projects/{pid}/views/{vid}/tasks → devuelve un ARRAY DE
    ///      BUCKETS con sus tasks nested adentro ([{ id, title, tasks: [...] }]). Un solo call por
    ///      view trae tanto el nombre del bucket como qué tasks le pertenecen. Cazado vía log: el
    ///      endpoint NO devuelve "tasks con bucket_id" sino "buckets con tasks", al revés de lo que
    ///      asumí inicialmente. Más simple — no hace falta el call separado a /buckets.
    /// Si un task aparece en varias kanban views (poco común), gana la última procesada — no es un
    /// problema real porque vos seguís un solo flujo por proyecto.
    /// </summary>
    private async Task<Dictionary<long, string>> FetchTaskStageMapAsync(
        HashSet<long> projectIds, string baseUrl, CancellationToken ct)
    {
        var taskStage = new Dictionary<long, string>();
        if (projectIds.Count == 0) return taskStage;

        // Fase A: views por proyecto, en paralelo.
        var viewsFetches = projectIds.Select(pid =>
            WrapWithPid(pid, FetchJsonAsync($"{baseUrl}/api/v1/projects/{pid}/views", ct))).ToList();
        await Task.WhenAll(viewsFetches).ConfigureAwait(false);

        // Fase B: filtrar kanban views.
        var kanbanViews = new List<(long pid, long vid)>();
        foreach (var t in viewsFetches)
        {
            var (pid, vRes) = t.Result;
            if (vRes.Error != null || vRes.Json is null) continue;
            try
            {
                using var doc = JsonDocument.Parse(vRes.Json);
                if (doc.RootElement.ValueKind != JsonValueKind.Array) continue;
                foreach (var v in doc.RootElement.EnumerateArray())
                {
                    string kind = v.TryGetProperty("view_kind", out var kEl) && kEl.ValueKind == JsonValueKind.String
                        ? kEl.GetString()! : "";
                    if (kind != "kanban") continue;
                    long vid = v.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.Number
                        ? idEl.GetInt64() : 0;
                    if (vid > 0) kanbanViews.Add((pid, vid));
                }
            }
            catch { }
        }
        DebugLog($"Kanban views detectados: [{string.Join(", ", kanbanViews.Select(k => $"({k.pid},{k.vid})"))}]");

        if (kanbanViews.Count == 0) return taskStage;

        // Fase C: por cada kanban view, GET /tasks → devuelve un array de BUCKETS con sus tasks
        // nested adentro (forma "agrupada por columna"). Eso ya nos da todo en UNA call por view:
        // bucket.title + tasks[*].id. No hace falta /buckets aparte.
        var viewTasksFetches = kanbanViews.Select(kv =>
            FetchJsonAsync($"{baseUrl}/api/v1/projects/{kv.pid}/views/{kv.vid}/tasks", ct)).ToList();
        await Task.WhenAll(viewTasksFetches).ConfigureAwait(false);

        // Fase D: por cada response, recorrer buckets → recorrer tasks nested → poblar taskStage.
        for (int i = 0; i < kanbanViews.Count; i++)
        {
            var (pid, vid) = kanbanViews[i];
            var vtRes = viewTasksFetches[i].Result;
            if (vtRes.Error != null) { DebugLog($"view-tasks ({pid},{vid}) ERROR: {vtRes.Error}"); continue; }

            try
            {
                using var doc = JsonDocument.Parse(vtRes.Json!);
                if (doc.RootElement.ValueKind != JsonValueKind.Array) continue;
                foreach (var bucket in doc.RootElement.EnumerateArray())
                {
                    string bname = bucket.TryGetProperty("title", out var bnEl) && bnEl.ValueKind == JsonValueKind.String
                        ? bnEl.GetString()! : "";
                    if (string.IsNullOrEmpty(bname)) continue;
                    if (!bucket.TryGetProperty("tasks", out var tasksEl) || tasksEl.ValueKind != JsonValueKind.Array) continue;
                    foreach (var t in tasksEl.EnumerateArray())
                    {
                        long taskId = t.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.Number
                            ? idEl.GetInt64() : 0;
                        if (taskId > 0) taskStage[taskId] = bname;
                    }
                }
            }
            catch { }
        }

        return taskStage;
    }

    private static string Truncate(string s) => s.Length <= 800 ? s : s.Substring(0, 800) + "…(truncado)";

    /// <summary>
    /// Strip básico de HTML para el body de las tasks de Vikunja (TipTap output). NO es un renderer
    /// real: saca tags con regex, convierte br/p a saltos de línea, decodifica entidades comunes.
    /// Si el body necesita formato real, el botón "Abrir tarea" lleva al UI nativo.
    /// </summary>
    private static string StripHtml(string html)
    {
        // Normalizar <br>, </p>, </div> a saltos de línea ANTES de borrar tags (sino se pierden).
        string s = System.Text.RegularExpressions.Regex.Replace(
            html, @"<\s*br\s*/?\s*>", "\n", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        s = System.Text.RegularExpressions.Regex.Replace(
            s, @"</\s*(p|div|li|h[1-6])\s*>", "\n", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        // Borramos lo que quede de tags.
        s = System.Text.RegularExpressions.Regex.Replace(s, @"<[^>]+>", "");
        // Entidades comunes.
        s = s.Replace("&nbsp;", " ").Replace("&amp;", "&").Replace("&lt;", "<")
             .Replace("&gt;", ">").Replace("&quot;", "\"").Replace("&#39;", "'");
        // Colapsar saltos triples+ a doble y trimear.
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\n{3,}", "\n\n");
        return s.Trim();
    }

    private static async Task<(long pid, (string? Json, string? Error) Res)> WrapWithPid(long pid, Task<(string? Json, string? Error)> t)
    {
        var r = await t.ConfigureAwait(false);
        return (pid, r);
    }

    private static void ParseBucketsInto((string? Json, string? Error) res, Dictionary<long, string> map)
    {
        if (res.Error != null || res.Json is null) return;
        try
        {
            using var doc = JsonDocument.Parse(res.Json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return;
            foreach (var b in doc.RootElement.EnumerateArray())
            {
                long bid = b.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.Number
                    ? idEl.GetInt64() : 0;
                // Probamos "title" (estándar v2) y "name" (alternativo en algunas versiones / future-proof).
                string bname = "";
                if (b.TryGetProperty("title", out var tEl) && tEl.ValueKind == JsonValueKind.String)
                    bname = tEl.GetString()!;
                else if (b.TryGetProperty("name", out var nEl) && nEl.ValueKind == JsonValueKind.String)
                    bname = nEl.GetString()!;
                if (bid > 0 && !string.IsNullOrEmpty(bname)) map[bid] = bname;
            }
        }
        catch { }
    }

    /// <summary>
    /// Mapea el array JSON de /api/v1/tasks a TaskItem. Tolerante: cualquier campo faltante o de tipo
    /// inesperado cae a un default razonable en vez de tirar.
    /// </summary>
    private TaskFetchResult Parse(string json, Dictionary<long, string> projectMap, Dictionary<long, string> taskStageMap)
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

            // Project = nombre del proyecto (lo que en v1 era "list").
            long projectId = el.TryGetProperty("project_id", out var pidEl) && pidEl.ValueKind == JsonValueKind.Number
                ? pidEl.GetInt64() : 0;
            string? projectName = (projectId > 0 && projectMap.TryGetValue(projectId, out var pname)) ? pname : null;

            // Stage = título del bucket kanban (To-Do / Doing / Done…). Resuelto vía taskStageMap
            // (taskId → bucketName) que el FetchTaskStageMapAsync arma cruzando /views + /buckets +
            // /views/{vid}/tasks. Si no hay match (task fuera de cualquier kanban view), queda null.
            string? stage = taskStageMap.TryGetValue(id, out var st) ? st : null;

            // Description: Vikunja la guarda como HTML (TipTap). Para el panel de detalle la pasamos
            // por un strip simple de tags + decode de entidades básicas. No es un renderer real, pero
            // alcanza para mostrar el cuerpo legible — el "Abrir tarea" lleva al UI nativo si la
            // persona quiere imágenes/links formateados.
            string? description = null;
            if (el.TryGetProperty("description", out var descEl) && descEl.ValueKind == JsonValueKind.String)
            {
                var raw = descEl.GetString();
                if (!string.IsNullOrWhiteSpace(raw))
                    description = StripHtml(raw);
            }

            string itemUrl = $"{baseUrl}/tasks/{id}";
            items.Add(new TaskItem(id.ToString(), title, identifier, done, due, priority, projectName, itemUrl,
                _account.Id, _account.DisplayName, stage, description));
        }

        return TaskFetchResult.Success(Id, items);
    }
}
