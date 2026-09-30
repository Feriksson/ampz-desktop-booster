using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AmpzDesktopBooster.Desktops;
using AmpzDesktopBooster.Services;
using AmpzDesktopBooster.Services.Localization;

namespace AmpzDesktopBooster;

/// <summary>
/// Navegador + editor de notas — la Win+Numpad/ del legacy (Notes Editor), crecida.
///
/// A la IZQUIERDA, una lista de la misma familia que Variables y Servicios (filtro, columnas
/// Título/Espacio/Contexto, divisor "en otros espacios", Ctrl+P, atajos impresos en los botones):
///   1. la nota de ESTE escritorio (espacio/contexto o global) — la que la ventana abre desde siempre;
///      abrir la ventana cae sobre ella, seleccionada y con el editor enfocado: memoria muscular intacta;
///   2. las notas SUELTAS (<see cref="LooseNoteStore"/>), última editada arriba;
///   3. bajo el divisor, las notas de los DEMÁS espacios/contextos (las no vacías) y la global — con
///      el toggle Ctrl+P, o SOLAS apenas escribís en el filtro (misma ampliación automática que
///      Variables/Servicios: "¿dónde había anotado esto?" no debería exigir apretar un toggle).
/// A la DERECHA, el editor de la nota seleccionada, y debajo el panel de notas de la CARPETA activa del
/// Explorer (sin cambios: atado al disco, se colapsa si no había carpeta).
///
/// Guardado — regla de oro: NUNCA se pierde texto. Sigue el flujo "abrir, editar, cerrar y listo" (no
/// hay que guardar a mano): al cambiar de fila se guarda la anterior ANTES de cargar la nueva, al
/// cerrar (Esc, botón, o el cierre solo por perder el foco de ShowFocused) se guarda en Closing, y
/// Ctrl+S guarda explícito sin cerrar. Siempre sólo si el texto cambió respecto de lo cargado.
/// </summary>
public partial class ProjectNotesWindow : Window
{
    private enum RowKind { Scope, Loose, Separator }

    /// <summary>Fila del navegador. Una nota de scope se identifica por su key; una suelta, por su Id.</summary>
    private sealed class Row
    {
        public required RowKind Kind { get; init; }
        public string ScopeKey { get; init; } = "";   // Kind == Scope ("" = global)
        public string LooseId { get; init; } = "";    // Kind == Loose
        public required string Title { get; init; }
        public string Project { get; init; } = "";
        public string Module { get; init; } = "";
        public System.Windows.Media.Brush ModuleBrush { get; init; } = System.Windows.Media.Brushes.Transparent;
        public bool IsCurrent { get; init; }

        public bool IsSeparator => Kind == RowKind.Separator;
        public bool IsLoose => Kind == RowKind.Loose;

        /// <summary>Identidad estable (sobrevive a RefreshList, que reconstruye las filas).</summary>
        public string Key => Kind == RowKind.Loose ? "l:" + LooseId : "s:" + ScopeKey;

        public string TypeIcon => Kind switch
        {
            RowKind.Loose => "📝",
            RowKind.Scope when IsCurrent => "📌",
            RowKind.Scope => "📋",
            _ => "",
        };
    }

    private readonly ProjectStore _store;
    private readonly string _deskName;
    private readonly string _currentKey; // scope del desk: "" global, "Espacio" o "Espacio/Contexto"

    // Lo que está cargado en el editor: identidad + texto con el que se cargó (para guardar sólo si cambió).
    private Row? _editing;
    private string _initial = "";

    // Fila que el usuario "tiene elegida" aunque el filtro la esconda: al volver a verse, se re-selecciona.
    private string? _preferredKey;
    private bool _refreshing;
    private bool _showAllProjects;

    // Notas de carpeta: path capturado al abrir, su texto inicial, y si hay carpeta del todo.
    private readonly string _activeFolder;
    private readonly bool _hasFolder;
    private string _folderInitial = "";

    public ProjectNotesWindow(ProjectStore store, string deskName, int deskIdx, string? activeFolder)
    {
        InitializeComponent();

        _store = store;
        _deskName = deskName;
        _currentKey = store.ResolveScopeKey(deskName, deskIdx);

        Icon = AppIcon.TryLoadForWindow();
        HeaderText.Text = Loc.T("Notes.WindowTitle");
        SubHeaderText.Text = $"{store.ScopeLabel(deskName, deskIdx)} · {deskName}";

        // ── Panel de carpeta: sólo si había un Explorer con carpeta real en foreground ──
        _activeFolder = activeFolder ?? "";
        _hasFolder = _activeFolder.Length > 0;
        if (_hasFolder)
        {
            // Header = nombre de la carpeta (lo que identifica la nota); subheader = path completo,
            // para que se vea de qué carpeta exacta son estas notas (la key es sólo el nombre).
            FolderHeaderText.Text = Path.GetFileName(_activeFolder.TrimEnd('\\', '/', ' '));
            FolderSubHeaderText.Text = $"{Loc.T("Notes.FolderScope")} · {_activeFolder}";
            _folderInitial = store.GetFolderNotes(_activeFolder);
            FolderNotesBox.Text = _folderInitial;
        }
        else
        {
            // Sin carpeta → colapsamos el panel inferior y su splitter: todo el alto para el editor.
            FolderPanel.Visibility = Visibility.Collapsed;
            PanelSplitter.Visibility = Visibility.Collapsed;
            SplitterRow.Height = new GridLength(0);
            FolderRow.Height = new GridLength(0);
        }

        this.SizeToWorkArea();

        _preferredKey = "s:" + _currentKey; // se abre SOBRE la nota de este escritorio, como siempre
        UpdateAllProjectsBtn();
        RefreshList();

        FilterBox.TextChanged += (_, _) => RefreshList();
        NoteList.SelectionChanged += (_, _) => OnSelectionChanged();
        NoteList.MouseDoubleClick += (_, _) => FocusEditor();

        NewBtn.Click += (_, _) => NewLooseNote();
        EditBtn.Click += (_, _) => EditSelectedLoose();
        DeleteBtn.Click += (_, _) => DeleteSelectedLoose();
        SaveBtn.Click += (_, _) => SaveExplicit();
        SearchBtn.Click += (_, _) => FocusFilter();
        AllProjectsBtn.Click += (_, _) => ToggleAllProjects();
        CloseBtn.Click += (_, _) => Close();

        PreviewKeyDown += OnKeyDown;
        // Closing corre TAMBIÉN cuando la ventana se cierra sola por perder el foco (ShowFocused →
        // CloseOnDeactivate): es el único punto que cubre todos los caminos de cierre.
        Closing += (_, _) => AutoSave();
        Loaded += (_, _) => FocusEditor();
    }

    // ── Lista ─────────────────────────────────────────────────────────────────

    private void RefreshList()
    {
        // Antes de reconstruir: lo tipeado en el editor se guarda, así (a) el filtro busca sobre el
        // texto REAL y (b) si la fila editada queda fuera del filtro, no se pierde nada.
        CommitEditor();

        string filter = FilterBox.Text.Trim();
        _refreshing = true;
        NoteList.Items.Clear();

        // 1) La nota de ESTE escritorio. Aunque esté vacía: es el lugar de siempre para escribir.
        AddIfMatch(ScopeRow(_currentKey, isCurrent: true), filter);

        // 2) Notas sueltas, la última editada arriba (lo que venís usando queda a mano).
        foreach (var n in _store.LooseNotes.All.OrderByDescending(n => n.Updated))
            AddIfMatch(LooseRow(n), filter);

        // 3) Otros espacios/contextos + la global, bajo UN solo divisor. Con el toggle, o solas en
        //    cuanto el filtro tiene texto (en CUALQUIER scope — misma regla que Variables/Servicios).
        if (_showAllProjects || filter != "")
        {
            int headerAt = NoteList.Items.Count;
            NoteList.Items.Add(new Row { Kind = RowKind.Separator, Title = Loc.T("Paths.SepAutoWiden") });

            foreach (var key in OtherScopeKeys())
                AddIfMatch(ScopeRow(key, isCurrent: false), filter);

            // Nada matcheó afuera → sin divisor colgando (se leería como "hay algo más abajo").
            if (NoteList.Items.Count == headerAt + 1)
                NoteList.Items.RemoveAt(headerAt);
        }

        // Selección: la fila que venías usando si sigue visible; si no, la primera.
        var rows = NoteList.Items.OfType<Row>().Where(r => !r.IsSeparator).ToList();
        var target = rows.FirstOrDefault(r => r.Key == _preferredKey) ?? rows.FirstOrDefault();
        NoteList.SelectedItem = target;
        if (target is not null) NoteList.ScrollIntoView(target);
        _refreshing = false;

        Load(target);
    }

    /// <summary>
    /// Keys de las notas de OTROS scopes: la global primero (si no es la tuya) y después las de espacio/
    /// contexto NO vacías, alfabéticas. Las vacías no se listan: una fila sin nada que leer es ruido
    /// (para escribir en otro espacio se va a ese escritorio). La global sí va aunque esté vacía: es un
    /// destino conocido, y así se puede escribir en ella desde cualquier escritorio.
    /// </summary>
    private IEnumerable<string> OtherScopeKeys()
    {
        if (_currentKey != ProjectStore.GlobalScope) yield return ProjectStore.GlobalScope;
        foreach (var (key, _) in _store.NonEmptyScopeNotes()
                     .Where(e => !string.Equals(e.Key, _currentKey, StringComparison.OrdinalIgnoreCase))
                     .OrderBy(e => e.Key, StringComparer.CurrentCultureIgnoreCase))
            yield return key;
    }

    /// <summary>Fila de la nota de un scope. El título dice QUÉ es; las columnas, DE QUIÉN.</summary>
    private Row ScopeRow(string key, bool isCurrent)
    {
        bool isGlobal = key == ProjectStore.GlobalScope;
        ProjectPathsWindow.ResolveScopeColumns(key, isGlobal, _store,
                                               out string project, out string module, out var brush);
        string title = isCurrent ? Loc.T("Notes.RowCurrent")
                     : isGlobal ? Loc.T("Notes.RowGlobal")
                     : module != "" ? Loc.T("Notes.RowContext")
                     : Loc.T("Notes.RowSpace");
        return new Row
        {
            Kind = RowKind.Scope, ScopeKey = key, Title = title, IsCurrent = isCurrent,
            Project = project, Module = module, ModuleBrush = brush,
        };
    }

    private Row LooseRow(LooseNote n)
    {
        // Misma lectura de columnas que las notas de scope: se arma la key y se parte con el helper
        // compartido, así el color del contexto sale idéntico al de Variables/Servicios.
        var brush = System.Windows.Media.Brushes.Transparent as System.Windows.Media.Brush;
        string module = "";
        if (n.Project != "")
            ProjectPathsWindow.ResolveScopeColumns(ProjectStore.ScopeKey(n.Project, n.Module), false, _store,
                                                   out _, out module, out brush);
        return new Row
        {
            Kind = RowKind.Loose, LooseId = n.Id,
            Title = n.Title.Trim() == "" ? Loc.T("Notes.Untitled") : n.Title,
            Project = n.Project, Module = module, ModuleBrush = brush,
        };
    }

    /// <summary>El filtro matchea título, CONTENIDO, espacio o contexto (case-insensitive).</summary>
    private void AddIfMatch(Row row, string filter)
    {
        if (filter == ""
            || Has(row.Title) || Has(row.Project) || Has(row.Module) || Has(ContentOf(row)))
            NoteList.Items.Add(row);

        bool Has(string s) => s.Contains(filter, StringComparison.OrdinalIgnoreCase);
    }

    private string ContentOf(Row row) => row.Kind switch
    {
        RowKind.Scope => _store.GetScopeNotes(row.ScopeKey),
        RowKind.Loose => _store.LooseNotes.Get(row.LooseId)?.Content ?? "",
        _ => "",
    };

    private void OnSelectionChanged()
    {
        if (_refreshing) return;
        if (NoteList.SelectedItem is Row { IsSeparator: false } row)
        {
            _preferredKey = row.Key;
            Load(row);
        }
    }

    // ── Editor ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Carga una fila en el editor, guardando PRIMERO la que estaba (si cambió). null = nada que
    /// editar (el filtro no dejó filas): el editor se deshabilita para que no se tipee "en el aire".
    /// </summary>
    private void Load(Row? row)
    {
        if (row is not null && _editing is not null && row.Key == _editing.Key)
        {
            _editing = row; // misma nota, fila reconstruida: sólo refrescamos la referencia
            UpdateEditorHeader();
            UpdateButtons();
            return;
        }

        CommitEditor();
        _editing = row;
        _initial = row is null ? "" : ContentOf(row);
        NotesBox.Text = _initial;
        NotesBox.IsEnabled = row is not null;
        UpdateEditorHeader();
        UpdateButtons();
    }

    /// <summary>Guarda lo que hay en el editor en SU nota, sólo si cambió. Idempotente.</summary>
    private void CommitEditor()
    {
        if (_editing is null || NotesBox.Text == _initial) return;

        if (_editing.Kind == RowKind.Scope)
            _store.SetScopeNotes(_editing.ScopeKey, NotesBox.Text);
        else if (_editing.Kind == RowKind.Loose)
            _store.LooseNotes.SetContent(_editing.LooseId, NotesBox.Text);
        _initial = NotesBox.Text;
    }

    private void UpdateEditorHeader()
    {
        if (_editing is null)
        {
            EditorTitleText.Text = Loc.T("Notes.NoMatch");
            EditorSubText.Text = "";
            return;
        }

        string owner = _editing.Project == "" ? ""
            : _editing.Module == "" ? _editing.Project
            : ProjectStore.PrettyScope(ProjectStore.ScopeKey(_editing.Project, _editing.Module));

        if (_editing.Kind == RowKind.Loose)
        {
            var note = _store.LooseNotes.Get(_editing.LooseId);
            EditorTitleText.Text = _editing.Title;
            var parts = new List<string> { Loc.T("Notes.Loose") };
            if (owner != "") parts.Add(owner);
            if (note is not null) parts.Add($"{Loc.T("Notes.Edited")} {note.Updated:dd/MM/yyyy HH:mm}");
            EditorSubText.Text = string.Join(" · ", parts);
        }
        else
        {
            // Nota de scope: el título grande es DE QUIÉN es (lo que la ventana siempre mostró).
            EditorTitleText.Text = _editing.ScopeKey == ProjectStore.GlobalScope
                ? Loc.T("Notes.RowGlobal")
                : ProjectStore.PrettyScope(_editing.ScopeKey);
            EditorSubText.Text = _editing.IsCurrent
                ? $"{Loc.T("Notes.ProjectScope")} · {_deskName}"
                : _editing.Title;
        }
    }

    /// <summary>Renombrar/borrar sólo aplican a las SUELTAS: la nota de un scope vive y muere con él.</summary>
    private void UpdateButtons()
    {
        bool loose = _editing is { IsLoose: true };
        EditBtn.IsEnabled = loose;
        DeleteBtn.IsEnabled = loose;
    }

    // ── Acciones ──────────────────────────────────────────────────────────────

    private void ToggleAllProjects()
    {
        _showAllProjects = !_showAllProjects;
        UpdateAllProjectsBtn();
        RefreshList();
    }

    private void UpdateAllProjectsBtn() =>
        AllProjectsBtn.Content = Loc.T(_showAllProjects ? "Paths.BtnAllProjectsOn" : "Paths.BtnAllProjectsOff");

    /// <summary>
    /// Nueva nota suelta, pre-etiquetada con el espacio/contexto de ESTE escritorio (lo más probable es
    /// que sea de lo que estás trabajando); el diálogo deja cambiarla o quitarla.
    /// </summary>
    private void NewLooseNote()
    {
        CommitEditor();
        SplitScope(_currentKey, out string project, out string module);
        var r = LooseNoteDialog.Show(this, _store, Loc.T("Notes.DlgNewHeading"), "", project, module);
        if (r is null) return;

        var note = _store.LooseNotes.Create(r.Title, r.Project, r.Module);
        _preferredKey = "l:" + note.Id;
        // Si había un filtro, la nota nueva (vacía) casi seguro no matchea: lo limpiamos para que se
        // vea. Limpiar dispara RefreshList por el TextChanged; si ya estaba vacío, refrescamos a mano.
        if (FilterBox.Text != "") FilterBox.Text = "";
        else RefreshList();
        FocusEditor();
    }

    private void EditSelectedLoose()
    {
        if (_editing is not { IsLoose: true } row || _store.LooseNotes.Get(row.LooseId) is not { } note) return;
        CommitEditor();
        var r = LooseNoteDialog.Show(this, _store, Loc.T("Notes.DlgEditHeading"),
                                     note.Title, note.Project, note.Module);
        if (r is null) return;
        _store.LooseNotes.Update(note.Id, r.Title, r.Project, r.Module);
        _editing = null; // la fila cambió de título/etiqueta: que RefreshList la recargue entera
        _preferredKey = "l:" + note.Id;
        RefreshList();
    }

    private void DeleteSelectedLoose()
    {
        if (_editing is not { IsLoose: true } row) return;
        var confirm = MessageBox.Show(this, string.Format(Loc.T("Notes.DeleteConfirm"), row.Title),
                                      Loc.T("Notes.WindowTitle"), MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        _store.LooseNotes.Delete(row.LooseId);
        // Soltamos el editor SIN guardar: la nota ya no existe, commitear la recrearía en ningún lado.
        _editing = null;
        _initial = NotesBox.Text;
        _preferredKey = null;
        RefreshList();
        FocusList();
    }

    /// <summary>Ctrl+S: guarda ambos paneles sin cerrar y reancla los "inicial" para no re-guardar de gusto.</summary>
    private void SaveExplicit()
    {
        CommitEditor();
        if (_hasFolder)
        {
            _store.SetFolderNotes(_activeFolder, FolderNotesBox.Text);
            _folderInitial = FolderNotesBox.Text;
        }
    }

    /// <summary>Guarda cada panel sólo si su texto cambió respecto al valor con el que se cargó.</summary>
    private void AutoSave()
    {
        CommitEditor();
        if (_hasFolder && FolderNotesBox.Text != _folderInitial)
            _store.SetFolderNotes(_activeFolder, FolderNotesBox.Text);
    }

    private static void SplitScope(string key, out string project, out string module)
    {
        int sep = key.IndexOf(ProjectStore.ScopeSeparator);
        project = sep >= 0 ? key[..sep] : key;
        module = sep >= 0 ? key[(sep + 1)..] : "";
    }

    // ── Teclado / foco ────────────────────────────────────────────────────────

    private void FocusEditor()
    {
        if (!NotesBox.IsEnabled) { FocusList(); return; }
        NotesBox.Focus();
        NotesBox.CaretIndex = NotesBox.Text.Length;
    }

    private void FocusFilter()
    {
        FilterBox.Focus();
        FilterBox.SelectAll();
    }

    /// <summary>Foco al ÍTEM seleccionado (no al ListView pelado): así las flechas arrancan desde ahí.</summary>
    private void FocusList()
    {
        var sel = NoteList.SelectedItem ?? NoteList.Items.OfType<Row>().FirstOrDefault(r => !r.IsSeparator);
        if (sel is null) { FilterBox.Focus(); return; }
        NoteList.SelectedItem = sel;
        NoteList.ScrollIntoView(sel);
        NoteList.UpdateLayout(); // recién reconstruida (RefreshList) el contenedor del ítem aún no existe
        if (NoteList.ItemContainerGenerator.ContainerFromItem(sel) is ListViewItem item) item.Focus();
        else NoteList.Focus();
    }

    private bool SelectedIsFirst =>
        NoteList.SelectedItem is not null
        && ReferenceEquals(NoteList.SelectedItem, NoteList.Items.OfType<Row>().FirstOrDefault(r => !r.IsSeparator));

    /// <summary>
    /// Atajos de TODA la ventana (como Variables: el keycap de cada botón no puede mentir según dónde
    /// esté el foco). Cuidando lo que el EDITOR necesita: Supr, Enter y Tab son del texto cuando
    /// escribís — sólo se interceptan en el filtro o en la lista. El resto (Ctrl+N/S/F/P, F2, Esc) no
    /// significa nada en un TextBox, así que funciona desde cualquier lado.
    /// </summary>
    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        bool inFilter = FilterBox.IsKeyboardFocused;
        bool inList = NoteList.IsKeyboardFocusWithin;

        switch (e.Key)
        {
            case Key.Escape:              Close();              break; // el auto-save corre en Closing
            case Key.S when ctrl:         SaveExplicit();       break;
            case Key.F when ctrl:         FocusFilter();        break;
            case Key.N when ctrl:         NewLooseNote();       break;
            case Key.P when ctrl:         ToggleAllProjects();  break;
            case Key.F2:                  EditSelectedLoose();  break;
            case Key.Delete when inList:  DeleteSelectedLoose(); break;

            case Key.Down when inFilter:  FocusList();          break;
            case Key.Up when inList && SelectedIsFirst: FocusFilter(); break;
            case Key.Enter when inFilter || inList:     FocusEditor(); break;
            case Key.Tab when (inFilter || inList) && !shift && !ctrl: FocusEditor(); break;

            default: return;
        }
        e.Handled = true;
    }
}
