using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Threading;
using AmpzDesktopBooster.Interop;

namespace AmpzDesktopBooster.Services;

/// <summary>
/// Avisa con <see cref="FullscreenChanged"/> (true al arrancar un fullscreen, false al terminar)
/// cuando una app entra/sale de PANTALLA COMPLETA "borderless" (YouTube con F, video, juegos modernos)
/// — el caso que ABN_FULLSCREENAPP NO dispara porque para Windows es sólo una ventana sin bordes.
///
/// ════════════════════════════════════════════════════════════════════════════════════════════
/// POR QUÉ ESTE WATCHER CORRE EN SU PROPIO THREAD (lo más importante de este archivo)
/// ════════════════════════════════════════════════════════════════════════════════════════════
/// El hook de teclado de la app (WH_KEYBOARD_LL) vive en el thread de UI a propósito (ver CLAUDE.md).
/// Windows DESCARTA un low-level keyboard hook si su callback no responde dentro de LowLevelHooksTimeout
/// (~300ms). Es decir: si el thread de UI se satura, se PIERDEN teclas.
///
/// EVENT_OBJECT_LOCATIONCHANGE es un evento de ALTÍSIMA frecuencia: una sola app parlanchina en foco
/// (un navegador con video, Electron, cualquier cosa que se repinte) genera una catarata de estos
/// eventos. Los callbacks WINEVENT_OUTOFCONTEXT se entregan al THREAD QUE INSTALÓ EL HOOK, vía su
/// bomba de mensajes. Si ese thread es el de UI, la catarata compite con el hook de teclado y te come
/// las hotkeys (síntoma: no responden hasta que hacés click en el escritorio y el foco pasa a algo
/// quieto). Acotar el hook al thread en foco NO alcanza: la app en foco puede ser, ella sola, la
/// parlanchina.
///
/// SOLUCIÓN DEFINITIVA: este watcher levanta un thread DEDICADO con su propio Dispatcher.Run(). Los
/// hooks se instalan ahí, así que TODA la catarata de WinEvents se bombea en ESE thread y jamás toca
/// el de UI. El hook de teclado queda intocable pase lo que pase. Cuando hay una transición real,
/// marshalamos FullscreenChanged de vuelta al Dispatcher de UI con BeginInvoke (async → no bloquea →
/// sin deadlock), porque tocar Topmost/la ventana WPF debe correr en el thread de UI.
///
/// Eventos que escuchamos:
///   · EVENT_SYSTEM_FOREGROUND      → cambió la ventana en primer plano (Alt+Tab a/desde un juego).
///                                     Hook GLOBAL: 1 evento por cambio de foco, volumen ínfimo.
///   · EVENT_OBJECT_LOCATIONCHANGE  → la ventana en foco cambió de tamaño/posición. Captura "apretar
///     F en el video" (misma ventana, sin cambio de foreground). Lo scopeamos igual al proceso+thread
///     en foco (re-enganchando en cada FOREGROUND): aunque ya no puede ahogar el teclado, menos ruido
///     en nuestro propio thread = menos trabajo. La ráfaga se coalesce con un debounce one-shot de 60ms.
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
    private readonly Dispatcher _uiDispatcher; // el de UI (capturado en el ctor): para marshalar el evento
    private readonly IntPtr _barHwnd;

    private Thread? _thread;          // thread dedicado que hostea la bomba de mensajes de los hooks
    private Dispatcher? _worker;      // Dispatcher de ESE thread (donde corren callbacks + debounce)
    private DispatcherTimer? _debounce;
    private IntPtr _hookForeground;
    private IntPtr _hookLocation;
    private bool _last;               // sólo se toca en el thread worker

    /// <summary>true cuando ARRANCA un fullscreen, false cuando TERMINA. Sólo en transición.</summary>
    public event Action<bool>? FullscreenChanged;

    /// <summary>Construir en el thread de UI: capturamos su Dispatcher para devolverle el evento.</summary>
    public FullscreenWatcher(IntPtr barHwnd)
    {
        _barHwnd = barHwnd;
        _proc = OnWinEvent;
        _uiDispatcher = Dispatcher.CurrentDispatcher;
    }

    public void Start()
    {
        if (_thread is not null) return;

        // Esperamos a que el thread tenga su Dispatcher y los hooks instalados antes de volver,
        // así Dispose/relanzados posteriores ven un estado consistente.
        using var ready = new ManualResetEventSlim(false);

        _thread = new Thread(() =>
        {
            _worker = Dispatcher.CurrentDispatcher;

            // El debounce se crea ACÁ → toma el Dispatcher de este thread (no el de UI). One-shot:
            // un resize dispara una RÁFAGA; esperamos a que se asiente y evaluamos una sola vez.
            _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
            _debounce.Tick += (_, _) => { _debounce!.Stop(); Evaluate(); };

            // FOREGROUND global (volumen ínfimo) + LOCATIONCHANGE scoped a la app en foco.
            _hookForeground = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND,
                IntPtr.Zero, _proc, 0, 0, WINEVENT_OUTOFCONTEXT);
            RehookLocationToForeground();

            Evaluate();    // estado inicial: por si ya hay algo en fullscreen al arrancar
            ready.Set();   // listos: el caller puede continuar

            Dispatcher.Run(); // bomba de mensajes: acá se entregan los callbacks de WinEvent
        })
        {
            IsBackground = true,
            Name = "FullscreenWatcher",
        };
        _thread.SetApartmentState(ApartmentState.STA); // un pump estilo-ventana va en STA
        _thread.Start();
        ready.Wait();
    }

    /// <summary>
    /// Re-engancha el hook de LOCATIONCHANGE para que escuche SÓLO al proceso+thread de la ventana en
    /// primer plano. Corre en el thread worker (al arrancar y en cada FOREGROUND). Aun en su propio
    /// thread, acotarlo reduce el trabajo a procesar. UnhookWinEvent debe llamarse desde el MISMO
    /// thread que llamó SetWinEventHook — acá siempre es el worker, así que está garantizado.
    /// </summary>
    private void RehookLocationToForeground()
    {
        if (_hookLocation != IntPtr.Zero) { UnhookWinEvent(_hookLocation); _hookLocation = IntPtr.Zero; }

        IntPtr fg = WindowMethods.GetForegroundWindow();
        if (fg == IntPtr.Zero) return;

        uint tid = GetWindowThreadProcessId(fg, out uint pid);
        if (tid == 0) return;

        _hookLocation = SetWinEventHook(EVENT_OBJECT_LOCATIONCHANGE, EVENT_OBJECT_LOCATIONCHANGE,
            IntPtr.Zero, _proc, pid, tid, WINEVENT_OUTOFCONTEXT);
    }

    // Corre en el thread WORKER (nunca en UI). No se puede bloquear → filtramos y diferimos.
    private void OnWinEvent(IntPtr hHook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (ev == EVENT_SYSTEM_FOREGROUND)
        {
            // Cambió la ventana en foco → re-scopear LOCATIONCHANGE a ella y reevaluar (cubre Alt+Tab
            // a/desde un juego fullscreen).
            RehookLocationToForeground();
            _debounce?.Stop();
            _debounce?.Start();
            return;
        }

        // LOCATIONCHANGE: sólo la ventana-objeto top-level en sí (idObject=0, idChild=0) y sólo si es
        // la que está en foco. Filtro baratísimo → el grueso muere acá.
        if (idObject != OBJID_WINDOW || idChild != 0 || hwnd == IntPtr.Zero) return;
        if (hwnd != WindowMethods.GetForegroundWindow()) return;

        _debounce?.Stop();
        _debounce?.Start();
    }

    // Corre en el thread WORKER. La geometría es P/Invoke puro (thread-safe). El evento se MARSHALA
    // al thread de UI: tocar Topmost / la ventana WPF debe pasar ahí. BeginInvoke = async → no bloquea
    // al worker → sin riesgo de deadlock con un Dispose que venga desde UI.
    private void Evaluate()
    {
        bool now = WindowMethods.IsForegroundFullscreenOnPrimary(_barHwnd);
        if (now == _last) return;   // sin cambio de estado → no molestamos a nadie
        _last = now;
        _uiDispatcher.BeginInvoke(() =>
        {
            try { FullscreenChanged?.Invoke(now); } catch { /* nunca dejar que un handler tumbe el hook */ }
        });
    }

    public void Dispose()
    {
        var w = _worker;
        if (w is not null)
        {
            // Unhook + stop EN el worker (mismo thread que instaló los hooks — requisito de la API),
            // y recién después apagamos su bomba de mensajes.
            w.Invoke(() =>
            {
                _debounce?.Stop();
                if (_hookForeground != IntPtr.Zero) { UnhookWinEvent(_hookForeground); _hookForeground = IntPtr.Zero; }
                if (_hookLocation   != IntPtr.Zero) { UnhookWinEvent(_hookLocation);   _hookLocation   = IntPtr.Zero; }
            });
            w.InvokeShutdown(); // termina Dispatcher.Run() → el thread sale
        }
        _thread?.Join(1000);
        _thread = null;
        _worker = null;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmod,
        WinEventDelegate proc, uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    // Thread+proceso dueños de un hwnd: pid para scopear el hook (return: tid). Propio y privado
    // (WindowMethods lo tiene private en otro partial → no podemos reusarlo desde acá).
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
}
