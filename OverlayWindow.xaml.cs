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
/// El overlay central que aparece al cambiar de desktop: nombre grande del desk, proyecto
/// (cuando exista), una fila de dots de navegación coloreados por tipo de desk (el activo
/// relleno y brillante), y el título de la app en foco. Se auto-oculta a los 800ms.
///
/// Es una ventana persistente: se crea una vez, se muestra/oculta y se le cambian los textos
/// — sin recrear nada (mismo patrón que el GUI persistente del legacy). No roba foco
/// (ShowActivated=False) y queda topmost por encima de la barra.
/// </summary>
public partial class OverlayWindow : Window
{
    private const double DotCellWidth = 40;
    private static readonly TimeSpan AutoHide = TimeSpan.FromMilliseconds(800);

    private readonly DispatcherTimer _hideTimer;

    public OverlayWindow()
    {
        InitializeComponent();

        _hideTimer = new DispatcherTimer { Interval = AutoHide };
        _hideTimer.Tick += (_, _) => { _hideTimer.Stop(); Hide(); };

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

    /// <summary>Renderiza y muestra el overlay para el desktop dado, y reinicia el auto-hide.</summary>
    public void ShowOverlay(int index, DesktopService desktops)
    {
        int count = desktops.Count;
        string name = desktops.GetName(index);
        string project = name.Contains("DESK", StringComparison.OrdinalIgnoreCase)
            ? desktops.GetProject(index)
            : "";

        TitleText.Text = name;

        // Proyecto en la última línea. Se colapsa si está vacío (los dots quedan como cierre).
        ProjectText.Text = project;
        ProjectText.Visibility = string.IsNullOrEmpty(project) ? Visibility.Collapsed : Visibility.Visible;

        // Módulo: sólo tiene sentido con proyecto arriba (sin proyecto no hay sub-scope posible).
        // El texto va PINTADO con el color del módulo, no sólo la barrita: duplicar la señal en dos
        // elementos es lo que la hace legible de reflejo, que es todo el punto de la feature.
        var module = string.IsNullOrEmpty(project) ? DeskModule.None : desktops.GetModule(index);
        if (module.IsSet)
        {
            var accent = new SolidColorBrush(module.Accent);
            ModuleText.Text = module.Name;
            ModuleText.Foreground = accent;
            ModuleAccent.Fill = accent;
            ModuleChip.Visibility = Visibility.Visible;
        }
        else
            ModuleChip.Visibility = Visibility.Collapsed;

        BuildDots(count, index, desktops);

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

    /// <summary>Un dot por desktop: el activo relleno (⬤) y brillante, el resto huecos (○) apagados.</summary>
    private void BuildDots(int count, int activeIndex, DesktopService desktops)
    {
        DotsPanel.Children.Clear();

        for (int d = 0; d < count; d++)
        {
            bool isActive = d == activeIndex;
            var (active, inactive) = desktops.GetName(d) is var dn ? DeskPalette.For(dn) : default;

            DotsPanel.Children.Add(new TextBlock
            {
                Text = isActive ? "⬤" : "○", // ⬤ / ○
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 22,
                Foreground = new SolidColorBrush(isActive ? active : inactive),
                Width = DotCellWidth,
                TextAlignment = TextAlignment.Center,
            });
        }
    }
}
