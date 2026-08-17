using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Threading;
using AmpzDesktopBooster.Desktops;
using AmpzDesktopBooster.Services;
using AmpzDesktopBooster.Services.Attention;
using AmpzDesktopBooster.Services.Localization;
using AmpzDesktopBooster.Services.Usage;

namespace AmpzDesktopBooster;

/// <summary>
/// La barra: una AppBar real de Windows con widgets modulares (hora, fecha, CPU,
/// RAM, red, batería) + ícono en la bandeja. Era el MainWindow del espacio "bar";
/// acá pasa a ser BarWindow para convivir con el resto de la app (hook de teclado).
/// </summary>
public partial class BarWindow : Window
{
    private readonly SystemMonitor _monitor = new();
    private readonly WidgetSettings _settings = WidgetSettings.Load();

    // IPs (LAN + pública). NO va en el tick de 1s como el resto de las métricas: la local es barata
    // pero la pública es una consulta de RED — se maneja por eventos del SO + un poll largo, todo
    // adentro del servicio. Corre SIEMPRE, esté el widget prendido o no: el aviso de "te cambió la IP
    // pública" (VPN, reconexión del ISP) vale por sí solo aunque no tengas el widget a la vista.
    private IpMonitor? _ips;
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

    /// <summary>Click en el widget de tarea activa → abrir el detalle. Lo setea App (lo rutea al router).</summary>
    public Action? OnTaskWidgetClicked { get; set; }

    /// <summary>Click en un dot de atención → saltar a ESE desk. Lo setea App (lo rutea a DesktopService.GoTo).</summary>
    public Action<int>? OnAttentionDeskClicked { get; set; }

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

        // Click en el widget de tarea activa → el detalle (lo rutea App al router).
        TaskWidget.MouseLeftButtonUp += (_, _) => OnTaskWidgetClicked?.Invoke();

        // Click en cada IP → al portapapeles. Mirás una IP para PEGARLA en algún lado; obligarte a
        // transcribirla a mano desde la barra sería tener el dato y no el uso.
        IpLocalText.MouseLeftButtonUp += (_, _) => CopyIp(_ips?.Current.Local, Loc.T("Bar.IpLocalTooltip"));
        IpPublicText.MouseLeftButtonUp += (_, _) => CopyIp(_ips?.Current.Public, Loc.T("Bar.IpPublicTooltip"));

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

        // Monitor de IPs. Se arranca acá (ya hay Dispatcher y HWND) y se libera en OnClosing.
        _ips = new IpMonitor(Dispatcher);
        _ips.Changed += OnIpsChanged;
        _ips.Start();
        RenderIps(); // pinta los "···" hasta que resuelva
    }

    /// <summary>
    /// Cambió alguna IP: repintamos y, si corresponde, avisamos por toast.
    ///
    /// El aviso se filtra a propósito. La PRIMERA resolución (de null a un valor) NO es una novedad,
    /// es el arranque de la app — toastear ahí sería ruido en cada login. Sólo avisamos cuando había
    /// un valor ANTERIOR y pasó a ser otro: eso sí es el evento que importa (levantaste la VPN, te
    /// reconectó el ISP, saltaste de WiFi a cable). La pública lleva más jerarquía que la local
    /// porque es la que suele romperte accesos remotos sin que te enteres.
    /// </summary>
    private void OnIpsChanged(IpSnapshot prev, IpSnapshot now)
    {
        RenderIps();

        if (prev.Public is not null && now.Public is not null && prev.Public != now.Public)
            Toasts.Info($"🌐  {Loc.T("Toast.PublicIpChanged")}", $"{prev.Public}  →  {now.Public}");

        if (prev.Local is not null && now.Local is not null && prev.Local != now.Local)
            Toasts.Info($"🖧  {Loc.T("Toast.LocalIpChanged")}", $"{prev.Local}  →  {now.Local}");
    }

    /// <summary>Vuelca el snapshot actual a los dos TextBlock. "···" = todavía sin resolver.</summary>
    private void RenderIps()
    {
        if (!_settings.Ip) return; // widget apagado: el monitor sigue vivo (para los toasts), la UI no

        var s = _ips?.Current ?? IpSnapshot.Empty;
        IpLocalText.Text = s.Local ?? "···";
        IpPublicText.Text = s.Public ?? "···";
    }

    /// <summary>Copia una IP al portapapeles con confirmación. Sin IP resuelta todavía, no hace nada.</summary>
    private static void CopyIp(string? ip, string what)
    {
        if (string.IsNullOrEmpty(ip)) return;
        try
        {
            Clipboard.SetText(ip);
            Toasts.Info($"📋  {ip}", what);
        }
        catch { /* el portapapeles lo puede tener tomado otra app: no vale un crash */ }
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
        IpWidget.Visibility = Vis(_settings.Ip);
        BatteryWidget.Visibility = Vis(batteryVisible);

        RenderIps(); // al re-prenderlo desde el tray, que muestre el último valor sin esperar red

        FixUpSeparators();
    }

    /// <summary>El separador del primer widget visible se oculta; los demás se muestran.</summary>
    private void FixUpSeparators()
    {
        var blocks = new (UIElement Widget, Rectangle Separator)[]
        {
            // ⚠ Este orden debe COINCIDIR con el orden visual del XAML: de él sale qué widget es el
            // "primero visible" (el único que NO lleva separador a la izquierda). Si movés un bloque
            // en el XAML, movelo también acá o vas a ver un separador colgando en el borde.
            (CpuWidget, CpuSeparator),
            (IpWidget, IpSeparator),
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
            UsageStatus.Text = Loc.T("Bar.UsageNoData");
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

        UsageWidget.ToolTip = $"Claude — {Loc.T("Bar.UsageUpdatedAt")} {snap.FetchedAt:HH:mm}";

        SetGauge(Fill5h, Pct5h, Gauge5h, FindGauge(snap, "session"));
        SetGauge(Fill7d, Pct7d, Gauge7d, FindGauge(snap, "weekly_all"));

        // Tercera isla: el tope semanal SCOPED, que sigue al modelo que Anthropic tope-e (hoy Fable,
        // ayer Sonnet). Su letra se deriva EN VIVO del nombre real ("Semanal · Fable" → "F") — así no
        // hay que tocar el XAML cuando Anthropic rote el modelo con límite semanal.
        var scoped = FindGauge(snap, "weekly_scoped");
        SetGauge(FillScoped, PctScoped, GaugeScoped, scoped);
        if (scoped is not null)
            LblScoped.Text = ScopedInitial(scoped.Label);
    }

    /// <summary>Inicial del modelo scoped para la mini-isla: "Semanal · Fable" → "F". Fallback "·".</summary>
    private static string ScopedInitial(string label)
    {
        int i = label.LastIndexOf('·');
        string model = (i >= 0 ? label[(i + 1)..] : label).Trim();
        return model.Length > 0 ? model[..1].ToUpperInvariant() : "·";
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
        string falta = remaining > TimeSpan.Zero ? FormatRemaining(remaining) : Loc.T("Bar.UsageAlreadyReset");
        return $"{g.Label}\n{Loc.T("Bar.UsageRemaining")}: {falta}\n{Loc.T("Bar.UsageResetsAt")}: {local:ddd dd/MM · HH:mm}";
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
    /// nombre, espacio en gold (oculto si no hay) y, si el desk tiene un CONTEXTO activo, su nombre
    /// pintado con el color propio del contexto. Lo llama el listener al cambiar de desktop.
    /// </summary>
    public void UpdateDesk(string name, string project, DeskModule module = default)
    {
        var dot = new SolidColorBrush(DeskPalette.For(name).Active);

        // El modo lo decide el ROL del desk (del catálogo), no si hay espacio cargado:
        //   · rol Espacio → SIEMPRE modo DUAL (le reservamos el lugar del nombre del espacio aunque
        //                   hoy esté vacío).
        //   · rol Main / Fijo → modo SOLO centrado (nunca aceptan espacio).
        // Antes era name.Contains("DESK +"): renombrar el desk le sacaba el panel dual de una.
        bool isProjectDesk = DeskCatalog.IsSpace(name);

        if (isProjectDesk)
        {
            DeskDualPanel.Visibility = Visibility.Visible;
            DeskSoloPanel.Visibility = Visibility.Collapsed;
            DeskDotDual.Fill = dot;
            DeskNameDual.Text = name;
            DeskProjectText.Text = project; // puede estar vacío: el espacio queda reservado igual

            // Contexto: sin espacio no puede haber sub-scope → ni lo evaluamos.
            bool hasModule = project != "" && module.IsSet;
            if (hasModule)
            {
                // Sólo el TEXTO toma el color del contexto. La barrita de al lado queda neutral (se
                // pinta en el XAML): está entre dos datos, así que lee como separador — teñirla del
                // color del contexto confundía a cuál de los dos pertenece.
                DeskModuleText.Text = module.Name;
                DeskModuleText.Foreground = new SolidColorBrush(module.Accent);
            }
            DeskModuleText.Visibility = hasModule ? Visibility.Visible : Visibility.Collapsed;
            DeskModuleAccent.Visibility = hasModule ? Visibility.Visible : Visibility.Collapsed;

            // ── Reparto del espacio: se decide ACÁ, no en el XAML ──
            // Con topes fijos en el XAML el reparto quedaba desparejo (150/110 = 58/42, no 50/50) y
            // el contexto se cortaba mientras al espacio le sobraba aire. Con columnas "*" el reparto
            // es proporcional de verdad, pero "*" en el contexto reservaría su mitad AUNQUE no haya
            // contexto — por eso el ancho no puede ser estático: depende del estado.
            //
            //   · Con contexto → 50/50 exacto y cada título CENTRADO en su mitad. Como las dos columnas
            //     "*" reparten el sobrante en partes iguales, el divisor (columna Auto del medio)
            //     cae exactamente en el centro del bloque: quedan dos celdas simétricas, no un texto
            //     pegado al otro. Centrar en la celda (y no alinear contra el divisor) hace que el
            //     largo de un nombre NO corra visualmente al otro.
            //   · Sin contexto → la columna del contexto va a 0 y el espacio se queda con el 100%,
            //     CENTRADO en todo el ancho para que no quede un hueco muerto a ningún lado.
            DeskProjectCol.Width = new GridLength(1, GridUnitType.Star);
            DeskModuleCol.Width = hasModule ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
            DeskProjectText.TextAlignment = TextAlignment.Center; // centrado en su mitad, o en todo si está solo
        }
        else
        {
            DeskSoloPanel.Visibility = Visibility.Visible;
            DeskDualPanel.Visibility = Visibility.Collapsed;
            DeskDotSolo.Fill = dot;
            DeskNameSolo.Text = name;
        }
    }

    /// <summary>
    /// Actualiza el widget de tarea activa (entre RAM y Desktop). null → lo oculta (el estado normal:
    /// arranca oculto y vuelve a ocultarse al desanclar). Con tarea, muestra identifier + título
    /// recortado. Lo llama App al cambiar de desk y tras pickear/desanclar, leyendo la TaskSessionStore.
    /// </summary>
    public void UpdateDeskTask(Services.Tasks.TaskItem? task)
    {
        if (task is null)
        {
            TaskWidget.Visibility = Visibility.Collapsed;
            return;
        }

        TaskIdText.Text = task.Identifier;
        TaskIdText.Visibility = string.IsNullOrEmpty(task.Identifier) ? Visibility.Collapsed : Visibility.Visible;
        TaskTitleText.Text = task.Title;
        TaskWidget.Visibility = Visibility.Visible;
    }

    // ───────────────────────── Atención por desk (dots) ─────────────────────────

    // Rojo coral = te necesita (urgente); verde = tarea lista. Mismos acentos que los toasts.
    private static readonly Color AttnUrgent = Color.FromRgb(0xE5, 0x63, 0x5A);
    private static readonly Color AttnDone   = Color.FromRgb(0x44, 0xDD, 0x88);

    /// <summary>
    /// Repinta los dots de atención. Cada item = un desk que reclama. Lista vacía → oculta el widget
    /// entero (estado normal). Lo llama App suscrito a AttentionService.Changed: alta cuando llega un
    /// aviso de OTRO desk, baja cuando entrás a ese desk (ClearDesk). Regeneramos todo de cero — son
    /// poquitos dots y así no arrastramos animaciones viejas.
    /// </summary>
    public void UpdateAttention(IReadOnlyList<(int Index, string DeskName, bool Urgent, string Project)> items)
    {
        AttentionDotsPanel.Children.Clear();

        if (items.Count == 0)
        {
            AttentionWidget.Visibility = Visibility.Collapsed;
            return;
        }

        foreach (var it in items)
            AttentionDotsPanel.Children.Add(BuildAttentionDot(it.Index, it.DeskName, it.Urgent, it.Project));

        AttentionWidget.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// Una pill chica y elegante: dot de color (la urgencia) + número del desk bien chico al lado,
    /// sobre un fondo sutil. El dot pulsa si es urgente. Click en la pill → saltás a ese desk.
    /// </summary>
    private UIElement BuildAttentionDot(int deskIndex, string deskName, bool urgent, string project)
    {
        var color = urgent ? AttnUrgent : AttnDone;

        var dot = new Ellipse
        {
            Width = 6,
            Height = 6,
            Fill = new SolidColorBrush(color),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 5, 0),
        };

        var label = new TextBlock
        {
            Text = ShortDeskLabel(deskName),
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0xD2, 0xD2, 0xD2)),
            VerticalAlignment = VerticalAlignment.Center,
        };

        var content = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        content.Children.Add(dot);
        content.Children.Add(label);

        var pill = new Border
        {
            CornerRadius = new CornerRadius(9),
            Background = new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)),
            Padding = new Thickness(7, 1, 8, 2),
            Margin = new Thickness(4, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = System.Windows.Input.Cursors.Hand,
            ToolTip = AttentionTip(deskName, urgent, project),
            Child = content,
        };

        // Click → saltar a ese desk. Al ENTRAR, el ClearDesk dispara Changed → la pill desaparece sola.
        pill.MouseLeftButtonUp += (_, _) => OnAttentionDeskClicked?.Invoke(deskIndex);

        // 'te necesita' PULSA suave (solo el dot, sutil) para tirarte del ojo; 'tarea lista' queda quieto.
        if (urgent)
        {
            var pulse = new DoubleAnimation(1.0, 0.3, new Duration(TimeSpan.FromMilliseconds(720)))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
            };
            dot.BeginAnimation(OpacityProperty, pulse);
        }

        return pill;
    }

    /// <summary>"DESK +3" → "3"; los nombrados (MAIN/CONSOLES/MISCS) → su inicial. El tooltip da el nombre completo.</summary>
    private static string ShortDeskLabel(string name)
    {
        var m = System.Text.RegularExpressions.Regex.Match(name, @"\+\s*(\d+)");
        if (m.Success) return m.Groups[1].Value;
        return name.Length > 0 ? name[..1].ToUpperInvariant() : "?";
    }

    /// <summary>Tooltip del dot: qué pasa en ese desk + el espacio (si tiene), en su línea.</summary>
    private static string AttentionTip(string deskName, bool urgent, string project)
    {
        string head = urgent ? $"{deskName} {Loc.T("Bar.AttentionNeedsYou")}" : $"{deskName}: {Loc.T("Bar.AttentionTaskDone")}";
        return string.IsNullOrEmpty(project) ? head : $"{head}\n{project}";
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
        _ips?.Dispose();        // desuscribe NetworkAddressChanged y frena los timers/HttpClient
        if (_usage is not null) _usage.Updated -= ApplyUsage; // el servicio lo dispone App, acá sólo desuscribimos
        _tray?.Dispose();      // sacamos el ícono de la bandeja
        _appBar?.Unregister(); // CRÍTICO: liberar el espacio reservado en Windows.
    }
}
