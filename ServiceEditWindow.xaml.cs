using System.Windows;
using AmpzDesktopBooster.Apps;
using AmpzDesktopBooster.Desktops;
using AmpzDesktopBooster.Persistence;
using AmpzDesktopBooster.Services;
using AmpzDesktopBooster.Services.Localization;

namespace AmpzDesktopBooster;

/// <summary>
/// Alta/edición de un servicio: los CUATRO campos en una sola pantalla.
///
/// A propósito no se resolvió encadenando <see cref="PromptDialog"/> (que es lo que hacía el alta de
/// puertos, con dos prompts seguidos): con cuatro campos, encadenar prompts te deja sin ver lo que ya
/// cargaste, sin poder corregir hacia atrás, y obliga a cancelar todo para arreglar un typo del primer
/// paso. Un formulario con los cuatro juntos es además el único lugar donde se ve la RELACIÓN entre
/// comando y puerto, que es lo que define si el servicio es un servidor o una tarea.
/// </summary>
public partial class ServiceEditWindow : Window
{
    /// <summary>
    /// IP de LAN para el PREVIEW, resuelta UNA vez al abrir el diálogo. No se re-resuelve en cada
    /// tecla a propósito: <see cref="LocalIp.Get"/> recorre todas las interfaces de red, y en la vida
    /// del diálogo la IP no va a cambiar. Ojo: esto es sólo el preview — al LANZAR, la IP se resuelve
    /// de nuevo y de cero (ver ServiceLauncher), que es lo que hace al token siempre actual.
    /// </summary>
    private readonly string? _previewIp = LocalIp.Get();

    /// <summary>
    /// El registro de puertos de todo el catálogo. Nullable para no volver obligatorio el parámetro
    /// en un diálogo que también se puede abrir desde otro lado: sin registro el editor funciona
    /// igual, simplemente sin la validación (degradar, nunca romper).
    /// </summary>
    private readonly PortRegistry? _ports;

    /// <summary>
    /// La entrada que se está editando, o null en un alta. Se guarda para excluirla del registro:
    /// si no, guardar un servicio sin tocarle el puerto se chocaría CONSIGO MISMO.
    /// </summary>
    private readonly ServiceEntry? _editing;

    private ServiceEditWindow(string header, string scope, ServiceEntry initial,
                              PortRegistry? ports, ServiceEntry? editing)
    {
        InitializeComponent();
        _ports = ports;
        _editing = editing;
        HeaderText.Text = header;
        ScopeText.Text = scope;

        TitleBox.Text = initial.Title;
        WorkDirBox.Text = initial.WorkDir;
        CommandBox.Text = initial.Command;
        // Puerto 0 = "sin puerto" (es una tarea) → el campo se muestra VACÍO, no con un "0" que el
        // usuario tendría que borrar y que además se lee como un puerto real mal cargado.
        PortBox.Text = initial.Port > 0 ? initial.Port.ToString() : "";
        AutoStartBox.IsChecked = initial.AutoStart; // null = indeterminado = "seguí el default"

        // El hint tiene que decir qué haría el default HOY, y el default depende del puerto → se
        // recalcula mientras tipeás. Si no, el usuario lee "arranca solo" con el checkbox en gris
        // mientras vacía el puerto, y la frase pasa a ser mentira sin que nada se mueva.
        PortBox.TextChanged += (_, _) => UpdateAutoStartHint();
        AutoStartBox.Checked += (_, _) => UpdateAutoStartHint();
        AutoStartBox.Unchecked += (_, _) => UpdateAutoStartHint();
        AutoStartBox.Indeterminate += (_, _) => UpdateAutoStartHint();
        UpdateAutoStartHint();

        // Preview en vivo del comando ya expandido. Es el que convierte a los tokens de "confiá en mí"
        // a algo VERIFICABLE antes de guardar: ves con qué IP y con qué puerto va a salir de verdad,
        // y ves al toque el caso roto ({port} sin puerto cargado) sin tener que lanzarlo para enterarte.
        CommandBox.TextChanged += (_, _) => UpdatePreview();
        PortBox.TextChanged += (_, _) => UpdatePreview();
        UpdatePreview();

        PortBox.TextChanged += (_, _) => UpdateConflict();
        UpdateConflict();

        OkBtn.Click += (_, _) => Accept();
        CancelBtn.Click += (_, _) => { DialogResult = false; };
        Loaded += (_, _) => { TitleBox.Focus(); TitleBox.SelectAll(); };
    }

    /// <summary>Traduce el estado del checkbox a lo que REALMENTE va a pasar en "levantar todo".</summary>
    private void UpdateAutoStartHint()
    {
        bool hasPort = int.TryParse(PortBox.Text.Trim(), out int p) && p > 0;
        AutoStartHint.Text = AutoStartBox.IsChecked switch
        {
            true  => Loc.T("Services.AutoStartOn"),
            false => Loc.T("Services.AutoStartOff"),
            _     => Loc.T(hasPort ? "Services.AutoStartAutoOn" : "Services.AutoStartAutoOff"),
        };
    }

    /// <summary>
    /// Muestra el comando REAL que se va a ejecutar. Sólo aparece si el comando usa tokens: para el
    /// 90% de los servicios (un `npm run dev` pelado) una línea que repite lo que acabás de tipear es
    /// ruido, y el ruido constante es lo que hace que después no leas el cartel que sí importa.
    /// </summary>
    private void UpdatePreview()
    {
        string command = CommandBox.Text.Trim();
        if (!CommandTokens.HasTokens(command))
        {
            PreviewText.Visibility = Visibility.Collapsed;
            return;
        }

        int.TryParse(PortBox.Text.Trim(), out int port);
        var result = CommandTokens.Expand(command, port, _previewIp, out string expanded);

        PreviewText.Visibility = Visibility.Visible;
        PreviewText.Text = "→ " + expanded + result switch
        {
            TokenResult.NoNetwork => "\n" + Loc.T("Services.PreviewNoIp"),
            TokenResult.NoPort => "\n" + Loc.T("Services.PreviewNoPort"),
            _ => "",
        };
    }

    /// <summary>
    /// Aviso en vivo de puerto tomado. Sólo informa: quién lo tiene y cuál es el primero libre. El
    /// bloqueo real vive en <see cref="Accept"/> — que el cartel no te frene mientras tipeás es lo
    /// que te deja llegar hasta el 3000 y corregirlo sin pelearte con el campo tecla por tecla.
    /// </summary>
    private void UpdateConflict()
    {
        if (_ports is null || !int.TryParse(PortBox.Text.Trim(), out int port) || port <= 0
            || _ports.FindOwner(port, _editing) is not { } owner)
        {
            PortConflictText.Visibility = Visibility.Collapsed;
            return;
        }

        int free = _ports.SuggestFree(port);
        PortConflictText.Visibility = Visibility.Visible;
        PortConflictText.Text =
            string.Format(Loc.T("Services.PortTakenInline"), owner.Port, owner.Title, owner.ScopeLabel)
            + (free > 0 ? "  " + string.Format(Loc.T("Services.PortFreeHint"), free) : "");
    }

    /// <summary>Lo que el usuario dejó cargado. Sólo se lee si <c>ShowDialog</c> devolvió true.</summary>
    private ServiceEntry Result { get; set; } = new();

    private void Accept()
    {
        string portText = PortBox.Text.Trim();
        int port = 0;
        if (portText != "" && (!int.TryParse(portText, out port) || port < 1 || port > 65535))
        {
            MessageBox.Show(Loc.T("Services.InvalidPort"), Loc.T("Services.WindowTitle"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // ⛔ UN PUERTO, UN DUEÑO — en TODO el catálogo (ver PortRegistry para el porqué del alcance).
        // Se bloquea acá y no se ofrece "guardar igual": un override que se puede clickear es una
        // regla que en la práctica no existe, y el choque que evita no se manifiesta al guardar sino
        // media hora después, cuando un server sirve la app del otro y el 🟢 dice que está todo bien.
        // El "no" viene siempre con el primer puerto libre a un Sí de distancia, para que acatar la
        // regla cueste un click y no una búsqueda a mano por el catálogo.
        if (port > 0 && _ports?.FindOwner(port, _editing) is { } owner)
        {
            string msg = string.Format(Loc.T("Services.PortTaken"), port, owner.Title, owner.ScopeLabel);
            int free = _ports.SuggestFree(port);
            if (free > 0)
            {
                var answer = MessageBox.Show(
                    msg + "\n\n" + string.Format(Loc.T("Services.PortTakenUseFree"), free),
                    Loc.T("Services.WindowTitle"), MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (answer == MessageBoxResult.Yes)
                {
                    PortBox.Text = free.ToString();
                    PortBox.Focus();
                    PortBox.SelectAll();
                }
            }
            else
            {
                MessageBox.Show(msg, Loc.T("Services.WindowTitle"),
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            return;
        }

        string title = TitleBox.Text.Trim();
        string command = CommandBox.Text.Trim();
        // Sin título no se puede identificar la fila en la lista. Caemos al comando (que es lo que
        // el servicio HACE) y, si tampoco hay, al puerto — mismo criterio de "autocompletar el título"
        // que traía el catálogo de puertos, para que nunca quede una fila anónima.
        if (title == "")
            title = command != "" ? command
                  : port > 0 ? string.Format(Loc.T("Services.AutoTitle"), port)
                  : "";
        if (title == "")
        {
            MessageBox.Show(Loc.T("Services.NeedSomething"), Loc.T("Services.WindowTitle"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Result = new ServiceEntry
        {
            Title = title,
            Command = command,
            WorkDir = WorkDirBox.Text.Trim(),
            Port = port,
            AutoStart = AutoStartBox.IsChecked, // null si quedó indeterminado → default por puerto
        };
        DialogResult = true;
    }

    /// <summary>
    /// Muestra el editor modal. Devuelve la entrada cargada, o null si se canceló.
    /// <paramref name="initial"/> null = alta; con valor = edición (los campos vienen pre-cargados).
    /// Ojo: <paramref name="initial"/> tiene que ser la entry VIVA de la pool, no una copia — el
    /// registro de puertos la excluye por referencia para que la edición no se choque consigo misma.
    /// </summary>
    public static ServiceEntry? Show(Window owner, string header, string scope,
                                     ServiceEntry? initial = null, PortRegistry? ports = null)
    {
        var dlg = new ServiceEditWindow(header, scope, initial ?? new ServiceEntry(), ports, initial)
        {
            Owner = owner,
        };
        return dlg.ShowDialog() == true ? dlg.Result : null;
    }
}
