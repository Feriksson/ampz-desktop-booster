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
/// El PLAYBOOKER (NumpadClear pelado): buscar una biblioteca, una naturaleza o una entrada, jugarla,
/// leerla, y mirar cómo viene todo. Una sola ventana con seis vistas que se alternan por Visibility
/// (mismo recurso que el Shortcuts Helper):
///
///   BÚSQUEDA → ARMADO (qué lado se muestra primero) → QUIZ → RESUMEN        (este archivo;
///                                             la ayuda del quiz, tecla A: PlaybookWindow.Helper.cs)
///   BÚSQUEDA → FICHA de una entrada, para leerla                             (PlaybookWindow.Ficha.cs)
///   QUIZ / FICHA: editar, eliminar y anotar un problema en la entrada        (PlaybookWindow.Actions.cs)
///   BÚSQUEDA → ESTADÍSTICAS                                                  (PlaybookWindow.Stats.cs)
///
/// La búsqueda, con sus filas y la captura rápida, vive en PlaybookWindow.Browse.cs.
///
/// Tres decisiones que explican casi todo lo de abajo:
///
///   1. Esto NO es Anki, y es una decisión explícita. No hay cola, ni fechas, ni contador de cosas
///      que "te tocan": una ronda es la selección entera mezclada con sesgo a lo más flojo, se puede
///      cortar cuando quieras (Esc) y nada queda debiéndose. Ningún texto ni número de esta ventana
///      puede sugerir una obligación — tampoco las estadísticas, que son un espejo y no una agenda.
///   2. Es teclado primero (la app entera lo es), pero NADA es sólo-teclado: cada tecla tiene su
///      botón a la vista con la tecla dibujada, y la pista del pie dice siempre qué vale ahora.
///   3. Es una ventana de trabajo LARGO: no es Topmost, se minimiza, figura en la barra de tareas y
///      se abre con <c>ShowFocused(closeOnDeactivate: false)</c> — una sesión de estudio tiene que
///      sobrevivir a un Alt+Tab o a un cambio de escritorio. Por lo mismo NADA la cierra de un solo
///      gesto: cerrar pide una segunda pulsación (PlaybookWindow.Guard.cs) y el atajo que la abre
///      sólo la trae al frente (<c>App.ShowPlaybook</c>).
///   4. El quiz se juega entero con la MANO IZQUIERDA (Espacio, 1..4, A, E, X, R, Esc; Q / W en la
///      ficha): es el gesto que se repite cientos de veces, y con la derecha en el mouse o en el
///      café. Las teclas de antes (H, Enter, flechas) siguen valiendo, sin anunciarse.
///
/// Los datos se cargan UNA vez al abrir (bibliotecas, entradas, naturalezas, historial) y todo lo
/// que muestran las listas —conteos, solidez— se arma en memoria: nada de una consulta por fila.
/// </summary>
public partial class PlaybookWindow : Window
{
    private enum Stage { Search, Setup, Quiz, Summary, Ficha, Stats }

    /// <summary>Qué entra a la ronda cuando la selección mezcla términos y cards.</summary>
    private enum KindFilter { All, Terms, Cards }

    /// <summary>
    /// Una opción de "qué lado se muestra primero". <see cref="Side"/> es el lado que se pregunta
    /// (frente, dorso o <c>lang:&lt;id&gt;</c>); null = "Mezclado", que sortea frente o dorso tarjeta por
    /// tarjeta. <see cref="Entries"/> son las que PUEDEN jugar esa opción: con un lado de idioma,
    /// sólo los términos que tienen esa traducción.
    /// </summary>
    private sealed record SideOption(string Key, string Label, string? Side, IReadOnlyList<Entry> Entries, string LanguageName = "");

    private const string MixedKey = "mixed";

    // La última elección de lado (su key), recordada mientras viva la app — no se persiste: es una
    // comodidad de sesión, no una preferencia. Estática porque la ventana se recrea en cada apertura.
    // Si la selección siguiente no ofrece ese lado (un idioma que ahí no tiene traducciones), vale
    // la primera opción.
    private static string _lastSideKey = ReviewSide.Front;

    private static readonly Random Rng = new();

    // Cuánto dura el destello del botón al responder. Corto a propósito: es la confirmación de
    // "anotado", no una animación — más que esto y el quiz se siente con freno de mano.
    private static readonly TimeSpan GradeFlash = TimeSpan.FromMilliseconds(120);

    private const double BarTrackWidth = 110; // el mismo ancho que la pista de la barra en el XAML
    private const double IndentPerLevel = 22;
    private const int MaxSideColumns = 4;     // más opciones que esto pasan a un segundo renglón

    // ── Tamaño y escala ──
    // La ventana ocupa el 90% del área de trabajo, a lo ancho y a lo alto: pedido del usuario después
    // de probarla — tener que scrollear una tarjeta para leerla es inaceptable, y una ventana chica
    // obligaba a eso con cualquier término que tuviera la ficha completa.
    private const double WorkAreaRatio = 0.90;
    // El XAML está diseñado para ESTE alto (el 90% de una pantalla de 1080). En cualquier otra
    // pantalla se escala todo el contenido parejo en vez de re-acomodarlo: así "la ficha del ejemplo
    // entra sin scroll" vale igual en 1080p, 1440p o 4K — si entra en la referencia, entra en todas —
    // y en un monitor grande la letra crece con él en vez de quedar perdida en el medio.
    private const double ReferenceHeight = 972;
    // Topes de la escala. Abajo: en una notebook de 768 de alto, achicar más que esto deja las pistas
    // por debajo de lo legible; ahí una ficha muy cargada puede necesitar el scroll de red. Arriba:
    // más que esto ya no mejora la lectura, sólo agranda.
    private const double MinScale = 0.82;
    private const double MaxScale = 1.6;
    private const string PathSeparator = " › ";

    private readonly KnowledgeStore _store;
    private Stage _stage = Stage.Search;

    // ── Foto de la base tomada al abrir (y cada vez que algo pudo cambiar) ──
    private IReadOnlyDictionary<long, Library> _libraries = new Dictionary<long, Library>();
    private Dictionary<long, Entry> _entries = new();
    private Dictionary<long, string> _natureNames = new();
    private Dictionary<long, string> _languageNames = new();
    private IReadOnlyList<Review> _reviews = Array.Empty<Review>();
    private IReadOnlyDictionary<(long EntryId, string Side), SolidityState> _states =
        new Dictionary<(long, string), SolidityState>();
    private DateTime _loadedAt = DateTime.UtcNow; // la fecha con la que se calculó _states
    private List<PickRow> _rows = new();

    // ── Lo elegido ──
    private PickRow? _picked;
    private Stage _setupOrigin = Stage.Search;      // adónde vuelve Esc desde el armado
    private List<Entry> _pickedEntries = new();     // todo lo que trae la fila elegida
    private List<Entry> _selection = new();         // …después del filtro por tipo
    private KindFilter _kindFilter = KindFilter.All;
    private List<SideOption> _sideOptions = new();
    private readonly List<RadioButton> _sideRadios = new();
    private bool _buildingSetup;                    // true mientras el armado marca radios por código

    // ── La ronda en curso ──
    private SideOption? _roundOption;
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

    public PlaybookWindow(KnowledgeStore store)
    {
        InitializeComponent();
        _store = store;

        // 90% del área de trabajo (que ya descuenta la barra), centrada, con la implementación
        // compartida de las utilitarias — que usa el monitor PRIMARIO, la convención de la app. El
        // tamaño es fijo: la ventana no crece ni se achica con el contenido.
        this.SizeToWorkArea(WorkAreaRatio, WorkAreaRatio);
        ApplyScale();

        PreviewKeyDown += OnPreviewKeyDown;
        HeaderBar.MouseLeftButtonDown += OnHeaderMouseDown;
        InitGuard();

        InitBrowse();
        InitFicha();
        InitStats();
        InitHelper();
        InitActions();

        StartBtn.Click += (_, _) => StartRound();
        SetupBackBtn.Click += (_, _) => LeaveSetup();
        InitKindRadio(KindAllRadio, KindFilter.All, "Playbook.KindAll");
        InitKindRadio(KindTermsRadio, KindFilter.Terms, "Playbook.KindTerms");
        InitKindRadio(KindCardsRadio, KindFilter.Cards, "Playbook.KindCards");

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
    /// Escala TODO el contenido según el alto de la ventana (ver <see cref="ReferenceHeight"/>). Es un
    /// LayoutTransform y no un zoom de render: el layout se calcula ya con la escala, así que el texto
    /// envuelve bien y se dibuja nítido (es vectorial), y las medidas son DIPs — WPF las lleva solo a
    /// los píxeles del monitor, sea cual sea su DPI.
    /// </summary>
    private void ApplyScale()
    {
        double scale = Math.Clamp(Height / ReferenceHeight, MinScale, MaxScale);
        RootBorder.LayoutTransform = new ScaleTransform(scale, scale);
    }

    // ──────────────────────────── Datos ────────────────────────────

    /// <summary>
    /// Lee todo lo que la ventana necesita y arma las filas. Cinco lecturas + el historial, y de
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
        // Los glosarios de TODAS las cards, de una vez: es lo que muestra la ayuda del quiz (A).
        _glossaries = _store.GetAllGlossaries();

        // UNA lectura del historial y un solo cálculo para todas las filas (y para las estadísticas,
        // que lo reusan: por eso se guardan el historial y la fecha del cálculo).
        _reviews = _store.GetAllReviews();
        _loadedAt = DateTime.UtcNow;
        _states = Solidity.ComputeAll(_reviews, _loadedAt);
        var states = _states;

        // Qué bibliotecas están plegadas: el mismo dato que usa el árbol de la config (se guarda con
        // la biblioteca). La lista lo aplica sólo con el buscador vacío — ver ApplyFilter.
        _collapsedLibraries = libraries.Where(l => l.Collapsed).Select(l => l.Id).ToHashSet();

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
            rows[slot] = LibraryRow(library, depth, ids, states, children[library.Id].Count());
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

        // VOCABULARIO: todos los términos de todas las bibliotecas, como una selección más. Va
        // primera (es la entrada al "playbook de vocabulario") y sólo si hay algún término: una fila
        // que lleva a una ronda vacía no le sirve a nadie.
        var terms = _entries.Values.Where(e => e.Kind == EntryKind.Term)
            .OrderBy(e => e.Front, StringComparer.CurrentCultureIgnoreCase).Select(e => e.Id).ToList();
        if (terms.Count > 0) rows.Insert(0, VocabularyRow(terms, states));

        // CON UN PROBLEMA ANOTADO: las entradas que tienen algo escrito para arreglar, la anotada más
        // recientemente primero. Sólo si hay alguna, justo debajo de Vocabulario. No se JUEGA: Enter
        // las abre como fichas, que es donde se lee la nota y se corrige (ver Activate).
        var flagged = _entries.Values.Where(e => e.HasIssue)
            .OrderByDescending(e => e.IssueAt).ThenBy(e => e.Id).Select(e => e.Id).ToList();
        if (flagged.Count > 0) rows.Insert(terms.Count > 0 ? 1 : 0, IssuesRow(flagged));

        _rows = rows;
    }

    private PickRow LibraryRow(Library library, int depth, List<long> entryIds,
        IReadOnlyDictionary<(long, string), SolidityState> states, int childCount)
    {
        var path = LibraryTree.PathOf(library.Id, _libraries);
        string prefix = path.Count > 1 ? string.Join(PathSeparator, path.Take(path.Count - 1)) + PathSeparator : "";
        var average = AverageOf(entryIds, states);
        string count = CountLabel(entryIds.Count);
        return new PickRow
        {
            Kind = PickKind.Library,
            LibraryId = library.Id,
            ParentLibraryId = library.ParentId,
            ChildCount = childCount,
            Depth = depth,
            PathPrefix = prefix,
            Name = library.Name,
            CountText = count,
            BaseCountText = count,
            Average = average,
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
            Kind = PickKind.Nature,
            Name = nature.Name,
            ChipText = Loc.T("Playbook.NatureChip"),
            ChipBrush = (Brush)FindResource("GoldBrush"),
            CountText = count,
            Average = average,
            SolidityText = SolidityLabel(average),
            BarFillWidth = BarFill(average),
            BarVisibility = average.Played > 0 ? Visibility.Visible : Visibility.Collapsed,
            AccessibleName = string.Format(Loc.T("Playbook.RowNatureAccessible"),
                nature.Name, count, SpokenSolidity(average)),
            Haystack = TextFold.Fold(nature.Name),
            EntryIds = entryIds,
        };
    }

    private PickRow VocabularyRow(List<long> termIds, IReadOnlyDictionary<(long, string), SolidityState> states)
    {
        var average = AverageOf(termIds, states);
        string chip = Loc.T("Playbook.VocabularyChip");
        string name = Loc.T("Playbook.VocabularyName");
        string count = termIds.Count == 1
            ? Loc.T("Playbook.TermsOne")
            : string.Format(Loc.T("Playbook.TermsMany"), termIds.Count);
        return new PickRow
        {
            Kind = PickKind.Vocabulary,
            Name = name,
            ChipText = chip,
            ChipBrush = (Brush)FindResource("AccentBrush"),
            CountText = count,
            Average = average,
            SolidityText = SolidityLabel(average),
            BarFillWidth = BarFill(average),
            BarVisibility = average.Played > 0 ? Visibility.Visible : Visibility.Collapsed,
            AccessibleName = string.Format(Loc.T("Playbook.RowVocabularyAccessible"), chip, name, count, SpokenSolidity(average)),
            Haystack = TextFold.Fold(chip + " " + name),
            EntryIds = termIds,
        };
    }

    private static PickRow IssuesRow(List<long> entryIds)
    {
        string name = Loc.T("Playbook.IssueMark");
        string count = CountLabel(entryIds.Count);
        return new PickRow
        {
            Kind = PickKind.Issues,
            Name = name,
            CountText = count,
            BarVisibility = Visibility.Collapsed,
            AccessibleName = string.Format(Loc.T("Playbook.RowIssuesAccessible"), name, count),
            Haystack = TextFold.Fold(name),
            EntryIds = entryIds,
        };
    }

    // ── Solidez de un CONJUNTO (una fila, o la selección antes/después de la ronda) ──

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
            if (Solidity.EntryValue(states, id) is not { } value) continue;
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

    /// <summary>
    /// La cultura de las FECHAS que muestra la ventana (historial, meses del gráfico): la del idioma
    /// de la app, no la de Windows — en una app en español, "12 Mar" y "Aug" se leen como un descuido.
    /// </summary>
    private static CultureInfo DateCulture => Loc.Current == AppLanguage.English
        ? CultureInfo.GetCultureInfo("en-US")
        : CultureInfo.GetCultureInfo("es-AR");

    private string PathText(long libraryId) =>
        string.Join(PathSeparator, LibraryTree.PathOf(libraryId, _libraries));

    /// <summary>La naturaleza EFECTIVA de una entrada (propia o heredada); "" si no tiene ninguna.</summary>
    private string NatureName(Entry entry) =>
        LibraryTree.EffectiveNatureId(entry, _libraries) is { } natureId
        && _natureNames.TryGetValue(natureId, out var name) ? name : "";

    // ──────────────────────────── Pasos ────────────────────────────

    private void ShowStage(Stage stage)
    {
        CommitIssueIfOpen(); // lo que se estaba anotando no se pierde por cambiar de paso
        _stage = stage;
        SearchView.Visibility = stage == Stage.Search ? Visibility.Visible : Visibility.Collapsed;
        SetupView.Visibility = stage == Stage.Setup ? Visibility.Visible : Visibility.Collapsed;
        QuizView.Visibility = stage == Stage.Quiz ? Visibility.Visible : Visibility.Collapsed;
        SummaryView.Visibility = stage == Stage.Summary ? Visibility.Visible : Visibility.Collapsed;
        FichaView.Visibility = stage == Stage.Ficha ? Visibility.Visible : Visibility.Collapsed;
        StatsView.Visibility = stage == Stage.Stats ? Visibility.Visible : Visibility.Collapsed;

        // El encabezado dice DÓNDE estás: en la búsqueda, la invitación; jugando, QUÉ se juega.
        TitleText.Text = stage switch
        {
            Stage.Ficha => Loc.T("Playbook.FichaTitle"),
            Stage.Stats => Loc.T("Playbook.StatsTitle"),
            Stage.Search => Loc.T("Playbook.Header"),
            _ => _picked?.Title ?? Loc.T("Playbook.Header"),
        };
        ProgressText.Visibility = stage == Stage.Quiz ? Visibility.Visible : Visibility.Collapsed;
        // El título de la VENTANA es lo que se lee en la barra de tareas y en Alt+Tab: dice el
        // paso, para reconocer una ronda a medias entre las demás ventanas.
        // (La ficha y las estadísticas ya traen "Playbook · …" en su encabezado: van tal cual.)
        Title = stage switch
        {
            Stage.Search => Loc.T("Playbook.WindowTitle"),
            Stage.Ficha or Stage.Stats => TitleText.Text,
            _ => string.Format(Loc.T("Playbook.WindowTitleStage"), TitleText.Text),
        };
        ClearGuard(); // cambiar de paso es "otra cosa": una confirmación a medias no lo sobrevive
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
            Stage.Setup => SetupHint(),
            Stage.Quiz => QuizHint(),
            Stage.Ficha => Loc.T("Playbook.HintFicha"),
            Stage.Stats => Loc.T("Playbook.HintStats"),
            _ => Loc.T("Playbook.HintSummary"),
        };
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Una sola puerta de teclado para toda la ventana (tunneling: corre ANTES que el control con
        // foco). Así Espacio jamás "clickea" el botón que quedó enfocado tras un click de mouse —
        // en el quiz eso anotaría una respuesta que nadie dio.

        // Los modificadores de ESTA tecla (los del dispositivo que la mandó): los consultan
        // PlainKey / LetterAllowed mientras dura su despacho.
        _keyModifiers = ModifiersOf(e.KeyboardDevice);

        // ⚠ AUTO-REPETICIÓN: una tecla MANTENIDA no es "apretarla otra vez". Sin este corte, dejar
        // el dedo en la X armaba y confirmaba el borrado sola, y seguía borrando las tarjetas que
        // venían; Esc mantenido cortaba la ronda, salía del resumen y cerraba la ventana en menos de
        // un segundo; Espacio / Enter arrancaban una ronda y la pasaban entera. Va ANTES de todo
        // (también de las guardas): una repetición no confirma, no desarma y no llega al botón con
        // foco (por eso se marca Handled: Enter repetido sobre «Eliminar» serían clicks seguidos).
        // La repetición sigue valiendo donde es inofensiva y se espera: las flechas y escribir.
        if (e.IsRepeat && IsRepeatSwallowed(e.Key, TextKeysAreTyped, EnterIsTyped))
        {
            e.Handled = true;
            return;
        }

        // Con el campo de "anotar un problema" abierto, el teclado es SUYO: ahí las letras son
        // texto y no puede dispararse ningún atajo del paso de atrás.
        if (_issueOpen)
        {
            OnIssueKey(e);
            return;
        }

        BeginGuardedKey();
        switch (_stage)
        {
            case Stage.Search: OnSearchKey(e); break;
            case Stage.Setup: OnSetupKey(e); break;
            case Stage.Quiz: OnQuizKey(e); break;
            case Stage.Summary: OnSummaryKey(e); break;
            case Stage.Ficha: OnFichaKey(e); break;
            case Stage.Stats: OnStatsKey(e); break;
        }
        EndGuardedKey(e);
    }

    /// <summary>¿Enter / Espacio le pertenecen al botón que tiene el foco? (Tab hasta "Volver" + Enter = Volver.)</summary>
    private static bool FocusIsOn(params Button?[] buttons) =>
        buttons.Any(b => b is not null && ReferenceEquals(Keyboard.FocusedElement, b));

    private ModifierKeys _keyModifiers; // los de la tecla en curso (ver OnPreviewKeyDown)

    /// <summary>
    /// Los modificadores apretados, CON la tecla Windows. <c>Keyboard.Modifiers</c> de WPF sólo
    /// informa Ctrl, Alt y Shift — nunca <see cref="ModifierKeys.Windows"/> —, así que preguntarle
    /// por Win era una condición que jamás se cumplía: Win+2 calificaba igual que un 2. Se lee del
    /// dispositivo que mandó la tecla.
    /// </summary>
    private static ModifierKeys ModifiersOf(KeyboardDevice keyboard)
    {
        var modifiers = keyboard.Modifiers;
        if (keyboard.IsKeyDown(Key.LWin) || keyboard.IsKeyDown(Key.RWin)) modifiers |= ModifierKeys.Windows;
        return modifiers;
    }

    /// <summary>
    /// ¿La tecla en curso vino SIN Ctrl / Alt / Win? Con alguno apretado es otra tecla (Ctrl+Espacio,
    /// Win+2…) y no puede responder, saltar ni empezar nada. Shift no cuenta: no cambia qué tecla es.
    /// </summary>
    private bool PlainKey => IsPlain(_keyModifiers);

    internal static bool IsPlain(ModifierKeys modifiers) =>
        (modifiers & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Windows)) == 0;

    /// <summary>
    /// ¿Una LETRA suelta puede ser un atajo ahora? No con Ctrl / Alt / Win apretados (ahí es otra
    /// tecla: Ctrl+A, Alt+E…) ni con el foco en un campo de texto (ahí es una letra que se escribe).
    /// La consultan todos los atajos de letra de la ventana: A, E, X, R, F, Q, W.
    /// </summary>
    private bool LetterAllowed =>
        PlainKey && Keyboard.FocusedElement is not System.Windows.Controls.Primitives.TextBoxBase;

    // ── Auto-repetición (ver OnPreviewKeyDown) ──

    /// <summary>¿Letras, números y Espacio son TEXTO ahora? En la búsqueda siempre (tipear en la
    /// lista también escribe en el buscador), con el campo de problema abierto, o en un campo de texto.</summary>
    private bool TextKeysAreTyped =>
        _issueOpen || _stage == Stage.Search
        || Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase;

    /// <summary>¿Enter es un salto de línea ahora? Sólo en un campo de varios renglones (la nota de
    /// problema) y sin Ctrl: Ctrl+Enter ahí es «Guardar».</summary>
    private bool EnterIsTyped =>
        (_keyModifiers & ModifierKeys.Control) == 0
        && Keyboard.FocusedElement is TextBox { AcceptsReturn: true };

    /// <summary>
    /// ¿La REPETICIÓN de esta tecla se ignora? Sí para todo lo que en esta ventana es una ACCIÓN:
    /// Esc, Enter, F2, Espacio, los números (respuestas, lados) y las letras (A/H, E, X, R, Q, W, F)
    /// — salvo cuando son texto que se está escribiendo. No para lo demás: flechas, Retroceso,
    /// Supr, Tab… se repiten como en cualquier lado. Decisión pura, aparte, para poder probarla.
    /// </summary>
    internal static bool IsRepeatSwallowed(Key key, bool textKeysAreTyped, bool enterIsTyped)
    {
        if (key is Key.Escape or Key.F2) return true;
        if (key == Key.Enter) return !enterIsTyped;
        bool textKey = key == Key.Space || key is >= Key.A and <= Key.Z
            || key is >= Key.D0 and <= Key.D9 || key is >= Key.NumPad0 and <= Key.NumPad9;
        return textKey && !textKeysAreTyped;
    }

    private void OnHeaderMouseDown(object sender, MouseButtonEventArgs e)
    {
        // DragMove tira si el botón ya no está apretado cuando corre (click muy corto): no es un
        // error que importe, simplemente no hubo arrastre.
        try { DragMove(); } catch (InvalidOperationException) { }
    }

    // ──────────────────────────── 2 · Armado ────────────────────────────

    private void EnterSetup(PickRow row)
    {
        _setupOrigin = _stage == Stage.Stats ? Stage.Stats : Stage.Search;
        _picked = row;
        _pickedEntries = row.EntryIds.Where(_entries.ContainsKey).Select(id => _entries[id]).ToList();

        // Las traducciones de TODA la selección, en una consulta: de ahí sale qué idiomas se pueden
        // ofrecer como lado, y después son las que muestra la ficha de cada término en el quiz.
        _translations = _store.GetTranslations(_pickedEntries.Select(e => e.Id));

        SetupKind.Text = row.Kind switch
        {
            PickKind.Nature => Loc.T("Playbook.NatureChip"),
            PickKind.Vocabulary => Loc.T("Playbook.VocabularyChip"),
            _ => Loc.T("Playbook.LibraryKind"),
        };
        SetupName.Text = row.FullName;

        // El filtro por tipo sólo tiene sentido si hay de los dos; y arranca siempre en "Todo".
        bool mixed = _pickedEntries.Any(e => e.Kind == EntryKind.Term) && _pickedEntries.Any(e => e.Kind == EntryKind.Card);
        KindPanel.Visibility = mixed ? Visibility.Visible : Visibility.Collapsed;
        _kindFilter = KindFilter.All;
        _buildingSetup = true;
        KindAllRadio.IsChecked = true;
        _buildingSetup = false;

        ShowStage(Stage.Setup);
        RebuildSideOptions(_lastSideKey, focus: true);
    }

    private void InitKindRadio(RadioButton radio, KindFilter filter, string key)
    {
        radio.Content = Loc.T(key);
        AutomationProperties.SetName(radio, Loc.T(key));
        radio.Checked += (_, _) =>
        {
            if (_buildingSetup || _kindFilter == filter) return;
            _kindFilter = filter;
            // Se conserva el lado elegido si la selección filtrada todavía lo ofrece. El foco NO se
            // mueve: quien está recorriendo este grupo con las flechas sigue en este grupo.
            RebuildSideOptions(CurrentOption()?.Key ?? _lastSideKey, focus: false);
        };
    }

    private RadioButton KindRadio(KindFilter filter) => filter switch
    {
        KindFilter.Terms => KindTermsRadio,
        KindFilter.Cards => KindCardsRadio,
        _ => KindAllRadio,
    };

    private void CycleKind(int delta, bool focus)
    {
        if (KindPanel.Visibility != Visibility.Visible) return;
        var radio = KindRadio((KindFilter)(((int)_kindFilter + delta + 3) % 3));
        radio.IsChecked = true;
        if (focus) { radio.Focus(); Keyboard.Focus(radio); }
    }

    /// <summary>
    /// Arma las opciones de lado para la selección ACTUAL (lo elegido, pasado por el filtro por tipo):
    /// Frente, Dorso y Mezclado siempre; y, si hay términos con traducciones, una opción por idioma
    /// ("Inglés → término": se muestra la traducción y hay que dar con la palabra). Un idioma sin
    /// ninguna traducción en la selección no se ofrece — sería una opción que lleva a una ronda vacía.
    /// </summary>
    private void RebuildSideOptions(string preferredKey, bool focus)
    {
        _selection = _kindFilter switch
        {
            KindFilter.Terms => _pickedEntries.Where(e => e.Kind == EntryKind.Term).ToList(),
            KindFilter.Cards => _pickedEntries.Where(e => e.Kind == EntryKind.Card).ToList(),
            _ => _pickedEntries,
        };
        SetupCount.Text = _selection.Count == _pickedEntries.Count
            ? CountLabel(_selection.Count)
            : string.Format(Loc.T("Playbook.SetupCountFiltered"), CountLabel(_selection.Count), _pickedEntries.Count);

        var options = new List<SideOption>
        {
            new(ReviewSide.Front, Loc.T("Playbook.SideFront"), ReviewSide.Front, _selection),
            new(ReviewSide.Back, Loc.T("Playbook.SideBack"), ReviewSide.Back, _selection),
            new(MixedKey, Loc.T("Playbook.SideMixed"), null, _selection),
        };
        foreach (var (languageId, languageName) in _languageNames.OrderBy(l => l.Value, StringComparer.CurrentCultureIgnoreCase))
        {
            var with = _selection.Where(e => e.Kind == EntryKind.Term && TranslationOf(e.Id, languageId) is not null).ToList();
            if (with.Count == 0) continue;
            options.Add(new SideOption(ReviewSide.ForLanguage(languageId),
                string.Format(Loc.T("Playbook.SideLanguage"), languageName), ReviewSide.ForLanguage(languageId), with, languageName));
        }
        _sideOptions = options;

        _buildingSetup = true;
        SideGrid.Children.Clear();
        _sideRadios.Clear();
        // Hasta cuatro en un renglón; con más, se reparten parejo en dos (o tres) renglones.
        SideGrid.Columns = options.Count <= MaxSideColumns
            ? options.Count
            : Math.Min(MaxSideColumns, (options.Count + 1) / 2);
        for (int i = 0; i < options.Count; i++)
        {
            var radio = BuildSideRadio(i, options[i]);
            _sideRadios.Add(radio);
            SideGrid.Children.Add(radio);
        }
        _buildingSetup = false;

        int preferred = options.FindIndex(o => o.Key == preferredKey);
        SelectSide(preferred < 0 ? 0 : preferred, focus);
    }

    private RadioButton BuildSideRadio(int index, SideOption option)
    {
        // Tecla dibujada + texto, igual que el resto de la ventana. Las teclas son 1..9: una opción
        // que quedara más allá de la novena no tendría número (se llega con las flechas o el mouse).
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        if (index < 9)
        {
            var keycap = new Border { Style = (Style)FindResource("Keycap"), Margin = new Thickness(0, 0, 8, 0) };
            keycap.Child = new TextBlock
            {
                Style = (Style)FindResource("KeycapText"),
                Text = (index + 1).ToString(CultureInfo.InvariantCulture),
            };
            content.Children.Add(keycap);
        }
        content.Children.Add(new TextBlock { Text = option.Label, VerticalAlignment = VerticalAlignment.Center });

        var radio = new RadioButton
        {
            GroupName = "PlaybookSide",
            Style = (Style)FindResource("Segment"),
            Content = content,
        };
        AutomationProperties.SetName(radio, option.Label);
        radio.Checked += (_, _) => { if (!_buildingSetup) OnSideChosen(); };
        return radio;
    }

    private string? TranslationOf(long entryId, long languageId)
    {
        if (!_translations.TryGetValue(entryId, out var list)) return null;
        string? text = list.FirstOrDefault(t => t.LanguageId == languageId)?.Text;
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private int CurrentSideIndex() => _sideRadios.FindIndex(r => r.IsChecked == true);

    private SideOption? CurrentOption() =>
        CurrentSideIndex() is var index && index >= 0 && index < _sideOptions.Count ? _sideOptions[index] : null;

    /// <summary>Marca el lado y, si se pide, le da el foco: en este grupo "elegido" y "enfocado" van juntos.</summary>
    private void SelectSide(int index, bool focus)
    {
        if (index < 0 || index >= _sideRadios.Count) return;
        var radio = _sideRadios[index];
        _buildingSetup = true;
        radio.IsChecked = true;
        _buildingSetup = false;
        OnSideChosen();
        if (!focus) return;
        UpdateLayout(); // los botones se acaban de crear: sin layout, Focus() no prende
        radio.Focus();
        Keyboard.Focus(radio);
    }

    /// <summary>
    /// Deja a la vista lo que cambia con el lado elegido: si se puede empezar y, con un lado de
    /// idioma, cuántas juegan de verdad (las que no tienen esa traducción quedan fuera de ESA ronda).
    /// </summary>
    private void OnSideChosen()
    {
        var option = CurrentOption();
        int playable = option?.Entries.Count ?? 0;

        // Sin entradas no hay ronda posible. El botón se apaga Y el motivo queda a la vista.
        StartBtn.IsEnabled = playable > 0;
        StartReason.Visibility = playable > 0 ? Visibility.Collapsed : Visibility.Visible;

        bool partial = option is not null && playable > 0 && playable < _selection.Count;
        SetupRoundNote.Visibility = partial ? Visibility.Visible : Visibility.Collapsed;
        if (partial)
            SetupRoundNote.Text = string.Format(Loc.T("Playbook.SetupLanguageNote"),
                playable, _selection.Count, option!.LanguageName);
        UpdateHint();
    }

    private string SetupHint()
    {
        if (_sideOptions.Count == 0 || _selection.Count == 0) return Loc.T("Playbook.HintSetupEmpty");
        string keys = Math.Min(_sideOptions.Count, 9).ToString(CultureInfo.InvariantCulture);
        return string.Format(
            Loc.T(KindPanel.Visibility == Visibility.Visible ? "Playbook.HintSetupKinds" : "Playbook.HintSetup"), keys);
    }

    /// <summary>Esc / Volver desde el armado: adonde se había venido (la búsqueda o las estadísticas).</summary>
    private void LeaveSetup()
    {
        if (_setupOrigin == Stage.Stats) ShowStats(reload: false);
        else BackToSearch(reload: false);
    }

    private void OnSetupKey(KeyEventArgs e)
    {
        bool inKinds = KindPanel.IsKeyboardFocusWithin;
        switch (e.Key)
        {
            case Key.Escape:
                e.Handled = true;
                LeaveSetup();
                break;

            // Espacio EMPIEZA (mano izquierda); Enter sigue valiendo. Sobre un botón enfocado las dos
            // son de ese botón: Tab hasta «Volver» + Espacio tiene que ser Volver, no una ronda.
            case Key.Space or Key.Enter when PlainKey && !FocusIsOn(SetupBackBtn, CloseBtn, MinimizeBtn):
                e.Handled = true;
                StartRound(); // con la selección vacía no arranca: el motivo ya está en pantalla
                break;

            // Flechas: se mueven por el grupo que tiene el foco y ELIGEN (por defecto WPF sólo mueve
            // el foco, y habría que confirmar con Espacio — un paso de más para tan pocas opciones).
            case Key.Left or Key.Right when inKinds:
                e.Handled = true;
                CycleKind(e.Key == Key.Left ? -1 : 1, focus: true);
                break;
            case Key.Left or Key.Right when _sideRadios.Count > 0:
                e.Handled = true;
                int step = e.Key == Key.Left ? -1 : 1;
                SelectSide((Math.Max(CurrentSideIndex(), 0) + step + _sideRadios.Count) % _sideRadios.Count, focus: true);
                break;

            case Key.F when LetterAllowed && KindPanel.Visibility == Visibility.Visible:
                e.Handled = true;
                CycleKind(1, focus: false);
                break;

            default:
                // 1..n: una tecla por opción de lado, sean las que sean.
                if (PlainKey && DigitOf(e.Key) is { } digit && digit <= _sideRadios.Count)
                {
                    e.Handled = true;
                    SelectSide(digit - 1, focus: true);
                }
                break;
        }
    }

    /// <summary>Fila superior o numpad, 1..9 (el numpad se acepta aunque esta app lo deja sin NumLock).</summary>
    private static int? DigitOf(Key key) => key switch
    {
        >= Key.D1 and <= Key.D9 => key - Key.D0,
        >= Key.NumPad1 and <= Key.NumPad9 => key - Key.NumPad0,
        _ => null,
    };

    // ──────────────────────────── 3 · Quiz ────────────────────────────

    /// <summary>
    /// Arma una ronda con TODO lo que puede jugar el lado elegido, en orden sorteado con sesgo a lo
    /// más flojo. Sirve igual para la primera ronda y para "jugar de nuevo": cada vez relee los
    /// repasos, así el orden nuevo ya pesa con lo que se acaba de responder.
    /// </summary>
    private void StartRound()
    {
        // En el resumen los botones de lado no están a la vista: ahí vale la opción de la ronda anterior.
        var option = _stage == Stage.Setup ? CurrentOption() : _roundOption;
        if (option is null || option.Entries.Count == 0 || _picked is null) return;
        _roundOption = option;
        _lastSideKey = option.Key;

        var ids = option.Entries.Select(e => e.Id).ToList();
        _roundStates = Solidity.ComputeAll(_store.GetReviews(ids), DateTime.UtcNow);
        _solidityBefore = AverageOf(ids, _roundStates);

        // El lado de cada tarjeta se decide ANTES de ordenar, porque el peso depende de la solidez
        // de ESE lado. WeightedOrder pesa un único lado para todo el mazo; con "Mezclado" cada
        // tarjeta tiene el suyo, así que se le pasa un mapa donde el lado que le tocó a cada una
        // figura bajo una misma key. Las que no entran al mapa cuentan como no jugadas (salen antes).
        var sideOf = new Dictionary<long, string>();
        var weights = new Dictionary<(long, string), SolidityState>();
        foreach (long id in ids)
        {
            string side = option.Side ?? (Rng.Next(2) == 0 ? ReviewSide.Front : ReviewSide.Back);
            sideOf[id] = side;
            var state = Solidity.StateOf(_roundStates, id, side);
            if (state.Played) weights[(id, ReviewSide.Front)] = state;
        }

        var byId = option.Entries.ToDictionary(e => e.Id);
        _round = Solidity.WeightedOrder(ids, ReviewSide.Front, weights, Rng)
            .Select(id => new QuizCard(byId[id], sideOf[id]))
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
        CommitIssueIfOpen(); // se pasó de tarjeta con el campo abierto (un click en una respuesta)
        // Otra tarjeta es "otra cosa": un «Eliminar» armado sobre la anterior NO puede confirmarse
        // sobre ésta (pasaba: click en «Eliminar», Espacio para saltar, X → se iba la siguiente).
        ClearGuard();
        var card = _round[_index];
        _revealed = false;

        RenderCardHeader(card);
        ShowQuestionWithHelpClosed(card);
        CardScroll.ScrollToTop();

        RevealBtn.Visibility = Visibility.Visible;
        GradePanel.Visibility = Visibility.Collapsed;
        CardSolidityText.Text = "";
        UpdateHelpControls();
        UpdateHint();

        // El foco vive en la tarjeta: las flechas scrollean el texto largo y ningún botón queda
        // "armado" bajo el Enter.
        CardScroll.Focus();
        Keyboard.Focus(CardScroll);
    }

    /// <summary>
    /// Lo que rodea a la tarjeta y depende de la ENTRADA: avance, qué lado se muestra, dónde vive,
    /// su naturaleza y la marca de problema anotado. Aparte de <see cref="ShowCard"/> porque también
    /// se repinta al volver de editarla, sin tocar si estaba revelada.
    /// </summary>
    private void RenderCardHeader(QuizCard card)
    {
        ProgressText.Text = string.Format(Loc.T("Playbook.Progress"), _index + 1, _round.Count);
        ShownSideText.Text = card.Side switch
        {
            ReviewSide.Front => Loc.T("Playbook.ShownFront"),
            ReviewSide.Back => Loc.T("Playbook.ShownBack"),
            _ => string.Format(Loc.T("Playbook.ShownLanguage"), LanguageOf(card.Side)),
        };
        CardPathText.Text = PathText(card.Entry.LibraryId);

        // La naturaleza EFECTIVA (propia o heredada). Sin ninguna en toda la cadena, no hay chip.
        string nature = NatureName(card.Entry);
        CardNatureText.Text = nature;
        CardNatureChip.Visibility = nature == "" ? Visibility.Collapsed : Visibility.Visible;

        ShowIssueMark(CardIssueChip, card.Entry);
    }

    /// <summary>El nombre del idioma de un lado <c>lang:&lt;id&gt;</c> ("" si ya no existe).</summary>
    private string LanguageOf(string side) =>
        ReviewSide.TryGetLanguage(side, out long id) && _languageNames.TryGetValue(id, out var name) ? name : "";

    private void Reveal()
    {
        if (_revealed || _advancing) return;
        _revealed = true;

        var card = _round[_index];
        // La respuesta re-acomoda la tarjeta entera (como siempre): vuelve a centrarse, con la ayuda
        // tal como estaba — si se la había abierto, sigue abierta debajo de la pregunta; si no, se le
        // prevé el lugar (con la respuesta a la vista puede abrirse en cualquier lado).
        CenterCard();
        RenderCard(card);
        CloseHelpIfAnswerDoesNotFit(card);
        ReserveHelpRoom(card);

        // La solidez que tenía ESTE lado al empezar la ronda (cada tarjeta sale una sola vez por
        // ronda, así que ese valor sigue siendo el vigente). Se muestra recién ahora: antes de
        // responder sería una pista de cuánto "deberías" saberla.
        var state = Solidity.StateOf(_roundStates, card.Entry.Id, card.Side);
        CardSolidityText.Text = state.Played
            ? string.Format(Loc.T("Playbook.SoliditySide"), Percent(state.Value))
            : Loc.T("Playbook.SolidityNotPlayed");

        RevealBtn.Visibility = Visibility.Collapsed;
        GradePanel.Visibility = Visibility.Visible;
        UpdateHelpControls(); // con la respuesta a la vista la ayuda ya no delata nada: se habilita
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
        // helped: sólo si la ayuda se abrió ANTES de revelar. No cambia la nota ni la solidez.
        if (!_store.AddReview(card.Entry.Id, card.Side, grade, helped: _helpedBeforeReveal))
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
            if (e.Key is Key.Space or Key.Escape or Key.Enter or Key.A or Key.H or Key.E or Key.X or Key.R
                || GradeFromKey(e.Key) is not null) e.Handled = true;
            return;
        }

        switch (e.Key)
        {
            case Key.Space:
                e.Handled = true; // SIEMPRE: Espacio nunca llega al botón enfocado
                // Con Ctrl / Alt / Win es otra tecla (Ctrl+Espacio): se traga igual —un botón con
                // foco la tomaría como su click— pero no revela ni salta.
                if (!PlainKey) break;
                if (_revealed) Skip();
                else Reveal();
                break;

            case Key.Escape:
                e.Handled = true;
                // Con la ayuda abierta, el primer Esc la cierra; recién el siguiente corta la ronda —
                // y si la ronda ya tiene respuestas anotadas, cortarla pide una segunda pulsación:
                // Esc está al lado del 1 y un dedo corrido no puede tirar veinte minutos de repaso.
                // Sin nada respondido no hay nada que cuidar y sale directo, como siempre.
                if (CloseHelpIfOpen()) break;
                if (_gradeCounts.Sum() == 0 || ConfirmTwice(GuardKind.EndRound, Loc.T("Playbook.ConfirmEndRound")))
                    EndRound();
                break;

            // A: la ayuda (H, la tecla original, sigue valiendo: quedaba del otro lado del teclado).
            // Se traga siempre (acá no hay dónde escribir una letra); si no hay ayuda que abrir no
            // pasa nada, y el motivo está escrito junto al botón.
            case Key.A or Key.H when LetterAllowed:
                e.Handled = true;
                ToggleHelp();
                break;

            // E / X / R: corregir, eliminar o anotarle un problema a ESTA entrada sin cortar la ronda
            // (PlaybookWindow.Actions.cs). Valen antes y después de revelar.
            case Key.E when LetterAllowed:
                e.Handled = true;
                EditQuizEntry();
                break;
            case Key.X when LetterAllowed:
                e.Handled = true;
                DeleteQuizEntry();
                break;
            case Key.R when LetterAllowed:
                e.Handled = true;
                OpenIssue();
                break;

            default:
                // Las respuestas valen recién con la respuesta a la vista. Antes, 1..4 no hacen nada
                // (y se tragan igual, para que no terminen en ningún otro lado).
                if (GradeFromKey(e.Key) is { } grade)
                {
                    e.Handled = true;
                    // Win+2 o Ctrl+3 no son una respuesta: misma regla que las letras.
                    if (_revealed && PlainKey) GradeCurrent(grade);
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
    ///
    /// Con un lado de IDIOMA la pregunta es la traducción sola ("Technical debt") y la respuesta es
    /// la ficha entera del término: la palabra, su definición y el resto.
    ///
    /// Con la AYUDA abierta, su bloque va pegado debajo de la pregunta (ver PlaybookWindow.Helper.cs).
    /// </summary>
    private void RenderCard(QuizCard card)
    {
        var entry = card.Entry;
        bool term = entry.Kind == EntryKind.Term;
        bool languageQuestion = ReviewSide.TryGetLanguage(card.Side, out long languageId);
        bool frontIsQuestion = card.Side == ReviewSide.Front;

        CardPanel.Children.Clear();
        if (languageQuestion)
            AddBigText(CardPanel, LanguageOf(card.Side), TranslationOf(entry.Id, languageId) ?? "", question: true);
        else
            AddSide(CardPanel, entry, front: frontIsQuestion, term, question: true);
        if (_helperOpen && HasHelp(entry)) CardPanel.Children.Add(BuildHelpBlock(entry));
        if (!_revealed) return;

        CardPanel.Children.Add(new Border
        {
            Height = 1, Margin = new Thickness(0, 20, 0, 18),
            Background = (Brush)FindResource("SoftLineBrush"),
        });
        if (languageQuestion)
        {
            AddSide(CardPanel, entry, front: true, term, question: false);
            AddSide(CardPanel, entry, front: false, term, question: false, topMargin: 18);
        }
        else
        {
            AddSide(CardPanel, entry, front: !frontIsQuestion, term, question: false);
        }

        // La traducción que se preguntó ya está arriba, en grande: repetirla en la lista de abajo
        // sólo gasta un renglón de una tarjeta que, con este lado, es la más alta de todas.
        if (term)
            AddTermSheet(CardPanel, entry, _translations.TryGetValue(entry.Id, out var list) ? list : null,
                skipLanguageId: languageQuestion ? languageId : null,
                // El origen es lo que muestra la ayuda de un término: abierta, no se repite acá abajo.
                skipOrigin: _helperOpen);
    }

    /// <summary>
    /// Lo que un término tiene además de palabra y definición: traducciones, contexto de uso y
    /// origen. Lo comparten la respuesta del quiz y la ficha de lectura.
    /// </summary>
    private void AddTermSheet(Panel panel, Entry entry, IReadOnlyList<Translation>? translations,
        long? skipLanguageId = null, bool skipOrigin = false)
    {
        if (translations is { Count: > 0 })
        {
            // Una línea por idioma: "Inglés: Technical debt". (AddField no pinta nada si queda vacío.)
            var lines = translations.Where(t => t.LanguageId != skipLanguageId).Select(t =>
                (_languageNames.TryGetValue(t.LanguageId, out var language) ? language + ": " : "") + t.Text);
            AddField(panel, Loc.T("Playbook.FieldTranslations"), string.Join(Environment.NewLine, lines));
        }
        AddField(panel, Loc.T("Playbook.FieldUsage"), entry.UsageContext);
        if (!skipOrigin) AddField(panel, Loc.T("Playbook.FieldOrigin"), entry.Origin);
    }

    private void AddSide(Panel panel, Entry entry, bool front, bool term, bool question, double topMargin = 0)
    {
        string label = term
            ? Loc.T(front ? "Playbook.FieldTerm" : "Playbook.FieldDefinition")
            : Loc.T(front ? "Playbook.SideFront" : "Playbook.SideBack");
        AddBigText(panel, label, front ? entry.Front : entry.Back, question, topMargin);

        // El tipo de palabra acompaña a la PALABRA (frente de un término), no a su definición.
        if (term && front && entry.WordType != "")
            panel.Children.Add(new TextBlock
            {
                Text = entry.WordType,
                FontSize = 19, FontStyle = FontStyles.Italic, Margin = new Thickness(0, 4, 0, 0),
                TextWrapping = TextWrapping.Wrap,
                Foreground = (Brush)FindResource("MutedBrush"),
            });
    }

    private void AddBigText(Panel panel, string label, string text, bool question, double topMargin = 0)
    {
        // Los tamaños están pensados para leer a la distancia de un monitor en una ventana que
        // ocupa casi toda la pantalla, y con el presupuesto de alto de la tarjeta a la vista: la
        // ficha más cargada del ejemplo (palabra + tipo + definición de tres renglones + dos
        // traducciones + uso + origen) tiene que entrar entera. Si se agrandan, revisar eso.
        bool brief = text.Length <= 80;
        AddLabel(panel, label, topMargin);
        panel.Children.Add(new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            // Una palabra o una pregunta corta se leen grandes; un párrafo, a tamaño de lectura.
            FontSize = brief ? (question ? 44 : 34) : 24,
            FontWeight = brief ? FontWeights.SemiBold : FontWeights.Normal,
            LineHeight = brief ? double.NaN : 35,
            Foreground = (Brush)FindResource("FgBrush"),
        });
    }

    /// <summary>Un campo de la ficha con su rótulo. Vacío = no se pinta (ni el rótulo).</summary>
    private void AddField(Panel panel, string label, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        AddLabel(panel, label, topMargin: 18);
        panel.Children.Add(new TextBlock
        {
            Text = text, FontSize = 19, LineHeight = 28, TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)FindResource("FgBrush"),
        });
    }

    private void AddLabel(Panel panel, string text, double topMargin = 0) => panel.Children.Add(new TextBlock
    {
        Text = text.ToUpper(CultureInfo.CurrentCulture),
        FontSize = 13, FontWeight = FontWeights.Bold, Margin = new Thickness(0, topMargin, 0, 5),
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
        // número refleja lo que de verdad quedó anotado. Es sobre lo que JUGÓ la ronda (con un lado
        // de idioma, los términos que tenían esa traducción), igual que el "antes".
        var ids = (_roundOption?.Entries ?? _selection).Select(e => e.Id).ToList();
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

            case Key.Space or Key.Enter when PlainKey && !FocusIsOn(SummaryBackBtn, CloseBtn, MinimizeBtn):
                e.Handled = true;
                StartRound(); // lo mismo, con un orden nuevo
                break;
        }
    }
}
