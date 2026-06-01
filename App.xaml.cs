using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using AmpzDesktopBooster.Desktops;
using AmpzDesktopBooster.Hotkeys;
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

        // Single-instance: si ya hay una corriendo, avisamos y salimos SIN montar hooks ni UI.
        _instanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out bool isNew);
        if (!isNew)
        {
            MessageBox.Show(
                "Ampz Desktop Booster ya está corriendo.",
                "Ampz Desktop Booster",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
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
        });

        // Watchdog del hook: cuando la barra cambia su z-order (ocultarse/reaparecer en pantalla
        // completa), Windows corta la entrega de teclas al hook global hasta el próximo cambio de
        // foco. Re-armamos el hook justo después de ese cambio de z-order → el "click que lo
        // arregla", automático. Sin esto, las hotkeys se cuelgan al salir de un video fullscreen.
        bar.OnBarZOrderChanged = () => _hotkeys?.ReinstallHook();

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
            _pendingOverlayIdx = idx;
            _overlayDebounce!.Stop();
            _overlayDebounce.Start();
            _governor!.OnDesktopEntered(idx); // aplicar restricciones del desk entrante
        };

        _hotkeys.Start();
        _governor.Start();

        // Estado inicial del widget (sin overlay — no hubo "cambio").
        int current = desktops.Current;
        bar.UpdateDesk(desktops.GetName(current), desktops.GetProject(current));
    }

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
