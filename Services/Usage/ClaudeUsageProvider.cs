using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AmpzDesktopBooster.Services.Usage;

/// <summary>
/// Lee el uso REAL de la suscripción Claude desde el endpoint de CUENTA
/// GET https://api.anthropic.com/api/oauth/usage, usando el token OAuth que Claude Code
/// mantiene fresco en ~/.claude/.credentials.json.
///
/// Por qué es legítimo: es un endpoint de cuenta (no de inferencia). Nuestra propia credencial
/// leyendo nuestro propio consumo — NO imitamos a Claude Code ni evadimos ningún control. Por eso
/// funciona con User-Agent honesto. El dato es el OFICIAL de Anthropic, el mismo que ve /usage.
/// </summary>
public sealed class ClaudeUsageProvider : IUsageProvider
{
    public string Id => "claude";
    public string DisplayName => "Claude (Anthropic)";

    private const string UsageUrl = "https://api.anthropic.com/api/oauth/usage";

    // Un solo HttpClient para toda la vida del proceso: crear uno por request agota los sockets.
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    public async Task<UsageSnapshot> GetUsageAsync(CancellationToken ct = default)
    {
        var token = ReadAccessToken();
        if (token is null)
            return UsageSnapshot.Failed(Id, "No encontré la credencial de Claude. ¿Iniciaste sesión en Claude Code?");

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, UsageUrl);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            req.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
            req.Headers.TryAddWithoutValidation("anthropic-beta", "oauth-2025-04-20");

            using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);

            if (resp.StatusCode == HttpStatusCode.Unauthorized || resp.StatusCode == HttpStatusCode.Forbidden)
                return UsageSnapshot.Failed(Id, "La sesión de Claude venció. Abrí Claude Code para renovarla.");

            resp.EnsureSuccessStatusCode();
            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return Parse(json);
        }
        catch (System.Exception ex)
        {
            return UsageSnapshot.Failed(Id, "No pude leer el uso: " + ex.Message);
        }
    }

    /// <summary>
    /// Mapea el JSON del endpoint a barras. Usamos keys de salida ESTABLES —"session", "weekly_all",
    /// "weekly_scoped"— que NO dependen del nombre del modelo: la UI las busca por esas keys fijas.
    ///
    /// Por qué: en jul-2026 Anthropic migró el modelo de datos. Las viejas keys por-modelo
    /// (seven_day_sonnet/seven_day_opus) ahora llegan SIEMPRE null del lado del server, y el tope
    /// semanal scoped pasó a declarar su modelo dinámicamente en el array "limits[]"
    /// (scope.model.display_name, hoy "Fable", ayer "Sonnet"). Si atáramos la UI al nombre del modelo,
    /// se rompería en cada rotación — como ya pasó. Por eso leemos limits[] y seguimos al modelo solo.
    /// </summary>
    private UsageSnapshot Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var gauges = new List<UsageGauge>();

        // Formato NUEVO: el array "limits[]". Cada límite trae kind (session|weekly_all|weekly_scoped),
        // percent (0-100), resets_at y —sólo el scoped— scope.model.display_name con el modelo topeado.
        if (root.TryGetProperty("limits", out var limits) && limits.ValueKind == JsonValueKind.Array)
            foreach (var lim in limits.EnumerateArray())
                AddLimit(gauges, lim);

        // Fallback al formato viejo (top-level) sólo si limits[] no vino: five_hour/seven_day SIGUEN
        // existiendo como objetos; las scoped por-modelo ya no (llegan null → AddGauge las saltea).
        if (gauges.Count == 0)
        {
            AddGauge(gauges, root, "five_hour", "session", "Sesión (5h)");
            AddGauge(gauges, root, "seven_day", "weekly_all", "Semanal · todos los modelos");
        }

        return new UsageSnapshot
        {
            ProviderId = Id,
            AccountLabel = ReadPlanLabel(),
            Gauges = gauges,
            FetchedAt = DateTimeOffset.Now,
        };
    }

    /// <summary>
    /// Mapea un elemento de "limits[]" (formato nuevo) a una barra con key de salida ESTABLE.
    /// El tope scoped toma el nombre real del modelo de scope.model.display_name → la mini-isla
    /// muestra "Semanal · Fable" hoy y lo que Anthropic tope-e mañana, sin tocar código.
    /// Nota: si algún día hubiera MÁS de un weekly_scoped, la UI (3 islas fijas) sólo muestra el
    /// primero — limitación aceptada del layout actual, no del parseo.
    /// </summary>
    private static void AddLimit(List<UsageGauge> into, JsonElement lim)
    {
        string kind = lim.TryGetProperty("kind", out var k) && k.ValueKind == JsonValueKind.String
            ? k.GetString() ?? ""
            : "";

        double pct = lim.TryGetProperty("percent", out var p) && p.ValueKind == JsonValueKind.Number
            ? p.GetDouble()
            : 0;

        DateTimeOffset? reset =
            lim.TryGetProperty("resets_at", out var r) &&
            r.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(r.GetString(), out var dt)
                ? dt
                : null;

        switch (kind)
        {
            case "session":
                into.Add(new UsageGauge("session", "Sesión (5h)", pct, reset));
                break;
            case "weekly_all":
                into.Add(new UsageGauge("weekly_all", "Semanal · todos los modelos", pct, reset));
                break;
            case "weekly_scoped":
                var model = ReadScopedModel(lim) ?? "modelo";
                into.Add(new UsageGauge("weekly_scoped", "Semanal · " + model, pct, reset));
                break;
            // kind desconocido → lo ignoramos (no rompemos si Anthropic agrega tipos nuevos).
        }
    }

    /// <summary>Nombre del modelo topeado de un límite scoped: scope.model.display_name (o null).</summary>
    private static string? ReadScopedModel(JsonElement lim)
    {
        if (lim.TryGetProperty("scope", out var scope) && scope.ValueKind == JsonValueKind.Object &&
            scope.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.Object &&
            m.TryGetProperty("display_name", out var dn) && dn.ValueKind == JsonValueKind.String)
            return dn.GetString();
        return null;
    }

    /// <summary>
    /// Formato VIEJO (top-level): agrega una barra sólo si la clave existe y es un objeto
    /// (null = no aplica al plan). La key de salida (gaugeKey) es estable y puede diferir de la
    /// clave JSON, para que la UI busque siempre por el mismo nombre venga del formato que venga.
    /// </summary>
    private static void AddGauge(List<UsageGauge> into, JsonElement root, string jsonKey, string gaugeKey, string label)
    {
        if (!root.TryGetProperty(jsonKey, out var el) || el.ValueKind != JsonValueKind.Object)
            return;

        double pct = el.TryGetProperty("utilization", out var u) && u.ValueKind == JsonValueKind.Number
            ? u.GetDouble()
            : 0;

        DateTimeOffset? reset =
            el.TryGetProperty("resets_at", out var r) &&
            r.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(r.GetString(), out var dt)
                ? dt
                : null;

        into.Add(new UsageGauge(gaugeKey, label, pct, reset));
    }

    /// <summary>
    /// Lee el access token FRESCO del archivo en CADA llamada. Claude Code lo refresca solo;
    /// si lo cacheáramos, tarde o temprano trabajaríamos con un token vencido.
    /// </summary>
    private static string? ReadAccessToken()
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(CredentialsPath));
            if (doc.RootElement.TryGetProperty("claudeAiOauth", out var oauth) &&
                oauth.TryGetProperty("accessToken", out var tok) &&
                tok.ValueKind == JsonValueKind.String)
            {
                return tok.GetString();
            }
        }
        catch { /* sin credencial / archivo ilegible → null, la UI muestra el aviso */ }
        return null;
    }

    /// <summary>
    /// Plan legible desde la credencial local (sin request extra): "default_claude_max_5x" → "Max 5x".
    /// </summary>
    private static string? ReadPlanLabel()
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(CredentialsPath));
            if (doc.RootElement.TryGetProperty("claudeAiOauth", out var oauth) &&
                oauth.TryGetProperty("rateLimitTier", out var tier) &&
                tier.ValueKind == JsonValueKind.String)
            {
                var t = tier.GetString() ?? "";
                if (t.Contains("max_20x")) return "Max 20x";
                if (t.Contains("max_5x")) return "Max 5x";
                if (t.Contains("pro")) return "Pro";
                return t; // tier desconocido → lo mostramos crudo, mejor que nada
            }
        }
        catch { }
        return null;
    }

    private static string CredentialsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".claude", ".credentials.json");
}
