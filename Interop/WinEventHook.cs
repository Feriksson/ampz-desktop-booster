using System;
using System.Runtime.InteropServices;

namespace AmpzDesktopBooster.Interop;

/// <summary>
/// Hook global de "apareció una ventana" (EVENT_OBJECT_SHOW). Es lo que el legacy usaba para
/// el enforcement de pins y restricciones: cuando una app abre/muestra una ventana, reaccionamos.
///
/// OUT_OF_CONTEXT: el callback corre en NUESTRO proceso (no inyectado en el otro), pero igual NO
/// se puede demorar — el handler debe diferir todo trabajo (Dispatcher). Filtramos idObject=0 al
/// tope: EVENT_OBJECT_SHOW dispara para botones, scrollbars, tooltips... sólo idObject=0 son
/// ventanas reales. El delegate VIVE en un field — si lo recolecta el GC, Windows llama a memoria
/// liberada y la app se cae (mismo cuidado que el hook de teclado).
/// </summary>
public sealed class WinEventHook : IDisposable
{
    private const uint EVENT_OBJECT_SHOW = 0x8002;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;

    private delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

    private readonly WinEventDelegate _proc; // field → no lo recolecta el GC
    private IntPtr _hook;

    /// <summary>Se dispara cuando aparece un objeto-ventana real (idObject=0). Arg = hwnd.</summary>
    public event Action<IntPtr>? WindowShown;

    public WinEventHook() => _proc = Callback;

    public void Install()
    {
        if (_hook != IntPtr.Zero) return;
        _hook = SetWinEventHook(EVENT_OBJECT_SHOW, EVENT_OBJECT_SHOW, IntPtr.Zero, _proc, 0, 0, WINEVENT_OUTOFCONTEXT);
    }

    private void Callback(IntPtr hHook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (idObject != 0 || hwnd == IntPtr.Zero) return; // sólo ventanas reales
        try { WindowShown?.Invoke(hwnd); } catch { /* nunca dejar reventar el callback del hook */ }
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero)
        {
            UnhookWinEvent(_hook);
            _hook = IntPtr.Zero;
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmod,
        WinEventDelegate proc, uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWinEvent(IntPtr hWinEventHook);
}
