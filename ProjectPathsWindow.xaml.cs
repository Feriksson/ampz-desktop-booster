using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using AmpzDesktopBooster.Desktops;
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
    /// <summary>Fila visible. Indexa a la entry real de la pool por <see cref="PoolIndex"/>.</summary>
    private sealed class Row
    {
        public required int PoolIndex { get; init; }
        public required string Title { get; init; }
        public required string Path { get; init; }
        public required bool IsDefault { get; init; }

        /// <summary>Lo que se ve en la columna Título: ⭐ adelante si es el predeterminado.</summary>
        public string Display => IsDefault ? "⭐ " + Title : Title;
    }

    private readonly PathPool _pool;
    private readonly string _deskName;
    private readonly string _explorerSeed;

    public ProjectPathsWindow(PathPool pool, string deskName, string explorerSeed = "")
    {
        InitializeComponent();

        _pool = pool;
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

        // Recorremos la pool real guardando el índice; el filtro busca SOLO en el título (como el legacy).
        var entries = _pool.Entries;
        for (int i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            if (filter != "" && !e.Title.Contains(filter, StringComparison.OrdinalIgnoreCase))
                continue;
            PathList.Items.Add(new Row { PoolIndex = i, Title = e.Title, Path = e.Path, IsDefault = e.Default });
        }

        if (PathList.Items.Count > 0)
            PathList.SelectedIndex = 0;
    }

    private Row? Selected =>
        PathList.SelectedItem as Row
        ?? (PathList.Items.Count == 1 ? (Row)PathList.Items[0] : null);

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
        var result = claude ? PathOpener.OpenInClaude(value) : PathOpener.Open(value);
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
        if (Selected is not { } row) return;
        _pool.Delete(row.PoolIndex);
        RefreshList();
    }

    private void ToggleDefaultSelected()
    {
        if (Selected is not { } row) return;
        _pool.ToggleDefault(row.PoolIndex);
        RefreshList();
    }

    private void RenameSelected()
    {
        if (Selected is not { } row) return;
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
