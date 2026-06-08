using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using AmpzDesktopBooster.Interop;

namespace AmpzDesktopBooster;

/// <summary>
/// Un toast: tarjeta arriba-centro de la pantalla, con barra de color por tipo, detalle + título.
/// Aparece sin robar foco (ShowActivated=False + WS_EX_NOACTIVATE), está pineado a todos los
/// virtual desktops (para que se vea aun justo después de cambiar de desk), y se auto-oculta con
/// un fade-out suave. Lo crea/orquesta <see cref="Services.Toasts"/>.
/// </summary>
public partial class ToastWindow : Window
{
    private const double TopMargin = 20;

    public ToastWindow(string title, string detail, Color accent, string extra = "")
    {
        InitializeComponent();
        TitleText.Text = title;
        DetailText.Text = detail;
        DetailText.Visibility = string.IsNullOrEmpty(detail) ? Visibility.Collapsed : Visibility.Visible;
        ExtraText.Text = extra;
        ExtraText.Visibility = string.IsNullOrEmpty(extra) ? Visibility.Collapsed : Visibility.Visible;
        AccentBar.Background = new SolidColorBrush(accent);

        // Crear el HWND ya, para aplicar exstyles + pin antes de mostrar.
        new WindowInteropHelper(this).EnsureHandle();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;

        // Fuera de Alt-Tab/taskbar y que NO robe foco.
        var ex = WindowMethods.GetWindowLongPtr(hwnd, WindowMethods.GWL_EXSTYLE).ToInt64();
        ex |= WindowMethods.WS_EX_TOOLWINDOW | WindowMethods.WS_EX_NOACTIVATE;
        WindowMethods.SetWindowLongPtr(hwnd, WindowMethods.GWL_EXSTYLE, new IntPtr(ex));

        // Visible en cualquier desktop (un enforcement puede dispararse justo al cambiar de desk).
        try { VirtualDesktopAccessor.PinWindow(hwnd); } catch { }
    }

    /// <summary>Muestra el toast en (x, yTop) con un hold por defecto (4s).</summary>
    public void ShowAt(double centerX, double yTop) =>
        ShowAt(centerX, yTop, TimeSpan.FromSeconds(4));

    /// <summary>Muestra el toast en (x, yTop) y arranca su ciclo de vida (visible → fade → cerrar).</summary>
    public void ShowAt(double centerX, double yTop, TimeSpan hold)
    {
        // Mostramos PRIMERO (invisible) y recién DESPUÉS centramos. Con SizeToContent, antes de Show()
        // ActualWidth todavía es 0 → "centrar" daba Left ≈ centerX y el toast aparecía corrido a la
        // derecha. Con la ventana ya renderizada, ActualWidth es real → centra de verdad. Como Opacity
        // arranca en 0, el reposicionamiento ocurre invisible: no se ve ningún salto.
        Opacity = 0;
        Show();
        UpdateLayout();

        Left = centerX - ActualWidth / 2;
        Top = yTop;

        // Fade-in rápido.
        BeginAnimation(OpacityProperty, Fade(0, 1, 140));

        // Tras 'hold', fade-out y cerrar.
        var hideTimer = new System.Windows.Threading.DispatcherTimer { Interval = hold };
        hideTimer.Tick += (_, _) =>
        {
            hideTimer.Stop();
            var fade = Fade(Opacity, 0, 320);
            fade.Completed += (_, _) => Close();
            BeginAnimation(OpacityProperty, fade);
        };
        hideTimer.Start();
    }

    public double MeasuredHeight
    {
        get { UpdateLayout(); return ActualHeight; }
    }

    private static DoubleAnimation Fade(double from, double to, int ms) =>
        new(from, to, new Duration(TimeSpan.FromMilliseconds(ms)));
}
