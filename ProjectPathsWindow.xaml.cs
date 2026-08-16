using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using AmpzDesktopBooster.Desktops;
using AmpzDesktopBooster.Interop;
using AmpzDesktopBooster.Persistence;
using AmpzDesktopBooster.Services;
using AmpzDesktopBooster.Services.Localization;

namespace AmpzDesktopBooster;

/// <summary>
/// Variables del espacio / globales — la Win+Numpad* del legacy (Paths Manager).
/// Lista de paths/URLs con título, filtrable. Acciones:
///   Enter / doble-clic → abrir (URL al browser, path al explorer)
///   Shift+Enter        → abrir el directorio en Claude CLI
///   Ctrl+C             → copiar el path al portapapeles
///   F2 / ✎             → renombrar el título
///   F3 / ⭐            → marcar/desmarcar predeterminado (1 por pool)
///   Supr               → borrar la variable
///   re-presionar Win+* con la ventana abierta → dispara el predeterminado (lo maneja el router)
///
/// El dual-scope (espacio vs global) ya viene resuelto: el caller pasa la PathPool correcta.
/// </summary>
public partial class ProjectPathsWindow : Window
{
    /// <summary>De qué pool viene la fila — define si es operable o sólo referencia, y cómo se pinta.</summary>
    /// <remarks>
    /// Project = operable (mutás). Global y Other = SOLO-LECTURA (abrís/copiás, no mutás): Global es la
    /// pool compartida anexada en scope de espacio; Other es OTRO espacio traído por el toggle "ver
    /// todos los espacios". Separator = rótulo de sección, no seleccionable.
    /// </remarks>
    private enum RowScope { Project, Parent, Global, Other, Separator }

    /// <summary>Fila visible. Indexa a la entry real de SU pool por <see cref="PoolIndex"/>.</summary>
    private sealed class Row
    {
        public required RowScope Scope { get; init; }
        public required int PoolIndex { get; init; }
        public required string Title { get; init; }
        public required string Path { get; init; }
        public required bool IsDefault { get; init; }

        /// <summary>
        /// El path apunta a algo que ya NO existe en disco (carpeta/archivo borrado o movido). Solo
        /// aplica a paths de filesystem — una URL nunca se considera "rota" acá (chequear existencia
        /// de una URL exigiría un request HTTP por fila al listar, absurdo). Se usa para pintar la
        /// fila en rojo con ⚠ y para que "purgar rotos" sepa qué borrar.
        /// </summary>
        public required bool IsBroken { get; init; }

        /// <summary>
        /// El predeterminado de esta fila existe pero está TAPADO por uno más cercano (caso único:
        /// estás en un contexto, la fila es del espacio padre y el contexto ya tiene su propio
        /// predeterminado). Se pinta con estrella HUECA: sigue siendo el predeterminado del espacio
        /// — y va a volver a mandar si borrás el del contexto — pero hoy no es el que dispara.
        /// </summary>
        public bool IsShadowed { get; init; }

        public bool IsProject   => Scope == RowScope.Project;
        public bool IsSeparator => Scope == RowScope.Separator; // rótulo de sección, no seleccionable

        /// <summary>Fila de SOLO-LECTURA (heredada del espacio padre, global u otro espacio).</summary>
        public bool IsReadOnlyRef => Scope is RowScope.Parent or RowScope.Global or RowScope.Other;

        /// <summary>
        /// ¿Se le puede marcar/desmarcar el predeterminado? CUALQUIER fila que veas en tu contexto:
        /// la del scope primario, la heredada del espacio padre y la global. Con el predeterminado
        /// guardado POR SCOPE, marcar una heredada NO toca al padre — sólo anota, en TU scope, cuál
        /// de las que ves es la tuya. Por eso ya no hay motivo para prohibirlo.
        ///
        /// Se excluyen sólo las de OTROS espacios (el toggle F4): eso es un modo de exploración
        /// para encontrar algo suelto, no tu contexto de trabajo.
        /// </summary>
        public bool IsDefaultable => Scope is RowScope.Project or RowScope.Parent or RowScope.Global;

        /// <summary>
        /// Columna Título. ⭐ = es el predeterminado de TU scope (el que dispara con el re-press).
        /// ☆ hueco = es el del espacio padre pero está tapado por el tuyo (ver <see cref="IsShadowed"/>).
        /// Va al FINAL del nombre (pedido del usuario): así los títulos quedan alineados a la
        /// izquierda y la marca no desplaza el texto de la fila predeterminada respecto de las demás.
        /// El ⚠ de "roto" se antepone a todo: es la señal más importante de la fila.
        /// </summary>
        public string Display
        {
            get
            {
                string t = Title;
                if (IsDefaultable && IsDefault)
                    t += IsShadowed ? " ☆" : " ⭐";
                return IsBroken ? "⚠ " + t : t;
            }
        }
    }

    /// <summary>
    /// Normaliza un título a "sentence case": primera letra en MAYÚSCULA, el resto en minúscula
    /// (pedido del usuario para que las variables se vean SIEMPRE parejas sin importar cómo se
    /// tipearon). Es SÓLO de presentación — el título real de la pool no se toca, así la regla de
    /// normalización es reversible y nunca perdemos el dato original que escribió el usuario.
    /// </summary>
    private static string NormalizeTitle(string title)
    {
        title = title.Trim();
        if (title.Length == 0) return title;
        return char.ToUpper(title[0]) + title[1..].ToLower();
    }

    /// <summary>
    /// true si <paramref name="path"/> es un path de filesystem que ya no existe (ni carpeta ni
    /// archivo). Las URLs y los strings vacíos NO se consideran rotos. Mismo criterio de "qué es URL"
    /// que <see cref="PathOpener.Open"/> para no clasificar distinto de cómo se abre.
    ///
    /// Es <c>internal</c> (y no privado) para que la pestaña Variables de la config marque la fila
    /// rota EXACTAMENTE con la misma regla. Una segunda implementación del criterio se desincroniza
    /// sola: dos ventanas mostrando el mismo dato con distinto ⚠ es peor que no mostrarlo.
    /// </summary>
    internal static bool IsBrokenPath(string path)
    {
        path = path.Trim();
        if (path == "" || UrlHelper.IsUrl(path)) return false;
        return !System.IO.Directory.Exists(path) && !System.IO.File.Exists(path);
    }

    private readonly PathPool _pool;
    private readonly PathPool? _globalPool; // no-null en scope de espacio: se anexa de solo-lectura
    private readonly PathPool? _parentPool; // no-null en scope de CONTEXTO: el espacio del que hereda
    private readonly IReadOnlyList<PathPool> _otherProjectPools; // los demás espacios (toggle F4), read-only
    private readonly string _deskName;
    private readonly string _explorerSeed;

    // Predeterminado POR SCOPE. El store es el dueño; la ventana sólo lee/escribe el de SU scope
    // (y lee el del padre, para la herencia). null en ambos = no hay ninguno elegido.
    private readonly ProjectStore? _store;
    private readonly string _scopeKey;        // "" global, "Espacio", o "Espacio/Contexto"
    private readonly string? _parentScopeKey; // el espacio, cuando estás en un contexto

    private string? OwnDefault => _store?.GetScopeDefault(_scopeKey);
    private string? ParentDefault => _parentScopeKey is null ? null : _store?.GetScopeDefault(_parentScopeKey);

    /// <summary>El que realmente dispara: el de tu scope y, si no elegiste, el heredado del padre.</summary>
    private string? EffectiveDefault => OwnDefault ?? ParentDefault;

    /// <summary>Toggle "ver todos los espacios". OFF por default: arrancás en TU contexto y te abrís al resto a pedido.</summary>
    private bool _showAllProjects;

    public ProjectPathsWindow(PathPool pool, string deskName, string explorerSeed = "",
                              PathPool? globalPool = null, IReadOnlyList<PathPool>? otherProjectPools = null,
                              PathPool? parentPool = null, ProjectStore? store = null,
                              string scopeKey = "", string? parentScopeKey = null)
    {
        InitializeComponent();

        _pool = pool;
        _globalPool = globalPool;
        _parentPool = parentPool;
        _store = store;
        _scopeKey = scopeKey;
        _parentScopeKey = parentScopeKey;
        _otherProjectPools = otherProjectPools ?? System.Array.Empty<PathPool>();
        _deskName = deskName;
        _explorerSeed = explorerSeed;

        Icon = AppIcon.TryLoadForWindow();
        HeaderText.Text = $"{pool.Label} — {Loc.T("Paths.HeaderSuffix")}";
        SubHeaderText.Text = deskName;

        // Sin otros espacios en el catálogo no hay nada que togglear → ocultamos el botón.
        AllProjectsBtn.Visibility = _otherProjectPools.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateAllProjectsBtn();

        RefreshList();

        FilterBox.TextChanged += (_, _) => RefreshList();
        PreviewKeyDown += OnWindowKeyDown;
        PathList.MouseDoubleClick += (_, _) => OpenSelected(claude: false);

        OpenBtn.Click += (_, _) => OpenSelected(claude: Shift);
        CopyBtn.Click += (_, _) => CopySelected();
        AddBtn.Click += (_, _) => AddNew();
        EditBtn.Click += (_, _) => RenameSelected();
        DefaultBtn.Click += (_, _) => ToggleDefaultSelected();
        DeleteBtn.Click += (_, _) => DeleteSelected();
        PurgeBtn.Click += (_, _) => PurgeBroken();
        AllProjectsBtn.Click += (_, _) => ToggleAllProjects();
        CloseBtn.Click += (_, _) => Close();

        Loaded += (_, _) => FilterBox.Focus();
    }

    /// <summary>Alterna la vista "todos los espacios" y repinta. Lo dispara el botón y F4.</summary>
    private void ToggleAllProjects()
    {
        if (_otherProjectPools.Count == 0) return; // nada que mostrar
        _showAllProjects = !_showAllProjects;
        UpdateAllProjectsBtn();
        RefreshList();
    }

    private void UpdateAllProjectsBtn() =>
        AllProjectsBtn.Content = Loc.T(_showAllProjects ? "Paths.BtnAllProjectsOn" : "Paths.BtnAllProjectsOff");

    /// <summary>
    /// Dispara el predeterminado (lo llama el router al re-presionar Win+*). Respeta la herencia:
    /// el predeterminado del scope PRIMARIO gana, y si el contexto no tiene uno propio cae al del
    /// ESPACIO padre. Así un contexto recién creado ya te abre lo del cliente sin configurar nada,
    /// y en cuanto le marcás su propio predeterminado (su localhost) el suyo pisa al heredado.
    /// </summary>
    public bool FireDefault()
    {
        if (EffectiveDefault is not { } path)
            return false;

        OpenValue(path, claude: false);
        Close();
        return true;
    }

    private void RefreshList()
    {
        string filter = FilterBox.Text.Trim();
        PathList.Items.Clear();

        // 1) Las del espacio (operables), agrupadas por tipo (carpetas / URLs).
        AddGroupedByType(PoolRows(_pool, RowScope.Project, filter).ToList());

        // 2) En scope de CONTEXTO anexamos las del ESPACIO PADRE, de solo-lectura. Es la herencia:
        //    lo que es del cliente (repo raíz, Jira, VPN) se carga UNA vez en el espacio y se ve
        //    desde todos sus contextos, sin duplicarlo en cada uno. Va antes que las globales porque
        //    está más cerca de tu scope: el orden de la lista ES el orden de cercanía.
        if (_parentPool is not null)
        {
            var parents = PoolRows(_parentPool, RowScope.Parent, filter).ToList();
            if (parents.Count > 0)
            {
                PathList.Items.Add(SeparatorRow(_parentPool.Label));
                AddGroupedByType(parents);
            }
        }

        // 3) En scope de espacio anexamos las GLOBALES de solo-lectura bajo un separador, para no
        //    quedar ciegos a las compartidas. El separador entra SÓLO si hay alguna que matchee el
        //    filtro (si no, no ensuciamos la lista con un rótulo de sección vacío). También se agrupan
        //    por tipo dentro de su sección.
        if (_globalPool is not null)
        {
            var globals = PoolRows(_globalPool, RowScope.Global, filter).ToList();
            if (globals.Count > 0)
            {
                PathList.Items.Add(SeparatorRow(Loc.T("Paths.SepGlobals")));
                AddGroupedByType(globals);
            }
        }

        // 4) Toggle "todos los espacios": cada OTRO espacio bajo su propio separador (su nombre),
        //    de SOLO-LECTURA. El separador entra sólo si ese espacio tiene alguna fila que matchee
        //    el filtro — así, filtrando, sólo ves los espacios que realmente tienen algo. Esto es lo
        //    que te deja "encontrar una variable de cualquier espacio" tipeando un fragmento.
        if (_showAllProjects)
        {
            foreach (var other in _otherProjectPools)
            {
                var rows = PoolRows(other, RowScope.Other, filter).ToList();
                if (rows.Count == 0) continue;
                PathList.Items.Add(SeparatorRow(other.Label));
                AddGroupedByType(rows);
            }
        }

        SelectFirstSelectable();
    }

    /// <summary>
    /// Agrega las filas de UN scope a la lista, partidas por TIPO: carpetas/paths primero, URLs
    /// después (lo que más usa un dev arriba). El criterio de "qué es URL" es el MISMO que usa
    /// <see cref="PathOpener.Open"/> (<see cref="UrlHelper.IsUrl"/>): así lo que se muestra agrupado
    /// coincide exacto con cómo se abre — no hay una clasificación paralela que se pueda desincronizar.
    /// El rótulo de tipo entra SÓLO si conviven ambos tipos: con uno solo no hay nada que separar y el
    /// rótulo sería ruido. Dentro de cada grupo las filas van ORDENADAS ALFABÉTICAMENTE por título
    /// (pedido del usuario) — se ordena por el título YA normalizado (<see cref="Row.Title"/>), así el
    /// orden que ves coincide con el texto que ves; ordenar por el crudo se vería "desordenado".
    /// </summary>
    private void AddGroupedByType(List<Row> rows)
    {
        var folders = rows.Where(r => !UrlHelper.IsUrl(r.Path)).OrderBy(r => r.Title, StringComparer.CurrentCultureIgnoreCase).ToList();
        var urls    = rows.Where(r =>  UrlHelper.IsUrl(r.Path)).OrderBy(r => r.Title, StringComparer.CurrentCultureIgnoreCase).ToList();
        bool label  = folders.Count > 0 && urls.Count > 0;

        if (folders.Count > 0)
        {
            if (label) PathList.Items.Add(SeparatorRow(Loc.T("Paths.SepFolders")));
            foreach (var r in folders) PathList.Items.Add(r);
        }
        if (urls.Count > 0)
        {
            if (label) PathList.Items.Add(SeparatorRow(Loc.T("Paths.SepUrls")));
            foreach (var r in urls) PathList.Items.Add(r);
        }
    }

    /// <summary>
    /// Filas de una pool que matchean el filtro (busca SOLO en el título, como el legacy).
    ///
    /// La marca de predeterminado NO sale de la entrada (ya no vive ahí): se resuelve comparando el
    /// PATH de la fila contra el predeterminado de TU scope y el del padre. Por eso la ⭐ puede caer
    /// en CUALQUIER sección — incluida la heredada: elegir una variable del espacio como tu
    /// predeterminado no la mueve ni la duplica, sólo la apunta desde tu scope.
    /// </summary>
    private IEnumerable<Row> PoolRows(PathPool pool, RowScope scope, string filter)
    {
        string? own = OwnDefault;
        string? parent = ParentDefault;

        var entries = pool.Entries;
        for (int i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            if (filter != "" && !e.Title.Contains(filter, StringComparison.OrdinalIgnoreCase))
                continue;

            bool isOwn = own is not null && e.Path == own;
            bool isParent = parent is not null && e.Path == parent;

            yield return new Row
            {
                Scope = scope,
                PoolIndex = i,
                Title = NormalizeTitle(e.Title),
                Path = e.Path,
                IsDefault = isOwn || isParent,
                // Hueco sólo si es el del PADRE y el tuyo (otro) lo está tapando.
                IsShadowed = !isOwn && isParent && own is not null,
                IsBroken = IsBrokenPath(e.Path),
            };
        }
    }

    /// <summary>Fila-rótulo (sección de globales o tipo). No es operable (ver estilo en el XAML).</summary>
    private static Row SeparatorRow(string text) => new()
    {
        Scope = RowScope.Separator, PoolIndex = -1, IsDefault = false, IsBroken = false, Path = "",
        Title = text,
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

    /// <summary>Fila del ESPACIO seleccionada. Las globales son de solo-lectura acá → null (no mutan).</summary>
    private Row? SelectedProject => Selected is { IsProject: true } row ? row : null;

    /// <summary>Fila a la que SÍ se le puede togglear el predeterminado (ver <see cref="Row.IsDefaultable"/>).</summary>
    private Row? SelectedDefaultable => Selected is { IsDefaultable: true } row ? row : null;

    // ── Teclado ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Atajos de TODA la ventana (PreviewKeyDown del Window), no del filtro y la lista por separado
    /// como antes. El cambio es consecuencia directa de que ahora cada botón muestre su atajo: si el
    /// atajo dejara de responder al tabular a un botón, el keycap del botón MENTIRÍA — y un cartel
    /// que miente es peor que no tener cartel.
    ///
    /// El precio son dos contextos donde una tecla ya significa otra cosa, y hay que respetarlos:
    ///   · foco en el FILTRO → Supr borra TEXTO y Ctrl+C copia lo seleccionado del textbox. Robarle
    ///     esas dos teclas al campo de búsqueda haría que no se pueda ni editar el filtro.
    ///   · foco en un BOTÓN → Enter es "apretar ESTE botón". Si lo interceptáramos, tabular hasta
    ///     Borrar y hacer Enter abriría la variable: lo contrario de lo que dice la pantalla.
    /// </summary>
    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        bool inFilter = FilterBox.IsKeyboardFocused;
        bool onButton = Keyboard.FocusedElement is System.Windows.Controls.Button;

        switch (e.Key)
        {
            case Key.Enter when !onButton:     OpenSelected(claude: Shift); break;
            case Key.Escape:                   Close();                     break;
            case Key.F2:                       RenameSelected();            break;
            case Key.F3:                       ToggleDefaultSelected();     break;
            case Key.N when Ctrl:              AddNew();                    break;
            case Key.K when Ctrl:              PurgeBroken();               break;
            case Key.P when Ctrl:              ToggleAllProjects();         break;
            case Key.C when Ctrl && !inFilter: CopySelected();              break;
            case Key.Delete when !inFilter:    DeleteSelected();            break;

            case Key.Down when inFilter && PathList.Items.Count > 0:
                PathList.SelectedIndex = 0;
                PathList.Focus();
                break;

            default: return;  // no es nuestra → que siga su curso normal
        }

        e.Handled = true;
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
                claude ? Loc.T("Paths.ClaudeOnlyDir") : $"{Loc.T("Paths.NotFound")}\n{value}",
                Loc.T("Paths.WindowTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void CopySelected()
    {
        if (Selected is not { } row) return;
        // El toast se dispara SOLO si la copia salió bien: Clipboard.SetText puede tirar si otro
        // proceso tiene el portapapeles tomado, y mentirle al usuario con un "Copiado" que no pasó
        // es peor que no darle feedback (que era el estado anterior).
        try { Clipboard.SetText(row.Path); } catch { return; }
        ShowCopyToast(row.Path);
    }

    /// <summary>
    /// Flash de "Copiado: &lt;valor&gt;" arriba de la lista. Mostramos el VALOR y no un genérico
    /// "copiado" porque en esta ventana conviven filas propias, heredadas y globales: ver el texto
    /// exacto que quedó en el portapapeles confirma que copiaste la fila que creías.
    /// </summary>
    private void ShowCopyToast(string value)
    {
        CopyToastText.Text = $"{Loc.T("Paths.Copied")}: {value}";
        if (TryFindResource("CopyToastAnim") is Storyboard sb)
            sb.Begin(this);
    }

    private void DeleteSelected()
    {
        if (SelectedProject is not { } row) return; // globales: solo-lectura, no se borran desde acá
        _pool.Delete(row.PoolIndex);
        RefreshList();
    }

    /// <summary>
    /// Marca/desmarca el predeterminado DE TU SCOPE apuntando a la fila seleccionada, venga de donde
    /// venga (propia, heredada del espacio o global). NO toca la entrada ni el scope de origen: por
    /// eso dos contextos del mismo espacio pueden elegir dos variables distintas del MISMO pool
    /// heredado sin pisarse. Re-marcar la que ya era la tuya la desmarca; si había una heredada del
    /// padre, vuelve a mandar sola.
    /// </summary>
    private void ToggleDefaultSelected()
    {
        if (SelectedDefaultable is not { } row || _store is null) return;

        bool alreadyMine = OwnDefault == row.Path;
        _store.SetScopeDefault(_scopeKey, alreadyMine ? null : row.Path);
        RefreshList();

        // RefreshList reconstruye las filas y SelectFirstSelectable manda la selección al tope. Sin
        // esto, marcar en la sección heredada te devolvía arriba y el siguiente F3 caía sobre OTRA
        // fila — pisabas un predeterminado sin querer. Re-seleccionamos por (scope, índice de pool),
        // que es la identidad estable de la fila (el título se re-normaliza, el orden se re-ordena).
        PathList.SelectedItem = PathList.Items.OfType<Row>()
            .FirstOrDefault(r => r.Scope == row.Scope && r.PoolIndex == row.PoolIndex);
    }

    private void RenameSelected()
    {
        if (SelectedProject is not { } row) return; // globales: solo-lectura
        string? title = PromptDialog.Show(this, Loc.T("Paths.DlgRenameTitle"), Loc.T("Paths.DlgRenameLabel"), row.Title);
        if (title is null) return;
        _pool.UpdateTitle(row.PoolIndex, title);
        RefreshList();
    }

    /// <summary>
    /// Borra de un saque todos los paths ROTOS del pool OPERABLE (<see cref="_pool"/>). Las globales
    /// en la vista de espacio son solo-lectura (igual que Delete/Rename) → no se purgan desde acá:
    /// para limpiarlas hay que pararse en un desk global, donde el pool global ES el operable.
    /// Pide confirmación con el conteo: borrar variables es destructivo y no hay undo.
    /// </summary>
    private void PurgeBroken()
    {
        var broken = new List<int>();
        var entries = _pool.Entries;
        for (int i = 0; i < entries.Count; i++)
            if (IsBrokenPath(entries[i].Path))
                broken.Add(i);

        if (broken.Count == 0)
        {
            MessageBox.Show(Loc.T("Paths.PurgeNone"), Loc.T("Paths.WindowTitle"),
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var confirm = MessageBox.Show(
            string.Format(Loc.T("Paths.PurgeConfirm"), broken.Count), Loc.T("Paths.WindowTitle"),
            MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        _pool.DeleteMany(broken);
        RefreshList();
    }

    private void AddNew()
    {
        // Path pre-cargado con el del Explorer activo (si lo capturamos al abrir), como el legacy.
        string? path = PromptDialog.Show(this, Loc.T("Paths.DlgNewTitle"), Loc.T("Paths.DlgNewPathLabel"), _explorerSeed);
        if (string.IsNullOrWhiteSpace(path)) return;
        string? title = PromptDialog.Show(this, Loc.T("Paths.DlgNewTitle"), Loc.T("Paths.DlgRenameLabel"), "");
        if (title is null) return;
        if (title.Trim() == "") title = path.Trim();
        _pool.Add(title, path);
        RefreshList();
    }
}
