using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AmpzDesktopBooster.Knowledge;
using AmpzDesktopBooster.Services.Localization;

namespace AmpzDesktopBooster;

// ── Playbooker · 1 · BÚSQUEDA ────────────────────────────────────────────────────────────────────
//
// La lista de la búsqueda mezcla cuatro clases de fila, y cada una hace otra cosa con Enter:
//
//   • biblioteca / naturaleza / «Vocabulario» → se JUEGA (pasa al armado);
//   • «Con un problema anotado»               → se REVISA (sus entradas, como fichas);
//   • una entrada (término o card)            → se LEE (pasa a su ficha);
//   • «Crear término / card "texto"…»         → se CARGA, con lo tipeado ya puesto;
//   • rótulos de grupo y avisos               → nada: no se pueden elegir.
//
// Con el buscador vacío se ve todo lo jugable (Vocabulario, el árbol de bibliotecas, las naturalezas).
// Con texto se suman, en su propio grupo, las ENTRADAS que coinciden: la biblioteca dejó de ser sólo
// algo que se juega y pasó a ser también algo que se consulta — "¿cómo era esa palabra?".
public partial class PlaybookWindow
{
    /// <summary>Tope del grupo de entradas. Con más coincidencias que esto la lista deja de ser
    /// algo que se recorre: se avisa que hay más y se invita a afinar la búsqueda.</summary>
    private const int EntryResultCap = 50;

    private enum PickKind { Library, Nature, Vocabulary, Issues }

    /// <summary>
    /// Base de toda fila de la lista. <see cref="Selectable"/> false = rótulo o aviso: se ve, pero
    /// ni las flechas ni el mouse paran ahí (lo aplica el estilo del contenedor y las teclas de acá).
    /// </summary>
    private abstract class ListRow
    {
        public string AccessibleName { get; init; } = "";
        public virtual bool Selectable => true;
    }

    /// <summary>
    /// Algo que se JUEGA: una biblioteca (con todo su subárbol), una naturaleza o el vocabulario
    /// entero. Trae ya resuelto lo que pinta la plantilla — incluidas las Visibility — para que el
    /// XAML no necesite convertidores ni lógica.
    /// </summary>
    private sealed class PickRow : ListRow
    {
        public PickKind Kind { get; init; }
        /// <summary>Sólo en una biblioteca: su id, el de su madre y cuántas hijas directas tiene.</summary>
        public long LibraryId { get; init; }
        public long? ParentLibraryId { get; init; }
        public int ChildCount { get; init; }
        public int Depth { get; init; }
        /// <summary>"Madre › " (vacío en una raíz, una naturaleza o el vocabulario): los ancestros, atenuados.</summary>
        public string PathPrefix { get; init; } = "";
        public string Name { get; init; } = "";
        /// <summary>El chip que dice qué es cuando no es una biblioteca ("Naturaleza", "Vocabulario").</summary>
        public string ChipText { get; init; } = "";
        public Brush ChipBrush { get; init; } = Brushes.Transparent;
        /// <summary>Lo que se ve en la columna del conteo. En una biblioteca PLEGADA suma cuántas
        /// tiene adentro; por eso lo recalcula ApplyFilter a partir de <see cref="BaseCountText"/>.</summary>
        public string CountText { get; set; } = "";
        public string BaseCountText { get; init; } = "";
        /// <summary>"▸" plegada · "▾" abierta · "" sin hijas o fuera del árbol. Lo pone ApplyFilter.</summary>
        public string Disclosure { get; set; } = "";
        public double DisclosureWidth { get; set; }
        public SolidityAverage Average { get; init; }
        public string SolidityText { get; init; } = "";
        public double BarFillWidth { get; init; }
        public Visibility BarVisibility { get; init; }
        public Visibility ChipVisibility => ChipText == "" ? Visibility.Collapsed : Visibility.Visible;
        /// <summary>Texto ya normalizado (sin tildes, minúsculas) contra el que se filtra.</summary>
        public string Haystack { get; init; } = "";
        public IReadOnlyList<long> EntryIds { get; init; } = Array.Empty<long>();
        /// <summary>Sangría del árbol. Sólo con el buscador vacío: filtrada, la lista es plana.</summary>
        public Thickness Indent { get; set; }
        public string FullName => PathPrefix + Name;
        /// <summary>Cómo se la nombra en el encabezado mientras se juega.</summary>
        public string Title => ChipText == "" || Kind == PickKind.Nature ? FullName : ChipText + " · " + Name;
    }

    /// <summary>Una ENTRADA encontrada por el buscador. Enter abre su ficha.</summary>
    private sealed class EntryRow : ListRow
    {
        public required Entry Entry { get; init; }
        public string KindText { get; init; } = "";
        public string Front { get; init; } = "";
        /// <summary>El comienzo del dorso, en una línea, ya con su separador delante ("" si no hay).</summary>
        public string Preview { get; init; } = "";
        public string PathText { get; init; } = "";
        public string SolidityText { get; init; } = "";
    }

    /// <summary>"Crear término «texto»…": la salida cuando lo buscado no está.</summary>
    private sealed class CreateRow : ListRow
    {
        public EntryKind Kind { get; init; }
        public string Text { get; init; } = "";
    }

    /// <summary>Un rótulo de grupo o un aviso dentro de la lista. No se elige.</summary>
    private sealed class InfoRow : ListRow
    {
        public string Text { get; init; } = "";
        public FontWeight Weight { get; init; } = FontWeights.Normal;
        public override bool Selectable => false;
    }

    /// <summary>Elige la plantilla de cada fila por su clase. En code-behind porque las filas son
    /// clases privadas de la ventana, y XAML no puede nombrar un tipo anidado.</summary>
    private sealed class RowTemplates : DataTemplateSelector
    {
        public DataTemplate? Pick { get; init; }
        public DataTemplate? Entry { get; init; }
        public DataTemplate? Create { get; init; }
        public DataTemplate? Info { get; init; }

        public override DataTemplate? SelectTemplate(object item, DependencyObject container) => item switch
        {
            EntryRow => Entry,
            CreateRow => Create,
            InfoRow => Info,
            _ => Pick,
        };
    }

    // Las entradas que mostró la última búsqueda, en su orden: es por donde se mueven Q / W en la ficha.
    private List<Entry> _entryResults = new();

    // Las bibliotecas PLEGADAS (ver LoadData). Con el buscador vacío sus hijas no se listan; al
    // buscar se ignora — una coincidencia nunca queda escondida dentro de una rama cerrada.
    private HashSet<long> _collapsedLibraries = new();
    private const double DisclosureColumn = 26;

    private void InitBrowse()
    {
        PickList.ItemTemplateSelector = new RowTemplates
        {
            Pick = (DataTemplate)FindResource("PickRowTemplate"),
            Entry = (DataTemplate)FindResource("EntryRowTemplate"),
            Create = (DataTemplate)FindResource("CreateRowTemplate"),
            Info = (DataTemplate)FindResource("InfoRowTemplate"),
        };
        SearchBox.TextChanged += (_, _) => ApplyFilter();
        PickList.PreviewTextInput += OnListTextInput;
        PickList.PreviewMouseLeftButtonUp += OnListMouseUp;
        StatsBtn.Click += (_, _) => ShowStats(reload: false);
    }

    private void FocusSearchBox()
    {
        SearchBox.Focus();
        Keyboard.Focus(SearchBox);
        SearchBox.CaretIndex = SearchBox.Text.Length;
    }

    /// <summary>
    /// Buscador vacío = TODO lo jugable: «Vocabulario», las bibliotecas como árbol con sangría y,
    /// después, las naturalezas. Con texto = lista plana de lo jugable que coincide (sin tildes ni
    /// mayúsculas, todas las palabras en cualquier orden) y, debajo, en su grupo, las ENTRADAS que
    /// coinciden. <paramref name="selectEntryId"/> deja marcada (y enfocada) la fila de esa entrada:
    /// es como se vuelve de una ficha al mismo lugar de la lista; devuelve si la encontró (y entonces
    /// el foco ya quedó en ella).
    /// </summary>
    private bool ApplyFilter(long? selectEntryId = null)
    {
        SearchPlaceholder.Visibility = SearchBox.Text == "" ? Visibility.Visible : Visibility.Collapsed;

        string typed = SearchBox.Text.Trim();
        var words = TextFold.Fold(typed).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        bool filtering = words.Length > 0;

        var visible = new List<ListRow>();
        var playable = filtering
            ? _rows.Where(r => words.All(w => r.Haystack.Contains(w, StringComparison.Ordinal))).ToList()
            : VisibleTreeRows();
        foreach (var row in playable)
        {
            bool inTree = !filtering && row.Kind == PickKind.Library;
            bool collapsed = inTree && row.ChildCount > 0 && _collapsedLibraries.Contains(row.LibraryId);
            row.Indent = new Thickness(inTree ? row.Depth * IndentPerLevel : 0, 0, 0, 0);
            row.DisclosureWidth = inTree ? DisclosureColumn : 0;
            row.Disclosure = !inTree || row.ChildCount == 0 ? "" : collapsed ? "▸" : "▾";
            // Plegada, dice cuántas bibliotecas tiene adentro (el conteo de entradas ya las incluía).
            // Sólo las bibliotecas tienen las dos versiones del conteo; el resto no se toca.
            if (row.Kind == PickKind.Library) row.CountText = collapsed
                ? (row.ChildCount == 1
                      ? Loc.T("Playbook.CollapsedOne")
                      : string.Format(Loc.T("Playbook.CollapsedMany"), row.ChildCount)) + " · " + row.BaseCountText
                : row.BaseCountText;
        }
        visible.AddRange(playable);

        _entryResults = new List<Entry>();
        if (filtering)
        {
            AddEntryResults(visible, typed);
            // Nada coincide, ni jugable ni entrada: lo más probable es que sea algo que todavía no
            // está cargado. Se ofrece cargarlo ahí mismo, con lo tipeado ya puesto — salvo que no
            // haya ninguna biblioteca donde guardarlo (eso lo explica el estado vacío de abajo).
            if (visible.Count == 0 && _libraries.Count > 0) AddCreateRows(visible, typed);
        }

        // Lista nueva cada vez: las filas no notifican cambios, y reasignar el origen es lo que hace
        // que la plantilla relea la sangría.
        PickList.ItemsSource = visible;

        // Queda marcada la que se pidió o, si no, la primera que se puede elegir: Enter desde el
        // buscador elige lo que se ve resaltado.
        int wanted = selectEntryId is { } id
            ? visible.FindIndex(r => r is EntryRow entry && entry.Entry.Id == id)
            : -1;
        PickList.SelectedIndex = wanted >= 0 ? wanted : visible.FindIndex(r => r.Selectable);

        if (visible.Count > 0)
        {
            EmptyText.Visibility = Visibility.Collapsed;
        }
        else
        {
            // Dos vacíos distintos: todavía no hay NINGUNA biblioteca (se arregla creándola en la
            // config, y hasta entonces tampoco hay dónde guardar lo que se tipeó) vs. no hay nada
            // cargado todavía dentro de las que hay.
            EmptyText.Text = _libraries.Count == 0
                ? Loc.T(filtering ? "Playbook.CreateNeedsLibrary" : "Playbook.EmptyNoLibraries")
                : string.Format(Loc.T("Playbook.NoResults"), typed);
            EmptyText.Visibility = Visibility.Visible;
        }

        if (wanted >= 0) FocusRow(wanted);
        return wanted >= 0;
    }

    /// <summary>
    /// Las filas del árbol que se VEN con el buscador vacío: todas, menos las bibliotecas que cuelgan
    /// de una plegada. <see cref="_rows"/> ya viene en orden de árbol (cada madre seguida de las
    /// suyas), así que alcanza con saltear lo que sea más profundo que la última plegada.
    /// </summary>
    private List<PickRow> VisibleTreeRows()
    {
        var rows = new List<PickRow>();
        int? hiddenBelow = null; // la profundidad de la biblioteca plegada bajo la que se está
        foreach (var row in _rows)
        {
            if (row.Kind != PickKind.Library)
            {
                hiddenBelow = null;
                rows.Add(row);
                continue;
            }
            if (hiddenBelow is { } depth && row.Depth > depth) continue;
            hiddenBelow = _collapsedLibraries.Contains(row.LibraryId) && row.ChildCount > 0 ? row.Depth : null;
            rows.Add(row);
        }
        return rows;
    }

    /// <summary>¿La fila es una biblioteca del ÁRBOL (buscador vacío)? Sólo ahí se pliega.</summary>
    private bool IsTreeRow(ListRow? row) =>
        row is PickRow { Kind: PickKind.Library } && SearchBox.Text.Trim() == "";

    /// <summary>
    /// Pliega o despliega una biblioteca de la lista y lo GUARDA: es el mismo estado que muestra el
    /// árbol de la config. Si la base no lo guarda vale igual por esta sesión (es cómo se mira la
    /// lista, no un dato que se pierda). La fila queda elegida y con el foco.
    /// </summary>
    private void SetRowCollapsed(PickRow row, bool collapsed)
    {
        if (row.ChildCount == 0) return;
        _store.SetLibraryCollapsed(row.LibraryId, collapsed);
        if (collapsed) _collapsedLibraries.Add(row.LibraryId);
        else _collapsedLibraries.Remove(row.LibraryId);
        ApplyFilter();
        FocusRow(PickList.Items.IndexOf(row));
    }

    /// <summary>
    /// ← → sobre una biblioteca del árbol, como en cualquier árbol (y como en la config): ← la pliega
    /// o, si ya lo está o no tiene hijas, sube a su madre; → la despliega o, si ya está abierta, baja
    /// a la primera hija. Devuelve si la tecla era para esto.
    /// </summary>
    private bool OnTreeArrow(Key key)
    {
        if (PickList.SelectedItem is not PickRow row || !IsTreeRow(row)) return false;
        bool open = row.ChildCount > 0 && !_collapsedLibraries.Contains(row.LibraryId);
        if (key == Key.Left)
        {
            if (open) SetRowCollapsed(row, true);
            else if (row.ParentLibraryId is { } parent)
                FocusRow(PickList.Items.Cast<object>().ToList()
                    .FindIndex(r => r is PickRow { Kind: PickKind.Library } p && p.LibraryId == parent));
        }
        else if (row.ChildCount > 0)
        {
            if (!open) SetRowCollapsed(row, false);
            else FocusRow(PickList.SelectedIndex + 1); // abierta: la fila de abajo es su primera hija
        }
        return true;
    }

    /// <summary>
    /// Suma a la lista las entradas que coinciden: primero los términos, después las cards, hasta
    /// <see cref="EntryResultCap"/>. Busca el store (frente, dorso y traducciones, sin tildes ni
    /// mayúsculas): son dos consultas por tecla, una por tipo, para que el tope no deje afuera
    /// términos por haber muchas cards — o al revés.
    /// </summary>
    private void AddEntryResults(List<ListRow> visible, string typed)
    {
        var terms = _store.SearchEntries(typed, EntryKind.Term, EntryResultCap + 1);
        var cards = _store.SearchEntries(typed, EntryKind.Card, EntryResultCap + 1);
        var found = terms.Concat(cards).ToList();
        if (found.Count == 0) return;

        bool more = found.Count > EntryResultCap;
        _entryResults = found.Take(EntryResultCap).ToList();

        visible.Add(new InfoRow
        {
            Text = Loc.T("Playbook.EntriesHeading").ToUpper(System.Globalization.CultureInfo.CurrentCulture),
            Weight = FontWeights.Bold,
            AccessibleName = Loc.T("Playbook.EntriesHeading"),
        });
        visible.AddRange(_entryResults.Select(BuildEntryRow));
        if (more)
        {
            string note = string.Format(Loc.T("Playbook.EntriesMore"), EntryResultCap);
            visible.Add(new InfoRow { Text = note, AccessibleName = note });
        }
    }

    private EntryRow BuildEntryRow(Entry entry)
    {
        string kind = Loc.T(entry.Kind == EntryKind.Term ? "Playbook.KindTerm" : "Playbook.KindCard");
        string front = OneLine(entry.Front);
        string back = OneLine(entry.Back);
        string path = PathText(entry.LibraryId);
        string solidity = Solidity.EntryValue(_states, entry.Id) is { } value
            ? Percent(value)
            : Loc.T("Playbook.NotPlayed");
        return new EntryRow
        {
            Entry = entry,
            KindText = kind,
            Front = front,
            Preview = back == "" ? "" : "   —   " + back,
            PathText = path,
            SolidityText = solidity,
            AccessibleName = string.Format(Loc.T("Playbook.RowEntryAccessible"), kind, front, back, path, solidity),
        };
    }

    private void AddCreateRows(List<ListRow> visible, string typed)
    {
        string nothing = string.Format(Loc.T("Playbook.NoResultsCreate"), typed);
        visible.Add(new InfoRow { Text = nothing, AccessibleName = nothing });
        foreach (var (kind, key) in new[] { (EntryKind.Term, "Playbook.CreateTerm"), (EntryKind.Card, "Playbook.CreateCard") })
        {
            string text = string.Format(Loc.T(key), Ellipsize(typed, 70));
            visible.Add(new CreateRow { Kind = kind, Text = text, AccessibleName = text });
        }
    }

    private static string OneLine(string text) => Ellipsize(string.Join(" ",
        text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)), 160);

    private static string Ellipsize(string text, int max) =>
        text.Length <= max ? text : text[..max].TrimEnd() + "…";

    private void OnSearchKey(KeyEventArgs e)
    {
        bool inList = PickList.IsKeyboardFocusWithin;
        switch (e.Key)
        {
            case Key.Escape:
                // Esc en tiempos: primero limpia el texto; con el buscador vacío PIDE cerrar, y cierra
                // recién con la pulsación siguiente (ver RequestClose).
                e.Handled = true;
                if (SearchBox.Text != "") { SearchBox.Clear(); FocusSearchBox(); }
                else RequestClose();
                break;

            case Key.F2:
                e.Handled = true;
                ShowStats(reload: false);
                break;

            case Key.Enter when !FocusIsOn(CloseBtn, MinimizeBtn, StatsBtn):
                e.Handled = true;
                ActivateSelected();
                break;

            // ↑ ↓ se resuelven ACÁ y no se le dejan al ListBox: la lista tiene filas que no se eligen
            // (los rótulos de grupo) y hay que saltearlas en los dos sentidos.
            case Key.Down when SearchBox.IsKeyboardFocusWithin || inList:
                e.Handled = true;
                // Desde el buscador, bajar ENTRA a la lista. La primera ya estaba marcada, así que el
                // primer ↓ pasa a la siguiente (si hay): igual que cualquier lanzador.
                if (NextSelectable(PickList.SelectedIndex, 1) is { } below) FocusRow(below);
                else if (!inList && PickList.SelectedIndex >= 0) FocusRow(PickList.SelectedIndex);
                break;

            case Key.Up when inList:
                e.Handled = true;
                if (NextSelectable(PickList.SelectedIndex, -1) is { } above) FocusRow(above);
                else FocusSearchBox(); // subir desde la primera vuelve al buscador
                break;

            // ← → en la LISTA pliegan y despliegan una biblioteca del árbol. En el buscador siguen
            // siendo del cursor del texto.
            case Key.Left or Key.Right when inList && _keyModifiers == ModifierKeys.None && OnTreeArrow(e.Key):
                e.Handled = true;
                break;

            case Key.Back when inList:
                // Borrar estando en la lista sigue siendo "editar la búsqueda".
                e.Handled = true;
                if (SearchBox.Text.Length > 0) SearchBox.Text = SearchBox.Text[..^1];
                FocusSearchBox();
                break;
        }
    }

    /// <summary>La próxima fila que se puede elegir en ese sentido; null si no hay más.</summary>
    private int? NextSelectable(int from, int step)
    {
        for (int i = from + step; i >= 0 && i < PickList.Items.Count; i += step)
            if (PickList.Items[i] is ListRow { Selectable: true }) return i;
        return null;
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
            && ItemsControl.ContainerFromElement(PickList, source) is ListBoxItem { DataContext: ListRow row })
        {
            e.Handled = true;
            // El click sobre el triángulo pliega / despliega; en el resto de la fila, la elige.
            if (source is TextBlock { Tag: "disclosure" } && row is PickRow { ChildCount: > 0 } tree && IsTreeRow(tree))
                SetRowCollapsed(tree, !_collapsedLibraries.Contains(tree.LibraryId));
            else
                Activate(row);
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

    private void ActivateSelected()
    {
        // La fila marcada. Sin ninguna (lista vacía) Enter no hace nada, y el estado vacío ya está
        // explicando por qué.
        if (PickList.SelectedItem is ListRow row) Activate(row);
    }

    /// <summary>Enter o click sobre una fila: jugarla, leerla o cargarla, según qué sea.</summary>
    private void Activate(ListRow row)
    {
        switch (row)
        {
            // Las que tienen un problema anotado se abren como FICHAS y no como ronda: lo que hay
            // que hacer con ellas es leer la nota y arreglarlas (E), no responderlas.
            case PickRow { Kind: PickKind.Issues } issues:
                OpenFicha(issues.EntryIds.Where(_entries.ContainsKey).Select(id => _entries[id]).ToList(), 0, fromIssues: true);
                break;
            case PickRow pick:
                EnterSetup(pick);
                break;
            case EntryRow entry:
                OpenFicha(_entryResults, Math.Max(0, _entryResults.FindIndex(e => e.Id == entry.Entry.Id)));
                break;
            case CreateRow create:
                Capture(create.Kind);
                break;
        }
    }

    /// <summary>
    /// CAPTURA RÁPIDA: abre el diálogo de alta con lo tipeado como frente. Al volver, si se guardó
    /// algo, se relee todo y la búsqueda — que sigue con el mismo texto — ya encuentra lo recién
    /// cargado y lo deja marcado: se ve que quedó, y Enter abre su ficha.
    /// </summary>
    private void Capture(EntryKind kind)
    {
        var outcome = KnowledgeEntryWindow.ShowCapture(this, _store, kind, SearchBox.Text);
        if (outcome.SavedCount == 0)
        {
            FocusSearchBox();
            return;
        }
        LoadData();
        // Si se guardó con otro texto que el buscado, no aparece en la lista: el foco vuelve al buscador.
        if (!ApplyFilter(selectEntryId: outcome.LastSavedId)) FocusSearchBox();
    }

    /// <summary>
    /// Vuelta a la búsqueda. <paramref name="reload"/> relee la base (tras una ronda hay repasos
    /// nuevos, tras una edición cambió una entrada); si sólo se volvió del armado no cambió nada y no
    /// se toca. El texto del buscador se conserva siempre; <paramref name="selectEntryId"/> deja
    /// además marcada esa entrada, que es como se vuelve de una ficha sin perder el lugar.
    /// Si por el camino se editó, eliminó o anotó algo (<see cref="_staleRows"/>) se relee igual.
    /// </summary>
    private void BackToSearch(bool reload, long? selectEntryId = null)
    {
        // ANTES de releer: un problema a medio anotar se guarda primero, o la fila «Con un problema
        // anotado» se arma con la foto de antes y queda vieja (ShowStage lo guardaba, pero después).
        CommitIssueIfOpen();
        if (reload || _staleRows) LoadData();
        _staleRows = false;
        _picked = null;
        _pickedEntries = new List<Entry>();
        _selection = new List<Entry>();
        ShowStage(Stage.Search);
        if (!ApplyFilter(selectEntryId)) FocusSearchBox();
    }
}
