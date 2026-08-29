using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Data;
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

        /// <summary>
        /// Etiqueta del GRUPO al que cae la fila. Es el nombre CRUDO del estado tal como lo llama tu
        /// gestor ("In progress", "Doing", "En curso") — NO uno normalizado. Normalizarlo sería
        /// mentirte: si tu tablero dice "Haciendo", el divisor tiene que decir "Haciendo".
        ///
        /// Los estados se agrupan CRUZANDO cuentas a propósito: un "Doing" de ClickUp y uno de Trello
        /// caen juntos. Lo que querés ver al abrir el picker es "qué tengo en curso", no "qué tengo
        /// en curso en Trello" — y de qué cuenta vino cada fila ya lo dice el chip [cuenta].
        /// </summary>
        public string GroupLabel => Task.IsCustom
                                      ? Loc.T("TaskPicker.GroupCustom")
                                      : (string.IsNullOrWhiteSpace(Task.Stage)
                                            ? Loc.T("TaskPicker.GroupNoStage")
                                            : Task.Stage!.Trim());

        /// <summary>Rango de flujo del grupo (menor = más arriba). Ver StageRank.</summary>
        public int GroupOrder => Task.IsCustom ? 90 : StageRank(Task.Stage);
    }

    /// <summary>
    /// Ordena los grupos por ETAPA DEL FLUJO, no alfabéticamente. Dos razones para que exista:
    ///
    /// 1. Sin un orden explícito, los grupos saldrían en el orden en que la API devolvió las tareas
    ///    — que cambia entre fetches. Una lista que navegás por teclado y que se reordena sola entre
    ///    aperturas es inusable: la memoria muscular ("la segunda de arriba") deja de valer.
    /// 2. Alfabético tampoco sirve: "Backlog" quedaría arriba de "Doing" y lo que estás haciendo AHORA
    ///    terminaría enterrado abajo. El picker se abre para RETOMAR algo, así que lo que ya está en
    ///    marcha va primero y el pozo sin fondo (backlog) va último.
    ///
    /// Match por Contains case-insensitive sobre el nombre del estado — mismo criterio que la
    /// heurística de listas terminales de Trello, porque el problema es el mismo: los nombres los
    /// pone el usuario y no hay taxonomía común entre gestores. Un estado que no matchea nada cae al
    /// medio (50), NUNCA se pierde ni se mezcla con otro grupo.
    /// </summary>
    private static int StageRank(string? stage)
    {
        if (string.IsNullOrWhiteSpace(stage)) return 95; // sin estado: al fondo, antes de las personales

        foreach (var (rank, tokens) in StageBuckets)
            foreach (var t in tokens)
                if (stage.Contains(t, StringComparison.OrdinalIgnoreCase))
                    return rank;

        return 50; // estado propio no reconocido → al medio, con su propio divisor
    }

    /// <summary>
    /// Tokens de flujo (ES + EN). El ORDEN de este array importa: se evalúa de arriba abajo y gana el
    /// primer match, así que los buckets más específicos van antes que los genéricos.
    /// </summary>
    private static readonly (int Rank, string[] Tokens)[] StageBuckets =
    {
        // Lo que está EN MARCHA — lo primero que querés ver al abrir el picker.
        (10, new[] { "progress", "doing", "curso", "haciendo", "desarrollo", "wip", "working", "activo" }),
        // Empezado pero DETENIDO por algo/alguien: sigue siendo tuyo y urge destrabarlo.
        (20, new[] { "block", "bloque", "hold", "pausa", "espera", "waiting", "impedimento" }),
        // Empezado y en manos de OTRO (revisión/QA): no lo trabajás, pero lo seguís.
        (30, new[] { "review", "revis", "qa", "testing", "prueba", "verificaci", "aprobaci" }),
        // Listo para agarrar.
        (40, new[] { "to do", "todo", "to-do", "por hacer", "pendiente", "open", "abierto",
                     "nuevo", "new", "next", "sprint", "planificado" }),
        // El pozo sin fondo: último SIEMPRE, aunque alfabéticamente iría primero.
        (80, new[] { "backlog", "icebox", "idea", "algún día", "someday", "futuro" }),
    };

    private readonly Action<TaskItem> _onPick;

    // Dos fuentes, UN solo listado: las web (efímeras, llegan por SetItems) y las personales
    // (durables, del store, cargadas al abrir). RefreshList las mezcla filtradas.
    private readonly List<Row> _webRows = new();
    private readonly List<Row> _customRows = new();

    // Lo que la ListBox muestra HOY (ya filtrado y ordenado). Es el ItemsSource: tiene que ser una
    // colección propia y no TaskList.Items, porque el agrupado sólo anda sobre una vista real
    // (ver la nota del constructor). ObservableCollection y no List para que la vista se entere de
    // los cambios sin tener que refrescarla a mano en cada tecla del filtro.
    private readonly ObservableCollection<Row> _visibleRows = new();
    private readonly CustomTaskStore _customStore = CustomTaskStore.Load();

    public TaskPickerWindow(string deskName, Action<TaskItem> onPick)
    {
        InitializeComponent();

        _onPick = onPick;
        SubHeaderText.Text = deskName;

        FitToScreen();

        // Agrupado por estado. Va por ItemsSource + colección propia, y NO por TaskList.Items.
        //
        // ⚠ NO lo "simplifiques" volviendo a TaskList.Items.Add(): se probó y NO agrupa. La
        // ItemCollection de una ListBox implementa ICollectionView, pero en MODO DIRECTO (Items.Add)
        // usa una vista interna que no soporta agrupamiento: acepta el GroupDescription sin chistar
        // y lo IGNORA. Falla en silencio — instrumentado, daba groupDescriptions=1 pero
        // isGrouping=False y Groups=null, así que el HeaderTemplate no se ejecutaba nunca y el
        // divisor no aparecía. Sólo con ItemsSource la vista es una ListCollectionView real, que sí
        // agrupa.
        //
        // Los headers que genera NO son items seleccionables → las flechas, el Enter y el
        // SelectedIndex=0 del searchbox siguen funcionando igual. TaskList.Items sigue siendo válido
        // para LEER (Count / indexador / SelectedItem): refleja el ItemsSource.
        TaskList.ItemsSource = _visibleRows;
        CollectionViewSource.GetDefaultView(_visibleRows)
            .GroupDescriptions.Add(new PropertyGroupDescription(nameof(Row.GroupLabel)));

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

    /// <summary>
    /// Ancho de la ventana y alto de la lista, dimensionados contra el WorkArea del monitor.
    ///
    /// Por qué en runtime y no números fijos más grandes en el XAML: la ventana es NoResize +
    /// CenterScreen, así que un ancho fijo que no entre en la pantalla NO se puede arreglar
    /// arrastrando — se sale y punto. Los valores del XAML (1360 / 320) quedan como el tamaño
    /// "cómodo" al que aspiramos; acá lo recortamos si el monitor no da, y lo estiramos si sobra.
    ///
    /// El alto va por la LISTA, no por la ventana: con SizeToContent="Height" el alto total lo
    /// manda el contenido, así que agrandar Window.Height no haría nada — es ListArea la que tiene
    /// que crecer. El 0.62 deja aire para cabecera, filtro, hints y botón, y el WorkArea ya excluye
    /// el alto de nuestra AppBar (está registrada como tal), así que no la tapamos.
    /// </summary>
    private void FitToScreen()
    {
        var wa = SystemParameters.WorkArea;

        // Ancho: aspiramos a 1360, nunca más que la pantalla menos un margen, nunca menos que 910
        // (abajo de eso las 4 columnas de la fila se empiezan a pisar).
        Width = Math.Max(910, Math.Min(1360, wa.Width - 80));

        // Alto de la lista: ~62% del área útil, con piso y techo. El techo evita que en un monitor
        // vertical la lista se estire hasta lo absurdo.
        ListArea.Height = Math.Max(320, Math.Min(820, wa.Height * 0.62));
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
        _visibleRows.Clear();

        // Web + personales en el MISMO listado filtrable, pero AGRUPADAS por estado.
        //
        // El orden de inserción ES el orden de los divisores: PropertyGroupDescription crea cada
        // grupo la primera vez que ve su clave, así que agrupar bien exige insertar ya ordenado.
        // Por eso el OrderBy va acá y no en la vista.
        //
        // Desempates, en orden: rango de flujo (StageRank) → nombre del estado (dos estados del
        // mismo rango, ej. "Doing" y "En curso" de gestores distintos, quedan siempre en el mismo
        // orden entre aperturas) → vencimiento (dentro del grupo, lo que vence antes va arriba;
        // las sin fecha al final) → título (desempate final estable).
        var visible = _webRows.Concat(_customRows)
            .Where(r => Matches(r, filter))
            .OrderBy(r => r.GroupOrder)
            .ThenBy(r => r.GroupLabel, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(r => r.Task.DueDate ?? DateTimeOffset.MaxValue)
            .ThenBy(r => r.Task.Title, StringComparer.CurrentCultureIgnoreCase);

        foreach (var r in visible) _visibleRows.Add(r);

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
