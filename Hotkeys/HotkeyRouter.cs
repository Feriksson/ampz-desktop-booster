using System;
using System.Windows;
using AmpzDesktopBooster.Apps;
using AmpzDesktopBooster.Desktops;
using AmpzDesktopBooster.Interop;
using AmpzDesktopBooster.Services;
using AmpzDesktopBooster.Services.Tasks;

namespace AmpzDesktopBooster.Hotkeys;

/// <summary>
/// Conecta los hotkeys del hook con sus acciones. Navegación de virtual desktops POR NOMBRE
/// (Numpad físico con NumLock OFF), proyectos por desk, y el DeskPicker.
///
/// Mapeo (igual que el legacy ampzWinTunner.ahk líneas 1289-1314, 3760, 3905-3906):
///   Win+Numpad 7/8/9 → MAIN / MAILS / MISCS
///   Win+Numpad 1..6  → DESK +1..+6
///   Win+Numpad +/−   → ciclar entre DESK+ (con proyecto)
///   Win+Shift+(nav)  → mandar ventana activa ahí + seguir
///   Win+NumpadEnter  → setear proyecto del desk actual (sólo DESK+)
///   NumpadClear solo → DeskPicker (saltar a un proyecto de la sesión)
///   Win+NumpadMult   → Variables del proyecto/global (Paths Manager) — re-press dispara el predeterminado
///   Win+NumpadDiv/Del → Notes / Send-picker (todavía no portados)
///
/// Corre headless: el feedback visual lo dispara el DesktopChangeListener, no este router.
/// </summary>
public sealed class HotkeyRouter
{
    private readonly DesktopService _desktops;
    private readonly ProjectStore _projects;
    private readonly AppsConfig _apps;
    private readonly PinStore _pins;
    private readonly RestrictionStore _restrictions;
    private readonly AppShortcutStore _shortcuts;
    private readonly Action _refreshCurrentDesk;
    private readonly TaskSessionStore _taskSession;
    private readonly Action _refreshTaskWidget;

    // Ventana de variables actualmente abierta (para el "re-press dispara el predeterminado").
    private ProjectPathsWindow? _pathsWindow;
    // Ventana de notas abierta (instancia única — re-press la trae al frente).
    private ProjectNotesWindow? _notesWindow;
    // Shortcuts Helper abierto (instancia única — re-press de Win+/ la cierra).
    private ShortcutsHelperWindow? _shortcutsWindow;
    // Picker de Hz abierto (Win+F12). Instancia única: re-press cicla; soltar Win aplica.
    private HzWindow? _hzWindow;
    // Setter de proyecto abierto (Win+NumpadEnter). Instancia única: re-press RESETEA el desk.
    private ProjectSetterWindow? _setterWindow;

    // Virtual-keys de las F que ruteamos (F1..F12 = 0x70..0x7B; backtick = 0xC0; barra /? = 0xBF).
    // F7 (Pin Manager) y F8 (Restricciones) SE QUITARON: eran popups de gestión compleja que hoy
    // viven mejor en la Config (pestañas Anclajes y Protecciones). Esas teclas quedan libres.
    private const int VK_F2 = 0x71, VK_F3 = 0x72, VK_F5 = 0x74, VK_F6 = 0x75,
                      VK_F9 = 0x78, VK_F11 = 0x7A, VK_F12 = 0x7B,
                      VK_OEM_3 = 0xC0, VK_OEM_2 = 0xBF;

    public HotkeyRouter(HotkeyService hotkeys, DesktopService desktops, ProjectStore projects,
        AppsConfig apps, PinStore pins, RestrictionStore restrictions, AppShortcutStore shortcuts,
        Action refreshCurrentDesk, TaskSessionStore taskSession, Action refreshTaskWidget)
    {
        _desktops = desktops;
        _projects = projects;
        _apps = apps;
        _pins = pins;
        _restrictions = restrictions;
        _shortcuts = shortcuts;
        _refreshCurrentDesk = refreshCurrentDesk;
        _taskSession = taskSession;
        _refreshTaskWidget = refreshTaskWidget;
        hotkeys.HotkeyFired += OnHotkeyFired;
        hotkeys.WinFunctionKey += OnWinFunctionKey;
        hotkeys.WinReleased += OnWinReleased;
    }

    /// <summary>
    /// Se soltó la Win. Si el picker de Hz está abierto, aplica la opción seleccionada (flujo
    /// Alt+Tab: mantener Win, ciclar con F12, soltar para aplicar). Diferido al Dispatcher porque
    /// corre dentro del callback del hook, que no se puede bloquear.
    /// </summary>
    private void OnWinReleased()
    {
        if (_hzWindow is null) return; // nada que aplicar → no agendamos trabajo de UI en cada tap de Win
        Application.Current.Dispatcher.BeginInvoke(() => _hzWindow?.ApplySelected());
    }

    private void OnWinFunctionKey(int vk, bool shift)
    {
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            switch (vk)
            {
                case VK_F2:    ShowAbrirCon();                          break;
                case VK_F3:    new EnvVarsWindow().ShowFocused();       break;
                case VK_F5:    new DockerWindow().ShowFocused();        break;
                case VK_F6:    TogglePinCurrent();                      break;
                case VK_F9:    ShowWhitelistPicker();                   break;
                case VK_F11:   QuickActions.OpenDownloads(_desktops);   break;
                case VK_F12:   ShowOrCycleHz();                         break;
                case VK_OEM_3: QuickActions.OpenTerminalInExplorerPath(); break;
                case VK_OEM_2: ToggleShortcutsHelper();                 break;
            }
        });
    }

    // ── Pins (Win+F6 toggle; la gestión completa vive en Config → Anclajes) ──────

    private void TogglePinCurrent()
    {
        IntPtr hwnd = WindowMethods.GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return;
        string proc = WindowMethods.ProcessNameOf(hwnd);
        if (proc == "") return;

        if (_pins.IsBlocked(proc))
        {
            MessageBox.Show($"'{proc}' no puede anclarse.", "Anclar",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (_pins.TryGet(proc, out string pinnedTo))
        {
            // pinnedTo YA es el nombre del desk anclado (el store es por nombre).
            if (MessageBox.Show($"'{proc}' está anclado a '{pinnedTo}'.\n¿Desanclar?", "Desanclar",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            {
                _pins.Unpin(proc);
                Toasts.Unpinned(proc, pinnedTo);
            }
        }
        else
        {
            // Anclamos por NOMBRE del desk actual, no por su índice (que cambia al reordenar).
            string deskName = _desktops.GetName(_desktops.Current);
            if (MessageBox.Show($"¿Anclar '{proc}' a este escritorio?\n→ '{deskName}'", "Anclar",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            {
                _pins.Pin(proc, deskName);
                Toasts.Pinned(proc, deskName);
            }
        }
    }

    // ── Restricciones (Win+F9 permitir app; proteger/whitelist viven en Config → Protecciones) ──

    private void ShowWhitelistPicker()
    {
        // Capturar la app activa ANTES de abrir la ventana (si no, el foreground pasa a ser ésta).
        IntPtr hwnd = WindowMethods.GetForegroundWindow();
        string proc = WindowMethods.ProcessNameOf(hwnd);
        if (proc == "")
        {
            MessageBox.Show("No se pudo identificar la app activa.", "Permitir app",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var w = new WhitelistPickerWindow(_restrictions, _desktops, proc, hwnd);
        w.ShowFocused();
    }

    private void ShowSendWindowPicker()
    {
        // Capturar la ventana activa ANTES de abrir el picker.
        IntPtr hwnd = WindowMethods.GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return;
        string title = WindowMethods.GetActiveWindowTitle();
        var w = new SendWindowPickerWindow(_desktops, hwnd, title);
        w.ShowFocused();
    }

    /// <summary>
    /// Win+F12: picker de Hz estilo Alt+Tab. Primera pulsación abre el diálogo con la selección ya
    /// puesta en la frecuencia SIGUIENTE a la actual; re-presionar (sin soltar Win) cicla entre las
    /// opciones. El "aplicar al soltar Win" lo dispara OnWinReleased. Instancia única.
    /// </summary>
    private void ShowOrCycleHz()
    {
        if (_hzWindow is not null)
        {
            _hzWindow.CycleNext();
            return;
        }
        _hzWindow = new HzWindow();
        _hzWindow.Closed += (_, _) => _hzWindow = null;
        _hzWindow.ShowFocused();
    }

    private void ShowAbrirCon()
    {
        var targets = ExplorerContext.GetTargetPaths();
        var w = new AbrirConWindow(targets, _apps);
        w.ShowFocused();
    }

    // ── Shortcuts Helper (Win+/) ─────────────────────────────────────────────────

    private void ToggleShortcutsHelper()
    {
        // Re-press con el panel abierto → cerrar (toggle), igual que el legacy.
        if (_shortcutsWindow is not null)
        {
            _shortcutsWindow.Close();
            return;
        }

        // Capturar la app activa ANTES de abrir: una vez abierto, el foreground pasa a ser el panel.
        IntPtr hwnd = WindowMethods.GetForegroundWindow();
        string proc = WindowMethods.ProcessNameOf(hwnd);
        string title = WindowMethods.GetActiveWindowTitle();
        if (proc == "AmpzDesktopBooster.exe") { proc = ""; title = ""; } // no nos mostramos a nosotros mismos

        _shortcutsWindow = new ShortcutsHelperWindow(_shortcuts, proc, title);
        _shortcutsWindow.Closed += (_, _) => _shortcutsWindow = null;
        _shortcutsWindow.ShowFocused();
    }

    private void OnHotkeyFired(object? sender, HotkeyEventArgs e)
    {
        // El callback del hook no se puede bloquear y abrir ventanas/cambiar desktop mueve el foco:
        // diferimos al Dispatcher para correr cuando el callback ya retornó.
        Application.Current.Dispatcher.BeginInvoke(() => Route(e));
    }

    private void Route(HotkeyEventArgs e)
    {
        // ── Atajos pelados (sin Win) ──
        if (!e.WinDown)
        {
            if (e.Key == NumpadKey.D5)   // NumpadClear
                ShowDeskPicker();
            return;
        }

        // ── Win + … ──
        switch (e.Key)
        {
            case NumpadKey.Add:      _desktops.CyclePlus(1);  return;
            case NumpadKey.Subtract: _desktops.CyclePlus(-1); return;
            case NumpadKey.Enter:    ShowProjectSetter();     return;
            case NumpadKey.Multiply: ShowProjectPaths();      return;
            case NumpadKey.Divide:   ShowProjectNotes();      return;
            case NumpadKey.Decimal:  ShowSendWindowPicker();  return; // Win+NumpadDel
            case NumpadKey.D0:       ShowTaskPicker();        return; // Win+NumpadInsert (scancode 0x52)
        }

        // ── Navegación por nombre ──
        string? target = TargetFor(e.Key);
        if (target is null)
            return;

        if (e.Shift)
        {
            // Guard de protección: si el desk destino está restringido y la app activa no está en su
            // whitelist, NEGAMOS el envío y avisamos por toast. Sin esto, el WindowGovernor rebotaría
            // la ventana a MAIN al entrar igual — pero rebotar DESPUÉS (verla saltar y volver) es
            // confuso; mejor prevenir de entrada y explicar el motivo.
            if (!CanSendForegroundTo(target, out string proc, out string deskName))
            {
                Toasts.SendBlockedByRestriction(proc, deskName);
                return;
            }
            _desktops.SendForegroundWindowToByName(target, follow: true);
        }
        else
            _desktops.GoToByName(target);
    }

    /// <summary>
    /// ¿Se puede mandar la ventana activa al desk con ese fragmento de nombre? Resuelve el desk real,
    /// y si está PROTEGIDO chequea la whitelist. Las exentas (sistema + la propia app) y las
    /// whitelisteadas pasan; mismo criterio que WindowGovernor. true = se puede enviar; si devuelve
    /// false, <paramref name="proc"/> y <paramref name="deskName"/> traen el motivo para el toast.
    /// </summary>
    private bool CanSendForegroundTo(string targetFragment, out string proc, out string deskName)
    {
        proc = ""; deskName = "";

        int idx = _desktops.FindByNameFragment(targetFragment);
        if (idx < 0) return true; // no existe → que SendForegroundWindowToByName devuelva false solo

        deskName = _desktops.GetName(idx);
        if (!_restrictions.IsRestricted(deskName)) return true; // desk libre → sin restricción

        IntPtr hwnd = WindowMethods.GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return true; // sin ventana real, no hay nada que negar
        proc = WindowMethods.ProcessNameOf(hwnd);

        // Exentas y whitelisteadas pasan; cualquier otra cosa se niega.
        return proc == "" || _restrictions.IsExempt(proc) || _restrictions.IsWhitelisted(deskName, proc);
    }

    private void ShowProjectSetter()
    {
        int idx = _desktops.Current;
        string name = _desktops.GetName(idx);

        // El setter es sólo para los desks "DESK +N" (igual que el legacy).
        if (!name.Contains("DESK +", StringComparison.OrdinalIgnoreCase))
            return;

        // Re-press con el setter abierto → RESET del desk: saca el proyecto y cierra. Mismo patrón de
        // instancia única que Variables (Win+*) y Notas (Win+/): la 2da pulsación NO abre otra ventana,
        // dispara la acción. Reusa el camino del botón "Quitar" (ResetAndClose) — un solo punto de verdad.
        if (_setterWindow is not null)
        {
            _setterWindow.ResetAndClose();
            return;
        }

        _setterWindow = new ProjectSetterWindow(idx, name, _projects, _refreshCurrentDesk);
        _setterWindow.Closed += (_, _) => _setterWindow = null;
        _setterWindow.ShowFocused();
    }

    private void ShowProjectPaths()
    {
        // Re-press con la ventana abierta → dispara el predeterminado (no abre otra). Como el legacy.
        if (_pathsWindow is not null)
        {
            _pathsWindow.FireDefault();
            return;
        }

        int idx = _desktops.Current;
        string name = _desktops.GetName(idx);
        // dual-scope: pool primaria (proyecto o global) + la global de SOLO-LECTURA para anexar
        // cuando estamos en scope de proyecto (globalPool == null en scope global → no se anexa nada).
        var pool = _projects.ResolvePoolWithGlobal(name, idx, out var globalPool);

        _pathsWindow = new ProjectPathsWindow(pool, name, globalPool: globalPool);
        _pathsWindow.Closed += (_, _) => _pathsWindow = null;
        _pathsWindow.ShowFocused();
    }

    private void ShowProjectNotes()
    {
        // Re-press con la ventana abierta → traerla al frente (no abrir otra).
        if (_notesWindow is not null)
        {
            _notesWindow.BringToFront();
            return;
        }

        int idx = _desktops.Current;
        string name = _desktops.GetName(idx);

        _notesWindow = new ProjectNotesWindow(_projects, name, idx);
        _notesWindow.Closed += (_, _) => _notesWindow = null;
        _notesWindow.ShowFocused();
    }

    private void ShowDeskPicker()
    {
        var w = new DeskPickerWindow(_desktops, _projects, jumpIdx => _desktops.GoTo(jumpIdx));
        w.ShowFocused();
    }

    // ── Tareas (Win+NumpadInsert pickear · click en el widget abre el detalle) ────────
    // NOTA: el atajo MIGRÓ de Win+NumLock a Win+NumpadInsert (=NumpadKey.D0, scancode 0x52). Razón:
    // Shift/Alt+NumLock togglea NumLock por un path por debajo de WH_KEYBOARD_LL que no podemos
    // neutralizar. El NumpadInsert se decodifica por scancode → a prueba de NumLock → sin pelea.

    /// <summary>
    /// Abre el picker INSTANTÁNEAMENTE (con loader) y dispara el fetch en background. Cuando el
    /// fetch termina, popula la lista vía Dispatcher (UI thread). Razón: con N cuentas activas y
    /// Vikunja haciendo fetches anidados, esperar al fetch ANTES de mostrar la ventana se sentía
    /// como freeze de la app (Win+NumpadInsert no respondía visualmente por 1-3 seg).
    ///
    /// Aislamiento por cuenta: una cuenta que falla NO bloquea las demás; se acumula su error y se
    /// reporta por toast al final, pero el picker muestra lo que sí vino.
    /// </summary>
    private void ShowTaskPicker()
    {
        int idx = _desktops.Current;
        var w = new TaskPickerWindow(_desktops.GetName(idx), picked =>
        {
            _taskSession.SetDeskTask(idx, picked);
            _refreshTaskWidget();
        });
        w.ShowFocused(); // YA aparece — con su loader

        // Fetch en background; cuando termina, marshaleamos al UI thread para popular.
        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            try
            {
                var settings = TasksSettings.Load();
                var results = await TasksService.FetchAllAsync(settings);
                _ = Application.Current.Dispatcher.BeginInvoke(() => OnFetchCompleted(w, results));
            }
            catch (Exception ex)
            {
                _ = Application.Current.Dispatcher.BeginInvoke(() =>
                {
                    if (w.IsLoaded) w.SetError("Falló el fetch: " + ex.Message);
                });
            }
        });
    }

    private void OnFetchCompleted(TaskPickerWindow w, System.Collections.Generic.IReadOnlyList<AccountFetchResult> results)
    {
        // Si el usuario cerró el picker mientras fetcheaba, salimos sin tocar nada (la ventana ya no existe).
        if (!w.IsLoaded) return;

        if (results.Count == 0)
        {
            w.SetEmpty("Sin cuentas activas",
                "Agregá una cuenta de Vikunja, JIRA o Trello en Configuración → Tareas.");
            return;
        }

        var allItems = new System.Collections.Generic.List<TaskItem>();
        var failed = new System.Collections.Generic.List<string>();
        foreach (var r in results)
        {
            if (r.Result.Ok)
                allItems.AddRange(r.Result.Items);
            else
                failed.Add($"{r.Account.DisplayName}: {r.Result.Error}");
        }

        if (failed.Count > 0)
            Toasts.Error($"Algunas cuentas fallaron ({failed.Count})", string.Join("\n", failed));

        if (allItems.Count == 0)
        {
            w.SetEmpty(
                failed.Count == 0 ? "Sin tareas abiertas" : "Ninguna cuenta trajo tareas",
                failed.Count == 0 ? "No hay tareas para pickear ahora mismo." : "Revisá las cuentas que fallaron.");
            return;
        }

        w.SetItems(allItems);
    }

    // Ventana de detalle actualmente abierta (o null). Sirve para que un 2do click en el widget
    // TOGGLE (cierre) en vez de abrir una nueva encima. CloseOnDeactivate por sí solo no alcanza
    // porque la BarWindow no roba foco al clickearla (es AppBar sin activación) → la detail no se
    // desactiva → no se autocierra.
    private TaskDetailWindow? _openTaskDetail;

    /// <summary>
    /// Click en el widget de tarea → toggle del mini-panel de detalle. Si ya está abierto, lo
    /// cierra. Si no, lo abre. Si el desk no tiene tarea activa (no debería: el widget está oculto
    /// sin tarea) no hacemos nada. "Elegir otra" reabre el picker; "Desanclar" la saca de la sesión.
    /// </summary>
    public void ShowTaskDetail()
    {
        // Toggle: si hay una abierta y aún cargada, cerrar y salir.
        if (_openTaskDetail is { IsLoaded: true } existing)
        {
            existing.Close();
            _openTaskDetail = null;
            return;
        }

        int idx = _desktops.Current;
        var task = _taskSession.GetDeskTask(idx);
        if (task is null)
            return;

        var w = new TaskDetailWindow(task,
            onPickAnother: () => Application.Current.Dispatcher.BeginInvoke(() => ShowTaskPicker()),
            onUnpin: () => { _taskSession.RemoveDeskTask(idx); _refreshTaskWidget(); });
        _openTaskDetail = w;
        // Cuando se cierra (por Esc, botón, CloseOnDeactivate, lo que sea), liberamos la referencia.
        w.Closed += (_, _) => { if (ReferenceEquals(_openTaskDetail, w)) _openTaskDetail = null; };
        w.ShowFocused();
    }

    /// <summary>Tecla física del numpad → fragmento de nombre del desktop destino.</summary>
    private static string? TargetFor(NumpadKey key) => key switch
    {
        NumpadKey.D7 => "MAIN",     // Numpad7 / Home
        NumpadKey.D8 => "MAILS",    // Numpad8 / Up
        NumpadKey.D9 => "MISCS",    // Numpad9 / PgUp
        NumpadKey.D1 => "DESK +1",  // Numpad1 / End
        NumpadKey.D2 => "DESK +2",  // Numpad2 / Down
        NumpadKey.D3 => "DESK +3",  // Numpad3 / PgDn
        NumpadKey.D4 => "DESK +4",  // Numpad4 / Left
        NumpadKey.D5 => "DESK +5",  // Numpad5 / Clear
        NumpadKey.D6 => "DESK +6",  // Numpad6 / Right
        _ => null,
    };
}
