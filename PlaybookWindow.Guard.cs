using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using AmpzDesktopBooster.Services;
using AmpzDesktopBooster.Services.Localization;

namespace AmpzDesktopBooster;

// ── Playbooker · CONFIRMAR EN DOS TIEMPOS, minimizar y cerrar ────────────────────────────────────
//
// Pedido del usuario tras los primeros días de uso: "necesita protección contra el Esc". La ventana
// dejó de ser un popup que se abre y se cierra con el mismo atajo: es una sesión que dura, y un Esc de
// más (está al lado del 1, que es una respuesta) la tiraba entera.
//
// La protección NO es un diálogo: un "¿Seguro?" modal en medio de un quiz es otro lugar donde hay que
// mirar y otra tecla que aprender. Es una segunda pulsación de LO MISMO: la primera muestra el aviso
// en el pie ("Esc otra vez para cerrar") durante unos segundos; repetir el gesto dentro de ese tiempo
// lo confirma, y cualquier otra cosa —otra tecla, un click en otro lado, cambiar de ventana o
// simplemente esperar— lo desarma.
//
// Tres cosas pasan por acá, con la misma mecánica:
//   • CERRAR la ventana, venga de donde venga: Esc en la búsqueda vacía, el botón «Cerrar» o Alt+F4.
//   • CORTAR una ronda (Esc en el quiz) cuando ya tiene alguna respuesta anotada.
//   • ELIMINAR una entrada (X), en el quiz y en la ficha.
public partial class PlaybookWindow
{
    private enum GuardKind { None, Close, EndRound, Delete, Message }

    // Cuánto dura la oportunidad de confirmar. Largo como para leer el aviso y repetir el gesto con
    // calma; corto como para que un Esc de hace un rato no deje la ventana "armada" para cerrarse.
    private static readonly TimeSpan GuardWindow = TimeSpan.FromMilliseconds(2500);

    private GuardKind _guard = GuardKind.None;
    private Button? _guardButton;   // el botón que también confirma la guarda en curso (si tiene uno)
    private bool _guardKept;        // la tecla en curso armó o confirmó una guarda: no la desarma
    private bool _closeConfirmed;   // el cierre ya pasó su confirmación: Closing lo deja seguir
    private DispatcherTimer? _guardTimer;

    private void InitGuard()
    {
        // El ícono de la app: ahora la ventana tiene botón en la barra de tareas y entrada en Alt+Tab.
        Icon = AppIcon.TryLoadForWindow();

        _guardTimer = new DispatcherTimer { Interval = GuardWindow };
        _guardTimer.Tick += (_, _) => ClearGuard();

        CloseBtn.Click += (_, _) => RequestClose();
        MinimizeBtn.Click += (_, _) => WindowState = WindowState.Minimized;
        Closing += OnWindowClosing;

        // "Cualquier otra cosa lo desarma": un click fuera del botón que confirma, e irse a otra ventana.
        PreviewMouseDown += OnGuardMouseDown;
        Deactivated += (_, _) => ClearGuard();
        Closed += (_, _) => _guardTimer.Stop();
    }

    /// <summary>
    /// El corazón de la mecánica. La PRIMERA vez arma la guarda (muestra el aviso, arranca el reloj) y
    /// devuelve false; la SEGUNDA, con la misma guarda todavía armada, devuelve true: confirmado.
    /// Pedir una guarda distinta de la que estaba armada cuenta como "otra cosa": desarma la anterior
    /// y arma la nueva.
    /// </summary>
    private bool ConfirmTwice(GuardKind kind, string notice, Button? button = null)
    {
        _guardKept = true;
        if (_guard == kind)
        {
            ClearGuard();
            return true;
        }

        ShowGuardNotice(kind, notice, button);
        return false;
    }

    /// <summary>
    /// Un aviso suelto en el mismo lugar y con el mismo reloj, para lo que NO se pudo hacer (la base
    /// no eliminó la entrada). No confirma nada: se va solo, o con la tecla siguiente.
    /// </summary>
    private void ShowNotice(string text)
    {
        _guardKept = true;
        ShowGuardNotice(GuardKind.Message, text, null);
    }

    private void ShowGuardNotice(GuardKind kind, string notice, Button? button)
    {
        ClearGuard();
        _guard = kind;
        _guardButton = button;
        GuardNoticeText.Text = notice;
        GuardNotice.Visibility = Visibility.Visible;
        HintText.Visibility = Visibility.Hidden; // Hidden: conserva su lugar, nada se mueve
        // El botón que confirma lo dice con su TEXTO mientras dura (no con un color).
        if (kind == GuardKind.Close) CloseBtn.Content = Loc.T("Playbook.CloseConfirm");
        // Que un lector de pantalla lo anuncie: aparece lejos del foco, y es lo único que explica
        // por qué la tecla "no hizo nada".
        UIElementAutomationPeer.FromElement(GuardNoticeText)
            ?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        _guardTimer?.Stop();
        _guardTimer?.Start();
    }

    private void ClearGuard()
    {
        if (_guard == GuardKind.None) return;
        _guard = GuardKind.None;
        _guardButton = null;
        _guardTimer?.Stop();
        GuardNotice.Visibility = Visibility.Collapsed;
        HintText.Visibility = Visibility.Visible;
        CloseBtn.Content = Loc.T("Playbook.Close");
    }

    // ── Qué cuenta como "otra cosa" ──
    // Cada tecla pasa por OnPreviewKeyDown, que llama a estas dos alrededor de su despacho: si la tecla
    // no armó ni confirmó una guarda (ConfirmTwice marca _guardKept), la que hubiera se desarma.

    private void BeginGuardedKey() => _guardKept = false;

    private void EndGuardedKey(KeyEventArgs e)
    {
        if (_guardKept || _guard == GuardKind.None) return;
        // Un modificador solo (apoyar Shift, Ctrl o Alt) todavía no es "otra tecla". Y Key.System es
        // lo que llega con Alt apretado: el segundo Alt+F4 tiene que poder confirmar el primero — el
        // cierre en sí lo decide Closing, no esta tecla.
        if (e.Key is Key.LeftShift or Key.RightShift or Key.LeftCtrl or Key.RightCtrl
            or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin or Key.System) return;
        // Enter / Espacio sobre el botón que confirma van a ser su Click: lo decide el Click.
        if (e.Key is Key.Enter or Key.Space && FocusIsOn(_guardButton)) return;
        ClearGuard();
    }

    private void OnGuardMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_guard == GuardKind.None) return;
        // El click sobre el botón que confirma sigue de largo: su Click resuelve la guarda.
        if (_guardButton is not null && e.OriginalSource is DependencyObject source
            && IsInside(source, _guardButton)) return;
        ClearGuard();
    }

    private static bool IsInside(DependencyObject? element, DependencyObject ancestor)
    {
        while (element is not null)
        {
            if (ReferenceEquals(element, ancestor)) return true;
            // Un Run (texto dentro de un TextBlock) no es visual: se sube por su árbol lógico.
            element = element is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
                ? System.Windows.Media.VisualTreeHelper.GetParent(element)
                : LogicalTreeHelper.GetParent(element);
        }
        return false;
    }

    // ── Cerrar ──

    /// <summary>
    /// Pide cerrar la ventana. Es el único camino que cierra: lo usan Esc (búsqueda vacía) y el botón;
    /// Alt+F4 y cualquier otro cierre que mande Windows llegan a <see cref="OnWindowClosing"/>, que
    /// aplica la misma regla. Los tres gestos confirman la MISMA guarda: Esc y después «Cerrar» cierra.
    /// </summary>
    private void RequestClose()
    {
        if (!ConfirmTwice(GuardKind.Close, Loc.T("Playbook.ConfirmClose"), CloseBtn)) return;
        CommitIssueIfOpen(); // un problema a medio anotar no se pierde por cerrar
        _closeConfirmed = true;
        Close();
    }

    /// <summary>
    /// La guardia de TODAS las salidas. Un cierre que no pasó por la confirmación se CANCELA y la
    /// arma; el siguiente intento dentro del tiempo lo deja pasar. Al apagarse la app
    /// (<c>Application.Shutdown</c>) WPF ignora esta cancelación: salir de la app no queda trabado
    /// por el playbook, y no hay nada que perder — cada respuesta se guarda al darla.
    /// </summary>
    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_closeConfirmed) return;
        if (ConfirmTwice(GuardKind.Close, Loc.T("Playbook.ConfirmClose"), CloseBtn))
        {
            CommitIssueIfOpen(); // un problema a medio anotar no se pierde por cerrar (Alt+F4 dos veces)
            _closeConfirmed = true;
            return;
        }
        e.Cancel = true;
    }
}
