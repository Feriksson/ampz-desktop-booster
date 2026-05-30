using System;
using System.Windows;
using AmpzDesktopBooster.Apps;
using AmpzDesktopBooster.Desktops;
using AmpzDesktopBooster.Interop;
using AmpzDesktopBooster.Services;

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

    // Ventana de variables actualmente abierta (para el "re-press dispara el predeterminado").
    private ProjectPathsWindow? _pathsWindow;
    // Ventana de notas abierta (instancia única — re-press la trae al frente).
    private ProjectNotesWindow? _notesWindow;
    // Shortcuts Helper abierto (instancia única — re-press de Win+/ la cierra).
    private ShortcutsHelperWindow? _shortcutsWindow;

    // Virtual-keys de las F que ruteamos (F1..F12 = 0x70..0x7B; backtick = 0xC0; barra /? = 0xBF).
    private const int VK_F2 = 0x71, VK_F3 = 0x72, VK_F5 = 0x74, VK_F6 = 0x75, VK_F7 = 0x76,
                      VK_F8 = 0x77, VK_F9 = 0x78, VK_F11 = 0x7A, VK_F12 = 0x7B,
                      VK_OEM_3 = 0xC0, VK_OEM_2 = 0xBF;

    public HotkeyRouter(HotkeyService hotkeys, DesktopService desktops, ProjectStore projects,
        AppsConfig apps, PinStore pins, RestrictionStore restrictions, AppShortcutStore shortcuts,
        Action refreshCurrentDesk)
    {
        _desktops = desktops;
        _projects = projects;
        _apps = apps;
        _pins = pins;
        _restrictions = restrictions;
        _shortcuts = shortcuts;
        _refreshCurrentDesk = refreshCurrentDesk;
        hotkeys.HotkeyFired += OnHotkeyFired;
        hotkeys.WinFunctionKey += OnWinFunctionKey;
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
                case VK_F7:    ShowPinManager();                        break;
                case VK_F8:    ShowDeskRestrictions();                  break;
                case VK_F9:    ShowWhitelistPicker();                   break;
                case VK_F11:   QuickActions.OpenDownloads(_desktops);   break;
                case VK_F12:   new HzWindow().ShowFocused();            break;
                case VK_OEM_3: QuickActions.OpenTerminalInExplorerPath(); break;
                case VK_OEM_2: ToggleShortcutsHelper();                 break;
            }
        });
    }

    // ── Pins (Win+F6 toggle, Win+F7 manager) ────────────────────────────────────

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

    private void ShowPinManager()
    {
        var w = new PinManagerWindow(_pins);
        w.ShowFocused();
    }

    // ── Restricciones (Win+F8 proteger, Win+F9 permitir app) ─────────────────────

    private void ShowDeskRestrictions()
    {
        var w = new DeskRestrictionsWindow(_restrictions, _desktops);
        w.ShowFocused();
    }

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
        }

        // ── Navegación por nombre ──
        string? target = TargetFor(e.Key);
        if (target is null)
            return;

        if (e.Shift)
            _desktops.SendForegroundWindowToByName(target, follow: true);
        else
            _desktops.GoToByName(target);
    }

    private void ShowProjectSetter()
    {
        int idx = _desktops.Current;
        string name = _desktops.GetName(idx);

        // El setter es sólo para los desks "DESK +N" (igual que el legacy).
        if (!name.Contains("DESK +", StringComparison.OrdinalIgnoreCase))
            return;

        var w = new ProjectSetterWindow(idx, name, _projects, _refreshCurrentDesk);
        w.ShowFocused();
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
        var pool = _projects.ResolvePool(name, idx); // dual-scope: proyecto o global

        _pathsWindow = new ProjectPathsWindow(pool, name);
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
