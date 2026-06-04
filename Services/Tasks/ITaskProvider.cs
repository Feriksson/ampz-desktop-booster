using System.Threading;
using System.Threading.Tasks;

namespace AmpzDesktopBooster.Services.Tasks;

/// <summary>
/// Puerto: una fuente de tareas de un gestor externo. Hoy hay dos adapters (Vikunja, JIRA);
/// mañana otros enchufan el suyo SIN tocar la UI ni la barra. Eso es todo el punto de la
/// abstracción — la UI depende de esta interfaz, no de Vikunja. Calcado de IUsageProvider.
/// </summary>
public interface ITaskProvider
{
    /// <summary>Id estable para persistir la selección (ej. "vikunja"). No cambia nunca.</summary>
    string Id { get; }

    /// <summary>Nombre lindo para el dropdown de Config (ej. "Vikunja").</summary>
    string DisplayName { get; }

    /// <summary>
    /// Trae las tareas ABIERTAS del usuario. NUNCA tira: ante cualquier fallo devuelve
    /// TaskFetchResult.Failed con un mensaje para la UI. El llamador no envuelve en try/catch.
    /// </summary>
    Task<TaskFetchResult> GetOpenTasksAsync(CancellationToken ct = default);
}
