using System.Diagnostics;
using System.Runtime.InteropServices;
using static AmpzDesktopBooster.Interop.NativeMethods;

namespace AmpzDesktopBooster.Interop;

/// <summary>
/// Hook global de teclado de bajo nivel (WH_KEYBOARD_LL).
/// Es LITERALMENTE lo que AutoHotkey instala por debajo: ve cada tecla del
/// sistema antes que cualquier aplicación. Si un handler marca e.Suppress = true,
/// devolvemos (IntPtr)1 en vez de CallNextHookEx y la tecla MUERE acá — no llega
/// a ninguna otra app. Eso es el "retornar en vacío" del que hablábamos.
/// </summary>
public sealed class LowLevelKeyboardHook : IDisposable
{
    // CRÍTICO: el delegate tiene que vivir en un field. Si lo recolecta el GC,
    // Windows llama a memoria liberada y la app se cae. Bug clásico de P/Invoke.
    private readonly LowLevelKeyboardProc _proc;
    private IntPtr _hookId = IntPtr.Zero;

    /// <summary>Se dispara en cada keydown/keyup que ve el hook.</summary>
    public event EventHandler<KeyboardHookEventArgs>? KeyEvent;

    public LowLevelKeyboardHook()
    {
        _proc = HookCallback;
    }

    public void Install()
    {
        if (_hookId != IntPtr.Zero) return;

        using var process = Process.GetCurrentProcess();
        using var module = process.MainModule!;
        IntPtr hMod = GetModuleHandle(module.ModuleName);

        _hookId = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, hMod, 0);
        if (_hookId == IntPtr.Zero)
            throw new InvalidOperationException(
                $"No se pudo instalar el hook de teclado (Win32 error {Marshal.GetLastWin32Error()}).");
    }

    /// <summary>
    /// Re-registra el hook (unhook + hook). Windows puede DEJAR DE ENTREGAR teclas a un hook
    /// existente tras ciertas alteraciones del estado de input global —en esta app, cuando la barra
    /// cambia su z-order al salir de pantalla completa— y se queda así hasta el próximo cambio de
    /// foco (lo que el usuario "arreglaba" con un click). Una registración FRESCA restaura la entrega.
    /// Debe correr en el thread que bombea mensajes (el de UI), igual que Install/Dispose.
    /// </summary>
    public void Reinstall()
    {
        if (_hookId != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hookId);
            _hookId = IntPtr.Zero;
        }
        Install();
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int msg = (int)wParam;
            bool isDown = msg is WM_KEYDOWN or WM_SYSKEYDOWN;
            bool isUp = msg is WM_KEYUP or WM_SYSKEYUP;

            if (isDown || isUp)
            {
                var data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);

                // Input que inyectamos nosotros (el masking de Win): dejarlo pasar sin
                // procesarlo, así no reentramos en nuestra propia lógica.
                if (data.dwExtraInfo == WindowMethods.InjectedSignature)
                    return CallNextHookEx(_hookId, nCode, wParam, lParam);

                bool extended = (data.flags & LLKHF_EXTENDED) != 0;
                bool injected = (data.flags & LLKHF_INJECTED) != 0;
                var args = new KeyboardHookEventArgs(data.vkCode, data.scanCode, extended, injected, isDown);

                KeyEvent?.Invoke(this, args);

                if (args.Suppress)
                    return (IntPtr)1; // tragamos la tecla
            }
        }

        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (_hookId != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hookId);
            _hookId = IntPtr.Zero;
        }
    }
}

public sealed class KeyboardHookEventArgs : EventArgs
{
    public uint VirtualKey { get; }
    public uint ScanCode { get; }
    public bool IsExtended { get; }
    public bool IsInjected { get; }
    public bool IsDown { get; }

    /// <summary>Si un handler lo pone en true, la tecla no se propaga a nadie más.</summary>
    public bool Suppress { get; set; }

    public KeyboardHookEventArgs(uint virtualKey, uint scanCode, bool isExtended, bool isInjected, bool isDown)
    {
        VirtualKey = virtualKey;
        ScanCode = scanCode;
        IsExtended = isExtended;
        IsInjected = isInjected;
        IsDown = isDown;
    }
}
