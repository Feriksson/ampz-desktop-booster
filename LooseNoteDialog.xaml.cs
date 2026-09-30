using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using AmpzDesktopBooster.Desktops;
using AmpzDesktopBooster.Services.Localization;

namespace AmpzDesktopBooster;

/// <summary>
/// Alta / renombre / re-etiquetado de una nota SUELTA: título + Espacio + Contexto.
///
/// Espacio y Contexto se ELIGEN de los que existen (combos), nunca se tipean: una etiqueta de texto
/// libre con un typo sería una nota colgada de un espacio que no existe — invisible para la búsqueda
/// por espacio e imposible de corregir desde la pestaña Espacios, justo lo que la disciplina de
/// scopes vino a evitar. El contexto se recarga al cambiar el espacio (sólo los de ESE espacio).
///
/// Se abre con ShowDialog sobre el navegador de notas: la guarda de CloseOnDeactivate ve que el foco
/// se fue a una ventana nuestra y NO cierra al padre.
/// </summary>
public partial class LooseNoteDialog : Window
{
    /// <summary>Resultado del diálogo. Project/Module "" = sin etiqueta.</summary>
    public sealed record Result(string Title, string Project, string Module);

    private readonly ProjectStore _store;
    private readonly string _initialModule;

    // Primer ítem de cada combo = "sin etiqueta". Se compara por referencia, no por texto: un espacio
    // podría llamarse literalmente "(sin espacio)".
    private readonly string _noSpace = Loc.T("Notes.DlgNoSpace");
    private readonly string _noContext = Loc.T("Notes.DlgNoContext");

    private LooseNoteDialog(ProjectStore store, string heading, string title, string project, string module)
    {
        InitializeComponent();
        _store = store;
        _initialModule = module;

        TitleText.Text = heading;
        TitleBox.Text = title;

        var spaces = new List<string> { _noSpace };
        spaces.AddRange(store.GetHistory().OrderBy(h => h, StringComparer.CurrentCultureIgnoreCase));
        // La etiqueta actual se ofrece aunque no esté en el historial (no debería pasar: los
        // reorganizadores la arrastran). Sin esto, un F2 para cambiar sólo el título la borraría en silencio.
        if (project != "" && !spaces.Skip(1).Any(s => Same(s, project))) spaces.Add(project);
        SpaceCombo.ItemsSource = spaces;
        SpaceCombo.SelectedItem = project == "" ? _noSpace : spaces.Skip(1).First(s => Same(s, project));

        SpaceCombo.SelectionChanged += (_, _) => FillContexts(keep: "");
        FillContexts(keep: _initialModule);

        OkBtn.Click += (_, _) => DialogResult = true;
        CancelBtn.Click += (_, _) => DialogResult = false;
        Loaded += (_, _) => { TitleBox.Focus(); TitleBox.SelectAll(); };
    }

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private string SelectedSpace =>
        SpaceCombo.SelectedItem is string s && !ReferenceEquals(s, _noSpace) ? s : "";

    /// <summary>Recarga los contextos del espacio elegido, dejando seleccionado <paramref name="keep"/> si existe.</summary>
    private void FillContexts(string keep)
    {
        var contexts = new List<string> { _noContext };
        string space = SelectedSpace;
        if (space != "")
        {
            contexts.AddRange(_store.GetModules(space).Select(m => m.Name)
                                    .OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase));
            if (keep != "" && !contexts.Skip(1).Any(c => Same(c, keep))) contexts.Add(keep); // ver el ctor
        }
        ContextCombo.ItemsSource = contexts;
        ContextCombo.SelectedItem = keep == "" || space == ""
            ? _noContext
            : contexts.Skip(1).FirstOrDefault(c => Same(c, keep)) ?? _noContext;
        // Sin espacio no hay contexto posible: deshabilitado se lee mejor que un combo con una sola opción.
        ContextCombo.IsEnabled = space != "";
    }

    /// <summary>Muestra el diálogo modal sobre <paramref name="owner"/>. null si se cancela.</summary>
    public static Result? Show(Window owner, ProjectStore store, string heading,
                               string title, string project, string module)
    {
        var dlg = new LooseNoteDialog(store, heading, title, project, module) { Owner = owner };
        if (dlg.ShowDialog() != true) return null;

        string t = dlg.TitleBox.Text.Trim();
        if (t == "") t = Loc.T("Notes.Untitled"); // una fila sin título en el navegador no se puede señalar
        string p = dlg.SelectedSpace;
        string m = p != "" && dlg.ContextCombo.SelectedItem is string c && !ReferenceEquals(c, dlg._noContext) ? c : "";
        return new Result(t, p, m);
    }
}
