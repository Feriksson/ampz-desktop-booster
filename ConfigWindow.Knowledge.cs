using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AmpzDesktopBooster.Knowledge;
using AmpzDesktopBooster.Services.Localization;

namespace AmpzDesktopBooster;

// ── Pestaña CONOCIMIENTO ─────────────────────────────────────────────────────────────────────────
//
// Vive en su propio archivo (misma clase, partial) porque ConfigWindow.xaml.cs ya pasa las 2.700
// líneas y esta pestaña no comparte NADA con las demás: ni el catálogo de Espacios ni sus stores. Lo
// único que toca del archivo principal es el parámetro del constructor y la llamada a InitKnowledgeTab.
//
// Cómo está armada:
//   • Izquierda, el árbol de bibliotecas como lista plana con sangría (ver el comentario de la pestaña
//     Espacios: un TreeView trae su template claro). Es la ÚNICA fuente de la selección.
//   • Derecha, las entradas de la biblioteca elegida. Se leen UNA vez por selección (entradas + todos
//     sus repasos en una consulta) y de ahí en más buscar, filtrar y ordenar es memoria: tipear en el
//     buscador no toca el disco, y la lista virtualiza, así que unas miles de entradas no pesan.
//   • Cada botón es UNA operación del store, que se guarda sola. Después de cada una se relee todo
//     (RefreshKnowledge) y se repone la selección donde estaba: no hay estado a medio guardar que
//     pueda quedar distinto de la base, ni un "Guardar" que alguien se olvide de apretar.
//
// Nada de lo de acá cachea entre ventanas: el playbooker relee la base cada vez que se abre, y esta
// pestaña relee cada vez que se entra a ella.
public partial class ConfigWindow
{
    private readonly KnowledgeStore _knowledge;

    private const string KnPathSeparator = " › ";
    private const double KnIndentPerLevel = 18;
    private const int KnPreviewLength = 180;

    // Mismos dos tonos del chip de naturaleza en el árbol y en las filas: dorado = la fijó esta
    // biblioteca / entrada; gris (y cursiva, que es la señal que no depende del color) = heredada.
    private static readonly Brush KnOwnNatureBrush = Frozen(0xFF, 0xD7, 0x00);
    private static readonly Brush KnInheritedNatureBrush = Frozen(0x9A, 0x9A, 0x9A);

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    // ── Foto de la base (se rehace entera en cada RefreshKnowledge) ──
    private Dictionary<long, Library> _knLibraries = new();
    private ILookup<long?, Library> _knChildren = Array.Empty<Library>().ToLookup(l => l.ParentId);
    private List<Nature> _knNatures = new();
    private Dictionary<long, string> _knNatureNames = new();
    private List<Language> _knLanguages = new();
    private IReadOnlyDictionary<long, int> _knNatureEntryCounts = new Dictionary<long, int>();
    private IReadOnlyDictionary<long, int> _knNatureLibraryCounts = new Dictionary<long, int>();
    private IReadOnlyDictionary<long, int> _knLanguageTranslationCounts = new Dictionary<long, int>();
    private IReadOnlyDictionary<long, int> _knLanguageReviewCounts = new Dictionary<long, int>();
    /// <summary>Entradas PROPIAS de cada biblioteca (sin descendientes). La que no aparece tiene cero.</summary>
    private IReadOnlyDictionary<long, int> _knEntryCounts = new Dictionary<long, int>();

    // ── Entradas de la biblioteca elegida ──
    private List<KnEntryRow> _knRows = new();
    private string _knSortKey = "";     // "" = orden de carga (el de la base)
    private bool _knSortDescending;

    /// <summary>
    /// true mientras se repinta alguna de las listas. Vaciar y volver a llenar dispara
    /// SelectionChanged con la selección en null: sin este guard cada repintado recargaría las
    /// entradas dos o tres veces, la primera de ellas para "ninguna biblioteca".
    /// </summary>
    private bool _knRebuilding;

    /// <summary>Un destino posible de "mover": una biblioteca, o la raíz (<see cref="Id"/> null).</summary>
    private sealed record KnLibraryChoice(long? Id, string Label)
    {
        public override string ToString() => Label;
    }

    /// <summary>Una opción del combo de naturaleza por defecto: heredar / una existente / crear una.</summary>
    private sealed record KnNatureChoice(long? Id, string Label, bool IsNew = false)
    {
        public override string ToString() => Label;
    }

    /// <summary>
    /// Una fila de la lista de entradas, con TODO lo que pinta la plantilla ya resuelto (textos,
    /// pinceles, visibilidades). La lista recicla contenedores al scrollear: cualquier cosa que se
    /// calculara al pintar se recalcularía en cada fila que entra en pantalla.
    /// </summary>
    private sealed class KnEntryRow
    {
        public Entry Entry { get; init; } = null!;
        public string KindText { get; init; } = "";
        public string Front { get; init; } = "";
        public string FrontTip { get; init; } = "";
        public string BackPreview { get; init; } = "";
        public string BackTip { get; init; } = "";
        public string NatureText { get; init; } = "";
        public string NatureTip { get; init; } = "";
        public Brush NatureBrush { get; init; } = KnInheritedNatureBrush;
        public FontStyle NatureFontStyle { get; init; }
        public Visibility NatureVisibility { get; init; }
        public string SolidityText { get; init; } = "";
        /// <summary>null = sin jugar. Para ordenar: las no jugadas van juntas, antes que el 0%.</summary>
        public double? SolidityValue { get; init; }
        public string LibraryText { get; init; } = "";
        public Visibility LibraryVisibility { get; init; }
        public int ReviewCount { get; init; }
        public string AccessibleName { get; init; } = "";
        /// <summary>Frente + dorso ya normalizados (sin tildes, minúsculas) para el buscador.</summary>
        public string Haystack { get; init; } = "";
    }

    private Library? SelectedKnLibrary => (KnLibList.SelectedItem as ListBoxItem)?.Tag as Library;
    private Nature? SelectedKnNature => (KnNatureList.SelectedItem as ListBoxItem)?.Tag as Nature;
    private Language? SelectedKnLanguage => (KnLanguageList.SelectedItem as ListBoxItem)?.Tag as Language;
    private List<KnEntryRow> SelectedKnEntries => KnEntryList.SelectedItems.Cast<KnEntryRow>().ToList();

    private void InitKnowledgeTab()
    {
        // Se carga recién al ENTRAR a la pestaña, y de nuevo cada vez que se vuelve: abrir la config
        // para tocar un escritorio no paga estas lecturas, y volver acá después de jugar una ronda
        // muestra la solidez de ahora. SelectionChanged burbujea desde cada lista y combo de adentro:
        // sólo interesa el cambio de las pestañas de la ventana.
        Tabs.SelectionChanged += (_, e) =>
        {
            if (ReferenceEquals(e.OriginalSource, Tabs) && ReferenceEquals(Tabs.SelectedItem, KnowledgeTab))
                RefreshKnowledge();
        };

        // ── Bibliotecas ──
        KnLibList.SelectionChanged += (_, _) => { if (!_knRebuilding) OnKnLibraryChanged(); };
        KnLibList.PreviewKeyDown += OnKnLibListKeyDown;
        KnLibNewBtn.Click += (_, _) => NewKnLibrary(asChild: false);
        KnLibEmptyNewBtn.Click += (_, _) => NewKnLibrary(asChild: false);
        KnLibNewChildBtn.Click += (_, _) => NewKnLibrary(asChild: true);
        KnLibRenameBtn.Click += (_, _) => RenameKnLibrary();
        KnLibDeleteBtn.Click += (_, _) => DeleteKnLibrary();
        KnLibMoveCombo.SelectionChanged += (_, _) => { if (!_knRebuilding) UpdateKnLibraryButtons(); };
        KnLibMoveBtn.Click += (_, _) => MoveKnLibrary();
        KnLibNatureCombo.SelectionChanged += (_, _) => { if (!_knRebuilding) UpdateKnLibraryButtons(); };
        KnLibNatureBtn.Click += (_, _) => ApplyKnLibraryNature();

        // ── Entradas ──
        KnIncludeSubChk.Click += (_, _) => LoadKnEntries(SelectedKnEntryIds());
        KnEntrySearchBox.TextChanged += (_, _) => ApplyKnEntryFilter(SelectedKnEntryIds());
        KnEntrySearchBox.PreviewKeyDown += OnKnSearchKeyDown;
        foreach (var radio in new[] { KnKindAllRadio, KnKindCardsRadio, KnKindTermsRadio })
            radio.Checked += (_, _) => { if (!_knRebuilding) ApplyKnEntryFilter(SelectedKnEntryIds()); };
        KnEntryClearBtn.Click += (_, _) => ClearKnEntryFilters();

        KnSortKindBtn.Click += (_, _) => SortKnEntriesBy("kind");
        KnSortFrontBtn.Click += (_, _) => SortKnEntriesBy("front");
        KnSortNatureBtn.Click += (_, _) => SortKnEntriesBy("nature");
        KnSortSolidityBtn.Click += (_, _) => SortKnEntriesBy("solidity");
        UpdateKnSortHeaders();

        KnEntryList.SelectionChanged += (_, _) => { if (!_knRebuilding) UpdateKnEntryButtons(); };
        KnEntryList.PreviewKeyDown += OnKnEntryListKeyDown;
        KnEntryList.MouseDoubleClick += OnKnEntryDoubleClick;
        KnEntryNewCardBtn.Click += (_, _) => NewKnEntry(EntryKind.Card);
        KnEntryNewTermBtn.Click += (_, _) => NewKnEntry(EntryKind.Term);
        KnEntryEditBtn.Click += (_, _) => EditKnEntry();
        KnEntryDeleteBtn.Click += (_, _) => DeleteKnEntries();
        KnEntryMoveCombo.SelectionChanged += (_, _) => { if (!_knRebuilding) UpdateKnEntryButtons(); };
        KnEntryMoveBtn.Click += (_, _) => MoveKnEntries();

        // ── Naturalezas e idiomas ──
        KnNatureList.SelectionChanged += (_, _) => { if (!_knRebuilding) UpdateKnListButtons(); };
        KnLanguageList.SelectionChanged += (_, _) => { if (!_knRebuilding) UpdateKnListButtons(); };
        KnNatureList.PreviewKeyDown += (_, e) => OnKnNamedListKeyDown(e, RenameKnNature, DeleteKnNature);
        KnLanguageList.PreviewKeyDown += (_, e) => OnKnNamedListKeyDown(e, RenameKnLanguage, DeleteKnLanguage);
        KnNatureAddBtn.Click += (_, _) => AddKnNature();
        KnNatureRenameBtn.Click += (_, _) => RenameKnNature();
        KnNatureDeleteBtn.Click += (_, _) => DeleteKnNature();
        KnLanguageAddBtn.Click += (_, _) => AddKnLanguage();
        KnLanguageRenameBtn.Click += (_, _) => RenameKnLanguage();
        KnLanguageDeleteBtn.Click += (_, _) => DeleteKnLanguage();

        // ── Importar / exportar (ver ConfigWindow.KnowledgeExchange.cs) ──
        InitKnowledgeExchange();
    }

    // ──────────────────────────── Refresco general ────────────────────────────

    /// <summary>
    /// Relee TODO lo de la pestaña y repone las selecciones. Un único refresco para las tres zonas a
    /// propósito (mismo criterio que <see cref="RefreshScopeTree"/>): borrar una naturaleza cambia los
    /// chips del árbol, los de las filas y los conteos de la otra sub-pestaña; si cada operación
    /// tuviera que acordarse de qué repintar, el día que una se olvidara quedaba un dato viejo en
    /// pantalla listo para que alguien actúe sobre él.
    /// </summary>
    /// <param name="selectLibraryId">La biblioteca a dejar elegida; null = la que estaba.</param>
    /// <param name="selectEntryIds">Las entradas a dejar elegidas; null = las que estaban.</param>
    private void RefreshKnowledge(long? selectLibraryId = null, IReadOnlyCollection<long>? selectEntryIds = null)
    {
        long? wantedLibrary = selectLibraryId ?? SelectedKnLibrary?.Id;
        selectEntryIds ??= SelectedKnEntryIds();
        long? wantedNature = SelectedKnNature?.Id;
        long? wantedLanguage = SelectedKnLanguage?.Id;

        var libraries = _knowledge.GetLibraries();
        _knLibraries = libraries.ToDictionary(l => l.Id);
        _knChildren = libraries.ToLookup(l => l.ParentId);
        _knNatures = _knowledge.GetNatures().ToList();
        _knNatureNames = _knNatures.ToDictionary(n => n.Id, n => n.Name);
        _knLanguages = _knowledge.GetLanguages().ToList();
        var entryCounts = _knEntryCounts = _knowledge.GetEntryCountsByLibrary();
        _knNatureEntryCounts = _knowledge.GetEntryCountsByNature();
        _knNatureLibraryCounts = _knowledge.GetLibraryCountsByDefaultNature();
        _knLanguageTranslationCounts = _knowledge.GetTranslationCountsByLanguage();
        _knLanguageReviewCounts = _knowledge.GetReviewCountsByLanguage();

        _knRebuilding = true;
        try
        {
            KnLibList.Items.Clear();
            foreach (var (library, depth) in KnTreeOrder())
                KnLibList.Items.Add(BuildKnLibraryItem(library, depth,
                    entryCounts.TryGetValue(library.Id, out int count) ? count : 0));

            // La que estaba elegida pudo borrarse: se cae a la primera, que es mejor que quedar sin
            // ninguna (los botones de la derecha necesitan una biblioteca para tener sentido).
            var items = KnLibList.Items.OfType<ListBoxItem>().ToList();
            KnLibList.SelectedItem = items.FirstOrDefault(i => i.Tag is Library l && l.Id == wantedLibrary)
                                     ?? items.FirstOrDefault();

            FillKnNamedList(KnNatureList, _knNatures.Select(n => (Tag: (object)n, n.Id, n.Name, Detail: KnNatureDetail(n))),
                wantedNature);
            FillKnNamedList(KnLanguageList, _knLanguages.Select(l => (Tag: (object)l, l.Id, l.Name, Detail: KnLanguageDetail(l))),
                wantedLanguage);

            // Los combos de destino también se llenan bajo el guard: cada uno dispara su
            // SelectionChanged al asignarle la selección, y todavía no hay nada coherente que recalcular.
            RefreshKnLibraryTargets();
            RefreshKnEntryTargets();
        }
        finally
        {
            _knRebuilding = false;
        }

        // Sin bibliotecas manda el estado vacío (qué es una + su botón) y los botones de abajo se
        // van: la misma acción no puede verse dos veces a la vez.
        bool none = _knLibraries.Count == 0;
        KnLibEmpty.Visibility = none ? Visibility.Visible : Visibility.Collapsed;
        KnLibActions.Visibility = none ? Visibility.Collapsed : Visibility.Visible;
        KnNatureEmpty.Visibility = _knNatures.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        KnLanguageEmpty.Visibility = _knLanguages.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        if (KnLibList.SelectedItem is not null) KnLibList.ScrollIntoView(KnLibList.SelectedItem);
        UpdateKnLibraryButtons();
        UpdateKnListButtons();
        LoadKnEntries(selectEntryIds);
    }

    /// <summary>
    /// Las bibliotecas en ORDEN DE ÁRBOL (cada madre seguida de sus hijas) con su profundidad.
    /// Las que no cuelgan de ninguna raíz —una base tocada a mano con un ciclo, A hija de B y B hija
    /// de A— se agregan al final como si fueran raíces: invisibles no se podrían ni borrar.
    /// </summary>
    private List<(Library Library, int Depth)> KnTreeOrder()
    {
        var ordered = new List<(Library, int)>();
        var seen = new HashSet<long>();

        void Walk(Library library, int depth)
        {
            if (!seen.Add(library.Id)) return; // corta el ciclo
            ordered.Add((library, depth));
            foreach (var child in _knChildren[library.Id].OrderBy(l => l.Sort).ThenBy(l => l.Id))
                Walk(child, depth + 1);
        }

        foreach (var root in _knChildren[null].OrderBy(l => l.Sort).ThenBy(l => l.Id)) Walk(root, 0);
        foreach (var orphan in _knLibraries.Values.OrderBy(l => l.Id)) Walk(orphan, 0);
        return ordered;
    }

    /// <summary>Ids de la biblioteca y de todo lo que cuelga de ella (para no ofrecerlas como destino).</summary>
    private HashSet<long> KnSubtreeIds(long libraryId)
    {
        var ids = new HashSet<long>();
        var queue = new Queue<long>();
        queue.Enqueue(libraryId);
        while (queue.Count > 0)
        {
            long id = queue.Dequeue();
            if (!ids.Add(id)) continue;
            foreach (var child in _knChildren[id]) queue.Enqueue(child.Id);
        }
        return ids;
    }

    private string KnPath(long libraryId) =>
        string.Join(KnPathSeparator, LibraryTree.PathOf(libraryId, _knLibraries));

    private static string KnCount(int count) => count == 1
        ? Loc.T("Playbook.EntriesOne")
        : string.Format(Loc.T("Playbook.EntriesMany"), count);

    // ──────────────────────────── Bibliotecas ────────────────────────────

    /// <summary>
    /// Fila del árbol: nombre con sangría, cuántas entradas PROPIAS tiene y el chip de su naturaleza
    /// efectiva — dorado si la fijó ella, gris y en cursiva si le llega de una biblioteca de arriba.
    /// </summary>
    private ListBoxItem BuildKnLibraryItem(Library library, int depth, int ownEntries)
    {
        var panel = new DockPanel { Margin = new Thickness(depth * KnIndentPerLevel, 0, 0, 0) };
        string count = KnCount(ownEntries);
        string accessible = string.Format(Loc.T("Config.KnowledgeLibRowAccessible"), KnPath(library.Id), count);

        // El chip va PRIMERO en el DockPanel (pegado a la derecha) para que, si no entra todo, lo que
        // se recorte con "…" sea el nombre y no la naturaleza.
        if (LibraryTree.EffectiveNatureId(library.Id, _knLibraries) is { } natureId
            && _knNatureNames.TryGetValue(natureId, out var natureName))
        {
            bool own = library.DefaultNatureId == natureId;
            var chip = BuildKnNatureChip(natureName, own,
                Loc.T(own ? "Config.KnowledgeNatureOwnLibTip" : "Config.KnowledgeNatureInheritedLibTip"));
            chip.Margin = new Thickness(8, 0, 0, 0);
            DockPanel.SetDock(chip, Dock.Right);
            panel.Children.Add(chip);
            accessible += ", " + string.Format(
                Loc.T(own ? "Config.KnowledgeNatureOwnAccessible" : "Config.KnowledgeNatureInheritedAccessible"), natureName);
        }

        var countText = new TextBlock
        {
            Text = count,
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 11,
            Foreground = (Brush)FindResource("FgMuted"),
        };
        DockPanel.SetDock(countText, Dock.Right);
        panel.Children.Add(countText);

        panel.Children.Add(new TextBlock
        {
            Text = library.Name,
            VerticalAlignment = VerticalAlignment.Center,
            FontWeight = depth == 0 ? FontWeights.SemiBold : FontWeights.Normal,
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = KnPath(library.Id),
        });

        var item = new ListBoxItem { Content = panel, Tag = library };
        System.Windows.Automation.AutomationProperties.SetName(item, accessible);
        return item;
    }

    private Border BuildKnNatureChip(string name, bool own, string tip) => new()
    {
        BorderBrush = own ? KnOwnNatureBrush : KnInheritedNatureBrush,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(9),
        Padding = new Thickness(7, 1, 7, 1),
        VerticalAlignment = VerticalAlignment.Center,
        ToolTip = tip,
        Child = new TextBlock
        {
            Text = name,
            FontSize = 11,
            FontStyle = own ? FontStyles.Normal : FontStyles.Italic,
            Foreground = own ? KnOwnNatureBrush : KnInheritedNatureBrush,
        },
    };

    private void OnKnLibraryChanged()
    {
        _knRebuilding = true;
        try { RefreshKnLibraryTargets(); }
        finally { _knRebuilding = false; }
        UpdateKnLibraryButtons();
        LoadKnEntries(null);
    }

    /// <summary>
    /// Llena los dos combos que dependen de la biblioteca elegida. Destinos de "mover": la raíz y
    /// toda biblioteca que NO sea ella ni cuelgue de ella (meterla adentro de sí misma dejaría el
    /// subárbol colgando de nada — el store lo rechaza, pero no ofrecerlo es mejor que rechazarlo).
    /// Naturaleza por defecto: heredar (diciendo QUÉ hereda), las existentes y "nueva…".
    /// </summary>
    private void RefreshKnLibraryTargets()
    {
        var library = SelectedKnLibrary;

        KnLibMoveCombo.Items.Clear();
        KnLibNatureCombo.Items.Clear();
        if (library is null) return;

        var excluded = KnSubtreeIds(library.Id);
        KnLibMoveCombo.Items.Add(new KnLibraryChoice(null, Loc.T("Config.KnowledgeLibMoveRoot")));
        foreach (var (candidate, _) in KnTreeOrder())
            if (!excluded.Contains(candidate.Id))
                KnLibMoveCombo.Items.Add(new KnLibraryChoice(candidate.Id, KnPath(candidate.Id)));
        // Arranca en donde YA está: el botón queda apagado con su motivo, y mover exige elegir otro.
        KnLibMoveCombo.SelectedItem = KnLibMoveCombo.Items.Cast<KnLibraryChoice>()
            .FirstOrDefault(c => c.Id == library.ParentId) ?? KnLibMoveCombo.Items[0];

        string? inherited = library.ParentId is { } parent
                            && LibraryTree.EffectiveNatureId(parent, _knLibraries) is { } inheritedId
                            && _knNatureNames.TryGetValue(inheritedId, out var inheritedName)
            ? inheritedName
            : null;
        KnLibNatureCombo.Items.Add(new KnNatureChoice(null, inherited is null
            ? Loc.T(library.ParentId is null ? "Config.KnowledgeLibNatureNone" : "Config.KnowledgeLibNatureInheritNone")
            : string.Format(Loc.T("Config.KnowledgeLibNatureInherit"), inherited)));
        foreach (var nature in _knNatures)
            KnLibNatureCombo.Items.Add(new KnNatureChoice(nature.Id, nature.Name));
        KnLibNatureCombo.Items.Add(new KnNatureChoice(null, Loc.T("Config.KnowledgeLibNatureNew"), IsNew: true));
        KnLibNatureCombo.SelectedItem = KnLibNatureCombo.Items.Cast<KnNatureChoice>()
            .First(c => !c.IsNew && c.Id == (library.DefaultNatureId is { } d && _knNatureNames.ContainsKey(d) ? d : null));
    }

    /// <summary>
    /// Habilita lo que aplica y, para lo que no, deja escrito POR QUÉ al lado del botón. Un botón
    /// gris sin explicación se lee igual que uno roto.
    /// </summary>
    private void UpdateKnLibraryButtons()
    {
        var library = SelectedKnLibrary;
        bool any = library is not null;

        KnLibNewChildBtn.IsEnabled = any;
        KnLibRenameBtn.IsEnabled = any;
        KnLibDeleteBtn.IsEnabled = any;
        KnLibReason.Text = any ? "" : Loc.T("Config.KnowledgeLibPickReason");
        KnLibReason.Visibility = any ? Visibility.Collapsed : Visibility.Visible;

        KnLibMoveLabel.Text = any
            ? string.Format(Loc.T("Config.KnowledgeLibMoveLabelFor"), library!.Name)
            : Loc.T("Config.KnowledgeLibMoveLabel");

        bool sameParent = any && KnLibMoveCombo.SelectedItem is KnLibraryChoice target && target.Id == library!.ParentId;
        KnLibMoveCombo.IsEnabled = any;
        KnLibMoveBtn.IsEnabled = any && KnLibMoveCombo.SelectedItem is KnLibraryChoice && !sameParent;
        KnLibMoveReason.Text = sameParent ? Loc.T("Config.KnowledgeLibMoveSameReason") : "";

        var choice = KnLibNatureCombo.SelectedItem as KnNatureChoice;
        bool sameNature = any && choice is { IsNew: false } && choice.Id == library!.DefaultNatureId;
        KnLibNatureCombo.IsEnabled = any;
        KnLibNatureBtn.IsEnabled = any && choice is not null && !sameNature;
        KnLibNatureReason.Text = sameNature ? Loc.T("Config.KnowledgeLibNatureSameReason") : "";

        // Lo que exporta «Exportar…» depende de la biblioteca elegida: se recalcula con ella.
        UpdateKnExchangeScope();
    }

    private void OnKnLibListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F2) { e.Handled = true; RenameKnLibrary(); }
        else if (e.Key == Key.Delete) { e.Handled = true; DeleteKnLibrary(); }
    }

    /// <summary>
    /// Traduce el motivo del store a un mensaje que ORIENTA (mismo contrato que
    /// <see cref="ReportScopeResult"/>): una operación que no puede avanzar nunca se queda muda.
    /// </summary>
    private bool ReportKnowledgeResult(KnowledgeOpResult result, string subject)
    {
        if (result == KnowledgeOpResult.Ok) return true;

        string message = result switch
        {
            KnowledgeOpResult.EmptyName => Loc.T("Config.KnowledgeErrEmptyName"),
            KnowledgeOpResult.NameTaken => string.Format(Loc.T("Config.KnowledgeErrNameTaken"), subject),
            KnowledgeOpResult.SameTarget => Loc.T("Config.KnowledgeErrSameTarget"),
            KnowledgeOpResult.WouldCycle => string.Format(Loc.T("Config.KnowledgeErrWouldCycle"), subject),
            KnowledgeOpResult.NotFound => Loc.T("Config.KnowledgeErrNotFound"),
            _ => Loc.T("Config.KnowledgeErrFailed"),
        };
        MessageBox.Show(this, message, Loc.T("Config.KnowledgeErrTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);

        // "Ya no existe" es justo el caso en que la pantalla quedó vieja: se relee para que el
        // usuario no vuelva a intentar sobre lo mismo.
        if (result == KnowledgeOpResult.NotFound) RefreshKnowledge();
        return false;
    }

    private void NewKnLibrary(bool asChild)
    {
        var parent = asChild ? SelectedKnLibrary : null;
        if (asChild && parent is null) return;

        string? name = PromptDialog.Show(this,
            parent is null
                ? Loc.T("Config.KnowledgeLibNewTitle")
                : string.Format(Loc.T("Config.KnowledgeLibNewChildTitle"), parent.Name),
            Loc.T("Config.KnowledgeNameLabel"));
        if (name is null) return;

        if (!ReportKnowledgeResult(_knowledge.CreateLibrary(name, parent?.Id, null, out long id), name)) return;
        RefreshKnowledge(selectLibraryId: id);
        KnLibList.Focus();
    }

    private void RenameKnLibrary()
    {
        var library = SelectedKnLibrary;
        if (library is null) return;

        string? name = PromptDialog.Show(this, Loc.T("Config.KnowledgeLibRenameTitle"),
            Loc.T("Config.KnowledgeNameLabel"), library.Name);
        if (name is null || name.Trim() == library.Name) return;

        if (!ReportKnowledgeResult(_knowledge.RenameLibrary(library.Id, name), name)) return;
        RefreshKnowledge(selectLibraryId: library.Id);
    }

    private void MoveKnLibrary()
    {
        var library = SelectedKnLibrary;
        if (library is null || KnLibMoveCombo.SelectedItem is not KnLibraryChoice target) return;

        if (!ReportKnowledgeResult(_knowledge.MoveLibrary(library.Id, target.Id), library.Name)) return;
        RefreshKnowledge(selectLibraryId: library.Id);
    }

    private void ApplyKnLibraryNature()
    {
        var library = SelectedKnLibrary;
        if (library is null || KnLibNatureCombo.SelectedItem is not KnNatureChoice choice) return;

        long? natureId = choice.Id;
        if (choice.IsNew)
        {
            string? name = PromptDialog.Show(this, Loc.T("Config.KnowledgeNatureNewTitle"),
                Loc.T("Config.KnowledgeNameLabel"));
            if (string.IsNullOrWhiteSpace(name)) return;
            // Si ya existía una con ese nombre se usa ésa: acá el objetivo es ASIGNARLA, no crear.
            if (_knowledge.GetOrCreateNature(name) is not { } nature)
            {
                ReportKnowledgeResult(KnowledgeOpResult.Failed, name);
                return;
            }
            natureId = nature.Id;
        }

        if (!_knowledge.SetLibraryDefaultNature(library.Id, natureId))
        {
            ReportKnowledgeResult(KnowledgeOpResult.Failed, library.Name);
            return;
        }
        RefreshKnowledge(selectLibraryId: library.Id);
    }

    private void DeleteKnLibrary()
    {
        var library = SelectedKnLibrary;
        if (library is null) return;

        // La confirmación dice CUÁNTO se va: borrar una biblioteca madre se lleva todo el subárbol
        // y el historial de cada entrada, y eso no vuelve.
        var contents = _knowledge.GetLibraryContents(library.Id);
        string message = string.Format(Loc.T("Config.KnowledgeLibDeleteConfirm"),
            library.Name, contents.SubLibraries, contents.Entries, contents.Reviews);
        if (MessageBox.Show(this, message, Loc.T("Config.KnowledgeDeleteTitle"), MessageBoxButton.YesNo,
                MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;

        if (!_knowledge.DeleteLibrary(library.Id))
        {
            ReportKnowledgeResult(KnowledgeOpResult.Failed, library.Name);
            return;
        }
        // Queda elegida la madre: es donde estaba parado quien la borró.
        RefreshKnowledge(selectLibraryId: library.ParentId);
        KnLibList.Focus();
    }

    // ──────────────────────────── Entradas ────────────────────────────

    private List<long> SelectedKnEntryIds() => SelectedKnEntries.Select(r => r.Entry.Id).ToList();

    /// <summary>
    /// Lee las entradas de la biblioteca elegida y arma sus filas. DOS consultas en total, sea cual
    /// sea el tamaño: las entradas, y todos sus repasos juntos (de ahí sale la solidez de cada fila
    /// y el conteo de historial que muestra la confirmación de borrado). Nada por fila.
    /// </summary>
    private void LoadKnEntries(IReadOnlyCollection<long>? selectEntryIds)
    {
        var library = SelectedKnLibrary;
        bool includeSub = KnIncludeSubChk.IsChecked == true;

        if (library is null)
        {
            _knRows = new List<KnEntryRow>();
        }
        else
        {
            var entries = _knowledge.GetEntries(library.Id, includeSub);
            var reviews = _knowledge.GetReviews(entries.Select(e => e.Id));
            var states = Solidity.ComputeAll(reviews, DateTime.UtcNow);
            var reviewCounts = reviews.GroupBy(r => r.EntryId).ToDictionary(g => g.Key, g => g.Count());
            _knRows = entries.Select(e => BuildKnEntryRow(e, states,
                reviewCounts.TryGetValue(e.Id, out int n) ? n : 0, includeSub)).ToList();
        }

        KnEntryHeader.Text = library is null
            ? Loc.T("Config.KnowledgeEntriesNoLibrary")
            : string.Format(Loc.T("Config.KnowledgeEntriesHeader"), KnPath(library.Id), KnCount(_knRows.Count));
        ApplyKnEntryFilter(selectEntryIds);
    }

    private KnEntryRow BuildKnEntryRow(Entry entry,
        IReadOnlyDictionary<(long, string), SolidityState> states, int reviewCount, bool showLibrary)
    {
        bool term = entry.Kind == EntryKind.Term;
        string kind = Loc.T(term ? "Config.KnowledgeKindTerm" : "Config.KnowledgeKindCard");
        string front = OneLine(entry.Front);

        // Naturaleza EFECTIVA: la que fijó la entrada, o la que le llega por el árbol.
        long? natureId = LibraryTree.EffectiveNatureId(entry, _knLibraries);
        string nature = natureId is { } id && _knNatureNames.TryGetValue(id, out var name) ? name : "";
        bool own = nature != "" && entry.NatureId == natureId;

        // La misma medida y las mismas palabras que la lista del playbooker: un porcentaje, o "sin
        // jugar" — que NO es 0%: no haberla visto nunca no es haberla visto y no saberla.
        double? value = Solidity.EntryValue(states, entry.Id);
        string solidity = value is { } v
            ? Math.Round(v).ToString("0", CultureInfo.CurrentCulture) + "%"
            : Loc.T("Playbook.NotPlayed");

        string accessible = string.Format(Loc.T("Config.KnowledgeEntryRowAccessible"), kind, front, solidity);
        if (nature != "")
            accessible += ", " + string.Format(
                Loc.T(own ? "Config.KnowledgeNatureOwnAccessible" : "Config.KnowledgeNatureInheritedAccessible"), nature);

        return new KnEntryRow
        {
            Entry = entry,
            KindText = kind,
            Front = front,
            FrontTip = KnClip(entry.Front, 1200),
            BackPreview = KnClip(OneLine(entry.Back), KnPreviewLength),
            BackTip = KnClip(entry.Back, 1200),
            NatureText = nature,
            NatureTip = Loc.T(own ? "Config.KnowledgeNatureOwnTip" : "Config.KnowledgeNatureInheritedTip"),
            NatureBrush = own ? KnOwnNatureBrush : KnInheritedNatureBrush,
            NatureFontStyle = own ? FontStyles.Normal : FontStyles.Italic,
            NatureVisibility = nature == "" ? Visibility.Collapsed : Visibility.Visible,
            SolidityText = solidity,
            SolidityValue = value,
            LibraryText = showLibrary ? KnPath(entry.LibraryId) : "",
            LibraryVisibility = showLibrary ? Visibility.Visible : Visibility.Collapsed,
            ReviewCount = reviewCount,
            AccessibleName = accessible,
            Haystack = TextFold.Fold(entry.Front + "\n" + entry.Back),
        };
    }

    private static string OneLine(string text) => text.ReplaceLineEndings(" ").Trim();

    private static string KnClip(string text, int max) => text.Length <= max ? text : text[..max].TrimEnd() + "…";

    private EntryKind? KnKindFilter =>
        KnKindCardsRadio.IsChecked == true ? EntryKind.Card
        : KnKindTermsRadio.IsChecked == true ? EntryKind.Term
        : null;

    /// <summary>
    /// Filtra y ordena EN MEMORIA las filas ya cargadas y las vuelca a la lista. Buscador: sin tildes
    /// ni mayúsculas, todas las palabras en cualquier orden, contra frente y dorso (mismo criterio que
    /// el buscador del playbooker).
    /// </summary>
    private void ApplyKnEntryFilter(IReadOnlyCollection<long>? selectEntryIds)
    {
        var terms = TextFold.Fold(KnEntrySearchBox.Text).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var kind = KnKindFilter;
        bool filtering = terms.Length > 0 || kind is not null;

        IEnumerable<KnEntryRow> visible = _knRows;
        if (kind is { } k) visible = visible.Where(r => r.Entry.Kind == k);
        if (terms.Length > 0)
            visible = visible.Where(r => terms.All(t => r.Haystack.Contains(t, StringComparison.Ordinal)));
        var list = SortKnRows(visible).ToList();

        _knRebuilding = true;
        try
        {
            // Lista nueva cada vez: las filas no notifican cambios, reasignar el origen es lo que repinta.
            KnEntryList.ItemsSource = list;
            if (selectEntryIds is { Count: > 0 })
            {
                var wanted = selectEntryIds as HashSet<long> ?? new HashSet<long>(selectEntryIds);
                foreach (var row in list)
                    if (wanted.Contains(row.Entry.Id)) KnEntryList.SelectedItems.Add(row);
                if (KnEntryList.SelectedItem is not null) KnEntryList.ScrollIntoView(KnEntryList.SelectedItem);
            }
        }
        finally
        {
            _knRebuilding = false;
        }

        KnEntryClearBtn.Visibility = filtering ? Visibility.Visible : Visibility.Collapsed;

        // Tres vacíos con tres salidas distintas; un mismo cartel para los tres no orienta a nadie.
        KnEntryEmpty.Text = SelectedKnLibrary is null ? Loc.T("Config.KnowledgeEntriesEmptyNoLibrary")
            : _knRows.Count == 0 ? Loc.T(KnIncludeSubChk.IsChecked == true
                ? "Config.KnowledgeEntriesEmptyTree" : "Config.KnowledgeEntriesEmpty")
            : Loc.T("Config.KnowledgeEntriesNoMatch");
        KnEntryEmpty.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        UpdateKnEntryButtons();
    }

    private IEnumerable<KnEntryRow> SortKnRows(IEnumerable<KnEntryRow> rows)
    {
        var text = StringComparer.CurrentCultureIgnoreCase;
        IOrderedEnumerable<KnEntryRow>? sorted = _knSortKey switch
        {
            "kind" => Order(rows, r => r.KindText, text),
            "front" => Order(rows, r => r.Front, text),
            // Sin naturaleza y sin jugar van siempre al FINAL, suba o baje el orden: son "no hay dato",
            // no un valor más de la escala.
            "nature" => Order(rows.OrderBy(r => r.NatureText == ""), r => r.NatureText, text),
            "solidity" => Order(rows.OrderBy(r => r.SolidityValue is null), r => r.SolidityValue ?? 0, Comparer<double>.Default),
            _ => null,
        };
        // Desempate estable por id: dos filas iguales no cambian de lugar entre un repintado y otro.
        return sorted is null ? rows : sorted.ThenBy(r => r.Entry.Id);
    }

    private IOrderedEnumerable<KnEntryRow> Order<TKey>(IEnumerable<KnEntryRow> rows,
        Func<KnEntryRow, TKey> key, IComparer<TKey> comparer) =>
        rows is IOrderedEnumerable<KnEntryRow> grouped
            ? (_knSortDescending ? grouped.ThenByDescending(key, comparer) : grouped.ThenBy(key, comparer))
            : (_knSortDescending ? rows.OrderByDescending(key, comparer) : rows.OrderBy(key, comparer));

    /// <summary>Click en un encabezado: primero ascendente, después descendente, después sin orden.</summary>
    private void SortKnEntriesBy(string key)
    {
        if (_knSortKey != key) { _knSortKey = key; _knSortDescending = false; }
        else if (!_knSortDescending) _knSortDescending = true;
        else { _knSortKey = ""; _knSortDescending = false; }

        UpdateKnSortHeaders();
        ApplyKnEntryFilter(SelectedKnEntryIds());
    }

    /// <summary>El encabezado activo lleva la flecha ESCRITA y su nombre accesible dice el sentido.</summary>
    private void UpdateKnSortHeaders()
    {
        foreach (var (button, key, label) in new[]
                 {
                     (KnSortKindBtn, "kind", Loc.T("Config.KnowledgeColKind")),
                     (KnSortFrontBtn, "front", Loc.T("Config.KnowledgeColFront")),
                     (KnSortNatureBtn, "nature", Loc.T("Config.KnowledgeColNature")),
                     (KnSortSolidityBtn, "solidity", Loc.T("Config.KnowledgeColSolidity")),
                 })
        {
            bool active = _knSortKey == key;
            button.Content = active ? label + (_knSortDescending ? " ↓" : " ↑") : label;
            System.Windows.Automation.AutomationProperties.SetName(button, string.Format(
                Loc.T(!active ? "Config.KnowledgeSortBy"
                    : _knSortDescending ? "Config.KnowledgeSortedDesc" : "Config.KnowledgeSortedAsc"), label));
        }
    }

    private void ClearKnEntryFilters()
    {
        _knRebuilding = true; // un solo repintado, no uno por control que se limpia
        try
        {
            KnKindAllRadio.IsChecked = true;
        }
        finally { _knRebuilding = false; }
        if (KnEntrySearchBox.Text == "") ApplyKnEntryFilter(SelectedKnEntryIds());
        else KnEntrySearchBox.Clear(); // dispara el repintado por TextChanged
        KnEntrySearchBox.Focus();
    }

    private void OnKnSearchKeyDown(object sender, KeyEventArgs e)
    {
        // Esc en dos tiempos: con texto, lo limpia (y se lo queda, para que no cierre la config);
        // vacío, sigue de largo y la ventana lo usa para cerrarse, como en el resto de las pestañas.
        if (e.Key == Key.Escape && KnEntrySearchBox.Text != "")
        {
            e.Handled = true;
            KnEntrySearchBox.Clear();
        }
        else if (e.Key == Key.Down && KnEntryList.Items.Count > 0)
        {
            // Bajar desde el buscador entra a la lista, como en cualquier lanzador.
            e.Handled = true;
            if (KnEntryList.SelectedIndex < 0) KnEntryList.SelectedIndex = 0;
            KnEntryList.UpdateLayout();
            (KnEntryList.ItemContainerGenerator.ContainerFromItem(KnEntryList.SelectedItem) as ListBoxItem)?.Focus();
        }
    }

    /// <summary>Llena el combo de destino de "mover entradas": todas las bibliotecas, con su camino.</summary>
    private void RefreshKnEntryTargets()
    {
        long? previous = (KnEntryMoveCombo.SelectedItem as KnLibraryChoice)?.Id;
        KnEntryMoveCombo.Items.Clear();
        foreach (var (library, _) in KnTreeOrder())
            KnEntryMoveCombo.Items.Add(new KnLibraryChoice(library.Id, KnPath(library.Id)));

        // Se conserva el destino elegido: mover varias tandas a la misma biblioteca es lo común.
        KnEntryMoveCombo.SelectedItem = KnEntryMoveCombo.Items.Cast<KnLibraryChoice>()
            .FirstOrDefault(c => c.Id == previous);
        if (KnEntryMoveCombo.SelectedItem is null && KnEntryMoveCombo.Items.Count > 0)
            KnEntryMoveCombo.SelectedIndex = 0;
    }

    private void UpdateKnEntryButtons()
    {
        var library = SelectedKnLibrary;
        var selected = SelectedKnEntries;
        bool hasLibrary = library is not null;

        KnEntryNewCardBtn.IsEnabled = hasLibrary;
        KnEntryNewTermBtn.IsEnabled = hasLibrary;
        KnIncludeSubChk.IsEnabled = hasLibrary;
        KnEntryEditBtn.IsEnabled = selected.Count == 1;
        KnEntryDeleteBtn.IsEnabled = selected.Count > 0;

        var target = KnEntryMoveCombo.SelectedItem as KnLibraryChoice;
        bool allThere = target is not null && selected.Count > 0 && selected.All(r => r.Entry.LibraryId == target.Id);
        KnEntryMoveCombo.IsEnabled = _knLibraries.Count > 0;
        KnEntryMoveBtn.IsEnabled = selected.Count > 0 && target is not null && !allThere;

        int shown = KnEntryList.Items.Count;
        KnEntryCountText.Text = selected.Count > 0
            ? string.Format(Loc.T("Config.KnowledgeEntriesCountSelected"), shown, _knRows.Count, selected.Count)
            : string.Format(Loc.T("Config.KnowledgeEntriesCount"), shown, _knRows.Count);

        // UNA línea que dice qué falta para lo que está apagado; con todo habilitado, las teclas.
        KnEntryReason.Text = !hasLibrary ? Loc.T("Config.KnowledgeEntryReasonNoLibrary")
            : selected.Count == 0 ? Loc.T("Config.KnowledgeEntryReasonNone")
            : allThere ? Loc.T(selected.Count == 1
                ? "Config.KnowledgeEntryReasonSameLibraryOne" : "Config.KnowledgeEntryReasonSameLibraryMany")
            : selected.Count > 1 ? string.Format(Loc.T("Config.KnowledgeEntryReasonMany"), selected.Count)
            : Loc.T("Config.KnowledgeEntryKeys");
    }

    private void OnKnEntryListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Return) { e.Handled = true; EditKnEntry(); }
        else if (e.Key == Key.Delete) { e.Handled = true; DeleteKnEntries(); }
    }

    private void OnKnEntryDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // Sólo si el doble click cayó SOBRE una fila: en la barra de scroll o en el hueco no edita nada.
        if (e.OriginalSource is DependencyObject source
            && ItemsControl.ContainerFromElement(KnEntryList, source) is ListBoxItem)
        {
            e.Handled = true;
            EditKnEntry();
        }
    }

    private void NewKnEntry(EntryKind kind)
    {
        var library = SelectedKnLibrary;
        if (library is null) return;

        var outcome = KnowledgeEntryWindow.ShowNew(this, _knowledge, library.Id, kind);
        // Se relee SIEMPRE, aunque no se haya guardado ninguna entrada: desde el diálogo se pudo
        // crear un idioma con "+ idioma", y esta pestaña lo tiene que mostrar.
        RefreshKnowledge(selectEntryIds: outcome.LastSavedId is { } id ? new[] { id } : null);
        FocusKnSelectedEntry();
    }

    private void EditKnEntry()
    {
        var selected = SelectedKnEntries;
        if (selected.Count != 1) return;
        long id = selected[0].Entry.Id;

        // Se edita la versión de la BASE, no la foto de la fila: si cambió por otro lado, el diálogo
        // abre con lo vigente; y si ya no existe, se dice en vez de abrir un diálogo sobre nada.
        if (_knowledge.GetEntry(id) is not { } entry)
        {
            ReportKnowledgeResult(KnowledgeOpResult.NotFound, selected[0].Front);
            return;
        }

        KnowledgeEntryWindow.ShowEdit(this, _knowledge, entry);
        RefreshKnowledge(selectEntryIds: new[] { id });
        FocusKnSelectedEntry();
    }

    /// <summary>Devuelve el foco a la fila elegida al volver de un diálogo (para seguir con el teclado).</summary>
    private void FocusKnSelectedEntry()
    {
        if (KnEntryList.SelectedItem is null) { KnEntryList.Focus(); return; }
        KnEntryList.ScrollIntoView(KnEntryList.SelectedItem);
        KnEntryList.UpdateLayout(); // la lista virtualiza: el contenedor existe recién después del scroll
        if (KnEntryList.ItemContainerGenerator.ContainerFromItem(KnEntryList.SelectedItem) is ListBoxItem item)
            item.Focus();
    }

    private void DeleteKnEntries()
    {
        var selected = SelectedKnEntries;
        if (selected.Count == 0) return;

        // Borrar una entrada se lleva su historial: la confirmación lo dice, con el número.
        int reviews = selected.Sum(r => r.ReviewCount);
        string message = selected.Count == 1
            ? string.Format(Loc.T("Config.KnowledgeEntryDeleteConfirmOne"), KnClip(selected[0].Front, 80), reviews)
            : string.Format(Loc.T("Config.KnowledgeEntryDeleteConfirmMany"), selected.Count, reviews);
        if (MessageBox.Show(this, message, Loc.T("Config.KnowledgeDeleteTitle"), MessageBoxButton.YesNo,
                MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;

        // La fila que queda en el lugar de la primera borrada hereda la selección: borrar varias
        // seguidas con Supr no obliga a volver a buscar dónde estabas.
        int index = KnEntryList.Items.IndexOf(selected[0]);
        if (_knowledge.DeleteEntries(selected.Select(r => r.Entry.Id)) == 0)
        {
            ReportKnowledgeResult(KnowledgeOpResult.Failed, "");
            return;
        }

        RefreshKnowledge(selectEntryIds: Array.Empty<long>());
        if (KnEntryList.Items.Count > 0)
            KnEntryList.SelectedIndex = Math.Min(Math.Max(index, 0), KnEntryList.Items.Count - 1);
        FocusKnSelectedEntry();
    }

    private void MoveKnEntries()
    {
        var selected = SelectedKnEntries;
        if (selected.Count == 0 || KnEntryMoveCombo.SelectedItem is not KnLibraryChoice { Id: { } targetId } target) return;

        var result = _knowledge.MoveEntries(selected.Select(r => r.Entry.Id), targetId, out _);
        if (!ReportKnowledgeResult(result, target.Label)) return;

        // Se queda en la biblioteca que se estaba mirando (lo movido desaparece de la lista, que es
        // la confirmación de que se fue) y el conteo del árbol muestra adónde llegó.
        RefreshKnowledge(selectEntryIds: selected.Select(r => r.Entry.Id).ToList());
    }

    // ──────────────────────────── Naturalezas e idiomas ────────────────────────────

    private string KnNatureDetail(Nature nature)
    {
        int entries = _knNatureEntryCounts.TryGetValue(nature.Id, out int e) ? e : 0;
        int libraries = _knNatureLibraryCounts.TryGetValue(nature.Id, out int l) ? l : 0;
        string text = KnCount(entries);
        if (libraries > 0)
            text += " · " + (libraries == 1
                ? Loc.T("Config.KnowledgeNatureLibrariesOne")
                : string.Format(Loc.T("Config.KnowledgeNatureLibrariesMany"), libraries));
        return text;
    }

    private string KnLanguageDetail(Language language)
    {
        int translations = _knLanguageTranslationCounts.TryGetValue(language.Id, out int t) ? t : 0;
        return translations == 1
            ? Loc.T("Config.KnowledgeTranslationsOne")
            : string.Format(Loc.T("Config.KnowledgeTranslationsMany"), translations);
    }

    /// <summary>Llena una de las dos listas (nombre + su conteo a la derecha) y repone la selección.</summary>
    private void FillKnNamedList(ListBox list, IEnumerable<(object Tag, long Id, string Name, string Detail)> rows, long? select)
    {
        list.Items.Clear();
        foreach (var (tag, id, name, detail) in rows)
        {
            var panel = new DockPanel();
            var detailText = new TextBlock
            {
                Text = detail,
                Margin = new Thickness(10, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 11,
                Foreground = (Brush)FindResource("FgMuted"),
            };
            DockPanel.SetDock(detailText, Dock.Right);
            panel.Children.Add(detailText);
            panel.Children.Add(new TextBlock
            {
                Text = name,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });

            var item = new ListBoxItem { Content = panel, Tag = tag };
            System.Windows.Automation.AutomationProperties.SetName(item, name + ", " + detail);
            list.Items.Add(item);
            if (id == select) list.SelectedItem = item;
        }
    }

    private void UpdateKnListButtons()
    {
        bool nature = SelectedKnNature is not null;
        KnNatureRenameBtn.IsEnabled = nature;
        KnNatureDeleteBtn.IsEnabled = nature;
        KnNatureReason.Text = nature || _knNatures.Count == 0 ? "" : Loc.T("Config.KnowledgeNaturePickReason");

        bool language = SelectedKnLanguage is not null;
        KnLanguageRenameBtn.IsEnabled = language;
        KnLanguageDeleteBtn.IsEnabled = language;
        KnLanguageReason.Text = language || _knLanguages.Count == 0 ? "" : Loc.T("Config.KnowledgeLanguagePickReason");
    }

    private static void OnKnNamedListKeyDown(KeyEventArgs e, Action rename, Action delete)
    {
        if (e.Key == Key.F2) { e.Handled = true; rename(); }
        else if (e.Key == Key.Delete) { e.Handled = true; delete(); }
    }

    /// <summary>
    /// Pide un nombre para una naturaleza / un idioma NUEVO y avisa si ya existe. El store, ante un
    /// nombre repetido, devuelve el que ya estaba (sirve para "asignar"); en un ALTA explícita eso se
    /// leería como que el botón no hizo nada, así que acá se dice.
    /// </summary>
    private string? PromptKnNewName(string title, IEnumerable<string> existing)
    {
        string? name = PromptDialog.Show(this, title, Loc.T("Config.KnowledgeNameLabel"))?.Trim();
        if (name is null) return null;
        if (name == "")
        {
            ReportKnowledgeResult(KnowledgeOpResult.EmptyName, name);
            return null;
        }
        if (existing.Any(x => string.Equals(x, name, StringComparison.OrdinalIgnoreCase)))
        {
            ReportKnowledgeResult(KnowledgeOpResult.NameTaken, name);
            return null;
        }
        return name;
    }

    private void SelectKnNamed(ListBox list, long id)
    {
        list.SelectedItem = list.Items.OfType<ListBoxItem>().FirstOrDefault(i =>
            (i.Tag as Nature)?.Id == id || (i.Tag as Language)?.Id == id);
        if (list.SelectedItem is not null) list.ScrollIntoView(list.SelectedItem);
        list.Focus();
    }

    private void AddKnNature()
    {
        if (PromptKnNewName(Loc.T("Config.KnowledgeNatureNewTitle"), _knNatures.Select(n => n.Name)) is not { } name) return;
        if (_knowledge.GetOrCreateNature(name) is not { } nature)
        {
            ReportKnowledgeResult(KnowledgeOpResult.Failed, name);
            return;
        }
        RefreshKnowledge();
        SelectKnNamed(KnNatureList, nature.Id);
    }

    private void RenameKnNature()
    {
        if (SelectedKnNature is not { } nature) return;
        string? name = PromptDialog.Show(this, Loc.T("Config.KnowledgeNatureRenameTitle"),
            Loc.T("Config.KnowledgeNameLabel"), nature.Name);
        if (name is null || name.Trim() == nature.Name) return;

        if (!ReportKnowledgeResult(_knowledge.RenameNature(nature.Id, name), name.Trim())) return;
        RefreshKnowledge();
        SelectKnNamed(KnNatureList, nature.Id);
    }

    private void DeleteKnNature()
    {
        if (SelectedKnNature is not { } nature) return;

        // Borrar una naturaleza NO borra contenido, y la confirmación lo dice: lo que la usaba
        // vuelve a heredar. Sin eso, "eliminar" sobre algo que usan 40 entradas asusta de más.
        int entries = _knNatureEntryCounts.TryGetValue(nature.Id, out int e) ? e : 0;
        int libraries = _knNatureLibraryCounts.TryGetValue(nature.Id, out int l) ? l : 0;
        string message = string.Format(Loc.T("Config.KnowledgeNatureDeleteConfirm"), nature.Name, entries, libraries);
        if (MessageBox.Show(this, message, Loc.T("Config.KnowledgeDeleteTitle"), MessageBoxButton.YesNo,
                MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;

        if (!_knowledge.DeleteNature(nature.Id))
        {
            ReportKnowledgeResult(KnowledgeOpResult.Failed, nature.Name);
            return;
        }
        RefreshKnowledge();
        KnNatureList.Focus();
    }

    private void AddKnLanguage()
    {
        if (PromptKnNewName(Loc.T("Config.KnowledgeLanguageNewTitle"), _knLanguages.Select(x => x.Name)) is not { } name) return;
        if (_knowledge.GetOrCreateLanguage(name) is not { } language)
        {
            ReportKnowledgeResult(KnowledgeOpResult.Failed, name);
            return;
        }
        RefreshKnowledge();
        SelectKnNamed(KnLanguageList, language.Id);
    }

    private void RenameKnLanguage()
    {
        if (SelectedKnLanguage is not { } language) return;
        string? name = PromptDialog.Show(this, Loc.T("Config.KnowledgeLanguageRenameTitle"),
            Loc.T("Config.KnowledgeNameLabel"), language.Name);
        if (name is null || name.Trim() == language.Name) return;

        if (!ReportKnowledgeResult(_knowledge.RenameLanguage(language.Id, name), name.Trim())) return;
        RefreshKnowledge();
        SelectKnNamed(KnLanguageList, language.Id);
    }

    private void DeleteKnLanguage()
    {
        if (SelectedKnLanguage is not { } language) return;

        // Acá SÍ se pierde contenido: las traducciones a ese idioma y los repasos jugados desde él.
        int translations = _knLanguageTranslationCounts.TryGetValue(language.Id, out int t) ? t : 0;
        int reviews = _knLanguageReviewCounts.TryGetValue(language.Id, out int r) ? r : 0;
        string message = string.Format(Loc.T("Config.KnowledgeLanguageDeleteConfirm"), language.Name, translations, reviews);
        if (MessageBox.Show(this, message, Loc.T("Config.KnowledgeDeleteTitle"), MessageBoxButton.YesNo,
                MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;

        if (!_knowledge.DeleteLanguage(language.Id))
        {
            ReportKnowledgeResult(KnowledgeOpResult.Failed, language.Name);
            return;
        }
        RefreshKnowledge();
        KnLanguageList.Focus();
    }
}
