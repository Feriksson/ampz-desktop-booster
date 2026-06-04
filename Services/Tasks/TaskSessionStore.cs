using System.Collections.Generic;

namespace AmpzDesktopBooster.Services.Tasks;

/// <summary>
/// Tarea activa POR ESCRITORIO, sólo en la sesión (efímera). Mismo espíritu y misma regla de oro
/// que <see cref="AmpzDesktopBooster.Desktops.ProjectStore"/>._session: vive en memoria, se pierde
/// al cerrar la app y NUNCA se rellena al arrancar (ver la tarea de ayer sin confirmar confundiría
/// igual que ver el proyecto de ayer). El widget de la barra arranca SIEMPRE oculto y sólo aparece
/// después de que el usuario pickea una tarea con Win+NumLock.
///
/// Por qué por-desk y no global: el usuario lo eligió así — cada DESK puede estar atado a una tarea
/// distinta, igual que cada DESK +N tiene su proyecto. El widget cambia con el desk activo (lo
/// alimenta el DesktopChangeListener, idéntico al widget de proyecto).
/// </summary>
public sealed class TaskSessionStore
{
    private readonly Dictionary<int, TaskItem> _session = new();

    /// <summary>La tarea activa del desk, o null si no hay ninguna pickeada.</summary>
    public TaskItem? GetDeskTask(int idx) => _session.TryGetValue(idx, out var t) ? t : null;

    /// <summary>Ancla una tarea al desk (pisa la anterior si había).</summary>
    public void SetDeskTask(int idx, TaskItem task) => _session[idx] = task;

    /// <summary>Desancla la tarea del desk (el widget se oculta).</summary>
    public void RemoveDeskTask(int idx) => _session.Remove(idx);
}
