using System.Runtime.InteropServices;
using System.Windows.Threading;
using AmpzDesktopBooster.Interop;

namespace AmpzDesktopBooster.Services;

/// <summary>
/// Avisa con <see cref="FullscreenChanged"/> (true al arrancar un fullscreen, false al terminar)
/// cuando una app entra/sale de PANTALLA COMPLETA "borderless" (YouTube con F, video, juegos modernos)
/// — el caso que ABN_FULLSCREENAPP NO dispara porque para Windows es sólo una ventana sin bordes.
///
/// 100% EVENT-DRIVEN, CERO polling: usa el mismo mecanismo que el <see cref="WinEventHook"/> del
/// WindowGovernor (SetWinEventHook). Reaccionamos a dos eventos que el sistema YA emite:
///   · EVENT_SYSTEM_FOREGROUND      → cambió la ventana en primer plano (Alt+Tab a/desde un juego).
///   · EVENT_OBJECT_LOCATIONCHANGE  → una ventana cambió de tamaño/posición. ESTE es el que captura
///     "apretar F en el video": la MISMA ventana en foco se redimensiona a fullscreen, sin cambio de
///     foreground ni un SHOW. Es ruidoso, así que lo filtramos durísimo en el callback (sólo la
///     ventana-objeto en sí y sólo si es la que está en foco) y coalescemos ráfagas con un debounce
///     one-shot de 60ms (mismo patrón que el overlay). El trabajo geométrico real corre UNA vez por
///     transición, no en un loop.
///
/// La DETECCIÓN del borderless es por geometría (rect == monitor) en
/// <see cref="WindowMethods.IsForegroundFullscreenOnPrimary"/> — inevitable: "fullscreen sin bordes"
/// es, por definición, una ventana que tapa el monitor; no hay un flag del SO que lo diga.
/// </summary>
public sealed class FullscreenWatcher : IDisposable
{
    private const uint EVENT_SYSTEM_FOREGROUND     = 0x0003;
    private const uint EVENT_OBJECT_LOCATIONCHANGE = 0x800B;
    private const uint WINEVENT_OUTOFCONTEXT       = 0x0000;
    private const int  OBJID_WINDOW                = 0; // la ventana en sí (no sub-objetos ni cursor)

    private delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

    private readonly WinEventDelegate _proc; // field → no lo recolecta el GC (si no, crash en el callback)
    private readonly DispatcherTimer _debounce;
    private readonly IntPtr _barHwnd;
    private IntPtr _hookForeground;
    private IntPtr _hookLocation;
    private bool _last;

    /// <summary>true cuando ARRANCA un fullscreen, false cuando TERMINA. Sólo en transición.</summary>
    public event Action<bool>? FullscreenChanged;

    public FullscreenWatcher(IntPtr barHwnd)
    {
        _barHwnd = barHwnd;
        _proc = OnWinEvent;
        // Debounce one-shot: un resize dispara una RÁFAGA de LOCATIONCHANGE; esperamos a que se asiente
        // y evaluamos una sola vez. No es polling — se ARMA recién cuando llega un evento.
        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
        _debounce.Tick += (_, _) => { _debounce.Stop(); Evaluate(); };
    }

    public void Start()
    {
        if (_hookForeground != IntPtr.Zero) return;
        _hookForeground = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero, _proc, 0, 0, WINEVENT_OUTOFCONTEXT);
        _hookLocation = SetWinEventHook(EVENT_OBJECT_LOCATIONCHANGE, EVENT_OBJECT_LOCATIONCHANGE,
            IntPtr.Zero, _proc, 0, 0, WINEVENT_OUTOFCONTEXT);
        Evaluate(); // estado inicial: por si ya hay algo en fullscreen al arrancar
    }

    // OUT_OF_CONTEXT: el callback corre en NUESTRO proceso, en el thread que instaló el hook (el de UI
    // de WPF, que bombea mensajes). NO se puede bloquear → filtramos y diferimos, nada de trabajo pesado.
    private void OnWinEvent(IntPtr hHook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        // Filtro baratísimo: el 99% de los LOCATIONCHANGE (sub-controles, cursor con idObject<0, etc.)
        // mueren acá en dos comparaciones.
        if (idObject != OBJID_WINDOW || idChild != 0 || hwnd == IntPtr.Zero) return;

        // Para LOCATIONCHANGE sólo importa si se movió/redimensionó la ventana EN FOCO; las de fondo
        // no nos interesan (y son la mayor parte del ruido).
        if (ev == EVENT_OBJECT_LOCATIONCHANGE && hwnd != WindowMethods.GetForegroundWindow()) return;

        _debounce.Stop();
        _debounce.Start();
    }

    private void Evaluate()
    {
        bool now = WindowMethods.IsForegroundFullscreenOnPrimary(_barHwnd);
        if (now == _last) return;   // sin cambio de estado → no molestamos a nadie
        _last = now;
        try { FullscreenChanged?.Invoke(now); } catch { /* nunca dejar que un handler tumbe el hook */ }
    }

    public void Dispose()
    {
        _debounce.Stop();
        if (_hookForeground != IntPtr.Zero) { UnhookWinEvent(_hookForeground); _hookForeground = IntPtr.Zero; }
        if (_hookLocation   != IntPtr.Zero) { UnhookWinEvent(_hookLocation);   _hookLocation   = IntPtr.Zero; }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmod,
        WinEventDelegate proc, uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWinEvent(IntPtr hWinEventHook);
}
