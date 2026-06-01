using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Threading;
using AmpzDesktopBooster.Desktops;
using AmpzDesktopBooster.Services;
using AmpzDesktopBooster.Services.Usage;

namespace AmpzDesktopBooster;

/// <summary>
/// La barra: una AppBar real de Windows con widgets modulares (hora, fecha, CPU,
/// RAM, red, batería) + ícono en la bandeja. Era el MainWindow del proyecto "bar";
/// acá pasa a ser BarWindow para convivir con el resto de la app (hook de teclado).
/// </summary>
public partial class BarWindow : Window
{
    private readonly SystemMonitor _monitor = new();
    private readonly WidgetSettings _settings = WidgetSettings.Load();
    private AppBarManager? _appBar;
    private FullscreenWatcher? _fullscreen;
    private TrayIconService? _tray;
    private DispatcherTimer? _timer;

    // El polling de uso lo hace UsageService (arranca en App.OnStartup); la barra sólo CONSUME.
    private UsageService? _usage;
    private bool _usageEverLoaded;   // true tras el 1er fetch OK → un error transitorio no vacía la barra

    private const double BarHeight = 32;

    /// <summary>Lo invoca el item "Configuración" del tray. Lo setea App antes del Show().</summary>
    public Action? OpenConfig { get; set; }

    /// <summary>
    /// App lo setea con HotkeyService.ReinstallHook. Lo reenviamos al AppBar: cuando la barra cambia
    /// su z-order (al entrar/salir de pantalla completa), eso corrompe la entrega de teclas del hook
    /// global, así que lo re-armamos justo después. Es el "click que lo arregla", automático.
    /// </summary>
    public Action? OnBarZOrderChanged
    {
        set { if (_appBar is not null) _appBar.ZOrderChanged = value; _pendingZOrderChanged = value; }
    }
    private Action? _pendingZOrderChanged; // por si se setea antes de que exista el AppBar

    public BarWindow()
    {
        InitializeComponent();

        // La fecha arranca ya, sin esperar el primer tick.
        UpdateDate();

        SourceInitialized += OnSourceInitialized;
        Closing += OnClosing;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        // Recién acá la ventana tiene HWND: registramos la AppBar real.
        _appBar = new AppBarManager(this, BarHeight);
        _appBar.ZOrderChanged = _pendingZOrderChanged; // si App ya lo seteó antes del handle
        _appBar.Register();

        // Pinear la barra a TODOS los virtual desktops. Sin esto vive sólo en el desktop
        // donde se creó y queda vacía/ausente en los demás (mismo bug que tenía el overlay).
        EnsurePinned();

        // Sacar la barra del Alt-Tab / Win+Tab (Task View). ShowInTaskbar=False la saca de la
        // taskbar pero NO del switcher — eso lo hace WS_EX_TOOLWINDOW. La barra es chrome, no
        // una "app" entre la que el usuario quiera alternar.
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        var ex = Interop.WindowMethods.GetWindowLongPtr(hwnd, Interop.WindowMethods.GWL_EXSTYLE).ToInt64();
        ex |= Interop.WindowMethods.WS_EX_TOOLWINDOW;
        Interop.WindowMethods.SetWindowLongPtr(hwnd, Interop.WindowMethods.GWL_EXSTYLE, new IntPtr(ex));

        // Bajar la barra cuando hay una app en PANTALLA COMPLETA (juego, video, app 3D). El AppBar
        // ya escucha ABN_FULLSCREENAPP para el fullscreen EXCLUSIVO; el watcher cubre el "borderless"
        // (YouTube con F, juegos modernos) que esa notificación no dispara. Ambos pegan al MISMO
        // setter idempotente del AppBar, así que no se pisan. Corre en su propio thread (no en UI),
        // así su catarata de WinEvents no le roba tiempo al hook de teclado.
        _fullscreen = new FullscreenWatcher(hwnd);
        _fullscreen.FullscreenChanged += suppressed => _appBar?.SetFullscreenSuppressed(suppressed);
        _fullscreen.Start();

        // Bloquear Alt+F4 (y el "Cerrar" del menú de sistema): la barra es chrome, NO una app —
        // igual que la taskbar de Windows, no se cierra con Alt+F4. Interceptamos el mensaje en su
        // origen (WM_SYSCOMMAND/SC_CLOSE) y lo tragamos. Esto NO afecta el "Salir" del tray, que
        // llama Application.Current.Shutdown() por la ruta interna de WPF (nunca pasa por SC_CLOSE),
        // así que el cleanup de OnClosing sigue corriendo intacto en el apagado legítimo.
        // Sin esto, Alt+F4 cierra sólo la ventana pero con ShutdownMode=OnExplicitShutdown el
        // proceso queda zombie (hook + mutex tomados) — el mismo pozo que evita el "Salir".
        System.Windows.Interop.HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);

        // Tray icon: cerrar, reposicionar y el submenú de widgets modulares.
        // "Salir" apaga la APP entera (Shutdown), no sólo la barra: con ShutdownMode=
        // OnExplicitShutdown y un overlay oculto siempre vivo, cerrar sólo esta ventana
        // dejaba el proceso colgado (con el hook, el listener y el mutex tomados → zombie).
        _tray = new TrayIconService(
            settings: _settings,
            onExit: () => Application.Current.Shutdown(),
            onReposition: () => _appBar?.PositionBar(),
            onToggle: OnWidgetToggled,
            onOpenConfig: () => OpenConfig?.Invoke(),
            autoStartEnabled: AutoStartService.IsEnabled(),
            onToggleAutoStart: AutoStartService.Set);

        // Aplicamos el estado persistido de los widgets antes del primer render.
        ApplyWidgetVisibility();

        // Un solo timer cada 1s para las métricas (CPU/RAM/red). La fecha se refresca en el
        // mismo tick: es barata y así el cambio de día queda cubierto sin lógica extra.
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += OnTick;
        _timer.Start();

        // Primera muestra inmediata (devuelve 0 en CPU/red por el baseline, es esperado).
        UpdateMetrics();
    }

    /// <summary>Un widget se prendió/apagó desde el tray: persistimos y re-aplicamos.</summary>
    private void OnWidgetToggled(WidgetKind kind, bool enabled)
    {
        // El evento del tray viene del hilo de UI de WinForms, que es el mismo STA,
        // pero por las dudas marshalamos al Dispatcher de WPF.
        Dispatcher.Invoke(() =>
        {
            _settings.Set(kind, enabled);
            _settings.Save();          // persistencia inmediata: la elección queda
            ApplyWidgetVisibility();
            UpdateMetrics();           // refresca contenido del widget recién activado
        });
    }

    /// <summary>
    /// Muestra/oculta cada bloque según settings y recalcula los separadores:
    /// el PRIMER widget visible del panel derecho NO debe mostrar su separador izquierdo.
    /// </summary>
    private void ApplyWidgetVisibility()
    {
        // El toggle "Clock" del tray ahora controla la fecha (la hora se quitó de la barra).
        DateWidget.Visibility = Vis(_settings.Clock);

        // Batería: además del toggle, requiere que exista batería física.
        bool batteryVisible = _settings.Battery && _monitor.Sample().HasBattery;

        CpuWidget.Visibility = Vis(_settings.Cpu);
        RamWidget.Visibility = Vis(_settings.Ram);
        NetworkWidget.Visibility = Vis(_settings.Network);
        BatteryWidget.Visibility = Vis(batteryVisible);

        FixUpSeparators();
    }

    /// <summary>El separador del primer widget visible se oculta; los demás se muestran.</summary>
    private void FixUpSeparators()
    {
        var blocks = new (UIElement Widget, Rectangle Separator)[]
        {
            (CpuWidget, CpuSeparator),
            (UsageWidget, UsageSeparator),   // siempre visible: va a la izquierda de la RAM
            (RamWidget, RamSeparator),
            (NetworkWidget, NetworkSeparator),
            (BatteryWidget, BatterySeparator),
        };

        bool firstVisibleFound = false;
        foreach (var (widget, separator) in blocks)
        {
            if (widget.Visibility != Visibility.Visible) continue;

            // El primero visible no lleva separador a la izquierda (queda colgando si no).
            separator.Visibility = firstVisibleFound ? Visibility.Visible : Visibility.Collapsed;
            firstVisibleFound = true;
        }
    }

    private static Visibility Vis(bool on) => on ? Visibility.Visible : Visibility.Collapsed;

    private void OnTick(object? sender, EventArgs e)
    {
        UpdateDate();
        UpdateMetrics();
    }

    private void UpdateDate()
    {
        // Día completo + fecha, en la cultura del sistema. Ej.: "Viernes 30 de mayo".
        // En español el nombre del día/mes viene en minúscula → capitalizamos la inicial.
        var date = DateTime.Now.ToString("dddd dd 'de' MMMM");
        DateText.Text = date.Length > 0 ? char.ToUpper(date[0]) + date[1..] : date;
    }

    private void UpdateMetrics()
    {
        var s = _monitor.Sample();

        // Solo calculamos/escribimos lo que está visible. Si está oculto, ni gastamos.
        if (_settings.Cpu)
            CpuText.Text = $"{s.CpuPercent,5:0.0}%";

        if (_settings.Ram)
            RamText.Text = $"{s.RamUsedGb:0.0}/{s.RamTotalGb:0.0} GB";

        if (_settings.Network)
        {
            NetDownText.Text = FormatSpeed(s.NetDownKbps);
            NetUpText.Text = FormatSpeed(s.NetUpKbps);
        }

        if (_settings.Battery)
        {
            // Si el toggle está pero no hay batería física, ocultamos el bloque.
            if (!s.HasBattery)
            {
                BatteryWidget.Visibility = Visibility.Collapsed;
                FixUpSeparators();
            }
            else
            {
                BatteryText.Text = $"{s.BatteryPercent}%{(s.OnAcPower ? " ⚡" : "")}";
                BatteryText.Foreground = s.BatteryPercent <= 20 && !s.OnAcPower
                    ? (Brush)FindResource("AccentWarn")
                    : (Brush)FindResource("TextPrimary");
            }
        }

        // Tooltip del tray: resumen vivo de lo más útil (CPU/RAM siempre se calculan baratos).
        _tray?.SetTooltip($"Ampz Booster — CPU {s.CpuPercent:0}% · RAM {s.RamPercent:0}%");
    }

    private static string FormatSpeed(double kbps)
    {
        if (kbps >= 1024)
            return $"{kbps / 1024.0,5:0.0} MB/s";
        return $"{kbps,5:0.0} KB/s";
    }

    // ───────────────────────── Uso de IA (token usage) ─────────────────────────

    /// <summary>Ancho de la pista de cada mini-barra (debe coincidir con el Border del XAML).</summary>
    private const double GaugeTrackWidth = 26.0;

    /// <summary>
    /// Engancha la barra al UsageService (lo crea y arranca App.OnStartup). Se suscribe a los
    /// snapshots futuros y, si el tiro inicial ya llegó (Latest), lo pinta de una. La idea: el
    /// fetch lo dispara el servicio en el arranque core; la barra es sólo el consumidor que pinta.
    /// </summary>
    public void AttachUsage(UsageService usage)
    {
        _usage = usage;
        usage.Updated += ApplyUsage;          // snapshots futuros (timer del servicio)
        if (usage.Latest is not null)         // el tiro inicial ya volvió (ej. fast-fail) → pintar ya
            ApplyUsage(usage.Latest);
    }

    private void ApplyUsage(UsageSnapshot snap)
    {
        if (!snap.Ok)
        {
            // Error transitorio (rate limit / red): si YA tuvimos datos, los dejamos — no vaciamos
            // la barra por un 429 pasajero; sólo avisamos en el tooltip.
            if (_usageEverLoaded)
            {
                UsageWidget.ToolTip = $"{snap.Error}\n(mostrando el último dato)";
                return;
            }

            // Nunca cargó: ocultamos TODO el bloque de islas junto (sin separadores huérfanos).
            GaugesPanel.Visibility = Visibility.Collapsed;
            PlanBadge.Visibility = Visibility.Collapsed;
            UsageStatus.Visibility = Visibility.Visible;
            UsageStatus.Text = "— sin datos";
            UsageWidget.ToolTip = snap.Error;
            return;
        }

        _usageEverLoaded = true;
        GaugesPanel.Visibility = Visibility.Visible;
        UsageStatus.Visibility = Visibility.Collapsed;

        // Badge del plan (ej. "Max 5x"). Si el provider no lo informa, lo ocultamos.
        if (string.IsNullOrEmpty(snap.AccountLabel))
        {
            PlanBadge.Visibility = Visibility.Collapsed;
        }
        else
        {
            PlanText.Text = snap.AccountLabel;
            PlanBadge.Visibility = Visibility.Visible;
        }

        UsageWidget.ToolTip = $"Claude — actualizado {snap.FetchedAt:HH:mm}";

        SetGauge(Fill5h, Pct5h, Gauge5h, FindGauge(snap, "five_hour"));
        SetGauge(Fill7d, Pct7d, Gauge7d, FindGauge(snap, "seven_day"));
        SetGauge(FillSonnet, PctSonnet, GaugeSonnet, FindGauge(snap, "seven_day_sonnet"));
    }

    private static UsageGauge? FindGauge(UsageSnapshot s, string key)
        => s.Gauges.FirstOrDefault(g => g.Key == key);

    /// <summary>Pinta una mini-barra: ancho del relleno, color por nivel, % y tooltip. null = oculta.</summary>
    private void SetGauge(Border fill, TextBlock pctText, FrameworkElement container, UsageGauge? g)
    {
        if (g is null)
        {
            container.Visibility = Visibility.Collapsed;
            return;
        }

        container.Visibility = Visibility.Visible;
        double pct = Math.Clamp(g.Percent, 0, 100);
        fill.Width = GaugeTrackWidth * pct / 100.0;
        fill.Background = BrushForLevel(pct);
        pctText.Text = $"{pct:0}%";
        container.ToolTip = BuildUsageTip(g);   // el reset (fecha/hora exacta) queda en el tooltip
    }

    /// <summary>Color de la barra por nivel de carga: celeste (ok) → amarillo → ámbar → rojo.</summary>
    private Brush BrushForLevel(double pct) => (Brush)FindResource(
        pct >= 80 ? "GaugeRed"
        : pct >= 65 ? "GaugeAmber"
        : pct >= 55 ? "GaugeYellow"
        : "Accent");

    /// <summary>
    /// Tooltip de una mini-barra. El % NO va acá: ya se ve al lado de la barra (Pct5h/7d/Sonnet),
    /// repetirlo es ruido. Lo que SÍ aporta el tooltip es lo que la barra NO muestra: el label
    /// completo (la barra sólo trae el código cripto "5h"/"7d"/"S"), CUÁNTO falta para el reset y
    /// CUÁNDO cae exactamente. El "falta" se calcula al refrescar el snapshot, no en vivo al hacer
    /// hover — para ventanas de 5h/7d el desfase del intervalo de polling es despreciable.
    /// </summary>
    private static string BuildUsageTip(UsageGauge g)
    {
        if (g.ResetsAt is not { } reset)
            return g.Label; // sin dato de reset → al menos el label completo, mejor que nada

        var local = reset.ToLocalTime();
        var remaining = local - DateTimeOffset.Now;
        string falta = remaining > TimeSpan.Zero ? FormatRemaining(remaining) : "ya se reinició";
        return $"{g.Label}\nFalta: {falta}\nSe reinicia: {local:ddd dd/MM · HH:mm}";
    }

    /// <summary>
    /// "Tiempo que falta" en formato humano y compacto, SIN segundos (el dato se refresca por minuto
    /// a lo sumo; los segundos sólo hacen ruido): "2 d 3 h", "3 h 15 min", "12 min". Nunca "0 min".
    /// </summary>
    private static string FormatRemaining(TimeSpan t)
    {
        if (t.TotalDays >= 1) return $"{(int)t.TotalDays} d {t.Hours} h";
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours} h {t.Minutes} min";
        return $"{Math.Max(1, (int)t.TotalMinutes)} min";
    }

    /// <summary>
    /// Pinea la barra a todos los virtual desktops. Idempotente — se puede llamar de nuevo en
    /// cada cambio de desktop como insurance por si el pin del arranque no prendió (timing).
    /// try/catch: si este build de la DLL no exporta PinWindow, degradamos sin crashear.
    /// </summary>
    public void EnsurePinned()
    {
        try
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero)
                Interop.VirtualDesktopAccessor.PinWindow(hwnd);
        }
        catch { }
    }

    /// <summary>
    /// Actualiza el widget de desktop (a la derecha del todo): dot coloreado por tipo de desk,
    /// nombre, y proyecto en gold (oculto si no hay). Lo llama el listener al cambiar de desktop.
    /// </summary>
    public void UpdateDesk(string name, string project)
    {
        var dot = new SolidColorBrush(DeskPalette.For(name).Active);

        // El modo lo decide el TIPO de desk, no si hay proyecto cargado:
        //   · DESK +N  → SIEMPRE modo DUAL (es un desk de proyecto: le reservamos el espacio
        //                del nombre del proyecto aunque hoy esté vacío).
        //   · MAIN/MAILS/MISCS → modo SOLO centrado (nunca aceptan proyecto).
        bool isProjectDesk = name.Contains("DESK +", StringComparison.OrdinalIgnoreCase);

        if (isProjectDesk)
        {
            DeskDualPanel.Visibility = Visibility.Visible;
            DeskSoloPanel.Visibility = Visibility.Collapsed;
            DeskDotDual.Fill = dot;
            DeskNameDual.Text = name;
            DeskProjectText.Text = project; // puede estar vacío: el espacio queda reservado igual
        }
        else
        {
            DeskSoloPanel.Visibility = Visibility.Visible;
            DeskDualPanel.Visibility = Visibility.Collapsed;
            DeskDotSolo.Fill = dot;
            DeskNameSolo.Text = name;
        }
    }

    // Alt+F4 / "Cerrar" del menú de sistema llegan como WM_SYSCOMMAND con wParam = SC_CLOSE.
    // Los 4 bits bajos del wParam los reserva el sistema → enmascaramos con 0xFFF0 antes de comparar.
    private const int WM_SYSCOMMAND = 0x0112;
    private const int SC_CLOSE      = 0xF060;

    /// <summary>
    /// Se traga SOLO el SC_CLOSE (Alt+F4 / menú de sistema). handled=true y sin reenviar → la
    /// ventana nunca arranca su secuencia de cierre. Todo lo demás pasa de largo sin tocar.
    /// </summary>
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_SYSCOMMAND && (wParam.ToInt32() & 0xFFF0) == SC_CLOSE)
        {
            handled = true; // bloqueado: la barra no se cierra desde acá
            return IntPtr.Zero;
        }
        return IntPtr.Zero;
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _timer?.Stop();
        _fullscreen?.Dispose(); // paramos el poll de fullscreen
        if (_usage is not null) _usage.Updated -= ApplyUsage; // el servicio lo dispone App, acá sólo desuscribimos
        _tray?.Dispose();      // sacamos el ícono de la bandeja
        _appBar?.Unregister(); // CRÍTICO: liberar el espacio reservado en Windows.
    }
}
