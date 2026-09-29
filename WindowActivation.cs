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

    /// <summary>
    /// Dimensiona y centra una ventana utilitaria sobre el ÁREA DE TRABAJO (que ya excluye la
    /// taskbar y nuestra AppBar). 90% × 80% es la proporción que viene del legacy y que ya usaba
    /// Notas; vive acá para que las ventanas que la necesiten compartan UNA implementación en vez de
    /// copiarse el cálculo — si divergieran, dos ventanas hermanas abrirían de tamaños distintos sin
    /// que nadie lo haya decidido.
    ///
    /// Usa <see cref="SystemParameters.WorkArea"/>, que es el área del monitor PRIMARIO. Es la misma
    /// convención que ya tenían Notas y el overlay: estas ventanas se abren centradas en el primario,
    /// no en el monitor donde está el mouse. Si algún día eso cambia, cambia acá y para todas.
    /// </summary>
    public static void SizeToWorkArea(this Window window, double widthRatio = 0.90, double heightRatio = 0.80)
    {
        var wa = SystemParameters.WorkArea;
        window.Width = wa.Width * widthRatio;
        window.Height = wa.Height * heightRatio;
        window.Left = wa.Left + (wa.Width - window.Width) / 2;
        window.Top = wa.Top + (wa.Height - window.Height) / 2;
    }

    /// <summary>
    /// Muestra la ventana y le fuerza el primer plano + foco de teclado. Por defecto la ventana además
    /// se CIERRA SOLA al perder el foco (<see cref="CloseOnDeactivate"/>): pedido del usuario — un
    /// modal olvidado abierto mientras trabajás en otra cosa es ruido, y todo acá se reabre con un
    /// atajo. <paramref name="closeOnDeactivate"/>=false es para superficies de trabajo largo (Config),
    /// donde ir y volver de otra app es el uso normal y cerrarla sería perder el lugar.
    /// </summary>
    public static void ShowFocused(this Window window, bool closeOnDeactivate = true)
    {
        // Al cerrarse, re-armamos el hook (ver OnUtilityWindowClosed). Una sola suscripción por
        // ventana: ShowFocused se llama una vez al abrir; el re-press de los singletons usa
        // BringToFront, que NO pasa por acá → no se suscribe dos veces.
        window.Closed += (_, _) => OnUtilityWindowClosed?.Invoke();
        // ANTES del Show(): CloseOnDeactivate arranca su timer en Loaded, que dispara dentro del Show.
        if (closeOnDeactivate) window.CloseOnDeactivate();
        window.Show();
        if (closeOnDeactivate) CloseOtherUtilityWindows(window);
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
    ///
    /// ⚠ NO se cierra si el foco se fue a OTRA ventana NUESTRA (un diálogo hijo: editar variable,
    /// editar servicio, el prompt, el QR, un MessageBox; o la ventana encadenada, como el picker de
    /// contexto). Abrir un hijo DESACTIVA al padre, y sin esta guarda el padre se cerraría con el
    /// diálogo recién abierto encima. Se exceptúan la barra y el overlay: clickear la barra ES irse.
    /// El chequeo se DIFIERE un tick: dentro de Deactivated el foreground todavía puede no haber
    /// cambiado, y leerlo ahí daría la ventana vieja.
    ///
    /// Idempotente: TaskPicker/TaskDetail lo llaman en su ctor y ADEMÁS se abren con ShowFocused,
    /// que ahora también lo arma. Sin el guard serían dos timers y dos Close().
    /// </summary>
    public static void CloseOnDeactivate(this Window window)
    {
        if (!_closeOnDeactivateArmed.Add(window)) return;

        bool armed = false;
        bool closing = false;
        var arm = new DispatcherTimer { Interval = System.TimeSpan.FromMilliseconds(700) };
        arm.Tick += (_, _) =>
        {
            arm.Stop();
            armed = true;
            if (!window.IsActive) window.Activate(); // ver doc: caso AppBar-click
        };

        window.Loaded += (_, _) => arm.Start();
        window.Deactivated += (_, _) =>
        {
            if (!armed) return;
            window.Dispatcher.BeginInvoke(() =>
            {
                if (closing || !window.IsLoaded || window.IsActive) return;
                if (FocusWentToOwnWindow()) return;
                closing = true;
                window.Close();
            }, DispatcherPriority.Input);
        };

        // RED por POLL: el Deactivated de WPF NO siempre llega. Cazado con log: abierta desde un
        // hotkey con otra app en foreground y dejada con Alt+Tab, la ventana nunca recibió
        // Deactivated (el click afuera sí lo disparaba) — quedaba abierta encima mientras escribías
        // en otra app. Mismo remedio que DesktopChangeListener: no confiar sólo en el aviso, mirar
        // el foreground REAL cada 250ms mientras está armada. Se cierra si el foreground es de OTRO
        // proceso (o nadie); una ventana nuestra (hijo, picker encadenado) la deja abierta.
        var poll = new DispatcherTimer { Interval = System.TimeSpan.FromMilliseconds(250) };
        var hwnd = System.IntPtr.Zero;
        poll.Tick += (_, _) =>
        {
            if (!armed || closing || !window.IsLoaded) return;
            if (hwnd == System.IntPtr.Zero) hwnd = new WindowInteropHelper(window).Handle;
            var fg = WindowMethods.GetForegroundWindow();
            if (fg == hwnd || FocusWentToOwnWindow()) return;
            closing = true;
            poll.Stop();
            window.Close();
        };
        window.Loaded += (_, _) => poll.Start();

        window.Closing += (_, _) => closing = true;
        window.Closed += (_, _) =>
        {
            arm.Stop(); // si cierra antes de armar, no dejamos el timer colgado
            poll.Stop();
            _closeOnDeactivateArmed.Remove(window);
        };
    }

    private static readonly System.Collections.Generic.HashSet<Window> _closeOnDeactivateArmed = new();

    /// <summary>
    /// UN solo modal utilitario a la vez: al abrir uno, se cierran los demás que estén armados con
    /// <see cref="CloseOnDeactivate"/>. Hace falta porque la guarda de ese método ("el foco se fue a
    /// una ventana NUESTRA → no cierres") no distingue un diálogo HIJO de un modal HERMANO: abrir
    /// Servicios con Variables abierta dejaba las dos apiladas. Quedan afuera los que no están
    /// armados — Config (opt-out) y los diálogos hijos (ShowDialog: editar variable/servicio, prompt,
    /// QR) — así que el padre de un diálogo nunca se cierra por esto.
    ///
    /// DIFERIDO (Background): el launcher abre el picker de contexto ANTES de cerrarse a sí mismo
    /// (a propósito, ver ProjectSetterWindow). Diferido, su propio Close() corre primero y acá ya
    /// no aparece; sin diferir lo cerraríamos nosotros en medio de su handler.
    /// </summary>
    private static void CloseOtherUtilityWindows(Window opened)
    {
        opened.Dispatcher.BeginInvoke(() =>
        {
            foreach (var w in new System.Collections.Generic.List<Window>(_closeOnDeactivateArmed))
                if (!ReferenceEquals(w, opened) && w.IsLoaded)
                    w.Close();
        }, DispatcherPriority.Background);
    }

    /// <summary>
    /// ¿El foreground actual es una ventana de nuestro proceso que NO sea la barra ni el overlay?
    /// (ver la guarda de <see cref="CloseOnDeactivate"/>).
    /// </summary>
    private static bool FocusWentToOwnWindow()
    {
        var fg = WindowMethods.GetForegroundWindow();
        if (!WindowMethods.IsOwnProcessWindow(fg)) return false;

        foreach (Window w in Application.Current.Windows)
        {
            if (w is not (BarWindow or OverlayWindow)) continue;
            if (new WindowInteropHelper(w).Handle == fg) return false;
        }
        return true;
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

        // preserveMaximized: SIEMPRE. ForceForeground manda SW_RESTORE, que des-MAXIMIZA la ventana
        // (ver WindowMethods). Como acá machacamos hasta 11 veces en ~600ms, una ventana que abre
        // maximizada (ConfigWindow) se veía maximizarse y volver sola a tamaño normal — el "blink".
        // Con el flag sólo des-minimizamos si la ventana vino ICONIZADA, que es lo único que este
        // camino necesitaba de SW_RESTORE (el re-press de un singleton minimizado). Para las demás
        // utilitarias no cambia nada: son ventanas Normal, y SW_RESTORE sobre una Normal no hacía
        // nada de todos modos.
        WindowMethods.ForceForeground(hwnd, preserveMaximized: true); // inmediato: caso común (no-racy)

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
            WindowMethods.ForceForeground(hwnd, preserveMaximized: true);
        };
        timer.Start();
    }
}
