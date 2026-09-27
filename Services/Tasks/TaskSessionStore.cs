using System.Collections.Generic;
using System.Linq;

namespace AmpzDesktopBooster.Services.Tasks;

/// <summary>
/// Tarea activa POR ESCRITORIO, sólo en la sesión (efímera). Mismo espíritu y misma regla de oro
/// que <see cref="AmpzDesktopBooster.Desktops.ProjectStore"/>._session: vive en memoria, se pierde
/// al cerrar la app y NUNCA se rellena al arrancar (ver la tarea de ayer sin confirmar confundiría
/// igual que ver el proyecto de ayer). El widget de la barra arranca SIEMPRE oculto y sólo aparece
/// después de que el usuario pickea una tarea con Win+NumpadInsert.
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

    /// <summary>
    /// Mismo re-alineo que <see cref="AmpzDesktopBooster.Desktops.ProjectStore.ShiftSessionAfterRemoval"/>,
    /// para esta otra sesión índice-keyed: al borrarse un desktop, Windows corre hacia abajo el
    /// índice de todo lo que estaba después — sin esto la tarea activa quedaría en el desk de al lado.
    /// </summary>
    public void ShiftAfterRemoval(int removedIndex)
    {
        _session.Remove(removedIndex);
        foreach (var oldIdx in _session.Keys.Where(k => k > removedIndex).OrderBy(k => k).ToList())
        {
            var task = _session[oldIdx];
            _session.Remove(oldIdx);
            _session[oldIdx - 1] = task;
        }
    }
}
