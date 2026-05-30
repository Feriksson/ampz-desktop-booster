using System.Threading;
using System.Threading.Tasks;

namespace AmpzDesktopBooster.Services.Usage;

/// <summary>
/// Puerto: una fuente de uso de tokens de una suscripción de IA. Hoy hay un solo adapter
/// (Claude); mañana otros proveedores enchufan el suyo SIN tocar la UI ni la barra. Eso es
/// todo el punto de la abstracción — la UI depende de esta interfaz, no de Anthropic.
/// </summary>
public interface IUsageProvider
{
    /// <summary>Id estable para persistir la selección (ej. "claude"). No cambia nunca.</summary>
    string Id { get; }

    /// <summary>Nombre lindo para el dropdown de Config (ej. "Claude (Anthropic)").</summary>
    string DisplayName { get; }

    /// <summary>
    /// Trae el uso actual. NUNCA tira: ante cualquier fallo devuelve UsageSnapshot.Failed con un
    /// mensaje para la UI. El llamador (timer de la barra) no tiene que envolver en try/catch.
    /// </summary>
    Task<UsageSnapshot> GetUsageAsync(CancellationToken ct = default);
}
