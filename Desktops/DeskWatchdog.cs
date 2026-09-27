using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Threading;
using AmpzDesktopBooster.Services;
using AmpzDesktopBooster.Services.Localization;
using AmpzDesktopBooster.Services.Tasks;

namespace AmpzDesktopBooster.Desktops;

/// <summary>
/// Self-heal EN CALIENTE: el hermano "vivo" de <see cref="DesktopBootstrapper"/> (que sólo corre una
/// vez, al arrancar). Mientras la app corre, vigila que:
///
///  1. Los desks FIJOS del catálogo sigan existiendo. Si Windows cerró uno (Task View, Win+Ctrl+F4),
///     lo RECREA con la misma rutina por-nombre del bootstrap (nunca por índice — ver el comentario
///     de <see cref="DesktopBootstrapper"/>) y avisa con un toast. Si <see cref="DesktopConfig.AutoCreate"/>
///     está apagado, en cambio, sólo avisa UNA VEZ por sesión que falta (con el hint de dónde prenderlo).
///  2. Los desks DINÁMICOS (del launcher) que Windows cerró por fuera de la app se PODEN del registro
///     ya mismo — si no, la tecla del numpad que tenían asignada queda fantasma hasta el próximo
///     reinicio (que es cuando <see cref="DynamicDeskStore.Load"/> volvería a podarlos).
///
/// ⚠ POR QUÉ NO DISPARA NADA cuando el propio launcher cierra un dinámico (re-press de
/// Win+NumpadEnter, ver <see cref="DeskLauncher.Close"/>): ese camino saca la entrada de
/// <see cref="DynamicDeskStore"/> ANTES de borrar el desktop real. Cuando este watchdog nota el
/// desktop desaparecido, ya no encuentra ninguna entrada dueña de ese GUID — no hay nada que podar
/// ni que avisar. Es una consecuencia natural del orden de <see cref="DeskLauncher.Close"/>, no un
/// caso especial que haya que codear acá.
///
/// DETECCIÓN: dos caminos que se complementan, ninguno alcanza solo.
///   · <see cref="CheckNow"/> — lo llama <see cref="DesktopChangeListener"/> en CADA cambio de desk:
///     reacciona rápido al caso típico "cerré un desk y caí en otro", pero un desk que Windows cierra
///     SIN que cambiemos de escritorio activo (por ejemplo, cerrás uno desde Task View sin pararte
///     encima) no dispara ningún mensaje de la DLL.
///   · Un <see cref="DispatcherTimer"/> de ~1.75s que compara la lista ordenada de GUIDs vivos contra
///     la última corrida — cubre justo ese hueco, con un costo mínimo (un array chico, cada rato).
///
/// Ambos caminos convergen en <see cref="Reconcile"/>, protegido con un guard de re-entrancia (un
/// tick no puede solapar con un CheckNow disparado a la vez) y un backoff simple: si RECREAR un fijo
/// falla, esperamos un rato antes de volver a intentarlo — sin esto, un catálogo mal armado (autocrear
/// prendido pero, por ejemplo, sin ningún desk libre) tiraría un toast de error cada 1.75s para siempre.
/// </summary>
public sealed class DeskWatchdog
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(1750);
    private static readonly TimeSpan Backoff = TimeSpan.FromSeconds(30);

    private readonly DesktopService _desktops;
    private readonly DesktopConfig _config;
    private readonly DynamicDeskStore _dynamic;
    private readonly ProjectStore _projects;
    private readonly TaskSessionStore _tasks;
    private readonly Action _onChanged;

    private readonly DispatcherTimer _timer;
    private List<(Guid Id, string Name)> _lastSnapshot;
    private bool _reconciling;
    private DateTime _backoffUntil = DateTime.MinValue;

    // Fijos por los que ya avisamos "falta y autocrear está apagado" — una sola vez por sesión, para
    // no repetir el mismo toast cada 1.75s mientras el usuario no lo resuelve.
    private readonly HashSet<string> _warnedMissing = new(StringComparer.OrdinalIgnoreCase);

    public DeskWatchdog(DesktopService desktops, DesktopConfig config, DynamicDeskStore dynamic,
        ProjectStore projects, TaskSessionStore tasks, Action onChanged)
    {
        _desktops = desktops;
        _config = config;
        _dynamic = dynamic;
        _projects = projects;
        _tasks = tasks;
        _onChanged = onChanged;

        _lastSnapshot = Snapshot();

        _timer = new DispatcherTimer { Interval = PollInterval };
        _timer.Tick += (_, _) => Reconcile();
        _timer.Start();
    }

    /// <summary>Reacción inmediata a un cambio de desk (lo cablea App sobre DesktopChangeListener).</summary>
    public void CheckNow() => Reconcile();

    private List<(Guid, string)> Snapshot()
    {
        int count = _desktops.Count;
        var list = new List<(Guid, string)>(count);
        for (int i = 0; i < count; i++)
            list.Add((_desktops.IdOf(i), _desktops.GetName(i)));
        return list;
    }

    private void Reconcile()
    {
        if (_reconciling) return;              // guard de re-entrancia: timer vs CheckNow solapados
        if (DateTime.UtcNow < _backoffUntil) return; // tras un fallo de recreación, enfriamos un rato

        _reconciling = true;
        try
        {
            var current = Snapshot();
            var currentIds = new HashSet<Guid>(current.Select(t => t.Item1));

            // Recorremos el snapshot VIEJO de atrás para adelante: si Windows cerró más de un desk en
            // el mismo tick, cada shift de sesión se aplica sobre índices que el anterior todavía no
            // corrió (ver ProjectStore.ShiftSessionAfterRemoval).
            for (int i = _lastSnapshot.Count - 1; i >= 0; i--)
            {
                var (id, name) = _lastSnapshot[i];
                if (currentIds.Contains(id)) continue; // sigue vivo, nada que hacer
                HandleGone(i, name, id);
            }

            _lastSnapshot = current;
        }
        finally { _reconciling = false; }
    }

    private void HandleGone(int oldIndex, string name, Guid id)
    {
        // La sesión (efímera, índice-keyed) referencia el índice VIEJO. Windows ya corrió hacia abajo
        // todo lo que estaba después — replicamos ese corrimiento acá o quedaría apuntando al desk de
        // al lado (peor caso: mostrar el espacio/tarea equivocada hasta el próximo cambio real).
        _projects.ShiftSessionAfterRemoval(oldIndex);
        _tasks.ShiftAfterRemoval(oldIndex);

        // ¿Era un DINÁMICO todavía registrado? Si el launcher lo hubiera cerrado él mismo, ya no
        // estaría acá (ver el comentario de la clase) — así que esto sólo dispara para lo que Windows
        // cerró por fuera. Podamos y refrescamos en silencio: no es un evento que amerite un toast,
        // el usuario mismo lo cerró a mano.
        if (_dynamic.Get(id) is not null)
        {
            _dynamic.Unregister(id);
            _onChanged();
            return;
        }

        // ¿Era uno de los FIJOS del catálogo (Main/Fixed)? Ahí sí hay que curarlo.
        var entry = _config.ByName(name);
        if (entry is null || entry.DeskRole == DeskRole.Space) return; // no gestionado / no aplica

        if (!_config.AutoCreate)
        {
            if (_warnedMissing.Add(name))
                Toasts.Info(string.Format(Loc.T("Toast.FixedDeskMissing"), name), Loc.T("Toast.FixedDeskMissingHint"));
            return;
        }

        try
        {
            DesktopBootstrapper.Ensure(_config, _desktops, _dynamic);
            if (_desktops.FindExact(name) >= 0)
            {
                Toasts.Info(string.Format(Loc.T("Toast.FixedDeskRestored"), name));
                _onChanged();
            }
            else
            {
                Toasts.Error(string.Format(Loc.T("Toast.FixedDeskRestoreFailed"), name));
                _backoffUntil = DateTime.UtcNow.Add(Backoff);
            }
        }
        catch
        {
            // Silent-degrade, mismo criterio que toda la persistencia del repo: un fallo acá no puede
            // tumbar la app. El backoff evita que un catálogo roto tire un toast cada 1.75s por siempre.
            Toasts.Error(string.Format(Loc.T("Toast.FixedDeskRestoreFailed"), name));
            _backoffUntil = DateTime.UtcNow.Add(Backoff);
        }
    }
}
