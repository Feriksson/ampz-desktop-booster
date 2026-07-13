using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using AmpzDesktopBooster.Desktops;
using AmpzDesktopBooster.Hotkeys;
using AmpzDesktopBooster.Persistence;
using AmpzDesktopBooster.Services.Browser;
using AmpzDesktopBooster.Services.Tasks;
using AmpzDesktopBooster.Services.Usage;

namespace AmpzDesktopBooster;

/// <summary>
/// Punto de arranque. Levanta la barra (AppBar + tray + widget de desktop), el overlay central,
/// el hook de teclado para navegar virtual desktops por nombre, y el listener que dispara el
/// feedback visual ante CUALQUIER cambio de desktop.
///
/// El hook se instala en el thread de UI a propósito: WPF bombea mensajes en ese thread, que es
/// justo lo que un WH_KEYBOARD_LL (y el PostMessage de VirtualDesktopAccessor) necesitan.
/// </summary>
public partial class App : Application
{
    private HotkeyService? _hotkeys;
    private HotkeyRouter? _router;
    private DesktopChangeListener? _vdListener;
    private WindowGovernor? _governor;
    private UsageService? _usage;
    private Services.Attention.AttentionService? _attention;
    private Services.Attention.AttentionPipeServer? _attentionPipe;
    private BrowserPipeServer? _browserPipe;
    private DispatcherTimer? _overlayDebounce;
    private int _pendingOverlayIdx = -1;

    // Guard de instancia única. El nombre "Global\" lo hace válido entre sesiones del usuario.
    // Si dos instancias corren a la vez tendríamos dos hooks de teclado y dos listeners
    // peleándose — exactamente el lío que viste con los procesos 15616 y 16396.
    private const string SingleInstanceMutexName = "Global\\AmpzDesktopBooster_SingleInstance";
    private Mutex? _instanceMutex;

    private DesktopConfig _desktopConfig = new();
    private Apps.AppsConfig _appsConfig = new();
    private ConfigWindow? _configWindow;

    // Una app que vive en tu escritorio NUNCA crashea en silencio.
    private static readonly string LogPath =
        Path.Combine(AppContext.BaseDirectory, "ampz-crash.log");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // i18n: cargar el idioma persistido ANTES de montar cualquier ventana o mostrar mensajes.
        // El modelo es por reinicio: con el idioma fijado acá, cada ventana se construye ya traducida.
        Services.Localization.Loc.Init();

        // ¿Nos lanzaron con una URL? (Windows hace esto cuando somos el navegador elegido y clickeás
        // un link.) La detectamos ANTES del mutex: define cómo se comporta la segunda instancia.
        string? urlArg = TryGetUrlArg(e.Args);

        // Single-instance: si ya hay una corriendo, NO montamos otra app.
        _instanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out bool isNew);
        if (!isNew)
        {
            if (urlArg is not null)
            {
                // Shim de navegador: le pasamos la URL a la instancia primaria (que la abrirá en SU
                // desk = el del usuario) y salimos EN SILENCIO. Si el pipe no responde, la abrimos
                // nosotros antes de morir para no perder el link.
                if (!BrowserPipeServer.SendUrl(urlArg))
                    BrowserShim.OpenInBrave(urlArg, BrowserSettings.Load().RealBrowserPath);
            }
            else
            {
                // Arranque manual con la app ya corriendo → el aviso de siempre.
                MessageBox.Show(
                    Services.Localization.Loc.T("App.AlreadyRunning"),
                    "Ampz Desktop Booster",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            _instanceMutex.Dispose();
            _instanceMutex = null;
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, args) =>
        {
            WriteCrash("Dispatcher", args.Exception);
            args.Handled = true; // no dejamos que el crash se lleve la app puesta
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            WriteCrash("AppDomain", args.ExceptionObject as Exception);

        var desktops = new DesktopService();
        var projects = new ProjectStore();
        _appsConfig = Apps.AppsConfig.Load();
        var pins = new PinStore();
        var restrictions = new RestrictionStore();
        var taskSession = new TaskSessionStore(); // tarea activa por desk (efímera, igual que la sesión de proyectos)
        desktops.ProjectLookup = projects.GetDeskProject; // el proyecto activo sale de la sesión
        Apps.Shell.Desktops = desktops; // ventaneo de terminal POR ESCRITORIO (Win+`, "Abrir con")

        // Cheatsheets de atajos per-app para el Shortcuts Helper (Win+/). Precarga los defaults
        // una sola vez (flag persistente) sin pisar lo que el usuario ya tenga cargado.
        var appShortcuts = Apps.AppShortcutStore.Load();
        appShortcuts.PreloadDefaults();

        // Bootstrap de escritorios: crea/renombra el set gestionado si está activado.
        // Corre ANTES de instalar el hook y de cablear el listener → sin overlay-spam.
        _desktopConfig = DesktopConfig.Load();
        if (_desktopConfig.AutoCreate)
        {
            try { DesktopBootstrapper.Ensure(_desktopConfig, desktops); }
            catch (Exception ex) { WriteCrash("Bootstrap", ex); }
        }

        // Uso de tokens de IA: el servicio es dueño del polling. Arranca ACÁ, en el core, ANTES de
        // la barra → el primer "tiro" está garantizado aunque la BarWindow tarde, falle o no exista.
        _usage = new UsageService();
        _usage.Start();

        // Atención por desk: un integrador externo (hoy los hooks de Claude, mañana lo que sea) postea
        // al Named Pipe que algo en SU proceso reclama tu atención. El servicio resuelve el desk por el
        // PID y mantiene el estado (futuro widget); por ahora dispara un Toast. Se construye en UI
        // (capturamos el Dispatcher para marshalear las señales) y vive en el core, como UsageService.
        _attention = new Services.Attention.AttentionService(desktops);
        _attentionPipe = new Services.Attention.AttentionPipeServer();
        _attentionPipe.Received += sig => _attention.OnSignal(sig);
        _attentionPipe.Start();

        // Shim de navegador: la instancia primaria escucha las URLs que le pasan las secundarias
        // (lanzadas por Windows al clickear un link) y las abre en el navegador real con --new-window,
        // en ESTE proceso → la ventana nace en el escritorio actual del usuario, sin catapulteo.
        // Releemos browser.json en cada URL (esporádicas) para tomar el path que el usuario tenga.
        _browserPipe = new BrowserPipeServer();
        _browserPipe.UrlReceived += url => BrowserShim.OpenInBrave(url, BrowserSettings.Load().RealBrowserPath);
        _browserPipe.Start();

        // Auto-cura del registro: si el shim está activado, re-registramos en CADA arranque. Así el
        // `command` del registro apunta SIEMPRE al exe actual — si la app se movió o se reinstaló, el
        // path viejo quedaría roto (handler huérfano, justo el fantasma que dejaba el AHK legacy). No
        // toca el default del SO (no se puede); solo mantiene sana nuestra entrada de candidato.
        if (BrowserSettings.Load().Enabled)
            BrowserShim.Register();

        // La barra: AppBar real + tray + widget de desktop a la derecha.
        var bar = new BarWindow();
        bar.OpenConfig = () => ShowConfig(desktops, restrictions, pins, () =>
        {
            int c = desktops.Current;
            bar.UpdateDesk(desktops.GetName(c), desktops.GetProject(c));
        });
        bar.AttachUsage(_usage); // la barra se suscribe y pinta el snapshot apenas llega
        bar.Show();

        // Gobierno de ventanas: enforcement de pins + restricciones (hook EVENT_OBJECT_SHOW).
        _governor = new WindowGovernor(desktops, pins, restrictions);

        // Hook global de teclado + ruteo (navegación, proyectos, paneles, pins, restricciones).
        _hotkeys = new HotkeyService();
        _router = new HotkeyRouter(_hotkeys, desktops, projects, _appsConfig, pins, restrictions,
            appShortcuts, () =>
        {
            int c = desktops.Current;
            bar.UpdateDesk(desktops.GetName(c), desktops.GetProject(c));
        },
            taskSession,
            // Refresca el widget de tarea del desk ACTUAL (tras pickear o desanclar).
            () => bar.UpdateDeskTask(taskSession.GetDeskTask(desktops.Current)),
            // Catálogo GLOBAL de puertos/servicios locales (Win+Numpad+). Durable en ports.json.
            PortStore.Load());

        // Click en el widget de tarea → el detalle (lo orquesta el router, que tiene la sesión).
        bar.OnTaskWidgetClicked = () => _router.ShowTaskDetail();

        // Widget de atención (dots por desk que reclama). App es el TRADUCTOR: convierte el estado del
        // servicio (Pending: desk→nivel) en lo que la barra pinta (idx + nombre + urgencia + proyecto).
        // El servicio no conoce la UI; la barra no conoce el dominio. Click en un dot → saltás al desk.
        bar.OnAttentionDeskClicked = idx => desktops.GoTo(idx);
        _attention.Changed += () =>
        {
            var items = _attention.Pending
                .OrderBy(kv => kv.Key)
                .Select(kv => (
                    Index: kv.Key,
                    DeskName: desktops.GetName(kv.Key),
                    Urgent: kv.Value == Services.Attention.AttentionLevel.ActionNeeded,
                    Project: desktops.GetProject(kv.Key)))
                .ToList();
            bar.UpdateAttention(items);
        };

        // Watchdog del hook: cuando la barra cambia su z-order (ocultarse/reaparecer en pantalla
        // completa), Windows corta la entrega de teclas al hook global hasta el próximo cambio de
        // foco. Re-armamos el hook justo después de ese cambio de z-order → el "click que lo
        // arregla", automático. Sin esto, las hotkeys se cuelgan al salir de un video fullscreen.
        bar.OnBarZOrderChanged = () => _hotkeys?.ReinstallHook();

        // Al CERRAR una ventana utilitaria (Esc) en un desk SIN más ventanas, el foreground queda
        // HUÉRFANO y el hook se cuelga. Reinstalar el hook NO alcanza si el foco sigue en el aire:
        // hay que DEVOLVER el foco a algo real (la ventana del frente, o el escritorio — como las
        // versiones viejas). Diferimos un toque para que el cierre se complete y Windows intente (y
        // falle) resolver el foreground; recién ahí lo corregimos. El ReinstallHook va de RED, por
        // si el ForceForeground tocó la entrega del hook.
        WindowActivation.OnUtilityWindowClosed = () =>
        {
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
            t.Tick += (_, _) =>
            {
                t.Stop();
                Interop.WindowMethods.RestoreForegroundOrDesktop("AmpzDesktopBooster.exe");
                _hotkeys?.ReinstallHook();
            };
            t.Start();
        };

        // Win+D ("Mostrar escritorio"): lo intercepta el hook (el shell NUNCA lo ve) y en su lugar
        // minimizamos TODO menos lo nuestro → la barra queda firme. La barra no puede ganarle la
        // guerra de z-order al Show Desktop nativo (es una AppBar de terceros, no la taskbar del
        // shell), así que reemplazamos el comportamiento entero. Diferido al Dispatcher: el callback
        // del hook no se puede bloquear. Tras minimizar todo, el foreground puede quedar HUÉRFANO
        // (mismo mecanismo que cerrar la última ventana de un desk) → aplicamos la MISMA red de
        // OnUtilityWindowClosed: ~80ms a que se asiente, foco al escritorio y ReinstallHook.
        _hotkeys.ShowDesktopRequested += () => Dispatcher.BeginInvoke(() =>
        {
            Interop.WindowMethods.MinimizeForeignTopLevel("AmpzDesktopBooster.exe");
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
            t.Tick += (_, _) =>
            {
                t.Stop();
                Interop.WindowMethods.RestoreForegroundOrDesktop("AmpzDesktopBooster.exe");
                _hotkeys?.ReinstallHook();
            };
            t.Start();
        });

        // El overlay central — persistente, oculto hasta el primer cambio de desktop.
        var overlay = new OverlayWindow();

        // Debounce del overlay: al saltar rápido entre desktops, la DLL postea un mensaje por
        // cada salto. Sin coalescer, renderizaríamos los intermedios y haría flicker. Esperamos
        // a que la ráfaga se asiente (~40ms) y mostramos SÓLO el desktop final.
        _overlayDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        _overlayDebounce.Tick += (_, _) =>
        {
            _overlayDebounce!.Stop();
            if (_pendingOverlayIdx >= 0)
                overlay.ShowOverlay(_pendingOverlayIdx, desktops);
        };

        // Una sola fuente de verdad para el feedback: el cambio de desktop (venga de donde venga)
        // actualiza el widget de la barra (inmediato) y dispara el overlay (debounced).
        _vdListener = new DesktopChangeListener();
        _vdListener.DesktopChanged += idx =>
        {
            bar.EnsurePinned(); // insurance: re-pin por si el del arranque no prendió
            bar.UpdateDesk(desktops.GetName(idx), desktops.GetProject(idx));
            bar.UpdateDeskTask(taskSession.GetDeskTask(idx)); // tarea activa de ESTE desk (o se oculta)
            _pendingOverlayIdx = idx;
            _overlayDebounce!.Stop();
            _overlayDebounce.Start();
            _governor!.OnDesktopEntered(idx); // aplicar restricciones del desk entrante
            _attention?.ClearDesk(idx);       // "lo viste, listo": apaga el aviso de atención de este desk
        };

        _hotkeys.Start();
        _governor.Start();

        // Estado inicial del widget (sin overlay — no hubo "cambio"). El widget de tarea arranca
        // oculto: la sesión es efímera, no hay tarea activa hasta que el usuario pickee una.
        int current = desktops.Current;
        bar.UpdateDesk(desktops.GetName(current), desktops.GetProject(current));
        bar.UpdateDeskTask(taskSession.GetDeskTask(current));

        // Caso "app cerrada + click en link": Windows nos lanzó CON la URL y somos la primaria.
        // Ya está todo montado → la abrimos en el navegador real, en el desk actual.
        if (urlArg is not null)
            BrowserShim.OpenInBrave(urlArg, BrowserSettings.Load().RealBrowserPath);
    }

    /// <summary>
    /// Primera arg que sea una URL http/https (lo que Windows nos pasa al ser el navegador elegido).
    /// null si no vino ninguna (arranque normal). Reusa <see cref="UrlHelper.IsUrl"/> para el criterio.
    /// </summary>
    private static string? TryGetUrlArg(string[] args) =>
        args.FirstOrDefault(a => UrlHelper.IsUrl(a) &&
            a.StartsWith("http", StringComparison.OrdinalIgnoreCase));

    /// <summary>Abre la ventana de configuración (instancia única — si ya está, la trae al frente).</summary>
    private void ShowConfig(DesktopService desktops, RestrictionStore restrictions, PinStore pins, Action onApplied)
    {
        if (_configWindow is not null)
        {
            _configWindow.BringToFront();
            return;
        }

        _configWindow = new ConfigWindow(_desktopConfig, _appsConfig, desktops, restrictions, pins, onApplied);
        _configWindow.Closed += (_, _) => _configWindow = null;
        _configWindow.ShowFocused();
    }

    /// <summary>
    /// Relanza la app desde cero y cierra la actual. Lo usa el "Restablecer todo" de la config.
    ///
    /// El nudo está en el mutex de instancia única: si lanzáramos la instancia nueva ya mismo, vería
    /// el mutex TOMADO por este proceso y se cerraría sola. Por eso delegamos a un relauncher externo
    /// (PowerShell oculto) que: (1) ESPERA a que este proceso muera —ahí OnExit libera el mutex—,
    /// (2) re-borra la config (cierra cualquier carrera si un servicio reescribió un archivo al
    /// cerrar), y (3) recién entonces abre la instancia nueva, que ya encuentra el mutex libre.
    /// </summary>
    public void RestartApplication()
    {
        string? exe = Environment.ProcessPath;
        if (exe is not null)
        {
            string dataDir = Persistence.AppPaths.DataDir;
            string ps =
                $"Wait-Process -Id {Environment.ProcessId} -ErrorAction SilentlyContinue; " +
                $"Remove-Item -Path '{dataDir}\\*' -Recurse -Force -ErrorAction SilentlyContinue; " +
                $"Start-Process -FilePath '{exe}'";
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -WindowStyle Hidden -Command \"{ps}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            try { System.Diagnostics.Process.Start(psi); }
            catch { /* si el relauncher no arranca, igual cerramos: el usuario reabre a mano y arranca limpio */ }
        }
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _governor?.Dispose();
        _vdListener?.Dispose();
        _hotkeys?.Dispose();
        _usage?.Dispose();
        _attentionPipe?.Dispose();
        _browserPipe?.Dispose();
        _instanceMutex?.ReleaseMutex();
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }

    private static void WriteCrash(string source, Exception? ex)
    {
        try
        {
            File.AppendAllText(LogPath,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] ({source}) {ex}\n\n");
        }
        catch { /* si ni siquiera podemos loguear, no hay nada que hacer */ }
    }
}
