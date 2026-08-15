using System.Windows;
using AmpzDesktopBooster.Desktops;
using AmpzDesktopBooster.Persistence;
using AmpzDesktopBooster.Services.Localization;

namespace AmpzDesktopBooster;

/// <summary>
/// Alta/edición de una VARIABLE (path o URL) con sus dos campos en una sola pantalla.
///
/// El alta desde el Paths Manager (Win+Numpad*) encadena dos <see cref="PromptDialog"/> seguidos, y
/// ahí tiene sentido: es un alta rápida sobre la marcha con el path ya sembrado del Explorer. Acá NO:
/// la pestaña de config es la superficie donde se EDITA lo ya cargado, y editar con prompts
/// encadenados te deja sin ver el valor viejo del otro campo y sin poder corregir hacia atrás.
/// Mismo criterio (y mismo piso visual) que <see cref="ServiceEditWindow"/>.
/// </summary>
public partial class VariableEditWindow : Window
{
    private VariableEditWindow(string header, string scope, PathEntry initial)
    {
        InitializeComponent();
        HeaderText.Text = header;
        ScopeText.Text = scope;

        TitleBox.Text = initial.Title;
        PathBox.Text = initial.Path;

        PathBox.TextChanged += (_, _) => UpdateStatus();
        UpdateStatus();

        OkBtn.Click += (_, _) => Accept();
        CancelBtn.Click += (_, _) => { DialogResult = false; };
        Loaded += (_, _) => { TitleBox.Focus(); TitleBox.SelectAll(); };
    }

    /// <summary>
    /// Avisa si el path apunta a algo que no existe, y distingue carpeta de URL. No BLOQUEA guardar:
    /// un path a un disco externo desconectado (o a un repo que vas a clonar) es perfectamente
    /// válido de cargar hoy — el aviso informa, no decide por el usuario.
    /// </summary>
    private void UpdateStatus()
    {
        string path = PathBox.Text.Trim();

        if (path == "")
        {
            StatusText.Visibility = Visibility.Collapsed;
            return;
        }

        StatusText.Visibility = Visibility.Visible;

        if (UrlHelper.IsUrl(path))
        {
            StatusText.Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x7F, 0xB8, 0xD4));
            StatusText.Text = Loc.T("Vars.StatusUrl");
            return;
        }

        bool exists = System.IO.Directory.Exists(path) || System.IO.File.Exists(path);
        StatusText.Foreground = new System.Windows.Media.SolidColorBrush(exists
            ? System.Windows.Media.Color.FromRgb(0x7A, 0xC4, 0x8A)
            : System.Windows.Media.Color.FromRgb(0xE5, 0x8A, 0x53));
        StatusText.Text = Loc.T(exists ? "Vars.StatusExists" : "Vars.StatusMissing");
    }

    /// <summary>Lo que quedó cargado. Sólo se lee si <c>ShowDialog</c> devolvió true.</summary>
    private PathEntry Result { get; set; } = new();

    private void Accept()
    {
        string path = PathBox.Text.Trim();
        if (path == "")
        {
            MessageBox.Show(this, Loc.T("Vars.NeedPath"), Loc.T("Vars.DlgTitle"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // Sin título, el path oficia de título — mismo fallback que la pool (PathPool.UpdateTitle) y
        // que el legacy. Una fila anónima en la lista sería imposible de identificar de un vistazo.
        string title = TitleBox.Text.Trim();
        Result = new PathEntry { Title = title == "" ? path : title, Path = path };
        DialogResult = true;
    }

    /// <summary>
    /// Muestra el editor modal. Devuelve la variable cargada, o null si se canceló.
    /// <paramref name="initial"/> null = alta; con valor = edición (campos pre-cargados).
    /// </summary>
    public static PathEntry? Show(Window owner, string header, string scope, PathEntry? initial = null)
    {
        var dlg = new VariableEditWindow(header, scope, initial ?? new PathEntry()) { Owner = owner };
        return dlg.ShowDialog() == true ? dlg.Result : null;
    }
}
