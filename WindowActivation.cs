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
    /// <summary>
    /// App lo cablea con HotkeyService.ReinstallHook. Cuando una ventana utilitaria (Variables,
    /// Notas, Config…) se CIERRA en un desk SIN otras ventanas, el foreground queda HUÉRFANO y el
    /// hook global deja de recibir teclas — mismo mecanismo que el bug de z-order: el SO corta la
    /// entrega hasta el próximo cambio de foco. Reinstalar el hook al cerrar restaura la entrega
    /// sin depender de que aparezca un nuevo foreground. (Las versiones viejas mandaban el foco al
    /// escritorio, lo que también revivía el hook; esto logra lo mismo sin tocar el foco.)
    /// </summary>
    public static System.Action? OnUtilityWindowClosed;

    /// <summary>Muestra la ventana y le fuerza el primer plano + foco de teclado.</summary>
    public static void ShowFocused(this Window window)
    {
        // Al cerrarse, re-armamos el hook (ver OnUtilityWindowClosed). Una sola suscripción por
        // ventana: ShowFocused se llama una vez al abrir; el re-press de los singletons usa
        // BringToFront, que NO pasa por acá → no se suscribe dos veces.
        window.Closed += (_, _) => OnUtilityWindowClosed?.Invoke();
        window.Show();
        // Show() es síncrono: al volver, el HWND ya existe y la ventana está visible.
        ForceWithRetry(window);
    }

    /// <summary>Trae al frente una ventana YA abierta (re-press de singletons: Config/Notes/Paths).</summary>
    public static void BringToFront(this Window window)
        => ForceWithRetry(window);

    /// <summary>
    /// Cierra la ventana sola cuando pierde el foreground: al hacer click AFUERA o al cambiar de
    /// virtual desktop (cambiar de desk activa una ventana del nuevo desk → ésta se DESACTIVA, así
    /// que un solo mecanismo cubre ambos). Para pickers/flyouts efímeros (TaskPicker, TaskDetail).
    ///
    /// ⚠ Se ARMA recién a los 700ms — NO antes. <see cref="ForceWithRetry"/> machaca el foreground
    /// hasta ~600ms tras el Show(); en ese tramo la activación rebota (Deactivated→Activated) y un
    /// Deactivated crudo cerraría la ventana al instante de abrirla. Esperar pasado ese techo evita
    /// el cierre-en-la-cara. 700ms es imperceptible: el usuario está leyendo/tipeando, no clickeando
    /// afuera en el primer instante. Corre en el hilo de UI (estas ventanas se abren ahí).
    ///
    /// ⚠ Al armarse, FORZAMOS Activate() si WPF aún no marcó IsActive. Bug cazado: cuando la ventana
    /// se abre desde un click en la BarWindow (AppBar sin activación) — vs un hotkey global —, el
    /// flujo de WM_ACTIVATE se descoordina y WPF NO marca IsActive nunca. Sin IsActive=true, el
    /// evento Deactivated NO dispara → clicks afuera no cierran. Sólo después de clickear ON la
    /// ventana se sincronizaba. Forzar Activate al armar tickea WPF y los Deactivated posteriores
    /// disparan normales.
    /// </summary>
    public static void CloseOnDeactivate(this Window window)
    {
        bool armed = false;
        var arm = new DispatcherTimer { Interval = System.TimeSpan.FromMilliseconds(700) };
        arm.Tick += (_, _) =>
        {
            arm.Stop();
            armed = true;
            if (!window.IsActive) window.Activate(); // ver doc: caso AppBar-click
        };

        window.Loaded += (_, _) => arm.Start();
        window.Deactivated += (_, _) => { if (armed) window.Close(); };
        window.Closed += (_, _) => arm.Stop(); // si cierra antes de armar, no dejamos el timer colgado
    }

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
