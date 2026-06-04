using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using AmpzDesktopBooster.Desktops;
using AmpzDesktopBooster.Interop;
using AmpzDesktopBooster.Persistence;
using AmpzDesktopBooster.Services;

namespace AmpzDesktopBooster;

/// <summary>
/// Variables del proyecto / globales — la Win+Numpad* del legacy (Paths Manager).
/// Lista de paths/URLs con título, filtrable. Acciones:
///   Enter / doble-clic → abrir (URL al browser, path al explorer)
///   Shift+Enter        → abrir el directorio en Claude CLI
///   Ctrl+C             → copiar el path al portapapeles
///   F2 / ✎             → renombrar el título
///   F3 / ⭐            → marcar/desmarcar predeterminado (1 por pool)
///   Supr               → borrar la variable
///   re-presionar Win+* con la ventana abierta → dispara el predeterminado (lo maneja el router)
///
/// El dual-scope (proyecto vs global) ya viene resuelto: el caller pasa la PathPool correcta.
/// </summary>
public partial class ProjectPathsWindow : Window
{
    /// <summary>De qué pool viene la fila — define si es operable o sólo referencia, y cómo se pinta.</summary>
    private enum RowScope { Project, Global, Separator }

    /// <summary>Fila visible. Indexa a la entry real de SU pool por <see cref="PoolIndex"/>.</summary>
    private sealed class Row
    {
        public required RowScope Scope { get; init; }
        public required int PoolIndex { get; init; }
        public required string Title { get; init; }
        public required string Path { get; init; }
        public required bool IsDefault { get; init; }

        public bool IsProject   => Scope == RowScope.Project;
        public bool IsGlobal    => Scope == RowScope.Global;    // solo-lectura en la vista de proyecto
        public bool IsSeparator => Scope == RowScope.Separator; // rótulo de sección, no seleccionable

        /// <summary>
        /// Columna Título. El ⭐ del predeterminado se muestra SÓLO en las del proyecto: en esta
        /// vista el re-press del Win+* dispara el predeterminado DEL PROYECTO, así que marcar una
        /// global confundiría (su predeterminado sólo aplica cuando estás parado en un desk global).
        /// </summary>
        public string Display => IsProject && IsDefault ? "⭐ " + Title : Title;
    }

    private readonly PathPool _pool;
    private readonly PathPool? _globalPool; // no-null en scope de proyecto: se anexa de solo-lectura
    private readonly string _deskName;
    private readonly string _explorerSeed;

    public ProjectPathsWindow(PathPool pool, string deskName, string explorerSeed = "", PathPool? globalPool = null)
    {
        InitializeComponent();

        _pool = pool;
        _globalPool = globalPool;
        _deskName = deskName;
        _explorerSeed = explorerSeed;

        Icon = AppIcon.TryLoadForWindow();
        HeaderText.Text = $"{pool.Label} — Variables";
        SubHeaderText.Text = deskName;

        RefreshList();

        FilterBox.TextChanged += (_, _) => RefreshList();
        FilterBox.PreviewKeyDown += OnFilterKeyDown;
        PathList.PreviewKeyDown += OnListKeyDown;
        PathList.MouseDoubleClick += (_, _) => OpenSelected(claude: false);

        AddBtn.Click += (_, _) => AddNew();
        EditBtn.Click += (_, _) => RenameSelected();
        DefaultBtn.Click += (_, _) => ToggleDefaultSelected();
        DeleteBtn.Click += (_, _) => DeleteSelected();
        CloseBtn.Click += (_, _) => Close();

        Loaded += (_, _) => FilterBox.Focus();
    }

    /// <summary>Dispara el predeterminado del pool (lo llama el router al re-presionar Win+*).</summary>
    public bool FireDefault()
    {
        int di = _pool.DefaultIndex;
        if (di < 0)
            return false;
        OpenValue(_pool.Entries[di].Path, claude: false);
        Close();
        return true;
    }

    private void RefreshList()
    {
        string filter = FilterBox.Text.Trim();
        PathList.Items.Clear();

        // 1) Las del proyecto (operables).
        foreach (var r in PoolRows(_pool, RowScope.Project, filter))
            PathList.Items.Add(r);

        // 2) En scope de proyecto anexamos las GLOBALES de solo-lectura bajo un separador, para no
        //    quedar ciegos a las compartidas. El separador entra SÓLO si hay alguna que matchee el
        //    filtro (si no, no ensuciamos la lista con un rótulo de sección vacío).
        if (_globalPool is not null)
        {
            var globals = PoolRows(_globalPool, RowScope.Global, filter).ToList();
            if (globals.Count > 0)
            {
                PathList.Items.Add(Separator());
                foreach (var r in globals)
                    PathList.Items.Add(r);
            }
        }

        SelectFirstSelectable();
    }

    /// <summary>Filas de una pool que matchean el filtro (busca SOLO en el título, como el legacy).</summary>
    private static IEnumerable<Row> PoolRows(PathPool pool, RowScope scope, string filter)
    {
        var entries = pool.Entries;
        for (int i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            if (filter != "" && !e.Title.Contains(filter, StringComparison.OrdinalIgnoreCase))
                continue;
            yield return new Row { Scope = scope, PoolIndex = i, Title = e.Title, Path = e.Path, IsDefault = e.Default };
        }
    }

    /// <summary>Fila-rótulo que separa la sección de globales. No es operable (ver estilo en el XAML).</summary>
    private static Row Separator() => new()
    {
        Scope = RowScope.Separator, PoolIndex = -1, IsDefault = false, Path = "",
        Title = "──  Globales (compartidas)  ──",
    };

    /// <summary>Selecciona la primera fila operable (saltea el separador). Lista vacía → sin selección.</summary>
    private void SelectFirstSelectable()
    {
        foreach (var item in PathList.Items)
            if (item is Row { IsSeparator: false })
            {
                PathList.SelectedItem = item;
                return;
            }
    }

    /// <summary>Fila seleccionada operable (el separador nunca cuenta). Fallback: única fila si hay una sola.</summary>
    private Row? Selected
    {
        get
        {
            if (PathList.SelectedItem is Row { IsSeparator: false } row)
                return row;
            var rows = PathList.Items.OfType<Row>().Where(r => !r.IsSeparator).ToList();
            return rows.Count == 1 ? rows[0] : null;
        }
    }

    /// <summary>Fila del PROYECTO seleccionada. Las globales son de solo-lectura acá → null (no mutan).</summary>
    private Row? SelectedProject => Selected is { IsProject: true } row ? row : null;

    // ── Teclado ─────────────────────────────────────────────────────────────────

    private void OnFilterKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)        { OpenSelected(claude: Shift); e.Handled = true; }
        else if (e.Key == Key.Escape)  { Close(); e.Handled = true; }
        else if (e.Key == Key.Down && PathList.Items.Count > 0)
        {
            PathList.SelectedIndex = 0;
            PathList.Focus();
            e.Handled = true;
        }
    }

    private void OnListKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:  OpenSelected(claude: Shift); e.Handled = true; break;
            case Key.Escape: Close();                     e.Handled = true; break;
            case Key.Delete: DeleteSelected();            e.Handled = true; break;
            case Key.F2:     RenameSelected();            e.Handled = true; break;
            case Key.F3:     ToggleDefaultSelected();     e.Handled = true; break;
            case Key.C when Ctrl: CopySelected();         e.Handled = true; break;
        }
    }

    private static bool Shift => (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
    private static bool Ctrl  => (Keyboard.Modifiers & ModifierKeys.Control) != 0;

    // ── Acciones ──────────────────────────────────────────────────────────────

    private void OpenSelected(bool claude)
    {
        if (Selected is not { } row) return;
        OpenValue(row.Path, claude);
        Close();
    }

    private void OpenValue(string value, bool claude)
    {
        // El monitor de ESTA ventana (la que el usuario está mirando) es "mi pantalla": la carpeta
        // debe aparecer acá, no saltar a otro monitor donde quedó abierta antes. Se captura ANTES del
        // Close() del caller, con la ventana aún viva, y viaja como valor (no dependemos de que siga
        // abierta cuando el foco diferido se resuelva).
        IntPtr monitor = WindowMethods.MonitorOf(new WindowInteropHelper(this).Handle);
        var result = claude ? PathOpener.OpenInClaude(value) : PathOpener.Open(value, monitor);
        if (result == PathOpener.Result.NotFound)
            MessageBox.Show(
                claude ? "Sólo se puede abrir un directorio en Claude CLI." : $"No existe o no es válido:\n{value}",
                "Variables", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void CopySelected()
    {
        if (Selected is not { } row) return;
        try { Clipboard.SetText(row.Path); } catch { }
    }

    private void DeleteSelected()
    {
        if (SelectedProject is not { } row) return; // globales: solo-lectura, no se borran desde acá
        _pool.Delete(row.PoolIndex);
        RefreshList();
    }

    private void ToggleDefaultSelected()
    {
        if (SelectedProject is not { } row) return; // globales: solo-lectura
        _pool.ToggleDefault(row.PoolIndex);
        RefreshList();
    }

    private void RenameSelected()
    {
        if (SelectedProject is not { } row) return; // globales: solo-lectura
        string? title = PromptDialog.Show(this, "Renombrar variable", "Título:", row.Title);
        if (title is null) return;
        _pool.UpdateTitle(row.PoolIndex, title);
        RefreshList();
    }

    private void AddNew()
    {
        // Path pre-cargado con el del Explorer activo (si lo capturamos al abrir), como el legacy.
        string? path = PromptDialog.Show(this, "Nueva variable", "Path o URL:", _explorerSeed);
        if (string.IsNullOrWhiteSpace(path)) return;
        string? title = PromptDialog.Show(this, "Nueva variable", "Título:", "");
        if (title is null) return;
        if (title.Trim() == "") title = path.Trim();
        _pool.Add(title, path);
        RefreshList();
    }
}
