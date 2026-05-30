namespace AmpzDesktopBooster.Hotkeys;

public enum NumpadKey
{
    None,
    D0, D1, D2, D3, D4, D5, D6, D7, D8, D9,
    Divide, Multiply, Subtract, Add, Enter, Decimal
}

/// <summary>
/// Identifica la tecla FÍSICA del numpad por scancode + flag extended.
///
/// Por qué scancode y no virtual-key: con NumLock OFF, el numpad manda los mismos
/// virtual-keys que el bloque de navegación (Numpad7 = VK_HOME, Numpad8 = VK_UP, etc.).
/// El scancode, en cambio, es de la tecla física y NO cambia con NumLock. El bloque
/// de navegación dedicado usa el flag extended; el numpad no (salvo Enter y Divide).
///
/// Esto resuelve, de raíz, el dolor de NumLock documentado en el .ahk original
/// (allá se forzaba SetNumLockState AlwaysOff + wildcards). Acá no hace falta.
/// </summary>
public static class NumpadDecoder
{
    public static NumpadKey Decode(uint scanCode, bool extended) => (scanCode, extended) switch
    {
        (0x52, false) => NumpadKey.D0,
        (0x4F, false) => NumpadKey.D1,
        (0x50, false) => NumpadKey.D2,
        (0x51, false) => NumpadKey.D3,
        (0x4B, false) => NumpadKey.D4,
        (0x4C, false) => NumpadKey.D5,
        (0x4D, false) => NumpadKey.D6,
        (0x47, false) => NumpadKey.D7,
        (0x48, false) => NumpadKey.D8,
        (0x49, false) => NumpadKey.D9,
        (0x53, false) => NumpadKey.Decimal,
        (0x37, false) => NumpadKey.Multiply,
        (0x4A, false) => NumpadKey.Subtract,
        (0x4E, false) => NumpadKey.Add,
        (0x35, true)  => NumpadKey.Divide,
        (0x1C, true)  => NumpadKey.Enter,
        _             => NumpadKey.None
    };

    public static string Label(NumpadKey key) => key switch
    {
        NumpadKey.D0 => "Numpad0",
        NumpadKey.D1 => "Numpad1",
        NumpadKey.D2 => "Numpad2",
        NumpadKey.D3 => "Numpad3",
        NumpadKey.D4 => "Numpad4",
        NumpadKey.D5 => "NumpadClear (5)",
        NumpadKey.D6 => "Numpad6",
        NumpadKey.D7 => "Numpad7",
        NumpadKey.D8 => "Numpad8",
        NumpadKey.D9 => "Numpad9",
        NumpadKey.Decimal => "NumpadDot",
        NumpadKey.Multiply => "Numpad* (Mult)",
        NumpadKey.Subtract => "Numpad- (Sub)",
        NumpadKey.Add => "Numpad+ (Add)",
        NumpadKey.Divide => "Numpad/ (Div)",
        NumpadKey.Enter => "NumpadEnter",
        _ => "?"
    };
}
