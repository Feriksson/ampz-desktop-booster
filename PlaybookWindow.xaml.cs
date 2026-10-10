using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AmpzDesktopBooster.Knowledge;
using AmpzDesktopBooster.Services.Localization;

namespace AmpzDesktopBooster;

/// <summary>
/// El PLAYBOOKER (NumpadClear pelado): buscar una biblioteca o una naturaleza y jugarla. Una sola
/// ventana con cuatro pasos que se alternan por Visibility (mismo recurso que el Shortcuts Helper):
/// BÚSQUEDA → ARMADO (qué lado se muestra primero) → QUIZ → RESUMEN.
///
/// Tres decisiones que explican casi todo lo de abajo:
///
///   1. Esto NO es Anki, y es una decisión explícita. No hay cola, ni fechas, ni contador de cosas
///      que "te tocan": una ronda es la selección entera mezclada con sesgo a lo más flojo, se puede
///      cortar cuando quieras (Esc) y nada queda debiéndose. Ningún texto ni número de esta ventana
///      puede sugerir una obligación.
///   2. Es teclado primero (la app entera lo es), pero NADA es sólo-teclado: cada tecla tiene su
///      botón a la vista con la tecla dibujada, y la pista del pie dice siempre qué vale ahora.
///   3. Se abre con <c>ShowFocused(closeOnDeactivate: false)</c>: una sesión de estudio tiene que
///      sobrevivir a un Alt+Tab o a un cambio de escritorio. Por lo mismo el re-press del atajo sólo
///      la cierra desde la búsqueda (<see cref="CanCloseOnRepress"/>): una tecla suelta no puede
///      matar un quiz a la mitad.
///
/// Los datos se cargan UNA vez al abrir (bibliotecas, entradas, naturalezas, historial) y todo lo
/// que muestra la lista —conteos, solidez— se arma en memoria: nada de una consulta por fila.
/// </summary>
public partial class PlaybookWindow : Window
{
    private enum Stage { Search, Setup, Quiz, Summary }

    /// <summary>Qué lado se muestra como pregunta. "Mezclado" lo sortea tarjeta por tarjeta.</summary>
    private enum SideMode { Front, Back, Mixed }

    // La última elección de lado, recordada mientras viva la app (no se persiste: es una comodidad
    // de sesión, no una preferencia). Estática porque la ventana se recrea en cada apertura.
    private static SideMode _lastSideMode = SideMode.Front;

    private static readonly Random Rng = new();

    // Cuánto dura el destello del botón al responder. Corto a propósito: es la confirmación de
    // "anotado", no una animación — más que esto y el quiz se siente con freno de mano.
    private static readonly TimeSpan GradeFlash = TimeSpan.FromMilliseconds(120);

    private const double BarTrackWidth = 70; // el mismo ancho que la pista de la barra en el XAML
    private const double IndentPerLevel = 18;
    private const string PathSeparator = " › ";

    private readonly KnowledgeStore _store;
    private Stage _stage = Stage.Search;

    // ── Foto de la base tomada al abrir (y al volver de una ronda) ──
    private IReadOnlyDictionary<long, Library> _libraries = new Dictionary<long, Library>();
    private Dictionary<long, Entry> _entries = new();
    private Dictionary<long, string> _natureNames = new();
    private Dictionary<long, string> _languageNames = new();
    private List<PickRow> _rows = new();

    // ── Lo elegido ──
    private PickRow? _picked;
    private List<Entry> _selection = new();

    // ── La ronda en curso ──
    private List<QuizCard> _round = new();
    private IReadOnlyDictionary<(long EntryId, string Side), SolidityState> _roundStates =
        new Dictionary<(long, string), SolidityState>();
    private IReadOnlyDictionary<long, IReadOnlyList<Translation>> _translations =
        new Dictionary<long, IReadOnlyList<Translation>>();
    private SolidityAverage _solidityBefore;
    private readonly int[] _gradeCounts = new int[5]; // índice = (int)Grade; el 0 no se usa
    private int _index;
    private int _skipped;
    private int _unsaved;
    private bool _revealed;
    private bool _advancing; // true durante el destello: las teclas se ignoran hasta pasar de tarjeta

    /// <summary>Una tarjeta de la ronda: la entrada y el lado que le tocó mostrarse como pregunta.</summary>
    private sealed record QuizCard(Entry Entry, string Side);

    /// <summary>
    /// Una fila de la lista de búsqueda: una biblioteca (con todo su subárbol) o una naturaleza.
    /// Trae ya resuelto lo que pinta la plantilla — incluidas las Visibility — para que el XAML no
    /// necesite convertidores ni lógica.
    /// </summary>
    private sealed class PickRow
    {
        public bool IsNature { get; init; }
        public int Depth { get; init; }
        /// <summary>"Madre › " (vacío en una raíz o en una naturaleza): los ancestros, atenuados.</summary>
        public string PathPrefix { get; init; } = "";
        public string Name { get; init; } = "";
        public string CountText { get; init; } = "";
        public string SolidityText { get; init; } = "";
        public double BarFillWidth { get; init; }
        public Visibility BarVisibility { get; init; }
        public Visibility ChipVisibility => IsNature ? Visibility.Visible : Visibility.Collapsed;
        public string AccessibleName { get; init; } = "";
        /// <summary>Texto ya normalizado (sin tildes, minúsculas) contra el que se filtra.</summary>
        public string Haystack { get; init; } = "";
        public IReadOnlyList<long> EntryIds { get; init; } = Array.Empty<long>();
        /// <summary>Sangría del árbol. Sólo con el buscador vacío: filtrada, la lista es plana.</summary>
        public Thickness Indent { get; set; }
        public string FullName => PathPrefix + Name;
    }

    public PlaybookWindow(KnowledgeStore store)
    {
        InitializeComponent();
        _store = store;

        // Tamaño FIJO y cómodo, recortado al área de trabajo (que ya descuenta la barra): el texto
        // largo scrollea DENTRO de la tarjeta, la ventana no crece ni se achica con el contenido.
        var workArea = SystemParameters.WorkArea;
        Width = Math.Min(820, workArea.Width * 0.92);
        Height = Math.Min(600, workArea.Height * 0.92);

        PreviewKeyDown += OnPreviewKeyDown;
        HeaderBar.MouseLeftButtonDown += OnHeaderMouseDown;
        CloseBtn.Click += (_, _) => Close();

        SearchBox.TextChanged += (_, _) => ApplyFilter();
        PickList.PreviewTextInput += OnListTextInput;
        PickList.PreviewMouseLeftButtonUp += OnListMouseUp;

        StartBtn.Click += (_, _) => StartRound();
        SetupBackBtn.Click += (_, _) => BackToSearch(reload: false);

        RevealBtn.Click += (_, _) => Reveal();
        SkipBtn.Click += (_, _) => Skip();
        Grade1Btn.Click += (_, _) => GradeCurrent(Grade.DontKnow);
        Grade2Btn.Click += (_, _) => GradeCurrent(Grade.Doubts);
        Grade3Btn.Click += (_, _) => GradeCurrent(Grade.Improvable);
        Grade4Btn.Click += (_, _) => GradeCurrent(Grade.Spectacle);

        PlayAgainBtn.Click += (_, _) => StartRound();
        SummaryBackBtn.Click += (_, _) => BackToSearch(reload: true);

        // Nombre accesible de cada respuesta: número + texto ("3: Bien pero mejorable"). El contenido
        // del botón son dos elementos sueltos y un lector de pantalla no los une solo.
        foreach (var (button, grade) in GradeButtons())
            AutomationProperties.SetName(button, string.Format(Loc.T("Playbook.GradeAccessible"),
                (int)grade, GradeLabel(grade)));
        AutomationProperties.SetName(RevealBtn, Loc.T("Playbook.Reveal"));

        LoadData();
        ShowStage(Stage.Search);
        ApplyFilter();

        // ShowFocused machaca el foreground con reintentos; el foco de TECLADO al buscador se fuerza
        // acá, cuando el árbol ya está armado (mismo motivo que en el Shortcuts Helper).
        Loaded += (_, _) => FocusSearchBox();
    }

    /// <summary>
    /// ¿El re-press del atajo puede cerrar la ventana? Sólo desde la búsqueda. En cualquier otro paso
    /// hay algo en curso (una elección, una ronda, un resumen sin leer) y el re-press la trae al frente.
    /// </summary>
    public bool CanCloseOnRepress => _stage == Stage.Search;

    // ──────────────────────────── Datos ────────────────────────────

    /// <summary>
    /// Lee todo lo que la búsqueda necesita y arma las filas. Cuatro lecturas + el historial, y de
    /// ahí en más es todo memoria. Con la base caída cada lectura degrada a vacío (el store ya avisó
    /// por su cuenta): la ventana muestra el estado "sin bibliotecas" en vez de romperse.
    /// </summary>
    private void LoadData()
    {
        var libraries = _store.GetLibraries();
        _libraries = libraries.ToDictionary(l => l.Id);
        _entries = _store.GetAllEntries().ToDictionary(e => e.Id);
        var natures = _store.GetNatures();
        _natureNames = natures.ToDictionary(n => n.Id, n => n.Name);
        _languageNames = _store.GetLanguages().ToDictionary(l => l.Id, l => l.Name);

        // UNA lectura del historial y un solo cálculo para todas las filas.
        var states = Solidity.ComputeAll(_store.GetAllReviews(), DateTime.UtcNow);

        var ownEntries = _entries.Values.ToLookup(e => e.LibraryId, e => e.Id);
        var children = libraries.ToLookup(l => l.ParentId);
        var rows = new List<PickRow>();

        // Recorrido en profundidad desde las raíces: es el ORDEN DE ÁRBOL de la lista. Cada fila
        // necesita las entradas de todo su subárbol, que recién se conocen al volver de las hijas;
        // por eso se reserva el lugar antes de bajar y se completa después.
        List<long> Walk(Library library, int depth)
        {
            int slot = rows.Count;
            rows.Add(null!);
            var ids = ownEntries[library.Id].ToList();
            foreach (var child in children[library.Id].OrderBy(l => l.Sort).ThenBy(l => l.Id))
                ids.AddRange(Walk(child, depth + 1));
            rows[slot] = LibraryRow(library, depth, ids, states);
            return ids;
        }
        foreach (var root in children[null].OrderBy(l => l.Sort).ThenBy(l => l.Id))
            Walk(root, 0);

        // Naturalezas: por naturaleza EFECTIVA (la propia de la entrada o la heredada del árbol).
        var byNature = _entries.Values
            .Select(e => (e.Id, Nature: LibraryTree.EffectiveNatureId(e, _libraries)))
            .Where(x => x.Nature is not null)
            .ToLookup(x => x.Nature!.Value, x => x.Id);
        foreach (var nature in natures)
            rows.Add(NatureRow(nature, byNature[nature.Id].ToList(), states));

        _rows = rows;
    }

    private PickRow LibraryRow(Library library, int depth, List<long> entryIds,
        IReadOnlyDictionary<(long, string), SolidityState> states)
    {
        var path = LibraryTree.PathOf(library.Id, _libraries);
        string prefix = path.Count > 1 ? string.Join(PathSeparator, path.Take(path.Count - 1)) + PathSeparator : "";
        var average = AverageOf(entryIds, states);
        string count = CountLabel(entryIds.Count);
        return new PickRow
        {
            Depth = depth,
            PathPrefix = prefix,
            Name = library.Name,
            CountText = count,
            SolidityText = SolidityLabel(average),
            BarFillWidth = BarFill(average),
            BarVisibility = average.Played > 0 ? Visibility.Visible : Visibility.Collapsed,
            AccessibleName = string.Format(Loc.T("Playbook.RowLibraryAccessible"),
                prefix + library.Name, count, SpokenSolidity(average)),
            Haystack = TextFold.Fold(prefix + library.Name),
            EntryIds = entryIds,
        };
    }

    private PickRow NatureRow(Nature nature, List<long> entryIds,
        IReadOnlyDictionary<(long, string), SolidityState> states)
    {
        var average = AverageOf(entryIds, states);
        string count = CountLabel(entryIds.Count);
        return new PickRow
        {
            IsNature = true,
            Name = nature.Name,
            CountText = count,
            SolidityText = SolidityLabel(average),
            BarFillWidth = BarFill(average),
            BarVisibility = average.Played > 0 ? Visibility.Visible : Visibility.Collapsed,
            AccessibleName = string.Format(Loc.T("Playbook.RowNatureAccessible"),
                nature.Name, count, SpokenSolidity(average)),
            Haystack = TextFold.Fold(nature.Name),
            EntryIds = entryIds,
        };
    }

    // ── Solidez de un CONJUNTO (una fila, o la selección antes/después de la ronda) ──

    /// <summary>
    /// La solidez de una entrada como UN número: el promedio de sus lados jugados (frente y dorso).
    /// null = no se jugó de ningún lado. Cada lado lleva su propia solidez (saber una palabra viéndola
    /// no es evocarla desde la definición), pero una fila de la lista necesita un solo valor.
    /// </summary>
    private static double? EntryValue(long entryId, IReadOnlyDictionary<(long, string), SolidityState> states)
    {
        var front = Solidity.StateOf(states, entryId, ReviewSide.Front);
        var back = Solidity.StateOf(states, entryId, ReviewSide.Back);
        if (front.Played && back.Played) return (front.Value + back.Value) / 2;
        if (front.Played) return front.Value;
        if (back.Played) return back.Value;
        return null;
    }

    /// <summary>
    /// Promedio del conjunto. Mismo criterio que <see cref="Solidity.Average"/>: las no jugadas pesan
    /// 0 (una biblioteca recién empezada arranca abajo y SUBE al jugarla) y jugadas/total van aparte.
    /// </summary>
    private static SolidityAverage AverageOf(IEnumerable<long> entryIds,
        IReadOnlyDictionary<(long, string), SolidityState> states)
    {
        double sum = 0;
        int played = 0, total = 0;
        foreach (long id in entryIds)
        {
            total++;
            if (EntryValue(id, states) is not { } value) continue;
            played++;
            sum += value;
        }
        return new SolidityAverage(total == 0 ? 0 : sum / total, played, total);
    }

    private static double BarFill(SolidityAverage average) =>
        Math.Clamp(average.Value / Solidity.Max, 0, 1) * BarTrackWidth;

    /// <summary>
    /// El texto de solidez de una fila. Nunca jugada NO es 0%: es "sin jugar" (un estado propio).
    /// Jugada en parte, el porcentaje va con su "N de M jugadas" para que un número bajo por recién
    /// empezar no se lea igual que uno bajo porque cuesta. Sin entradas no hay nada que decir.
    /// </summary>
    private static string SolidityLabel(SolidityAverage average)
    {
        if (average.Total == 0) return "";
        if (average.Played == 0) return Loc.T("Playbook.NotPlayed");
        string percent = Percent(average.Value);
        return average.Played < average.Total
            ? percent + " · " + string.Format(Loc.T("Playbook.PlayedOf"), average.Played, average.Total)
            : percent;
    }

    /// <summary>Para el nombre accesible de la fila: ahí un hueco se leería "solidez:" y nada más,
    /// así que una fila sin entradas dice "sin jugar" (que además es cierto).</summary>
    private static string SpokenSolidity(SolidityAverage average) =>
        average.Total == 0 ? Loc.T("Playbook.NotPlayed") : SolidityLabel(average);

    /// <summary>Lo mismo en corto, para el "antes → después" del resumen.</summary>
    private static string ShortSolidity(SolidityAverage average) =>
        average.Played == 0 ? Loc.T("Playbook.NotPlayed") : Percent(average.Value);

    private static string Percent(double value) =>
        Math.Round(value).ToString("0", CultureInfo.CurrentCulture) + "%";

    private static string CountLabel(int count) => count == 1
        ? Loc.T("Playbook.EntriesOne")
        : string.Format(Loc.T("Playbook.EntriesMany"), count);

    // ──────────────────────────── Pasos ────────────────────────────

    private void ShowStage(Stage stage)
    {
        _stage = stage;
        SearchView.Visibility = stage == Stage.Search ? Visibility.Visible : Visibility.Collapsed;
        SetupView.Visibility = stage == Stage.Setup ? Visibility.Visible : Visibility.Collapsed;
        QuizView.Visibility = stage == Stage.Quiz ? Visibility.Visible : Visibility.Collapsed;
        SummaryView.Visibility = stage == Stage.Summary ? Visibility.Visible : Visibility.Collapsed;

        // Fuera de la búsqueda el encabezado dice QUÉ se está jugando; el avance sólo en el quiz.
        TitleText.Text = stage == Stage.Search || _picked is null ? Loc.T("Playbook.Header") : _picked.FullName;
        ProgressText.Visibility = stage == Stage.Quiz ? Visibility.Visible : Visibility.Collapsed;
        UpdateHint();

        // Lo recién mostrado todavía no pasó por layout y Focus() sobre algo sin medir no prende:
        // forzamos el layout para que quien llama pueda enfocar enseguida.
        UpdateLayout();
    }

    /// <summary>La pista del pie: las teclas que valen AHORA, ni una más.</summary>
    private void UpdateHint()
    {
        HintText.Text = _stage switch
        {
            Stage.Search => Loc.T("Playbook.HintSearch"),
            Stage.Setup => Loc.T(_selection.Count == 0 ? "Playbook.HintSetupEmpty" : "Playbook.HintSetup"),
            Stage.Quiz => Loc.T(_revealed ? "Playbook.HintQuizAnswer" : "Playbook.HintQuizQuestion"),
            _ => Loc.T("Playbook.HintSummary"),
        };
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Una sola puerta de teclado para toda la ventana (tunneling: corre ANTES que el control con
        // foco). Así Espacio jamás "clickea" el botón que quedó enfocado tras un click de mouse —
        // en el quiz eso anotaría una respuesta que nadie dio.
        switch (_stage)
        {
            case Stage.Search: OnSearchKey(e); break;
            case Stage.Setup: OnSetupKey(e); break;
            case Stage.Quiz: OnQuizKey(e); break;
            case Stage.Summary: OnSummaryKey(e); break;
        }
    }

    /// <summary>¿Enter le pertenece al botón que tiene el foco? (Tab hasta "Volver" + Enter = Volver.)</summary>
    private static bool FocusIsOn(params Button[] buttons) =>
        buttons.Any(b => ReferenceEquals(Keyboard.FocusedElement, b));

    private void OnHeaderMouseDown(object sender, MouseButtonEventArgs e)
    {
        // DragMove tira si el botón ya no está apretado cuando corre (click muy corto): no es un
        // error que importe, simplemente no hubo arrastre.
        try { DragMove(); } catch (InvalidOperationException) { }
    }

    // ──────────────────────────── 1 · Búsqueda ────────────────────────────

    private void FocusSearchBox()
    {
        SearchBox.Focus();
        Keyboard.Focus(SearchBox);
        SearchBox.CaretIndex = SearchBox.Text.Length;
    }

    /// <summary>
    /// Buscador vacío = TODO: las bibliotecas como árbol con sangría y, después, las naturalezas.
    /// Con texto = lista plana de lo que coincide (sin tildes ni mayúsculas, todas las palabras en
    /// cualquier orden) contra el camino de la biblioteca o el nombre de la naturaleza.
    /// </summary>
    private void ApplyFilter()
    {
        SearchPlaceholder.Visibility = SearchBox.Text == "" ? Visibility.Visible : Visibility.Collapsed;

        var terms = TextFold.Fold(SearchBox.Text).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        bool filtering = terms.Length > 0;
        var visible = filtering
            ? _rows.Where(r => terms.All(t => r.Haystack.Contains(t, StringComparison.Ordinal))).ToList()
            : _rows;
        foreach (var row in visible)
            row.Indent = new Thickness(filtering || row.IsNature ? 0 : row.Depth * IndentPerLevel, 0, 0, 0);

        // Lista nueva cada vez: las filas no notifican cambios, y reasignar el origen es lo que hace
        // que la plantilla relea la sangría.
        PickList.ItemsSource = visible.ToList();
        // La primera queda marcada: Enter desde el buscador elige lo que se ve resaltado.
        PickList.SelectedIndex = visible.Count > 0 ? 0 : -1;

        if (visible.Count > 0)
        {
            EmptyText.Visibility = Visibility.Collapsed;
        }
        else
        {
            // Dos vacíos distintos: no hay NADA cargado (se arregla creando una biblioteca) vs. el
            // filtro no encontró (se arregla borrando texto: Esc).
            EmptyText.Text = _rows.Count == 0
                ? Loc.T("Playbook.EmptyNoLibraries")
                : string.Format(Loc.T("Playbook.NoResults"), SearchBox.Text.Trim());
            EmptyText.Visibility = Visibility.Visible;
        }
    }

    private void OnSearchKey(KeyEventArgs e)
    {
        bool inList = PickList.IsKeyboardFocusWithin;
        switch (e.Key)
        {
            case Key.Escape:
                // Esc en dos tiempos: primero limpia el texto, recién con el buscador vacío cierra.
                e.Handled = true;
                if (SearchBox.Text != "") { SearchBox.Clear(); FocusSearchBox(); }
                else Close();
                break;

            case Key.Enter when !FocusIsOn(CloseBtn):
                e.Handled = true;
                PickSelected();
                break;

            case Key.Down when SearchBox.IsKeyboardFocusWithin && PickList.Items.Count > 0:
                // Bajar desde el buscador ENTRA a la lista. La primera ya estaba marcada, así que el
                // primer ↓ pasa a la segunda (si hay): igual que cualquier lanzador.
                e.Handled = true;
                FocusRow(Math.Min(Math.Max(PickList.SelectedIndex, -1) + 1, PickList.Items.Count - 1));
                break;

            case Key.Up when inList && PickList.SelectedIndex <= 0:
                e.Handled = true; // subir desde la primera fila vuelve al buscador
                FocusSearchBox();
                break;

            case Key.Back when inList:
                // Borrar estando en la lista sigue siendo "editar la búsqueda".
                e.Handled = true;
                if (SearchBox.Text.Length > 0) SearchBox.Text = SearchBox.Text[..^1];
                FocusSearchBox();
                break;
        }
    }

    /// <summary>Tipear con el foco en la lista vuelve al buscador y sigue escribiendo ahí.</summary>
    private void OnListTextInput(object sender, TextCompositionEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Text) || char.IsControl(e.Text[0])) return;
        e.Handled = true;
        SearchBox.Text += e.Text;
        FocusSearchBox();
    }

    private void OnListMouseUp(object sender, MouseButtonEventArgs e)
    {
        // Un click en una fila la ELIGE (no hace falta doble click ni Enter). Un click en la barra de
        // scroll o en el hueco no cae sobre ninguna fila y no hace nada.
        if (e.OriginalSource is DependencyObject source
            && ItemsControl.ContainerFromElement(PickList, source) is ListBoxItem { DataContext: PickRow row })
        {
            e.Handled = true;
            EnterSetup(row);
        }
    }

    private void FocusRow(int index)
    {
        if (index < 0 || index >= PickList.Items.Count) return;
        PickList.SelectedIndex = index;
        PickList.ScrollIntoView(PickList.Items[index]);
        PickList.UpdateLayout(); // la lista virtualiza: el contenedor existe recién después del scroll
        if (PickList.ItemContainerGenerator.ContainerFromIndex(index) is ListBoxItem item)
            item.Focus();
    }

    private void PickSelected()
    {
        // Prioridad: la fila marcada → la única visible. Sin ninguna (lista vacía) Enter no hace
        // nada, y el estado vacío ya está explicando por qué.
        var row = PickList.SelectedItem as PickRow
                  ?? (PickList.Items.Count == 1 ? PickList.Items[0] as PickRow : null);
        if (row is not null) EnterSetup(row);
    }

    private void BackToSearch(bool reload)
    {
        // Tras una ronda hay repasos nuevos: se relee todo para que las barras muestren los valores
        // de AHORA. Si sólo se volvió del armado no cambió nada y no se toca la base.
        if (reload) LoadData();
        _picked = null;
        _selection = new List<Entry>();
        ShowStage(Stage.Search);
        ApplyFilter();
        FocusSearchBox();
    }

    // ──────────────────────────── 2 · Armado ────────────────────────────

    private void EnterSetup(PickRow row)
    {
        _picked = row;
        _selection = row.EntryIds.Where(_entries.ContainsKey).Select(id => _entries[id]).ToList();

        SetupKind.Text = Loc.T(row.IsNature ? "Playbook.NatureChip" : "Playbook.LibraryKind");
        SetupName.Text = row.FullName;
        SetupCount.Text = CountLabel(_selection.Count);

        // Sin entradas no hay ronda posible. El botón se apaga Y el motivo queda a la vista.
        bool empty = _selection.Count == 0;
        StartBtn.IsEnabled = !empty;
        StartReason.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;

        ShowStage(Stage.Setup);
        SetSideMode(_lastSideMode);
    }

    private RadioButton RadioFor(SideMode mode) => mode switch
    {
        SideMode.Back => SideBackRadio,
        SideMode.Mixed => SideMixedRadio,
        _ => SideFrontRadio,
    };

    private SideMode CurrentSideMode() =>
        SideBackRadio.IsChecked == true ? SideMode.Back
        : SideMixedRadio.IsChecked == true ? SideMode.Mixed
        : SideMode.Front;

    /// <summary>Marca el lado Y le da el foco: en este grupo "elegido" y "enfocado" van juntos.</summary>
    private void SetSideMode(SideMode mode)
    {
        var radio = RadioFor(mode);
        radio.IsChecked = true;
        radio.Focus();
        Keyboard.Focus(radio);
    }

    private void OnSetupKey(KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                e.Handled = true;
                BackToSearch(reload: false);
                break;

            case Key.Enter when !FocusIsOn(SetupBackBtn, CloseBtn):
                e.Handled = true;
                StartRound(); // con la selección vacía no arranca: el motivo ya está en pantalla
                break;

            // Flechas: se mueven por el grupo y ELIGEN (por defecto WPF sólo mueve el foco, y habría
            // que confirmar con Espacio — un paso de más para tres opciones).
            case Key.Left:
                e.Handled = true;
                SetSideMode((SideMode)(((int)CurrentSideMode() + 2) % 3));
                break;
            case Key.Right:
                e.Handled = true;
                SetSideMode((SideMode)(((int)CurrentSideMode() + 1) % 3));
                break;

            case Key.D1 or Key.NumPad1: e.Handled = true; SetSideMode(SideMode.Front); break;
            case Key.D2 or Key.NumPad2: e.Handled = true; SetSideMode(SideMode.Back); break;
            case Key.D3 or Key.NumPad3: e.Handled = true; SetSideMode(SideMode.Mixed); break;
        }
    }

    // ──────────────────────────── 3 · Quiz ────────────────────────────

    /// <summary>
    /// Arma una ronda con TODA la selección, en orden sorteado con sesgo a lo más flojo. Sirve igual
    /// para la primera ronda y para "jugar de nuevo": cada vez relee los repasos de la selección, así
    /// el orden nuevo ya pesa con lo que se acaba de responder.
    /// </summary>
    private void StartRound()
    {
        if (_selection.Count == 0 || _picked is null) return;

        // En el resumen los botones de lado no están a la vista: ahí vale la última elección.
        var mode = _stage == Stage.Setup ? CurrentSideMode() : _lastSideMode;
        _lastSideMode = mode;

        var ids = _selection.Select(e => e.Id).ToList();
        _roundStates = Solidity.ComputeAll(_store.GetReviews(ids), DateTime.UtcNow);
        _translations = _store.GetTranslations(ids);
        _solidityBefore = AverageOf(ids, _roundStates);

        // El lado de cada tarjeta se decide ANTES de ordenar, porque el peso depende de la solidez
        // de ESE lado. WeightedOrder pesa un único lado para todo el mazo; con "Mezclado" cada
        // tarjeta tiene el suyo, así que se le pasa un mapa donde el lado que le tocó a cada una
        // figura bajo una misma key. Las que no entran al mapa cuentan como no jugadas (salen antes).
        var sideOf = new Dictionary<long, string>();
        var weights = new Dictionary<(long, string), SolidityState>();
        foreach (long id in ids)
        {
            string side = mode switch
            {
                SideMode.Front => ReviewSide.Front,
                SideMode.Back => ReviewSide.Back,
                _ => Rng.Next(2) == 0 ? ReviewSide.Front : ReviewSide.Back,
            };
            sideOf[id] = side;
            var state = Solidity.StateOf(_roundStates, id, side);
            if (state.Played) weights[(id, ReviewSide.Front)] = state;
        }

        _round = Solidity.WeightedOrder(ids, ReviewSide.Front, weights, Rng)
            .Select(id => new QuizCard(_entries[id], sideOf[id]))
            .ToList();

        Array.Clear(_gradeCounts);
        _index = 0;
        _skipped = 0;
        _unsaved = 0;
        _advancing = false;
        SaveNotice.Visibility = Visibility.Collapsed;

        ShowStage(Stage.Quiz);
        ShowCard();
    }

    private void ShowCard()
    {
        var card = _round[_index];
        _revealed = false;

        ProgressText.Text = string.Format(Loc.T("Playbook.Progress"), _index + 1, _round.Count);
        ShownSideText.Text = Loc.T(card.Side == ReviewSide.Front ? "Playbook.ShownFront" : "Playbook.ShownBack");
        CardPathText.Text = string.Join(PathSeparator, LibraryTree.PathOf(card.Entry.LibraryId, _libraries));

        // La naturaleza EFECTIVA (propia o heredada). Sin ninguna en toda la cadena, no hay chip.
        string nature = LibraryTree.EffectiveNatureId(card.Entry, _libraries) is { } natureId
                        && _natureNames.TryGetValue(natureId, out var name) ? name : "";
        CardNatureText.Text = nature;
        CardNatureChip.Visibility = nature == "" ? Visibility.Collapsed : Visibility.Visible;

        RenderCard(card);
        CardScroll.ScrollToTop();

        RevealBtn.Visibility = Visibility.Visible;
        GradePanel.Visibility = Visibility.Collapsed;
        CardSolidityText.Text = "";
        UpdateHint();

        // El foco vive en la tarjeta: las flechas scrollean el texto largo y ningún botón queda
        // "armado" bajo el Enter.
        CardScroll.Focus();
        Keyboard.Focus(CardScroll);
    }

    private void Reveal()
    {
        if (_revealed || _advancing) return;
        _revealed = true;

        var card = _round[_index];
        RenderCard(card);

        // La solidez que tenía ESTE lado al empezar la ronda (cada tarjeta sale una sola vez por
        // ronda, así que ese valor sigue siendo el vigente). Se muestra recién ahora: antes de
        // responder sería una pista de cuánto "deberías" saberla.
        var state = Solidity.StateOf(_roundStates, card.Entry.Id, card.Side);
        CardSolidityText.Text = state.Played
            ? string.Format(Loc.T("Playbook.SoliditySide"), Percent(state.Value))
            : Loc.T("Playbook.SolidityNotPlayed");

        RevealBtn.Visibility = Visibility.Collapsed;
        GradePanel.Visibility = Visibility.Visible;
        UpdateHint();
        CardScroll.Focus();
    }

    private IEnumerable<(Button Button, Grade Grade)> GradeButtons()
    {
        yield return (Grade1Btn, Grade.DontKnow);
        yield return (Grade2Btn, Grade.Doubts);
        yield return (Grade3Btn, Grade.Improvable);
        yield return (Grade4Btn, Grade.Spectacle);
    }

    private static string GradeLabel(Grade grade) => Loc.T("Playbook.Grade" + (int)grade);

    /// <summary>
    /// Anota la respuesta y pasa a la siguiente. El lado que se guarda es el que se MOSTRÓ como
    /// pregunta. Antes de revelar no hace nada: no se puede calificar lo que todavía no se vio.
    /// </summary>
    private void GradeCurrent(Grade grade)
    {
        if (_stage != Stage.Quiz || !_revealed || _advancing) return;

        var card = _round[_index];
        _gradeCounts[(int)grade]++;
        if (!_store.AddReview(card.Entry.Id, card.Side, grade))
        {
            // No se pudo guardar (base tomada, disco). La ronda SIGUE — cortarla sería peor — pero
            // se dice acá mismo y queda dicho hasta el final: el resumen no puede mentir un guardado.
            _unsaved++;
            SaveNotice.Visibility = Visibility.Visible;
        }

        // Destello del botón elegido y recién después la tarjeta siguiente. Mientras dura se
        // ignoran las teclas: un 3 doble no puede responder también la que viene.
        var button = GradeButtons().First(b => b.Grade == grade).Button;
        button.Tag = "flash";
        _advancing = true;
        var timer = new DispatcherTimer { Interval = GradeFlash };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            button.Tag = null;
            _advancing = false;
            if (IsLoaded && _stage == Stage.Quiz) Advance(); // la ventana pudo cerrarse en el medio
        };
        timer.Start();
    }

    /// <summary>Siguiente tarjeta SIN anotar nada: verla y pasar no dice cuánto la sabés.</summary>
    private void Skip()
    {
        if (_stage != Stage.Quiz || !_revealed || _advancing) return;
        _skipped++;
        Advance();
    }

    private void Advance()
    {
        _index++;
        if (_index >= _round.Count) EndRound();
        else ShowCard();
    }

    /// <summary>
    /// Fin de la ronda: porque se terminó la selección o porque se cortó con Esc (cortar es un final
    /// tan válido como el otro). Sin ninguna respuesta no hay nada que resumir y se vuelve directo.
    /// </summary>
    private void EndRound()
    {
        if (_gradeCounts.Sum() == 0) BackToSearch(reload: false);
        else ShowSummary();
    }

    private void OnQuizKey(KeyEventArgs e)
    {
        if (_advancing)
        {
            // Durante el destello sólo se traga lo que tendría efecto; el resto (Alt+Tab, etc.) pasa.
            if (e.Key is Key.Space or Key.Escape or Key.Enter || GradeFromKey(e.Key) is not null) e.Handled = true;
            return;
        }

        switch (e.Key)
        {
            case Key.Space:
                e.Handled = true; // SIEMPRE: Espacio nunca llega al botón enfocado
                if (_revealed) Skip();
                else Reveal();
                break;

            case Key.Escape:
                e.Handled = true;
                EndRound();
                break;

            default:
                // Las respuestas valen recién con la respuesta a la vista. Antes, 1..4 no hacen nada
                // (y se tragan igual, para que no terminen en ningún otro lado).
                if (GradeFromKey(e.Key) is { } grade)
                {
                    e.Handled = true;
                    if (_revealed) GradeCurrent(grade);
                }
                break;
        }
    }

    /// <summary>Fila superior 1..4. El numpad se acepta también, aunque esta app lo deja sin NumLock.</summary>
    private static Grade? GradeFromKey(Key key) => key switch
    {
        Key.D1 or Key.NumPad1 => Grade.DontKnow,
        Key.D2 or Key.NumPad2 => Grade.Doubts,
        Key.D3 or Key.NumPad3 => Grade.Improvable,
        Key.D4 or Key.NumPad4 => Grade.Spectacle,
        _ => null,
    };

    // ── La tarjeta ──

    /// <summary>
    /// Pinta la tarjeta actual: arriba el lado que se pregunta y, una vez revelada, el otro lado
    /// debajo de una línea. En un TÉRMINO el frente es la palabra con su tipo y el dorso la
    /// definición; la ficha (traducciones, contexto de uso, origen) aparece SIEMPRE con la respuesta
    /// y nunca con la pregunta, porque cuando se pregunta por la definición las traducciones y el
    /// ejemplo de uso delatan la palabra.
    /// </summary>
    private void RenderCard(QuizCard card)
    {
        var entry = card.Entry;
        bool term = entry.Kind == EntryKind.Term;
        bool frontIsQuestion = card.Side == ReviewSide.Front;

        CardPanel.Children.Clear();
        AddSide(entry, front: frontIsQuestion, term, question: true);
        if (!_revealed) return;

        CardPanel.Children.Add(new Border
        {
            Height = 1, Margin = new Thickness(0, 18, 0, 16),
            Background = (Brush)FindResource("SoftLineBrush"),
        });
        AddSide(entry, front: !frontIsQuestion, term, question: false);

        if (!term) return;
        if (_translations.TryGetValue(entry.Id, out var translations) && translations.Count > 0)
        {
            // Una línea por idioma: "Inglés: Technical debt".
            var lines = translations.Select(t =>
                (_languageNames.TryGetValue(t.LanguageId, out var language) ? language + ": " : "") + t.Text);
            AddField(Loc.T("Playbook.FieldTranslations"), string.Join(Environment.NewLine, lines));
        }
        AddField(Loc.T("Playbook.FieldUsage"), entry.UsageContext);
        AddField(Loc.T("Playbook.FieldOrigin"), entry.Origin);
    }

    private void AddSide(Entry entry, bool front, bool term, bool question)
    {
        string label = term
            ? Loc.T(front ? "Playbook.FieldTerm" : "Playbook.FieldDefinition")
            : Loc.T(front ? "Playbook.SideFront" : "Playbook.SideBack");
        string text = front ? entry.Front : entry.Back;

        AddLabel(label);
        CardPanel.Children.Add(new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            // Una palabra o una pregunta corta se leen grandes; un párrafo, a tamaño de lectura.
            FontSize = text.Length <= 80 ? (question ? 26 : 22) : 17,
            FontWeight = text.Length <= 80 ? FontWeights.SemiBold : FontWeights.Normal,
            LineHeight = text.Length <= 80 ? double.NaN : 25,
            Foreground = (Brush)FindResource("FgBrush"),
        });

        // El tipo de palabra acompaña a la PALABRA (frente de un término), no a su definición.
        if (term && front && entry.WordType != "")
            CardPanel.Children.Add(new TextBlock
            {
                Text = entry.WordType,
                FontSize = 14, FontStyle = FontStyles.Italic, Margin = new Thickness(0, 4, 0, 0),
                TextWrapping = TextWrapping.Wrap,
                Foreground = (Brush)FindResource("MutedBrush"),
            });
    }

    /// <summary>Un campo de la ficha con su rótulo. Vacío = no se pinta (ni el rótulo).</summary>
    private void AddField(string label, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        AddLabel(label, topMargin: 16);
        CardPanel.Children.Add(new TextBlock
        {
            Text = text, FontSize = 15, LineHeight = 22, TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)FindResource("FgBrush"),
        });
    }

    private void AddLabel(string text, double topMargin = 0) => CardPanel.Children.Add(new TextBlock
    {
        Text = text.ToUpper(CultureInfo.CurrentCulture),
        FontSize = 11, FontWeight = FontWeights.Bold, Margin = new Thickness(0, topMargin, 0, 4),
        Foreground = (Brush)FindResource("MutedBrush"),
    });

    // ──────────────────────────── 4 · Resumen ────────────────────────────

    private void ShowSummary()
    {
        int graded = _gradeCounts.Sum();
        SummaryCounts.Text = string.Format(Loc.T("Playbook.SummaryCounts"), graded, _skipped);
        SummaryGrade1.Text = _gradeCounts[1].ToString(CultureInfo.CurrentCulture);
        SummaryGrade2.Text = _gradeCounts[2].ToString(CultureInfo.CurrentCulture);
        SummaryGrade3.Text = _gradeCounts[3].ToString(CultureInfo.CurrentCulture);
        SummaryGrade4.Text = _gradeCounts[4].ToString(CultureInfo.CurrentCulture);

        // "Después" se RECALCULA desde la base, no se estima: si alguna respuesta no se guardó, el
        // número refleja lo que de verdad quedó anotado.
        var ids = _selection.Select(e => e.Id).ToList();
        var after = AverageOf(ids, Solidity.ComputeAll(_store.GetReviews(ids), DateTime.UtcNow));
        SummarySolidity.Text = string.Format(Loc.T("Playbook.SummarySolidity"),
            ShortSolidity(_solidityBefore), ShortSolidity(after));

        SummaryUnsaved.Visibility = _unsaved > 0 ? Visibility.Visible : Visibility.Collapsed;
        SummaryUnsavedText.Text = string.Format(Loc.T("Playbook.SummaryUnsaved"), _unsaved);

        ShowStage(Stage.Summary);
        PlayAgainBtn.Focus();
        Keyboard.Focus(PlayAgainBtn);
    }

    private void OnSummaryKey(KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                e.Handled = true;
                BackToSearch(reload: true);
                break;

            case Key.Enter when !FocusIsOn(SummaryBackBtn, CloseBtn):
                e.Handled = true;
                StartRound(); // la misma selección, con un orden nuevo
                break;
        }
    }
}
