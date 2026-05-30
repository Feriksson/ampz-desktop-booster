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
    /// Mapea el JSON del endpoint a barras. Cada métrica viene como objeto
    /// { "utilization": 0-100, "resets_at": ISO8601 } o null si no aplica al plan.
    /// </summary>
    private UsageSnapshot Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var gauges = new List<UsageGauge>();
        AddGauge(gauges, root, "five_hour", "Sesión (5h)");
        AddGauge(gauges, root, "seven_day", "Semanal · todos los modelos");
        AddGauge(gauges, root, "seven_day_sonnet", "Semanal · Sonnet");
        AddGauge(gauges, root, "seven_day_opus", "Semanal · Opus");

        return new UsageSnapshot
        {
            ProviderId = Id,
            AccountLabel = ReadPlanLabel(),
            Gauges = gauges,
            FetchedAt = DateTimeOffset.Now,
        };
    }

    /// <summary>Agrega una barra sólo si la clave existe y es un objeto (null = no aplica al plan).</summary>
    private static void AddGauge(List<UsageGauge> into, JsonElement root, string key, string label)
    {
        if (!root.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.Object)
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

        into.Add(new UsageGauge(key, label, pct, reset));
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
