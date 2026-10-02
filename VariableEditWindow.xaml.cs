using System.Windows;
using AmpzDesktopBooster.Desktops;
using AmpzDesktopBooster.Persistence;
using AmpzDesktopBooster.Services.Localization;

namespace AmpzDesktopBooster;

/// <summary>
/// Alta/edición de una VARIABLE (path o URL) con sus dos campos en una sola pantalla.
///
/// Es la ÚNICA puerta de alta de variables: la usan la pestaña de config y el Paths Manager
/// (Win+Numpad*). El Paths Manager encadenaba antes dos <see cref="PromptDialog"/> (path, después
/// título) y se descartó por lo mismo que acá: con prompts encadenados no ves el otro campo, no podés
/// corregir hacia atrás y un typo del primer paso te obliga a cancelar todo. Mismo criterio (y mismo
/// piso visual) que <see cref="ServiceEditWindow"/>.
///
/// En un ALTA con catálogo suma los combos Espacio + Contexto (<see cref="ScopePicker"/>) para elegir
/// dónde se guarda, con default en el scope del que abre. En una EDICIÓN no: cambiarle el scope a una
/// variable existente es moverla, y eso ya vive en la config (con su disciplina de no dejar huérfanos).
/// </summary>
public partial class VariableEditWindow : Window
{
    /// <summary>null = sin picker (edición, o alta sin catálogo): el scope es el que pasó el caller.</summary>
    private readonly ScopePicker? _picker;

    private VariableEditWindow(string header, string scope, PathEntry initial,
                               ProjectStore? store = null, string? scopeKey = null)
    {
        InitializeComponent();
        HeaderText.Text = header;
        ScopeText.Text = scope;

        TitleBox.Text = initial.Title;
        PathBox.Text = initial.Path;

        if (store is not null && scopeKey is not null)
        {
            ScopePickerPanel.Visibility = Visibility.Visible;
            _picker = new ScopePicker(store, SpaceCombo, ContextCombo, scopeKey, UpdateScopeText);
            UpdateScopeText();
        }

        PathBox.TextChanged += (_, _) => UpdateStatus();
        UpdateStatus();

        OkBtn.Click += (_, _) => Accept();
        CancelBtn.Click += (_, _) => { DialogResult = false; };
        // El foco arranca en el Título aunque los combos estén arriba: el default del scope ya es el
        // caso común, y obligar a tabular dos combos para empezar a tipear castigaría el 90% de las altas.
        Loaded += (_, _) => { TitleBox.Focus(); TitleBox.SelectAll(); };
    }

    /// <summary>El renglón de scope deja de ser "dónde estás" y pasa a decir DÓNDE VA A QUEDAR.</summary>
    private void UpdateScopeText()
    {
        if (_picker is null) return;
        ScopeText.Text = string.Format(Loc.T("Scope.SavesIn"), _picker.Label);
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
    /// Para un alta que elige DÓNDE se guarda, usar <see cref="ShowNew"/>.
    /// </summary>
    public static PathEntry? Show(Window owner, string header, string scope, PathEntry? initial = null)
    {
        var dlg = new VariableEditWindow(header, scope, initial ?? new PathEntry()) { Owner = owner };
        return dlg.ShowDialog() == true ? dlg.Result : null;
    }

    /// <summary>
    /// Alta con elección de scope. Devuelve la variable y la key de scope ELEGIDA ("" global,
    /// "Espacio" o "Espacio/Contexto"), o null si se canceló. Sin <paramref name="store"/> no hay
    /// combos (degradar, nunca romper) y el scope devuelto es <paramref name="scopeKey"/> tal cual;
    /// <paramref name="scopeLabel"/> es lo que se muestra en ese caso.
    /// <paramref name="pathSeed"/> pre-carga el path (el del Explorer activo, como el legacy).
    /// </summary>
    public static (PathEntry Entry, string ScopeKey)? ShowNew(Window owner, string header,
        ProjectStore? store, string scopeKey, string scopeLabel, string pathSeed = "")
    {
        var dlg = new VariableEditWindow(header, scopeLabel, new PathEntry { Path = pathSeed },
                                         store, scopeKey) { Owner = owner };
        if (dlg.ShowDialog() != true) return null;
        return (dlg.Result, dlg._picker?.ScopeKey ?? scopeKey);
    }
}
