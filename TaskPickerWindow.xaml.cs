using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using AmpzDesktopBooster.Services.Localization;
using AmpzDesktopBooster.Services.Tasks;

namespace AmpzDesktopBooster;

/// <summary>
/// Picker de tareas (Win+NumpadInsert): lista las tareas ABIERTAS que devolvió el aggregator, con filtro
/// en vivo. Enter / doble-click ancla la tarea al desk actual.
///
/// ABRE INSTANTE + LOADER: el picker NO espera al fetch para mostrarse. Se construye con cabecera y
/// loader visible, el llamador (HotkeyRouter) lo muestra de inmediato y después dispara el fetch en
/// background; cuando termina, llama a SetItems / SetError vía Dispatcher. La razón: con varias
/// cuentas + Vikunja haciendo N fetches anidados, la espera previa al picker se sentía como freeze.
///
/// TAREAS PERSONALES (custom) — todo por TECLADO, sin secciones aparte:
/// el MISMO searchbox filtra Y crea. Ctrl+Enter sobre el texto tipeado genera una tarea personal
/// (CustomTaskStore, durable) y la mete en el MISMO listado: queda filtrable y pickeable igual que
/// una web, así al otro día la retomás sin pensar. Supr sobre una fila personal seleccionada la
/// descarta. Las personales se ven SIEMPRE, aunque el fetch web falle o no haya cuentas — son
/// locales e independientes del gestor. Se distinguen con un chip "✎ personal".
///
/// La ventana NO sabe de HTTP ni de providers: recibe items o un error ya armados y devuelve la
/// elegida por callback. Separación de capas — el router orquesta el fetch, la ventana sólo muestra.
/// </summary>
public partial class TaskPickerWindow : Window
{
    /// <summary>
    /// Una fila del picker. Expone trozos PRE-FORMATEADOS que el DataTemplate (TaskPickerWindow.xaml)
    /// pinta en colores distintos. Por qué no un solo ToString: con todo en un string se pierde la
    /// jerarquía visual (título vs metadata vs columna vs board, todo gris). Separar por TextBlock
    /// permite color por trozo y que los vacíos se Colapsen vía EmptyStringToCollapsedConverter sin
    /// dejar huecos. Una tarea personal usa la MISMA fila: en la columna de cuenta muestra el chip
    /// "✎ personal" en vez del [account], para que se distinga de un vistazo.
    /// </summary>
    private sealed record Row(TaskItem Task)
    {
        public string AccountChip   => Task.IsCustom
                                         ? Loc.T("TaskPicker.CustomTag")
                                         : (string.IsNullOrEmpty(Task.AccountName) ? "" : $"[{Task.AccountName}]");
        public string IdLabel       => string.IsNullOrEmpty(Task.Identifier)  ? "" : $"{Task.Identifier}  —";
        public string Title         => Task.Title;
        public string StageInParens => string.IsNullOrEmpty(Task.Stage)   ? "" : $"({Task.Stage})";
        public string ProjectName   => Task.Project ?? "";
    }

    private readonly Action<TaskItem> _onPick;

    // Dos fuentes, UN solo listado: las web (efímeras, llegan por SetItems) y las personales
    // (durables, del store, cargadas al abrir). RefreshList las mezcla filtradas.
    private readonly List<Row> _webRows = new();
    private readonly List<Row> _customRows = new();
    private readonly CustomTaskStore _customStore = CustomTaskStore.Load();

    public TaskPickerWindow(string deskName, Action<TaskItem> onPick)
    {
        InitializeComponent();

        _onPick = onPick;
        SubHeaderText.Text = deskName;

        FilterBox.TextChanged += (_, _) => { UpdateFilterPlaceholder(); RefreshList(); };
        FilterBox.PreviewKeyDown += OnFilterKeyDown;
        TaskList.PreviewKeyDown += OnListKeyDown;
        TaskList.MouseDoubleClick += (_, _) => PickSelected();
        CloseBtn.Click += (_, _) => Close();

        // Tareas personales del store → al listado. Si hay aunque sea una, el loader se va de una
        // (se ven SIN esperar el fetch web): son locales e independientes del gestor.
        foreach (var e in _customStore.Entries)
            _customRows.Add(new Row(ToCustomTask(e)));
        if (_customRows.Count > 0)
        {
            LoadingOverlay.Visibility = Visibility.Collapsed;
            RefreshList();
        }

        // Cierre al clickear afuera o cambiar de desktop (se arma a los 700ms, ver CloseOnDeactivate).
        this.CloseOnDeactivate();

        Loaded += (_, _) => FilterBox.Focus();
    }

    /// <summary>Inyecta las tareas web ya traídas. Oculta el loader y arma la lista filtrable.</summary>
    public void SetItems(IReadOnlyList<TaskItem> tasks)
    {
        _webRows.Clear();
        foreach (var t in tasks) _webRows.Add(new Row(t));
        LoadingOverlay.Visibility = Visibility.Collapsed;
        RefreshList();
    }

    /// <summary>
    /// Reemplaza el loader por un mensaje de error. Si YA hay tareas personales visibles, no hacemos
    /// nada: el listado (con las personales) se queda — el fallo web no debe tapar lo local.
    /// </summary>
    public void SetError(string message)
    {
        if (_customRows.Count > 0) return;
        LoadingText.Text = message;
        LoadingHint.Text = Loc.T("TaskPicker.ErrorHint");
    }

    /// <summary>Loader con mensaje custom (ej. "Sin tareas abiertas"). Mismo respeto por lo local que SetError.</summary>
    public void SetEmpty(string title, string hint)
    {
        if (_customRows.Count > 0) return;
        LoadingText.Text = title;
        LoadingHint.Text = hint;
    }

    private bool Matches(Row r, string filter) =>
        filter == ""
        || r.Task.Title.Contains(filter, StringComparison.OrdinalIgnoreCase)
        || r.Task.Identifier.Contains(filter, StringComparison.OrdinalIgnoreCase)
        || r.Task.AccountName.Contains(filter, StringComparison.OrdinalIgnoreCase)
        || (r.Task.Project ?? "").Contains(filter, StringComparison.OrdinalIgnoreCase)
        || (r.Task.Stage   ?? "").Contains(filter, StringComparison.OrdinalIgnoreCase);

    private void RefreshList()
    {
        string filter = FilterBox.Text.Trim();
        TaskList.Items.Clear();
        // Web primero (el grueso), personales al final — pero ambas en el MISMO listado filtrable.
        foreach (var r in _webRows)    if (Matches(r, filter)) TaskList.Items.Add(r);
        foreach (var r in _customRows) if (Matches(r, filter)) TaskList.Items.Add(r);
    }

    private void OnFilterKeyDown(object sender, KeyEventArgs e)
    {
        // Ctrl+Enter sobre el texto tipeado → CREAR tarea personal con ese título (el searchbox es
        // filtro Y creador a la vez). Va antes que el Enter pelado para ganarle el handle.
        if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            CreateCustomFromFilter();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Enter)       { PickSelected(); e.Handled = true; }
        else if (e.Key == Key.Escape) { Close();        e.Handled = true; }
        else if (e.Key == Key.Down && TaskList.Items.Count > 0)
        {
            TaskList.SelectedIndex = 0;
            TaskList.Focus();
            e.Handled = true;
        }
    }

    private void OnListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)        { PickSelected();   e.Handled = true; }
        else if (e.Key == Key.Escape)  { Close();          e.Handled = true; }
        else if (e.Key == Key.Delete)  { DiscardSelected(); e.Handled = true; }
    }

    private void PickSelected()
    {
        // Fila seleccionada, o el único resultado visible desde el searchbox (igual que el DeskPicker).
        var row = TaskList.SelectedItem as Row;
        if (row is null && TaskList.Items.Count == 1)
            row = (Row)TaskList.Items[0];
        if (row is null)
            return;

        _onPick(row.Task);
        Close();
    }

    // ── Tareas personales (en el mismo listado) ──────────────────────────────────

    /// <summary>
    /// Crea una tarea personal con el texto actual del searchbox, la persiste y la inserta en el
    /// listado. Limpia el filtro para volver a la lista completa y deja la nueva fila SELECCIONADA y
    /// enfocada → un Enter la ancla al toque. Título vacío = no-op.
    /// </summary>
    private void CreateCustomFromFilter()
    {
        var entry = _customStore.Add(FilterBox.Text);
        if (entry is null) return;

        var row = new Row(ToCustomTask(entry));
        _customRows.Add(row);

        FilterBox.Clear(); // dispara RefreshList si había texto…
        RefreshList();     // …y si ya estaba vacío, igual la pintamos (idempotente)

        TaskList.SelectedItem = row;
        TaskList.ScrollIntoView(row);
        TaskList.Focus();
    }

    /// <summary>Supr sobre una fila PERSONAL seleccionada → la descarta del store (web: no-op).</summary>
    private void DiscardSelected()
    {
        if (TaskList.SelectedItem is not Row row || !row.Task.IsCustom)
            return;

        _customStore.Remove(row.Task.Id);
        _customRows.RemoveAll(r => r.Task.Id == row.Task.Id);
        RefreshList();
    }

    /// <summary>CustomTaskEntry → TaskItem mínimo (solo título, marcado IsCustom). El detalle ya colapsa
    /// "Abrir tarea" sin URL, así que una personal encaja en el flujo de anclado sin tocar nada más.</summary>
    private static TaskItem ToCustomTask(CustomTaskEntry e) => new(
        Id: e.Id, Title: e.Title, Identifier: "", Done: false,
        DueDate: null, Priority: 0, Project: null, Url: null, IsCustom: true);

    private void UpdateFilterPlaceholder() =>
        FilterPlaceholder.Visibility = FilterBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
}
