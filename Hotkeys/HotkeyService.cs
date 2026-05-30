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

    public void Start()
    {
        _hook.KeyEvent += OnKeyEvent;
        _hook.Install();
        WindowMethods.EnsureNumLockOff(); // arranca apagado; el hook lo mantiene así
    }

    private void OnKeyEvent(object? sender, KeyboardHookEventArgs e)
    {
        // NumLock SIEMPRE suprimido (el wildcard *NumLock del .ahk): si Windows nunca lo procesa,
        // nunca togglea → queda clavado en OFF, sin polling.
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

    /// <summary>true si la Win estaba presionada al disparar (navegación/proyectos); false = atajo pelado.</summary>
    public bool WinDown { get; }

    public HotkeyEventArgs(NumpadKey key, bool shift, bool winDown)
    {
        Key = key;
        Shift = shift;
        WinDown = winDown;
    }
}
