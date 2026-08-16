using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using AmpzDesktopBooster.Interop;
using AmpzDesktopBooster.Services.Attention;

namespace AmpzDesktopBooster;

/// <summary>
/// Destello direccional en el lateral de la pantalla: cuando entra un aviso de atención de OTRO
/// desk, un chevron se enciende en el borde HACIA EL QUE está ese desk y se apaga solo en ~2s.
///
/// Por qué existe, si la barra ya pinta la pill de atención: la pill es un ESTADO consultable (está
/// ahí hasta que vayas), pero vive abajo, chiquita, y sólo la ves si mirás. El destello es el
/// EVENTO: dura lo que dura el aviso y se dispara en la PERIFERIA del campo visual, que es donde el
/// ojo detecta movimiento sin tener que enfocar. Los dos se complementan — el destello te avisa
/// AHORA, la pill te lo recuerda DESPUÉS. Por eso el destello no reemplaza nada ni pide ser clickeado.
///
/// Ventana persistente (se crea una vez, se muestra/oculta), igual que <see cref="OverlayWindow"/>:
/// no roba foco, no aparece en Alt-Tab, está pineada a todos los escritorios y es CLICK-THROUGH.
/// </summary>
public partial class AttentionArrowWindow : Window
{
    // El ciclo entero. Corto a propósito: si dura más, deja de leerse como un destello y pasa a ser
    // un cartel que tapa — y un overlay que tapa es un overlay que el usuario termina odiando.
    private static readonly TimeSpan Cycle = TimeSpan.FromSeconds(2.0);

    // Mismos acentos que la pill de la barra y los toasts (BarWindow.AttnUrgent/AttnDone): el color
    // ES el vocabulario de la feature (coral = te necesita, verde = terminó). Si acá pusiéramos otro
    // rojo, el usuario tendría que aprender dos códigos de color para la misma información.
    private static readonly Color Urgent = Color.FromRgb(0xE5, 0x63, 0x5A);
    private static readonly Color Done   = Color.FromRgb(0x44, 0xDD, 0x88);

    private readonly DispatcherTimer _hideTimer;

    public AttentionArrowWindow()
    {
        InitializeComponent();

        // Se oculta por timer y no por Storyboard.Completed: el Completed no dispara si el
        // storyboard se REINICIA (llega un segundo aviso a mitad de camino), y ahí la ventana
        // quedaría visible con opacidad 0 para siempre. El timer se reinicia con cada aviso.
        _hideTimer = new DispatcherTimer { Interval = Cycle + TimeSpan.FromMilliseconds(60) };
        _hideTimer.Tick += (_, _) => { _hideTimer.Stop(); Hide(); };

        // Mismo motivo que el overlay: creamos el HWND YA (sin mostrar) para aplicarle los estilos
        // extendidos y pinearlo a todos los desktops ANTES del primer aviso.
        new WindowInteropHelper(this).EnsureHandle();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        var hwnd = new WindowInteropHelper(this).Handle;

        var ex = WindowMethods.GetWindowLongPtr(hwnd, WindowMethods.GWL_EXSTYLE).ToInt64();
        // TRANSPARENT es OBLIGATORIO acá y no en el overlay: esto aparece SOBRE el borde de la
        // pantalla mientras el usuario trabaja. Sin click-through se comería clics de la app que
        // está debajo — un overlay informativo que roba un clic es un bug, no una feature.
        ex |= WindowMethods.WS_EX_TOOLWINDOW | WindowMethods.WS_EX_NOACTIVATE | WindowMethods.WS_EX_TRANSPARENT;
        WindowMethods.SetWindowLongPtr(hwnd, WindowMethods.GWL_EXSTYLE, new IntPtr(ex));

        // Pinear a todos los virtual desktops: el aviso llega mientras estás en CUALQUIER desk.
        try { VirtualDesktopAccessor.PinWindow(hwnd); } catch { }
    }

    /// <summary>
    /// Dispara el destello apuntando hacia <paramref name="targetDesk"/> desde <paramref name="currentDesk"/>.
    /// Si el desk objetivo es el mismo (o no se puede ubicar) NO hay dirección que señalar y no se
    /// muestra nada — señalar "hacia acá" sería ruido puro.
    /// </summary>
    public void Flash(int targetDesk, int currentDesk, AttentionLevel level)
    {
        if (targetDesk == currentDesk || targetDesk < 0 || currentDesk < 0) return;

        // La dirección sale del ÍNDICE del desk, que es el modelo espacial que Windows ya te enseñó
        // con Win+Ctrl+←/→: los escritorios son una fila, el 4 está a la derecha del 2. No hay nada
        // que configurar ni que adivinar — la flecha apunta a donde tu propia mano iría.
        bool toRight = targetDesk > currentDesk;

        Mirror.ScaleX = toRight ? 1 : -1;
        PositionOnEdge(toRight);
        Paint(level == AttentionLevel.ActionNeeded ? Urgent : Done);

        Visibility = Visibility.Visible;
        Show();
        Animate();

        _hideTimer.Stop();
        _hideTimer.Start();
    }

    /// <summary>
    /// Corta el destello YA — sin fade de salida. Lo llama App ante CUALQUIER cambio de desk.
    ///
    /// Por qué en cualquiera y no sólo al llegar al desk objetivo: la flecha no dice "el desk 2
    /// reclama", dice "desde donde estás parado, andá PARA ALLÁ". Es una instrucción RELATIVA a tu
    /// posición. En cuanto te movés, la posición desde la que se calculó dejó de existir y el dibujo
    /// pasa a mentir — literalmente te sigue empujando hacia el costado cuando ya llegaste. Que la
    /// dirección resultara "casualmente correcta" desde el desk nuevo no la hace válida: seguirías
    /// leyendo "te falta uno más".
    ///
    /// Y se corta EN SECO, sin desvanecer: el fade es para cuando el mensaje CUMPLIÓ su ciclo. Acá
    /// el mensaje quedó OBSOLETO, y desvanecer algo obsoleto es estirar una mentira medio segundo más.
    /// </summary>
    public void Cancel()
    {
        if (!IsVisible) return;

        _hideTimer.Stop();
        // null = soltar la animación y volver al valor base (Opacity=0, X=0). Sin esto, la animación
        // sigue "dueña" de la propiedad y el próximo Flash arrancaría desde donde quedó ésta.
        Root.BeginAnimation(OpacityProperty, null);
        Slide.BeginAnimation(TranslateTransform.XProperty, null);
        Hide();
    }

    /// <summary>
    /// Clava la ventana contra el borde correspondiente del monitor PRIMARIO, centrada en vertical.
    /// Primario y no "el del foreground" a propósito: es el mismo criterio que el overlay central,
    /// así los dos feedbacks aparecen siempre en la misma pantalla y el usuario sabe dónde mirar.
    /// </summary>
    private void PositionOnEdge(bool toRight)
    {
        Left = toRight ? SystemParameters.PrimaryScreenWidth - Width : 0;
        Top  = (SystemParameters.PrimaryScreenHeight - Height) / 2;
    }

    /// <summary>Tiñe chevron, halo y resplandor con el color del nivel.</summary>
    private void Paint(Color c)
    {
        ChevronBrush.Color = c;
        ChevronGlow.Color  = c;

        // El resplandor va MUY translúcido: es luz de fondo, no una franja. Los tres stops arman la
        // caída (nada adentro → insinuación → borde). Con alfa alto se convertiría en una barra
        // sólida que compite con el chevron en vez de acompañarlo.
        GlowInner.Color  = Color.FromArgb(0x00, c.R, c.G, c.B);
        GlowMiddle.Color = Color.FromArgb(0x1E, c.R, c.G, c.B);
        GlowOuter.Color  = Color.FromArgb(0x6E, c.R, c.G, c.B);
    }

    /// <summary>
    /// Entra rápido, se sostiene, se va lento — y mientras tanto DERIVA hacia afuera. La deriva es
    /// lo que convierte una luz que prende en un gesto que señala: el ojo periférico detecta
    /// movimiento mucho antes que color o forma, así que el desplazamiento es lo que hace que se
    /// perciba sin mirar. Es también por qué no alcanza con un simple fade.
    /// </summary>
    private void Animate()
    {
        var easeOut = new CubicEase { EasingMode = EasingMode.EaseOut };

        var fade = new DoubleAnimationUsingKeyFrames();
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        // Pico rápido (200ms): la aparición tiene que ser un destello, no un amanecer.
        fade.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(200)), easeOut));
        // Meseta corta: el tiempo justo para registrarlo si estabas mirando a otro lado.
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(900))));
        // Salida larga y suave: lo que hace que se sienta "elegante" y no un parpadeo de alarma.
        fade.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(Cycle),
                                                    new CubicEase { EasingMode = EasingMode.EaseIn }));

        // Arranca 18px HACIA ADENTRO y termina 10px hacia afuera: el recorrido corto basta para leer
        // la dirección. Un recorrido largo se leería como un objeto que cruza la pantalla.
        var slide = new DoubleAnimation(-18, 10, new Duration(Cycle)) { EasingFunction = easeOut };

        // BeginAnimation (y no un Storyboard de recursos) porque cada disparo tiene su propio color
        // y dirección, y porque re-invocarlo REEMPLAZA la animación en curso: dos avisos seguidos
        // reinician el destello limpio, sin encimarse.
        Root.BeginAnimation(OpacityProperty, fade);
        Slide.BeginAnimation(TranslateTransform.XProperty, slide);
    }
}
