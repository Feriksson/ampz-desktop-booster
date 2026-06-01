using System;
using System.Windows.Threading;
using AmpzDesktopBooster.Interop;

namespace AmpzDesktopBooster.Apps;

/// <summary>
/// Trae al frente, con foco de teclado, una ventana que la app acaba de lanzar pero que CREA OTRO
/// proceso (explorer.exe para carpetas/Descargas; el "monarca" de Windows Terminal). El problema:
/// esas ventanas nacen SIN foreground. El proceso creador no es el foreground —explorer.exe ya
/// estaba vivo, el monarca de WT también— y encima nosotros venimos de un hotkey global, así que
/// tampoco somos foreground; el anti-robo de foco de Windows entonces deja la ventana nueva atrás.
///
/// Además la ventana aparece ASÍNCRONO (la crea ese otro proceso), así que justo después del
/// Process.Start todavía no existe. Por eso esperamos —sin bloquear el hilo de UI, con un
/// <see cref="DispatcherTimer"/> que reintenta— a que aparezca y recién ahí la traemos con
/// <see cref="WindowMethods.ForceForeground"/> (que vence el anti-robo vía AttachThreadInput).
///
/// El patrón nació en Shell para Windows Terminal; acá está generalizado para reusarlo también en
/// carpetas (PathOpener) y Descargas (QuickActions) — una sola copia, no tres.
/// </summary>
public static class WindowFocuser
{
    /// <summary>
    /// Espera (con techo de reintentos) a que aparezca una ventana visible que cumpla
    /// <paramref name="match"/> y la trae al frente. NO bloquea: corre en el hilo de UI (el router
    /// difiere todo al Dispatcher), por eso el <see cref="DispatcherTimer"/> es seguro. Si la ventana
    /// nunca aparece (la app falló al abrir), larga al agotar los reintentos en vez de girar para
    /// siempre. Sobrevive al cierre de la ventana que lo disparó: el timer cuelga del Dispatcher del
    /// hilo, no de una ventana.
    /// </summary>
    /// <param name="match">Identifica la ventana por HWND (clase/título/escritorio). Ya viene filtrada por visible.</param>
    /// <param name="preserveMaximized">No tocarle el tamaño a la ventana (solo des-minimizar si vino iconizada).</param>
    /// <param name="retries">Reintentos antes de largar (25 × 120ms ≈ 3s por defecto).</param>
    /// <param name="intervalMs">Período entre reintentos, en milisegundos.</param>
    public static void FocusWhenReady(Func<IntPtr, bool> match, bool preserveMaximized = true,
                                      int retries = 25, int intervalMs = 120)
    {
        int attempts = 0;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(intervalMs) };
        timer.Tick += (_, _) =>
        {
            IntPtr w = WindowMethods.FindVisible(match);
            if (w != IntPtr.Zero)
            {
                timer.Stop();
                WindowMethods.ForceForeground(w, preserveMaximized);
            }
            else if (++attempts >= retries)
            {
                timer.Stop();
            }
        };
        timer.Start();
    }
}
