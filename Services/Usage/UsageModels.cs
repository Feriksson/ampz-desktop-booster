using System.Collections.Generic;

namespace AmpzDesktopBooster.Services.Usage;

/// <summary>
/// Una barra de uso: un porcentaje 0-100 y cuándo se resetea. ResetsAt null = el provider
/// no informó reset para esta métrica (o no aplica al plan).
/// </summary>
public sealed record UsageGauge(string Key, string Label, double Percent, DateTimeOffset? ResetsAt);

/// <summary>
/// Foto del uso de una suscripción en un instante. Es lo que devuelve cualquier IUsageProvider.
/// Agnóstico del proveedor: una lista de barras + metadatos. La UI no sabe (ni le importa) si
/// el dato vino de Claude, OpenAI o quien sea — sólo pinta gauges.
/// </summary>
public sealed class UsageSnapshot
{
    /// <summary>Id del provider que generó esta foto (ej. "claude").</summary>
    public required string ProviderId { get; init; }

    /// <summary>Etiqueta de cuenta/plan para mostrar arriba (ej. "Max 5x"). Opcional.</summary>
    public string? AccountLabel { get; init; }

    /// <summary>Las barras a mostrar, en orden. Vacío si hubo error.</summary>
    public required IReadOnlyList<UsageGauge> Gauges { get; init; }

    /// <summary>Cuándo se tomó esta foto (para el "actualizado hace X").</summary>
    public DateTimeOffset FetchedAt { get; init; }

    /// <summary>null = todo OK. Si no, mensaje listo para mostrarle al usuario.</summary>
    public string? Error { get; init; }

    public bool Ok => Error is null;

    /// <summary>Snapshot de error: sin barras, con un mensaje para la UI.</summary>
    public static UsageSnapshot Failed(string providerId, string error) => new()
    {
        ProviderId = providerId,
        Gauges = System.Array.Empty<UsageGauge>(),
        Error = error,
        FetchedAt = DateTimeOffset.Now,
    };
}
