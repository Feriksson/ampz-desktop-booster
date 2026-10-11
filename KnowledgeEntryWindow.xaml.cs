using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using AmpzDesktopBooster.Knowledge;
using AmpzDesktopBooster.Services.Localization;

namespace AmpzDesktopBooster;

/// <summary>
/// Alta / edición de una ENTRADA de la biblioteca de conocimiento: una tarjeta (frente, dorso y su
/// glosario de ayuda) o un término (palabra, tipo, definición, una traducción por idioma, contexto de
/// uso y origen).
///
/// Mismo criterio que <see cref="VariableEditWindow"/>: todos los campos en una pantalla (con
/// prompts encadenados no ves el campo anterior ni podés corregir hacia atrás). Tres cosas propias:
///
///   1. El diálogo GUARDA ÉL MISMO contra el store, en vez de devolverle un borrador a quien lo abrió.
///      Hace falta por "Guardar y nueva": cargar veinte términos seguidos es el uso real, y cerrar y
///      reabrir el diálogo por cada uno es la fricción que hace que nadie cargue vocabulario.
///   2. La naturaleza es un combo donde se puede ESCRIBIR: elegir una existente, tipear una nueva (se
///      crea al guardar) o dejar la primera opción, "heredar", que muestra el valor que hereda de
///      verdad. La lista de naturalezas es personal y crece mientras se carga — obligar a ir a otra
///      pantalla a crearla cortaría la carga.
///   3. Enter es salto de línea en los campos largos, así que guardar es Ctrl+Enter. Y Esc no tira lo
///      cargado sin preguntar: un dorso de tres párrafos no se pierde por una tecla.
///
/// Tiene además una puerta de CAPTURA RÁPIDA (<see cref="ShowCapture"/>), que usa el playbooker: se
/// abre con el frente ya escrito —lo que se buscó y no estaba— y, si hay más de una biblioteca, con
/// un selector de dónde guardarla. Desde la config ese selector no aparece: ahí la biblioteca ya está
/// elegida en la pantalla, y ofrecer cambiarla sería una segunda forma de mover entradas.
/// </summary>
public partial class KnowledgeEntryWindow : Window
{
    /// <summary>Qué pasó mientras el diálogo estuvo abierto: cuántas entradas se guardaron (puede
    /// ser más de una, por "Guardar y nueva") y cuál fue la última, para dejarla seleccionada.</summary>
    public readonly record struct Outcome(int SavedCount, long? LastSavedId);

    /// <summary>Una biblioteca del selector de la captura rápida: su id y su camino entero.</summary>
    private sealed record LibraryChoice(long Id, string Label)
    {
        public override string ToString() => Label; // lo que pinta el combo y lo que busca al tipear
    }

    // La última biblioteca elegida en una captura rápida, mientras viva la app: quien carga varias
    // palabras seguidas casi siempre las quiere en la misma. No se persiste (es una comodidad de
    // sesión, igual que el último lado jugado del playbooker).
    private static long? _lastCaptureLibraryId;

    private readonly KnowledgeStore _store;
    private readonly IReadOnlyDictionary<long, Library> _libraries;
    private readonly bool _pickLibrary;
    private long _libraryId; // fija salvo en la captura rápida, donde la cambia el selector
    private readonly EntryKind _kind;
    private readonly bool _adding;

    /// <summary>
    /// La entrada sobre la que se está parado. En una edición, la de siempre. En un alta arranca en
    /// null y pasa a tener valor si la entrada se creó pero sus traducciones no se pudieron guardar:
    /// el reintento tiene que ACTUALIZAR esa entrada, no crear una segunda igual.
    /// </summary>
    private long? _entryId;

    private string _inheritLabel = "";
    private List<Nature> _natures;
    private readonly List<Language> _languages;
    private readonly Dictionary<long, TextBox> _translationBoxes = new();

    /// <summary>Un renglón del glosario de una card: la palabra, su definición y su botón de quitar.</summary>
    private sealed record GlossaryRow(Grid Root, TextBox Term, TextBox Definition, Button Remove);

    private readonly List<GlossaryRow> _glossaryRows = new();
    // Los renglones por los que ya se pasó: recién ahí uno a medias se marca en rojo (mientras se
    // está escribiendo la palabra, que todavía no tenga definición no es un error).
    private readonly HashSet<GlossaryRow> _glossaryTouched = new();

    private string _snapshot = "";
    private bool _attempted;      // ya se intentó guardar: los errores de campo quedan a la vista
    private bool _frontTouched;   // el campo ya tuvo el foco: recién ahí reclama si quedó vacío
    private bool _backTouched;
    private bool _closeConfirmed;
    private int _savedCount;
    private long? _lastSavedId;

    private KnowledgeEntryWindow(KnowledgeStore store, long libraryId, EntryKind kind, Entry? existing,
        string? prefillFront = null, bool pickLibrary = false)
    {
        InitializeComponent();
        _store = store;
        _libraryId = libraryId;
        _kind = kind;
        _adding = existing is null;
        _entryId = existing?.Id;
        _pickLibrary = pickLibrary && existing is null;

        // Nunca más alto que la pantalla: pasado esto, scrollea el formulario (los botones no).
        MaxHeight = SystemParameters.WorkArea.Height * 0.92;

        var libraryList = store.GetLibraries();
        var libraries = libraryList.ToDictionary(l => l.Id);
        _libraries = libraries;
        _natures = store.GetNatures().ToList();
        _languages = store.GetLanguages().ToList();
        bool term = kind == EntryKind.Term;

        HeaderText.Text = Loc.T((_adding, term) switch
        {
            (true, false) => "KnowledgeEntry.TitleNewCard",
            (true, true) => "KnowledgeEntry.TitleNewTerm",
            (false, false) => "KnowledgeEntry.TitleEditCard",
            (false, true) => "KnowledgeEntry.TitleEditTerm",
        });
        LibraryText.Text = string.Format(Loc.T("KnowledgeEntry.SavesIn"),
            string.Join(" › ", LibraryTree.PathOf(libraryId, libraries)));
        if (_pickLibrary) FillLibraryCombo(libraryList);

        // ── Frente / dorso: mismos dos campos, con el nombre que tienen en cada tipo ──
        string frontName = Loc.T(term ? "KnowledgeEntry.FldTerm" : "KnowledgeEntry.FldFront");
        string backName = Loc.T(term ? "KnowledgeEntry.FldDefinition" : "KnowledgeEntry.FldBack");
        FrontLabel.Text = frontName + " *";
        BackLabel.Text = backName + " *";
        AutomationProperties.SetName(FrontBox, frontName);
        AutomationProperties.SetName(BackBox, backName);
        AutomationProperties.SetIsRequiredForForm(FrontBox, true);
        AutomationProperties.SetIsRequiredForForm(BackBox, true);
        if (term)
        {
            // Una palabra es UNA línea: ahí Enter no tiene nada que partir.
            FrontBox.AcceptsReturn = false;
            FrontBox.TextWrapping = TextWrapping.NoWrap;
            FrontBox.MinHeight = 0;
            FrontBox.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
            WordTypePanel.Visibility = Visibility.Visible;
            TermPanel.Visibility = Visibility.Visible;
        }
        else
        {
            // ── Glosario: sólo en una card (la ayuda de un término es su origen) ──
            GlossaryPanel.Visibility = Visibility.Visible;
            GlossaryHint.Text = string.Format(Loc.T("KnowledgeEntry.GlossaryHint"), Glossary.MaxGlossaryItems);
            if (existing is not null)
                foreach (var item in store.GetGlossary(existing.Id)) AddGlossaryRow(item.Term, item.Definition);
            AddGlossaryBtn.Click += (_, _) => AddGlossaryRow("", "", focus: true);
            UpdateGlossaryState();
        }

        // ── Tipo de palabra: primero los que ya usó (los más usados arriba), después los de siempre ──
        foreach (string type in store.GetWordTypes()
                     .Concat(Loc.T("KnowledgeEntry.WordTypeSuggestions").Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
            WordTypeCombo.Items.Add(type);

        // ── Naturaleza: "heredar" dice QUÉ hereda (la de la biblioteca o la de la primera de arriba
        //    que tenga una). Es el valor real, no una promesa genérica. ──
        _inheritLabel = InheritLabelFor(libraryId);
        FillNatureCombo();

        // ── Valores iniciales ──
        FrontBox.Text = existing?.Front ?? prefillFront ?? "";
        BackBox.Text = existing?.Back ?? "";
        WordTypeCombo.Text = existing?.WordType ?? "";
        UsageBox.Text = existing?.UsageContext ?? "";
        OriginBox.Text = existing?.Origin ?? "";
        NatureCombo.Text = existing?.NatureId is { } own && _natures.FirstOrDefault(n => n.Id == own) is { } ownNature
            ? ownNature.Name
            : _inheritLabel;

        var translations = existing is not null && term
            ? store.GetTranslations(existing.Id).ToDictionary(t => t.LanguageId, t => t.Text)
            : new Dictionary<long, string>();
        foreach (var language in _languages)
            AddTranslationRow(language, translations.TryGetValue(language.Id, out var text) ? text : "");
        NoLanguagesText.Visibility = _languages.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        // ── Botones y teclas ──
        SaveNewBtn.Visibility = _adding ? Visibility.Visible : Visibility.Collapsed;
        KeysText.Text = Loc.T(_adding ? "KnowledgeEntry.KeysAdd" : "KnowledgeEntry.KeysEdit");
        SaveBtn.Click += (_, _) => Save(thenNew: false);
        SaveNewBtn.Click += (_, _) => Save(thenNew: true);
        CancelBtn.Click += (_, _) => Close();
        AddLanguageBtn.Click += (_, _) => AddLanguage();

        FrontBox.TextChanged += (_, _) => Revalidate();
        BackBox.TextChanged += (_, _) => Revalidate();
        FrontBox.LostKeyboardFocus += (_, _) => { _frontTouched = true; Revalidate(); };
        BackBox.LostKeyboardFocus += (_, _) => { _backTouched = true; Revalidate(); };

        PreviewKeyDown += OnPreviewKeyDown;
        KeyDown += OnKeyDown;
        Closing += OnClosing;

        _snapshot = Snapshot();
        Revalidate();

        // Foco en el primer campo, con el texto elegido en una edición (lo común es reescribirlo).
        Loaded += (_, _) => FocusFront(selectAll: !_adding);
    }

    /// <summary>Alta de una tarjeta o un término en <paramref name="libraryId"/>.</summary>
    public static Outcome ShowNew(Window owner, KnowledgeStore store, long libraryId, EntryKind kind) =>
        Run(new KnowledgeEntryWindow(store, libraryId, kind, null) { Owner = owner });

    /// <summary>Edición de una entrada existente (no cambia de biblioteca ni de tipo: eso es moverla
    /// o rehacerla, y vive en la config).</summary>
    public static Outcome ShowEdit(Window owner, KnowledgeStore store, Entry entry) =>
        Run(new KnowledgeEntryWindow(store, entry.LibraryId, entry.Kind, entry) { Owner = owner });

    /// <summary>
    /// CAPTURA RÁPIDA desde el playbooker: alta con el frente ya escrito (<paramref name="front"/> es
    /// lo que se buscó y no estaba). Con una sola biblioteca va ahí; con varias, el diálogo trae un
    /// selector, parado en la última que se usó en esta sesión. Sin ninguna biblioteca no hay dónde
    /// guardar y no se abre nada: quien llama ya lo dijo en pantalla.
    /// </summary>
    public static Outcome ShowCapture(Window owner, KnowledgeStore store, EntryKind kind, string front)
    {
        var libraries = TreeOrder(store.GetLibraries());
        if (libraries.Count == 0) return default;

        long libraryId = _lastCaptureLibraryId is { } last && libraries.Any(l => l.Id == last)
            ? last
            : libraries[0].Id;
        return Run(new KnowledgeEntryWindow(store, libraryId, kind, null, front.Trim(), pickLibrary: libraries.Count > 1)
        {
            Owner = owner,
        });
    }

    private static Outcome Run(KnowledgeEntryWindow dialog)
    {
        dialog.ShowDialog();
        return new Outcome(dialog._savedCount, dialog._lastSavedId);
    }

    // ──────────────────────────── Biblioteca (sólo captura rápida) ────────────────────────────

    /// <summary>Las bibliotecas en ORDEN DE ÁRBOL (cada madre seguida de sus hijas), como en la config.</summary>
    private static List<Library> TreeOrder(IReadOnlyList<Library> libraries)
    {
        var children = libraries.ToLookup(l => l.ParentId);
        var ordered = new List<Library>();
        var seen = new HashSet<long>(); // una base tocada a mano con un ciclo no puede girar para siempre
        void Walk(long? parentId)
        {
            foreach (var library in children[parentId].OrderBy(l => l.Sort).ThenBy(l => l.Id))
            {
                if (!seen.Add(library.Id)) continue;
                ordered.Add(library);
                Walk(library.Id);
            }
        }
        Walk(null);
        return ordered;
    }

    private void FillLibraryCombo(IReadOnlyList<Library> libraries)
    {
        foreach (var library in TreeOrder(libraries))
        {
            var choice = new LibraryChoice(library.Id, string.Join(" › ", LibraryTree.PathOf(library.Id, _libraries)));
            LibraryCombo.Items.Add(choice);
            if (library.Id == _libraryId) LibraryCombo.SelectedItem = choice;
        }
        // Con el selector a la vista, el renglón "Se guarda en…" diría lo mismo dos veces.
        LibraryText.Visibility = Visibility.Collapsed;
        LibraryPanel.Visibility = Visibility.Visible;
        LibraryCombo.SelectionChanged += (_, _) => OnLibraryChanged();
    }

    /// <summary>
    /// Cambió la biblioteca de destino. Lo único del formulario que depende de ella es la opción
    /// "heredar" de la naturaleza, que nombra lo que REALMENTE se hereda: si estaba elegida, sigue
    /// elegida con el valor de la biblioteca nueva; si se había fijado otra naturaleza, queda.
    /// </summary>
    private void OnLibraryChanged()
    {
        if (LibraryCombo.SelectedItem is not LibraryChoice choice || choice.Id == _libraryId) return;

        // Si no había nada sin guardar, el cambio de rótulo de acá abajo no puede fabricarlo: sin
        // esto, elegir otra biblioteca y arrepentirse con Esc preguntaría "¿descartar?" sobre nada.
        bool clean = Snapshot() == _snapshot;
        bool inheriting = string.Equals(NatureCombo.Text.Trim(), _inheritLabel, StringComparison.OrdinalIgnoreCase);

        _libraryId = choice.Id;
        _inheritLabel = InheritLabelFor(choice.Id);
        string typed = NatureCombo.Text;
        FillNatureCombo();
        NatureCombo.Text = inheriting ? _inheritLabel : typed;
        if (clean) _snapshot = Snapshot();
    }

    private string InheritLabelFor(long libraryId)
    {
        string? inherited = LibraryTree.EffectiveNatureId(libraryId, _libraries) is { } inheritedId
            ? _natures.FirstOrDefault(n => n.Id == inheritedId)?.Name
            : null;
        return inherited is null
            ? Loc.T("KnowledgeEntry.NatureInheritNone")
            : string.Format(Loc.T("KnowledgeEntry.NatureInherit"), inherited);
    }

    // ──────────────────────────── Naturaleza e idiomas ────────────────────────────

    private void FillNatureCombo()
    {
        NatureCombo.Items.Clear();
        NatureCombo.Items.Add(_inheritLabel);
        foreach (var nature in _natures) NatureCombo.Items.Add(nature.Name);
    }

    /// <summary>
    /// Lo que quedó escrito en el combo, llevado a un id. Vacío o la opción "heredar" = null (hereda).
    /// Un nombre que no existe se CREA acá, al guardar — nunca antes: tipear y arrepentirse no deja
    /// naturalezas huérfanas. Devuelve false sólo si hubo que crearla y la base no respondió.
    /// </summary>
    private bool TryResolveNature(out long? natureId)
    {
        natureId = null;
        string text = NatureCombo.Text.Trim();
        if (text == "" || string.Equals(text, _inheritLabel, StringComparison.OrdinalIgnoreCase)) return true;

        var nature = _natures.FirstOrDefault(n => string.Equals(n.Name, text, StringComparison.OrdinalIgnoreCase))
                     ?? _store.GetOrCreateNature(text);
        if (nature is null) return false;

        natureId = nature.Id;
        if (_natures.All(n => n.Id != nature.Id))
        {
            // Recién creada: entra a la lista del combo para la próxima de esta misma tanda.
            _natures.Add(nature);
            _natures = _natures.OrderBy(n => n.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            FillNatureCombo();
        }
        NatureCombo.Text = nature.Name;
        return true;
    }

    private void AddTranslationRow(Language language, string text)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var label = new TextBlock
        {
            Text = language.Name,
            Style = (Style)FindResource("Lbl"),
            FontSize = 13,
            Margin = new Thickness(0, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = language.Name,
        };
        var box = new TextBox { Text = text, Style = (Style)FindResource("Field") };
        AutomationProperties.SetName(box, string.Format(Loc.T("KnowledgeEntry.TranslationAccessible"), language.Name));
        Grid.SetColumn(box, 1);

        row.Children.Add(label);
        row.Children.Add(box);
        TranslationsPanel.Children.Add(row);
        _translationBoxes[language.Id] = box;
    }

    /// <summary>
    /// "+ idioma": suma un idioma a la lista y deja su campo listo para escribir, sin salir del
    /// diálogo. El idioma se crea EN EL MOMENTO (no al guardar): es un dato de la lista del usuario,
    /// vale aunque después cancele esta entrada, y así el campo nuevo ya tiene a qué id apuntar.
    /// </summary>
    private void AddLanguage()
    {
        string? name = PromptDialog.Show(this, Loc.T("KnowledgeEntry.AddLanguageTitle"),
            Loc.T("KnowledgeEntry.AddLanguageLabel"));
        name = name?.Trim();
        if (string.IsNullOrEmpty(name)) return;

        var language = _languages.FirstOrDefault(l => string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase));
        if (language is null)
        {
            language = _store.GetOrCreateLanguage(name);
            if (language is null)
            {
                ShowNotice(Loc.T("KnowledgeEntry.LanguageFailed"), error: true);
                return;
            }
            _languages.Add(language);
            AddTranslationRow(language, "");
            NoLanguagesText.Visibility = Visibility.Collapsed;
        }

        // Existía o es nuevo: en los dos casos el próximo paso es escribir ahí.
        UpdateLayout();
        var box = _translationBoxes[language.Id];
        box.BringIntoView();
        box.Focus();
        Keyboard.Focus(box);
    }

    // ──────────────────────────── Glosario (sólo cards) ────────────────────────────

    private void AddGlossaryRow(string term, string definition, bool focus = false)
    {
        if (_glossaryRows.Count >= Glossary.MaxGlossaryItems) return;

        // Mismas columnas que los rótulos de arriba (GlossaryHeaders): palabra · definición · quitar.
        var root = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(180) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(86) });

        var termBox = new TextBox { Text = term, Style = (Style)FindResource("Field"), VerticalAlignment = VerticalAlignment.Top };
        // La definición ENVUELVE (una frase no entra en un renglón de este ancho, y editar un texto
        // que no se ve entero es editar a ciegas). Sigue siendo UNA frase: Enter no parte línea, y
        // pasadas unas pocas líneas scrollea adentro en vez de estirar el diálogo.
        var definitionBox = new TextBox
        {
            Text = definition, Style = (Style)FindResource("Field"),
            TextWrapping = TextWrapping.Wrap, MaxHeight = 84, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        var remove = new Button
        {
            Content = Loc.T("KnowledgeEntry.GlossaryRemove"), VerticalAlignment = VerticalAlignment.Top,
            MinWidth = 0, Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(8, 0, 0, 0),
        };
        Grid.SetColumn(definitionBox, 2);
        Grid.SetColumn(remove, 3);
        root.Children.Add(termBox);
        root.Children.Add(definitionBox);
        root.Children.Add(remove);

        var row = new GlossaryRow(root, termBox, definitionBox, remove);
        _glossaryRows.Add(row);
        GlossaryRows.Children.Add(root);

        termBox.TextChanged += (_, _) => Revalidate();
        definitionBox.TextChanged += (_, _) => Revalidate();
        // "Se pasó por el renglón" = el foco salió de él (no cuando salta de la palabra a su definición).
        root.IsKeyboardFocusWithinChanged += (_, e) =>
        {
            if ((bool)e.NewValue) return;
            _glossaryTouched.Add(row);
            Revalidate();
        };
        remove.Click += (_, _) => RemoveGlossaryRow(row);

        UpdateGlossaryState();
        Revalidate();
        if (!focus) return;
        UpdateLayout();
        termBox.BringIntoView();
        termBox.Focus();
        Keyboard.Focus(termBox);
    }

    private void RemoveGlossaryRow(GlossaryRow row)
    {
        int index = _glossaryRows.IndexOf(row);
        if (index < 0) return;
        _glossaryRows.RemoveAt(index);
        _glossaryTouched.Remove(row);
        GlossaryRows.Children.Remove(row.Root);
        UpdateGlossaryState();
        Revalidate();

        // El botón que tenía el foco acaba de desaparecer: va al renglón que ocupó su lugar o, si
        // era el último, al botón de sumar (que tras quitar uno siempre está habilitado).
        UpdateLayout();
        IInputElement next = index < _glossaryRows.Count ? _glossaryRows[index].Term : AddGlossaryBtn;
        next.Focus();
        Keyboard.Focus(next);
    }

    /// <summary>Lo que depende de CUÁNTOS renglones hay: los rótulos de columna, el nombre accesible
    /// de cada campo (lleva su número), y el botón de sumar con su motivo cuando ya no se puede.</summary>
    private void UpdateGlossaryState()
    {
        int max = Glossary.MaxGlossaryItems;
        for (int i = 0; i < _glossaryRows.Count; i++)
        {
            var row = _glossaryRows[i];
            AutomationProperties.SetName(row.Term, string.Format(Loc.T("KnowledgeEntry.GlossaryTermAccessible"), i + 1));
            AutomationProperties.SetName(row.Definition, string.Format(Loc.T("KnowledgeEntry.GlossaryDefinitionAccessible"), i + 1));
            AutomationProperties.SetName(row.Remove, string.Format(Loc.T("KnowledgeEntry.GlossaryRemoveAccessible"), i + 1));
        }

        bool full = _glossaryRows.Count >= max;
        GlossaryHeaders.Visibility = _glossaryRows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        AddGlossaryBtn.IsEnabled = !full;
        GlossaryCountText.Text = full
            ? string.Format(Loc.T("KnowledgeEntry.GlossaryFullReason"), max)
            : string.Format(Loc.T("KnowledgeEntry.GlossaryCount"), _glossaryRows.Count, max);
        AutomationProperties.SetHelpText(AddGlossaryBtn, full ? GlossaryCountText.Text : "");
    }

    /// <summary>Los renglones con algo escrito, tal cual están (el store los recorta y ordena).</summary>
    private List<GlossaryItem> GlossaryItems() => _glossaryRows
        .Where(r => r.Term.Text.Trim() != "" || r.Definition.Text.Trim() != "")
        .Select(r => new GlossaryItem(r.Term.Text, r.Definition.Text))
        .ToList();

    /// <summary>
    /// Qué le impide guardarse al glosario, o null si está bien. Dos cosas, y las dos son las que el
    /// store resolvería EN SILENCIO tirando el renglón: uno a medias (palabra sin definición, o al
    /// revés) y una palabra repetida. Perder lo tipeado sin avisar es peor que frenar el guardado.
    /// Un renglón entero en blanco no es un problema: simplemente no se guarda.
    /// </summary>
    private (string Message, GlossaryRow Row, TextBox Box)? GlossaryProblem()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in _glossaryRows)
        {
            string term = row.Term.Text.Trim(), definition = row.Definition.Text.Trim();
            if (term == "" && definition == "") continue;
            if (term == "") return (Loc.T("KnowledgeEntry.GlossaryErrIncomplete"), row, row.Term);
            if (definition == "") return (Loc.T("KnowledgeEntry.GlossaryErrIncomplete"), row, row.Definition);
            if (!seen.Add(term)) return (string.Format(Loc.T("KnowledgeEntry.GlossaryErrDuplicate"), term), row, row.Term);
        }
        return null;
    }

    // ──────────────────────────── Validación ────────────────────────────

    private bool FrontMissing => string.IsNullOrWhiteSpace(FrontBox.Text);
    private bool BackMissing => string.IsNullOrWhiteSpace(BackBox.Text);

    /// <summary>
    /// Recalcula todo lo que depende de si el formulario se puede guardar: el error de cada campo
    /// obligatorio (texto al lado del campo, además del borde), el botón, y el motivo a la vista
    /// cuando está apagado. Un campo recién reclama cuando ya pasaste por él o ya intentaste guardar:
    /// un formulario que abre gritando errores sobre campos que nadie tocó todavía es ruido.
    /// </summary>
    private void Revalidate()
    {
        bool term = _kind == EntryKind.Term;
        SetFieldError(FrontBox, FrontError, FrontMissing && (_attempted || _frontTouched),
            Loc.T(term ? "KnowledgeEntry.ErrTermRequired" : "KnowledgeEntry.ErrFrontRequired"));
        SetFieldError(BackBox, BackError, BackMissing && (_attempted || _backTouched),
            Loc.T(term ? "KnowledgeEntry.ErrDefinitionRequired" : "KnowledgeEntry.ErrBackRequired"));

        // El glosario: el campo en falta se marca (y el error se escribe debajo) recién cuando ya se
        // pasó por ese renglón o se intentó guardar; el botón, en cambio, se apaga enseguida.
        var glossaryProblem = GlossaryProblem();
        bool showGlossaryError = glossaryProblem is { } shown && (_attempted || _glossaryTouched.Contains(shown.Row));
        foreach (var row in _glossaryRows)
        {
            row.Term.Tag = showGlossaryError && ReferenceEquals(glossaryProblem!.Value.Box, row.Term) ? "invalid" : null;
            row.Definition.Tag = showGlossaryError && ReferenceEquals(glossaryProblem!.Value.Box, row.Definition) ? "invalid" : null;
        }
        GlossaryError.Text = glossaryProblem?.Message ?? "";
        GlossaryError.Visibility = showGlossaryError ? Visibility.Visible : Visibility.Collapsed;

        bool valid = !FrontMissing && !BackMissing && glossaryProblem is null;
        SaveBtn.IsEnabled = valid;
        SaveNewBtn.IsEnabled = valid;

        var missing = new List<string>();
        if (FrontMissing) missing.Add(Loc.T(term ? "KnowledgeEntry.MissingTerm" : "KnowledgeEntry.MissingFront"));
        if (BackMissing) missing.Add(Loc.T(term ? "KnowledgeEntry.MissingDefinition" : "KnowledgeEntry.MissingBack"));
        if (glossaryProblem is not null) missing.Add(Loc.T("KnowledgeEntry.MissingGlossary"));
        ReasonText.Text = valid
            ? ""
            : string.Format(Loc.T("KnowledgeEntry.SaveDisabledReason"), string.Join(Loc.T("KnowledgeEntry.MissingJoin"), missing));
    }

    private static void SetFieldError(TextBox box, TextBlock error, bool show, string message)
    {
        box.Tag = show ? "invalid" : null;
        error.Text = message;
        error.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        // El lector de pantalla lee el error junto con el campo, no como un texto suelto de más abajo.
        AutomationProperties.SetHelpText(box, show ? message : "");
    }

    // ──────────────────────────── Guardar ────────────────────────────

    /// <summary>
    /// Guarda la entrada (y, en un término, sus traducciones; en una card, su glosario). <paramref name="thenNew"/> deja el
    /// diálogo abierto y limpio para la siguiente, conservando biblioteca, tipo y naturaleza.
    /// Si no se puede guardar, NUNCA se queda mudo: marca qué falta y lleva el foco ahí, o dice que
    /// la base no respondió — y lo cargado queda en pantalla.
    /// </summary>
    private void Save(bool thenNew)
    {
        if (FrontMissing || BackMissing || GlossaryProblem() is not null)
        {
            _attempted = true;
            Revalidate();
            var first = FrontMissing ? FrontBox : BackMissing ? BackBox : GlossaryProblem()!.Value.Box;
            first.BringIntoView();
            first.Focus();
            Keyboard.Focus(first);
            return;
        }

        if (!TryResolveNature(out long? natureId))
        {
            ShowNotice(Loc.T("KnowledgeEntry.SaveFailed"), error: true);
            return;
        }

        bool term = _kind == EntryKind.Term;
        var draft = new EntryDraft(_kind, FrontBox.Text, BackBox.Text, natureId,
            term ? WordTypeCombo.Text : "", term ? UsageBox.Text : "", term ? OriginBox.Text : "");

        if (_entryId is { } id)
        {
            if (!_store.UpdateEntry(id, draft))
            {
                ShowNotice(Loc.T("KnowledgeEntry.SaveFailed"), error: true);
                return;
            }
        }
        else
        {
            if (_store.CreateEntry(_libraryId, draft) is not { } created)
            {
                ShowNotice(Loc.T("KnowledgeEntry.SaveFailed"), error: true);
                return;
            }
            _entryId = id = created;
        }

        // La entrada YA está guardada: desde acá cuenta, aunque las traducciones fallen.
        _savedCount++;
        _lastSavedId = id;
        if (_pickLibrary) _lastCaptureLibraryId = _libraryId;

        if (term)
        {
            var translations = _translationBoxes.ToDictionary(p => p.Key, p => p.Value.Text);
            if (!_store.SetTranslations(id, translations))
            {
                // Media ficha guardada. Se dice tal cual, y el diálogo queda sobre ESA entrada: el
                // reintento actualiza, no duplica.
                _snapshot = "";
                ShowNotice(Loc.T("KnowledgeEntry.TranslationsFailed"), error: true);
                return;
            }
        }
        else if (!_store.SetGlossary(id, GlossaryItems()))
        {
            // Lo mismo para una card y su glosario: la card quedó, su ayuda no. Se dice y el diálogo
            // sigue sobre ESA card, con los renglones en pantalla para reintentar.
            _snapshot = "";
            ShowNotice(Loc.T("KnowledgeEntry.GlossaryFailed"), error: true);
            return;
        }

        if (!thenNew)
        {
            _closeConfirmed = true;
            Close();
            return;
        }

        string saved = FrontBox.Text.Trim();
        ResetForNext();
        ShowNotice(string.Format(Loc.T("KnowledgeEntry.SavedNext"), Ellipsize(saved, 60), _savedCount), error: false);
    }

    /// <summary>Deja el formulario listo para la siguiente entrada de la MISMA tanda: se conserva
    /// lo que casi siempre se repite (biblioteca, tipo y naturaleza) y se limpia el resto.</summary>
    private void ResetForNext()
    {
        _entryId = null;
        FrontBox.Clear();
        BackBox.Clear();
        WordTypeCombo.Text = "";
        UsageBox.Clear();
        OriginBox.Clear();
        foreach (var box in _translationBoxes.Values) box.Clear();
        // El glosario es de ESTA card: la siguiente arranca sin renglones.
        _glossaryRows.Clear();
        _glossaryTouched.Clear();
        GlossaryRows.Children.Clear();
        if (_kind == EntryKind.Card) UpdateGlossaryState();

        _attempted = _frontTouched = _backTouched = false;
        _snapshot = Snapshot();
        Revalidate();
        FormScroll.ScrollToTop();
        FocusFront(selectAll: false);
    }

    private void FocusFront(bool selectAll)
    {
        FrontBox.Focus();
        Keyboard.Focus(FrontBox);
        if (selectAll) FrontBox.SelectAll();
        else FrontBox.CaretIndex = FrontBox.Text.Length;
    }

    private void ShowNotice(string text, bool error)
    {
        NoticeText.Text = text;
        NoticeBox.BorderBrush = (System.Windows.Media.Brush)FindResource(error ? "ErrorText" : "Accent");
        NoticeBox.Visibility = Visibility.Visible;
        // Que un lector de pantalla lo anuncie: aparece lejos del foco.
        AutomationProperties.SetLiveSetting(NoticeText, AutomationLiveSetting.Polite);
    }

    private static string Ellipsize(string text, int max)
    {
        text = text.ReplaceLineEndings(" ");
        return text.Length <= max ? text : text[..max].TrimEnd() + "…";
    }

    // ──────────────────────────── Teclas y cierre ────────────────────────────

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Ctrl+Enter / Ctrl+Shift+Enter. En PREVIEW (antes que el control con foco): un campo de
        // varias líneas se quedaría el Enter para hacer un salto de línea.
        if (e.Key is not (Key.Enter or Key.Return)) return;
        var modifiers = Keyboard.Modifiers;
        if ((modifiers & ModifierKeys.Control) == 0) return;

        e.Handled = true;
        Save(thenNew: _adding && (modifiers & ModifierKeys.Shift) != 0);
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        // Esc BURBUJEANTE y no en preview: si un combo tiene la lista abierta, el primer Esc es suyo
        // (la cierra y lo marca manejado) y recién el siguiente llega acá.
        if (e.Key != Key.Escape || e.Handled) return;
        e.Handled = true;
        Close();
    }

    /// <summary>
    /// La guardia de TODAS las salidas (Esc, Cancelar, Alt+F4): con algo cargado sin guardar, pregunta
    /// antes de tirarlo. Sin cambios cierra directo — preguntar "¿descartar?" sobre nada es ruido.
    /// </summary>
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_closeConfirmed || Snapshot() == _snapshot) return;

        var answer = MessageBox.Show(this, Loc.T("KnowledgeEntry.DiscardConfirm"),
            Loc.T("KnowledgeEntry.DiscardTitle"), MessageBoxButton.YesNo, MessageBoxImage.Question,
            MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) e.Cancel = true;
    }

    /// <summary>Todo lo editable, en un texto: comparar dos de éstos dice si hay algo sin guardar.</summary>
    private string Snapshot() => string.Join("\u001f",
        new[] { FrontBox.Text, BackBox.Text, WordTypeCombo.Text, UsageBox.Text, OriginBox.Text, NatureCombo.Text }
            // Sólo las traducciones con texto: sumar un idioma con "+ idioma" agrega un campo vacío,
            // y eso solo no es "tener algo sin guardar".
            .Concat(_translationBoxes.Where(p => p.Value.Text != "").OrderBy(p => p.Key)
                .Select(p => p.Key + "=" + p.Value.Text))
            // Igual con el glosario: un renglón recién sumado y todavía en blanco no cuenta.
            .Concat(_glossaryRows.Where(r => r.Term.Text != "" || r.Definition.Text != "")
                .Select(r => "g:" + r.Term.Text + "\u001e" + r.Definition.Text)));
}
