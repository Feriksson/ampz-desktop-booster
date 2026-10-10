using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using AmpzDesktopBooster.Desktops;
using AmpzDesktopBooster.Interop;

namespace AmpzDesktopBooster;

/// <summary>
/// El overlay central que aparece al cambiar de desktop: nombre grande del desk, espacio
/// (cuando exista), una fila de dots de navegación coloreados por tipo de desk (el activo
/// relleno y brillante), y el título de la app en foco. Se auto-oculta a los 800ms.
///
/// Es una ventana persistente: se crea una vez, se muestra/oculta y se le cambian los textos
/// — sin recrear nada (mismo patrón que el GUI persistente del legacy). No roba foco
/// (ShowActivated=False) y queda topmost por encima de la barra.
/// </summary>
public partial class OverlayWindow : Window
{
    private const double DotCellWidth = 50;
    private static readonly TimeSpan AutoHide = TimeSpan.FromMilliseconds(800);

    // Dorado del rol Space (#FFD700), el mismo que pinta el espacio en la barra.
    private static readonly Brush SpaceGold = new SolidColorBrush(Color.FromRgb(0xFF, 0xD7, 0x00));

    private readonly DispatcherTimer _hideTimer;

    public OverlayWindow()
    {
        InitializeComponent();

        _hideTimer = new DispatcherTimer { Interval = AutoHide };
        _hideTimer.Tick += (_, _) => { _hideTimer.Stop(); BlankThenHide(); };

        // Auto-corrección del centrado: con SizeToContent la ventana se re-mide cada vez que
        // cambian los textos/dots, y ESTE evento llega con el ancho REAL ya arrangeado (no una
        // estimación). Recentrar acá cubre el primer show tras el arranque, que es donde la
        // predicción fallaba. Dispara durante el layout pass, antes del render → no se ve saltar.
        SizeChanged += (_, _) => Recenter();

        // Forzamos la creación del HWND YA (sin mostrar) para aplicar los estilos extendidos
        // y pinear el overlay a todos los desktops ANTES del primer cambio — así aparece en
        // cualquier desktop desde el arranque, aun si saltás rápido apenas abre la app.
        new WindowInteropHelper(this).EnsureHandle();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        var hwnd = new WindowInteropHelper(this).Handle;

        // Tool window + no-activate: fuera del taskbar/Alt-Tab y sin robar foco al mostrarse.
        var ex = WindowMethods.GetWindowLongPtr(hwnd, WindowMethods.GWL_EXSTYLE).ToInt64();
        ex |= WindowMethods.WS_EX_TOOLWINDOW | WindowMethods.WS_EX_NOACTIVATE;
        WindowMethods.SetWindowLongPtr(hwnd, WindowMethods.GWL_EXSTYLE, new IntPtr(ex));

        // Pinear a TODOS los virtual desktops: sin esto la ventana vive sólo en el desktop
        // donde se mostró y no te "sigue" al cambiar (el bug de no verla al saltar rápido).
        // try/catch: si este build de la DLL no exporta PinWindow, degradamos sin crashear.
        try { VirtualDesktopAccessor.PinWindow(hwnd); } catch { }
    }

    /// <summary>
    /// Oculta en DOS tiempos: primero deja la card transparente y recién cuando ESE cuadro se pintó
    /// oculta la ventana.
    ///
    /// Por qué: una ventana oculta conserva su último cuadro dibujado, y al volver a mostrarla ese
    /// cuadro VIEJO se ve un instante antes de que WPF pinte el nuevo. El último cuadro de este
    /// overlay es siempre el desk del que venís, así que el popup aparecía con el nombre del desk
    /// ACTUAL y saltaba al del destino — el usuario lo vio y lo rechazó. Se destapó al anunciar los
    /// cambios propios en el acto: antes el popup salía más tarde y el parpadeo quedaba tapado por
    /// la transición de Windows. Con el último cuadro en blanco, lo viejo que reaparece no se ve.
    /// </summary>
    private void BlankThenHide()
    {
        Card.Opacity = 0;
        int ticket = ++_hideTicket;
        Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
        {
            // Si entre el blanqueo y este pase entró otro ShowOverlay, ese show manda: no ocultar.
            if (ticket == _hideTicket) Hide();
        }));
    }

    private int _hideTicket; // invalida un ocultado pendiente cuando llega un show nuevo

    /// <summary>
    /// Lo que el overlay necesita saber de UN desk para pintarse, ya resuelto. Existe para poder
    /// pintar SIN consultarle nada al shell — ver <see cref="ShowCached"/>.
    /// </summary>
    private sealed record DeskView(string Title, bool SpaceTitle, DeskModule Module, Color Active, Color Inactive);

    private DeskView[] _snapshot = Array.Empty<DeskView>();

    /// <summary>
    /// Relee del shell todo lo que el overlay muestra (un <see cref="DeskView"/> por desk). Hay que
    /// llamarlo con el shell LIBRE: durante una transición de escritorio cada consulta bloquea el
    /// thread de UI hasta que termina (medido: 65–200ms), que es justo lo que este snapshot evita.
    /// </summary>
    public void RefreshSnapshot(DesktopService desktops)
    {
        try
        {
            int count = desktops.Count;
            var views = new DeskView[count];
            for (int d = 0; d < count; d++)
            {
                string name = desktops.GetName(d);
                // El ROL se pregunta a DeskCatalog (idx-aware: ve los dinámicos), nunca al nombre. Acá
                // quedaba el viejo name.Contains("DESK") y con los escritorios DINÁMICOS (que se llaman
                // "Espacio / Contexto") el overlay los tomaba por fijos: escondía espacio y contexto y
                // mostraba el nombre crudo con la "/" pegada — el mismo error que ya se había corregido
                // en la barra y que el usuario rechazó.
                string project = DeskCatalog.IsSpace(name, d) ? desktops.GetProject(d) : "";
                bool spaceTitle = !string.IsNullOrEmpty(project);
                // Contexto: sólo tiene sentido con espacio arriba (sin espacio no hay sub-scope posible).
                var module = spaceTitle ? desktops.GetModule(d) : DeskModule.None;
                var (active, inactive) = DeskPalette.For(name);
                // Desk de espacio: el TÍTULO es el espacio en MAYÚSCULAS (pedido explícito: manda la
                // jerarquía sobre el contexto de abajo). NO se muestra el nombre del desk: ya ES
                // "Espacio / Contexto", repetirlo arriba sería el dato dos veces.
                views[d] = new DeskView(spaceTitle ? project.ToUpperInvariant() : name, spaceTitle, module, active, inactive);
            }
            _snapshot = views;
        }
        catch { /* shell en un estado raro: nos quedamos con el snapshot anterior */ }
    }

    /// <summary>Relee el estado del shell, muestra el overlay para el desktop dado y reinicia el auto-hide.</summary>
    public void ShowOverlay(int index, DesktopService desktops)
    {
        RefreshSnapshot(desktops);
        ShowCached(index);
    }

    /// <summary>
    /// Muestra el overlay para <paramref name="index"/> con lo que ya hay en el snapshot, SIN tocar el
    /// shell. Es lo que se llama en el instante en que Windows avisa un cambio de escritorio hecho
    /// por fuera de la app (Win+Ctrl+Flechas, gestos, vista de tareas): ahí el shell sigue en plena
    /// transición y consultarle el nombre dejaba el cartel 65–200ms atrás del cambio. false si el
    /// snapshot no conoce ese índice (un desk recién creado por fuera): el show normal lo cubre.
    /// </summary>
    public bool ShowCached(int index)
    {
        var views = _snapshot;
        if (index < 0 || index >= views.Length) return false;
        var view = views[index];

        _hideTicket++;    // cancela un BlankThenHide a medio camino
        Card.Opacity = 1;

        // Título dorado en un desk de espacio (el acento del rol Space, igual que en la barra).
        TitleText.Text = view.Title;
        TitleText.Foreground = view.SpaceTitle ? SpaceGold : Brushes.White;

        // El TEXTO del contexto va pintado con su color — es la señal que se percibe de reflejo.
        if (view.Module.IsSet)
        {
            ModuleText.Text = view.Module.Name;
            ModuleText.Foreground = new SolidColorBrush(view.Module.Accent);
            ModuleChip.Visibility = Visibility.Visible;
        }
        else
            ModuleChip.Visibility = Visibility.Collapsed;

        BuildDots(views, index);

        // Posicionamiento tentativo ANTES de mostrar, para que no aparezca en 0,0 y salte.
        // OJO: acá el ancho es sólo una ESTIMACIÓN — la primera vez tras el arranque la ventana
        // nunca pasó por un layout/arrange real (el HWND se creó con EnsureHandle sin mostrar),
        // así que ni ActualWidth (0/stale) ni DesiredSize (lo que el contenido PIDE, no lo que la
        // ventana MIDE una vez arrangeada en su monitor con su DPI) coinciden con el ancho final.
        // Si la estimación sale corta, (pantalla - anchoCorto)/2 deja la card corrida a la DERECHA:
        // ése era el bug del primer cambio de desk. El centrado DEFINITIVO lo hace Recenter()
        // desde SizeChanged (ancho real) y desde el pase diferido de abajo. No borres esa red.
        Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Recenter(DesiredSize.Width);

        Visibility = Visibility.Visible;
        Show();

        // Red final: si el arrange definitivo no cambió el tamaño (misma medida que el show
        // anterior) SizeChanged no dispara, pero para entonces ActualWidth YA es el real.
        // Recentrar en Loaded priority corrige sin depender de que la ventana haya cambiado de
        // tamaño. Es idempotente: si ya estaba centrada, reescribe el mismo Left.
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => Recenter()));

        _hideTimer.Stop();
        _hideTimer.Start();
        return true;
    }

    /// <summary>
    /// Centra horizontalmente en el monitor primario y fija el alto (un poco por encima del
    /// centro). Con <paramref name="width"/> en null usa el ancho REAL de la ventana.
    /// </summary>
    private void Recenter(double? width = null)
    {
        double w = width ?? ActualWidth;
        if (w <= 0) return; // sin ancho útil todavía: lo resuelve el próximo pase

        Left = (SystemParameters.PrimaryScreenWidth - w) / 2;
        Top = SystemParameters.PrimaryScreenHeight / 2 - 220;
    }

    /// <summary>Un dot por desktop: el activo relleno y brillante, el resto huecos y apagados.</summary>
    private void BuildDots(DeskView[] views, int activeIndex)
    {
        DotsPanel.Children.Clear();

        for (int d = 0; d < views.Length; d++)
        {
            bool isActive = d == activeIndex;

            DotsPanel.Children.Add(new TextBlock
            {
                Text = isActive ? "\u2B24" : "\u25CB",
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 28,
                Foreground = new SolidColorBrush(isActive ? views[d].Active : views[d].Inactive),
                Width = DotCellWidth,
                TextAlignment = TextAlignment.Center,
            });
        }
    }
}
