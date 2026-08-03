using AmpzDesktopBooster.Interop;

namespace AmpzDesktopBooster.Hotkeys;

/// <summary>
/// Capa de hotkeys sobre el hook de bajo nivel. Trackea Win y Shift (el hook nos da
/// tecla por tecla, así que los modificadores los llevamos a mano), suprime el combo,
/// enmascara la tecla Win y dispara el evento correspondiente.
///
/// Dos familias de atajos:
///   - Win + numpad  → <see cref="HotkeyFired"/> (NumpadKey, decodificado por scancode por NumLock).
///   - Win + F-key / backtick → <see cref="WinFunctionKey"/> (vkCode, estable, sin lío de NumLock).
///   - NumpadClear PELADO → también HotkeyFired (DeskPicker).
///
/// Los handlers deben ser NO bloqueantes (Dispatcher.BeginInvoke): corren dentro del callback
/// del hook, que no se puede demorar.
/// </summary>
public sealed class HotkeyService : IDisposable
{
    private const uint VK_LWIN = 0x5B;
    private const uint VK_RWIN = 0x5C;
    private const uint VK_SHIFT = 0x10;
    private const uint VK_LSHIFT = 0xA0;
    private const uint VK_RSHIFT = 0xA1;
    private const uint VK_NUMLOCK = 0x90;

    // F1..F12 = 0x70..0x7B; backtick (`) = VK_OEM_3 = 0xC0; barra (/?) = VK_OEM_2 = 0xBF.
    // Son los que interceptamos con Win.
    private const uint VK_F1 = 0x70, VK_F12 = 0x7B, VK_OEM_3 = 0xC0, VK_OEM_2 = 0xBF;

    // 'D' = 0x44. Interceptamos Win+D para reemplazar el "Mostrar escritorio" nativo.
    private const uint VK_D = 0x44;

    private readonly LowLevelKeyboardHook _hook = new();
    private bool _winDown;
    private bool _shiftDown;
    // True si en este "hold" de Win tragamos algún Win+algo: hay que enmascarar el Win-up para que
    // el shell no abra el menú Inicio. Un tap PELADO de Win (sin combo) lo deja en false → Start
    // se abre normal, como el usuario espera.
    private bool _winComboConsumed;

    public bool SuppressWinNumpad { get; set; } = true;

    public event EventHandler<HotkeyEventArgs>? HotkeyFired;

    /// <summary>Win + F-key o Win + backtick. Args: (vkCode, shift).</summary>
    public event Action<int, bool>? WinFunctionKey;

    /// <summary>Win + D ("Mostrar escritorio"): el shell NO lo ve (lo tragamos); el caller decide qué
    /// hacer en su lugar (minimizar todo menos la barra).</summary>
    public event Action? ShowDesktopRequested;

    /// <summary>
    /// La tecla Win se SOLTÓ (fin de un "hold"). Observador PASIVO: se dispara DESPUÉS de que el
    /// masking del Win-up ya corrió, sin alterarlo en nada. Lo usa el picker de Hz (Win+F12) para
    /// aplicar la opción seleccionada al soltar, estilo Alt+Tab. Se dispara en cada release real.
    /// </summary>
    public event Action? WinReleased;

    public void Start()
    {
        _hook.KeyEvent += OnKeyEvent;
        _hook.Install();
        WindowMethods.EnsureNumLockOff(); // arranca apagado; el hook lo mantiene así
    }

    /// <summary>
    /// Re-registra el hook de teclado para restaurar la entrega de teclas tras una alteración del
    /// estado de input global (la barra reordenándose al salir de pantalla completa la dispara). Es
    /// el "click que lo arregla", pero automático. También reseteamos los modificadores: si quedó un
    /// down sin su up durante el lío, su estado pegado se limpia acá.
    /// </summary>
    public void ReinstallHook()
    {
        // Re-sincronizamos los modificadores con su estado FÍSICO real — NO asumimos "soltado".
        // Este reinstall lo dispara el watchdog de z-order de la barra, que NO sólo salta al salir
        // de pantalla completa: también salta al navegar a un desk CON ventana (esa ventana toma el
        // foreground y la barra re-evalúa su z-order). Forzar _winDown=false ahí descolgaba el combo
        // Win+Numpad EN PLENO USO, con la tecla Win físicamente apretada — los desks VACÍOS no
        // disparan el watchdog (por eso se podía rafaguear infinito entre ellos) y el primer desk
        // CON ventana lo mataba. GetAsyncKeyState nos da la verdad del hardware en ese instante.
        bool winDown = WindowMethods.IsKeyPhysicallyDown(WindowMethods.VK_LWIN)
                    || WindowMethods.IsKeyPhysicallyDown(WindowMethods.VK_RWIN);
        _winDown = winDown;
        _shiftDown = WindowMethods.IsKeyPhysicallyDown(WindowMethods.VK_SHIFT);
        // Si la Win ya NO está apretada, limpiamos la marca: no hay Win-up futuro que enmascarar.
        // Si SIGUE apretada, la conservamos para que el masking del menú Inicio se haga al soltar.
        if (!winDown) _winComboConsumed = false;

        _hook.Reinstall();
    }

    private void OnKeyEvent(object? sender, KeyboardHookEventArgs e)
    {
        // NumLock SIEMPRE suprimido cuando llega PELADO. Funciona limpio así. NO intentamos
        // protegerlo con modificadores apretados: ahí Windows toggea por un path por debajo del
        // WH_KEYBOARD_LL (input stack del kernel) que no se puede neutralizar de forma confiable
        // — ni con un counter-tap inyectado, ni desactivando los hotkeys de accesibilidad
        // (StickyKeys/ToggleKeys/MouseKeys, probado y descartado con instrumentación a archivo).
        // Por eso el picker de tareas se movió de `Win+NumLock` a `Win+NumpadInsert` (= NumpadKey.D0
        // por scancode 0x52): la decodificación por scancode es a prueba de NumLock, y dejamos de
        // pelear una guerra imposible. Si el usuario hace Shift/Alt+NumLock por accidente, NumLock
        // togglea como en cualquier otra app de Windows — es el comportamiento estándar y aceptado.
        if (e.VirtualKey == VK_NUMLOCK)
        {
            e.Suppress = true;
            return;
        }

        // Tecla Win. El mask del menú Inicio se hace acá, en el RELEASE (como AHK), no en el down:
        // el shell mira el evento inmediatamente anterior al Win-up, así que el mask tiene que ir
        // pegado a ese Win-up. Lo enmascaramos SOLO si usamos la Win como modificador en este hold
        // (_winComboConsumed) — un tap pelado de Win tiene que seguir abriendo Start normal.
        if (e.VirtualKey is VK_LWIN or VK_RWIN)
        {
            if (e.IsDown)
            {
                // OJO: la tecla Win MANTENIDA auto-repite (genera key-downs repetidos). Reseteamos
                // el flag SÓLO en la transición real soltado→apretado, NO en cada repeat — si no, un
                // repeat que llega DESPUÉS del combo borraba la marca y el Win-up no se enmascaraba.
                if (!_winDown)
                {
                    _winDown = true;
                    _winComboConsumed = false;
                }
                return;
            }

            _winDown = false;
            if (_winComboConsumed)
            {
                _winComboConsumed = false;
                // Tragamos el Win-up físico y lo reponemos DETRÁS de un Ctrl (un único SendInput,
                // orden garantizado). Sólo suprimimos si la inyección entró: si fallara, dejamos
                // pasar el Win-up real para NO dejar la tecla pegada (el bug del intento anterior).
                if (WindowMethods.SendMaskedWinUp((ushort)e.VirtualKey))
                    e.Suppress = true;
            }
            // Aviso PASIVO de "se soltó la Win", DESPUÉS de todo el masking de arriba (no lo altera).
            // El handler debe ser no-bloqueante (difiere al Dispatcher); corre dentro del callback.
            WinReleased?.Invoke();
            return;
        }
        if (e.VirtualKey is VK_SHIFT or VK_LSHIFT or VK_RSHIFT)
        {
            if (!e.IsInjected) _shiftDown = e.IsDown;
            return;
        }

        // ── F1 PELADO: lo tragamos (down Y up) para que NUNCA llegue a la app activa y abra la
        //    Ayuda de Windows — igual que el legacy. Por ahora SÓLO lo suprimimos: cuando se
        //    defina "el explorador helper", su acción se dispara acá, en el down (e.IsDown).
        //    Sólo F1 sin Win (Win+F1 sigue por la rama de abajo). ──
        if (e.VirtualKey == VK_F1 && !_winDown)
        {
            e.Suppress = true;
            return;
        }

        if (!e.IsDown)
            return;

        // ── Win + D: interceptamos el "Mostrar escritorio" nativo. El Win+D del shell esconde la
        //    barra peleando el z-order (batalla imposible para una AppBar de terceros), así que lo
        //    TRAGAMOS y disparamos NUESTRA versión (minimizar todo menos la barra). Marcamos el combo
        //    consumido → el Win-up se enmascara y NO se abre el menú Inicio. ──
        if (_winDown && e.VirtualKey == VK_D)
        {
            e.Suppress = true;
            _winComboConsumed = true;
            ShowDesktopRequested?.Invoke();
            return;
        }

        // ── Numpad (por scancode, a prueba de NumLock) ──
        var key = NumpadDecoder.Decode(e.ScanCode, e.IsExtended);
        if (key != NumpadKey.None)
        {
            if (_winDown)
            {
                if (SuppressWinNumpad)
                {
                    // Tragamos el combo → el shell NO ve la tecla del numpad, así que el Win quedaría
                    // "tap solo". Marcamos para enmascarar el Win-up cuando se suelte.
                    e.Suppress = true;
                    _winComboConsumed = true;
                }
                HotkeyFired?.Invoke(this, new HotkeyEventArgs(key, _shiftDown, winDown: true));
                return;
            }

            // Sin Win: el único atajo pelado es NumpadClear (Numpad5) → DeskPicker.
            if (key == NumpadKey.D5 && !_shiftDown)
            {
                e.Suppress = true;
                HotkeyFired?.Invoke(this, new HotkeyEventArgs(key, false, winDown: false));
            }
            return;
        }

        // ── Win + F-key / backtick (vkCode estable) ──
        if (_winDown && IsFunctionVk(e.VirtualKey))
        {
            // Tragamos el combo → marcamos para enmascarar el Win-up en el release (si no, el
            // backtick/F-key suprimido deja al Win como "tap solo" y abre el menú Inicio).
            e.Suppress = true;
            _winComboConsumed = true;
            WinFunctionKey?.Invoke((int)e.VirtualKey, _shiftDown);
        }
    }

    private static bool IsFunctionVk(uint vk) =>
        (vk >= VK_F1 && vk <= VK_F12) || vk == VK_OEM_3 || vk == VK_OEM_2;

    public void Dispose() => _hook.Dispose();
}

public sealed class HotkeyEventArgs : EventArgs
{
    public NumpadKey Key { get; }
    public bool Shift { get; }

    /// <summary>true si la Win estaba presionada al disparar (navegación/espacios); false = atajo pelado.</summary>
    public bool WinDown { get; }

    public HotkeyEventArgs(NumpadKey key, bool shift, bool winDown)
    {
        Key = key;
        Shift = shift;
        WinDown = winDown;
    }
}
