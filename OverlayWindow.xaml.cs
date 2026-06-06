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

        BuildDots(count, index, desktops);

        // Posicionar centrado horizontal, un poco por encima del centro vertical.
        // OJO: NO usar ActualWidth acá. La primera vez que se llama a ShowOverlay tras el
        // arranque, la ventana nunca pasó por un layout pass real (el HWND se creó con
        // EnsureHandle sin mostrar) → ActualWidth viene 0/stale y el cálculo
        // (screenWidth - 0) / 2 deja la card corrida a la derecha. A partir del segundo
        // cambio ActualWidth ya es correcto y centra bien. Forzamos un Measure y usamos
        // DesiredSize, que está disponible sin necesidad de que la ventana esté visible.
        Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Left = (SystemParameters.PrimaryScreenWidth - DesiredSize.Width) / 2;
        Top = SystemParameters.PrimaryScreenHeight / 2 - 220;

        Visibility = Visibility.Visible;
        Show();

        _hideTimer.Stop();
        _hideTimer.Start();
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
