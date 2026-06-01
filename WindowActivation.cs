using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using AmpzDesktopBooster.Interop;

namespace AmpzDesktopBooster;

/// <summary>
/// Extensiones para mostrar ventanas utilitarias con foco de teclado CONFIABLE. Las utilidades se
/// abren desde hotkeys globales: en ese instante nuestro proceso NO es el foreground, así que
/// <c>Show()</c> + <c>Activate()</c> no alcanza (Windows bloquea el cambio de foreground como
/// protección anti-robo-de-foco). Forzamos el primer plano con el truco de AttachThreadInput
/// (ver <see cref="WindowMethods.ForceForeground"/>) y, encima, REINTENTAMOS — ver <see cref="ForceWithRetry"/>.
/// </summary>
internal static class WindowActivation
{
    /// <summary>Muestra la ventana y le fuerza el primer plano + foco de teclado.</summary>
    public static void ShowFocused(this Window window)
    {
        window.Show();
        // Show() es síncrono: al volver, el HWND ya existe y la ventana está visible.
        ForceWithRetry(window);
    }

    /// <summary>Trae al frente una ventana YA abierta (re-press de singletons: Config/Notes/Paths).</summary>
    public static void BringToFront(this Window window)
        => ForceWithRetry(window);

    /// <summary>
    /// Fuerza foreground + foco de teclado, con REINTENTOS. Un solo <c>ForceForeground</c> sincrónico
    /// justo después de <c>Show()</c> es el instante MÁS racy: todavía se está soltando la tecla Win
    /// del hotkey y WPF está activando la ventana por su cuenta, así que el <c>SetForegroundWindow</c>
    /// inicial a veces NO prende. Como estas ventanas son <c>Topmost</c>, quedan al frente igual pero
    /// SIN foco de teclado → el teclado sigue en la ventana de atrás (ej. Esc no cierra). Por eso, igual
    /// que <see cref="WindowFocuser"/> hace con las ventanas de otros procesos, reintentamos en ticks
    /// del Dispatcher —ya con el foreground asentado— hasta SER el foreground real. Techo ~600ms para
    /// no girar para siempre. Corre en el hilo de UI (el router difiere todo al Dispatcher).
    ///
    /// ⚠ CORTE SI LA VENTANA YA CERRÓ — esto es CRÍTICO, no opcional: si el usuario cierra la ventana
    /// (Esc) antes de que ganemos el foreground, el hwnd queda muerto y la condición "soy foreground"
    /// no se cumple NUNCA → el timer machacaría <c>ForceForeground</c> (y su <c>AttachThreadInput</c>)
    /// las 10 veces contra una ventana fantasma. Eso deja el estado de input del thread de UI trabado,
    /// y como el hook de teclado vive en ESE thread, se COMEN las hotkeys hasta que un click rompe el
    /// attach. Era exactamente el bug de "hotkeys muertas hasta hacer click". Por eso chequeamos
    /// <c>IsVisible</c>/<c>IsLoaded</c> en cada tick y largamos apenas la ventana deja de estar viva.
    /// </summary>
    private static void ForceWithRetry(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == System.IntPtr.Zero) return;

        WindowMethods.ForceForeground(hwnd); // intento inmediato: cubre el caso común (no-racy)

        int attempts = 0;
        var timer = new DispatcherTimer { Interval = System.TimeSpan.FromMilliseconds(60) };
        timer.Tick += (_, _) =>
        {
            // Cortar si: la ventana ya cerró (¡el fix del cuelgue!), ya SOMOS el foreground, o
            // agotamos el techo (~10 × 60ms). Cualquiera de las tres frena el machaque.
            if (!window.IsVisible || !window.IsLoaded
                || WindowMethods.GetForegroundWindow() == hwnd || ++attempts >= 10)
            {
                timer.Stop();
                return;
            }
            WindowMethods.ForceForeground(hwnd);
        };
        timer.Start();
    }
}
