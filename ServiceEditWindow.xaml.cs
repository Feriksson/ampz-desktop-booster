using System.Windows;
using AmpzDesktopBooster.Persistence;
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
    private ServiceEditWindow(string header, string scope, ServiceEntry initial)
    {
        InitializeComponent();
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
    /// </summary>
    public static ServiceEntry? Show(Window owner, string header, string scope, ServiceEntry? initial = null)
    {
        var dlg = new ServiceEditWindow(header, scope, initial ?? new ServiceEntry()) { Owner = owner };
        return dlg.ShowDialog() == true ? dlg.Result : null;
    }
}
