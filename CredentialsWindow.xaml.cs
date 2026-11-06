using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using AmpzDesktopBooster.Credentials;
using AmpzDesktopBooster.Desktops;
using AmpzDesktopBooster.Interop;
using AmpzDesktopBooster.Services;
using AmpzDesktopBooster.Services.Localization;

namespace AmpzDesktopBooster;

/// <summary>
/// Gestor de credenciales (Win+Shift+Numpad /). Es la ventana HERMANA de Variables
/// (<see cref="ProjectPathsWindow"/>): mismo scope por escritorio, misma herencia
/// contexto → espacio → global, la misma búsqueda que se abre a otros espacios, y el mismo piso de
/// teclado (cada botón muestra su atajo). Lo que cambia es que acá hay SECRETOS:
///
///   · La contraseña nunca se pinta en la lista (máscara de largo fijo). Se ve en claro sólo en el
///     panel de detalle, para la fila seleccionada, después de pedirlo (Ctrl+R) — y se vuelve a
///     tapar al cambiar de fila o al cerrar. Revelar es un acto, no un estado que se olvida prendido.
///   · Copiar va por <see cref="SecureClipboard"/>: los secretos (contraseña, campos marcados como
///     secretos) quedan fuera del historial de Win+V y de la nube, y se borran solos a los 20s.
///   · La búsqueda NUNCA mira valores secretos: buscar "hunter2" no tiene que decirte qué
///     credencial lo usa. Mira título, usuario, URL, Espacio/Contexto y NOMBRES de campos.
///   · Ningún aviso de esta ventana lleva un secreto adentro: el flash dice QUÉ se copió, no el valor.
///
/// A diferencia de Variables, las filas heredadas o de otros espacios NO son de sólo lectura: una
/// credencial es UN objeto que vive en un scope, y editarla o borrarla desde donde la estás viendo
/// actúa sobre ESE scope (la columna Espacio/Contexto dice cuál, y el borrado lo repite al confirmar).
/// </summary>
public partial class CredentialsWindow : Window
{
    /// <summary>De dónde viene la fila, por cercanía: el orden de la lista ES este orden.</summary>
    private enum RowScope { Own, Parent, Global, Other, Separator }

    /// <summary>Largo FIJO de la máscara: el largo real de una contraseña también es información.</summary>
    private const string Mask = "••••••••";

    private sealed class Row
    {
        public required RowScope Scope { get; init; }
        public required string ScopeKey { get; init; }
        public Credential? Credential { get; init; }
        public required string Title { get; init; }
        public string User { get; init; } = "";
        public string Url { get; init; } = "";
        public string Project { get; init; } = "";
        public string Module { get; init; } = "";
        public Brush ModuleBrush { get; init; } = Brushes.Transparent;

        /// <summary>La URL alcanza con su host (y puerto): el path largo no ayuda a reconocer la fila.</summary>
        public string Host => HostOf(Url);

        public string PasswordMask => Credential is { Password.Length: > 0 } ? Mask : "—";
        public bool IsSeparator => Scope == RowScope.Separator;

        /// <summary>No es de TU scope (heredada u otro espacio): se pinta atenuada, ver el XAML.</summary>
        public bool IsForeign => Scope is RowScope.Parent or RowScope.Global or RowScope.Other;
    }

    private readonly ProjectStore _store;
    private readonly CredentialStore _creds;
    private readonly string _scopeKey;        // "" global, "Espacio" o "Espacio/Contexto"
    private readonly string? _parentScopeKey; // el espacio, cuando estás en un contexto

    /// <summary>Toggle "otros espacios". OFF por defecto: arrancás en TU scope y te abrís a pedido.</summary>
    private bool _showAllProjects;

    /// <summary>
    /// ¿La fila seleccionada está revelada? Es UN bool y no un set de ids a propósito: se revela la
    /// fila que estás mirando, y cualquier cambio de selección lo apaga (ver OnSelectionChanged).
    /// </summary>
    private bool _revealed;

    public CredentialsWindow(ProjectStore store, string deskName, string scopeKey, string? parentScopeKey)
    {
        InitializeComponent();

        _store = store;
        _creds = store.Credentials;
        _scopeKey = scopeKey;
        _parentScopeKey = parentScopeKey;

        Icon = AppIcon.TryLoadForWindow();
        HeaderText.Text = $"{ScopePicker.LabelOf(scopeKey)} — {Loc.T("Creds.HeaderSuffix")}";
        SubHeaderText.Text = deskName;

        RefreshList();

        FilterBox.TextChanged += (_, _) => RefreshList();
        CredList.SelectionChanged += (_, _) => OnSelectionChanged();
        CredList.MouseDoubleClick += (_, _) => CopyPassword();
        PreviewKeyDown += OnWindowKeyDown;

        CopyPasswordBtn.Click += (_, _) => CopyPassword();
        CopyUserBtn.Click += (_, _) => CopyUser();
        CopyUrlBtn.Click += (_, _) => CopyUrl();
        OpenUrlBtn.Click += (_, _) => OpenUrl();
        RevealBtn.Click += (_, _) => ToggleReveal();
        AddBtn.Click += (_, _) => AddNew();
        EditBtn.Click += (_, _) => EditSelected();
        DeleteBtn.Click += (_, _) => DeleteSelected();
        AllProjectsBtn.Click += (_, _) => ToggleAllProjects();
        CloseBtn.Click += (_, _) => Close();

        // Al cerrar se tapa todo: la ventana muere igual, pero no dejamos un secreto en un TextBlock
        // vivo mientras el GC decide cuándo juntarla.
        Closing += (_, _) => { _revealed = false; DetailPassword.Text = ""; FieldsPanel.Children.Clear(); };
        Loaded += (_, _) => FilterBox.Focus();
    }

    // ── Lista ──────────────────────────────────────────────────────────────────

    /// <summary>Scopes de OTROS espacios con credenciales: todo menos el propio, el padre y la global.</summary>
    private List<string> OtherScopeKeys(IReadOnlyDictionary<string, IReadOnlyList<Credential>> all) =>
        all.Keys.Where(k => k != ProjectStore.GlobalScope
                         && !Same(k, _scopeKey)
                         && (_parentScopeKey is null || !Same(k, _parentScopeKey)))
                .OrderBy(k => k, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private void RefreshList(Guid? keep = null)
    {
        keep ??= SelectedRow?.Credential?.Id;
        string filter = FilterBox.Text.Trim();
        var all = _creds.GetAll(); // una sola lectura: copias de todo, repartidas en memoria
        var others = OtherScopeKeys(all);

        AllProjectsBtn.Visibility = others.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        AllProjectsBtn.Content = Loc.T(_showAllProjects ? "Paths.BtnAllProjectsOn" : "Paths.BtnAllProjectsOff");

        CredList.Items.Clear();

        // Orden de CERCANÍA, igual que Variables: lo tuyo, lo del espacio padre, lo global.
        AddRows(all, _scopeKey, RowScope.Own, filter);
        if (_parentScopeKey is not null) AddRows(all, _parentScopeKey, RowScope.Parent, filter);
        if (_scopeKey != ProjectStore.GlobalScope) AddRows(all, ProjectStore.GlobalScope, RowScope.Global, filter);

        // Otros espacios: con el toggle, o SOLOS en cuanto escribís algo — buscar "¿dónde estaba la
        // del Jira?" no tiene que exigirte apretar nada. Debajo de UN divisor, que se saca si abajo
        // no quedó nada (colgando solo se leería como "hay algo más").
        if ((_showAllProjects || filter != "") && others.Count > 0)
        {
            int headerAt = CredList.Items.Count;
            CredList.Items.Add(new Row { Scope = RowScope.Separator, ScopeKey = "", Title = Loc.T("Paths.SepAutoWiden") });
            foreach (var key in others) AddRows(all, key, RowScope.Other, filter);
            if (CredList.Items.Count == headerAt + 1) CredList.Items.RemoveAt(headerAt);
        }

        UpdateEmptyState(filter);
        Reselect(keep);
    }

    private void AddRows(IReadOnlyDictionary<string, IReadOnlyList<Credential>> all, string key,
                         RowScope scope, string filter)
    {
        if (!all.TryGetValue(key, out var list) || list.Count == 0) return;

        ProjectPathsWindow.ResolveScopeColumns(key, key == ProjectStore.GlobalScope, _store,
                                               out string project, out string module, out var brush);

        foreach (var c in list.Where(c => Matches(c, project, module, filter))
                              .OrderBy(c => c.Title, StringComparer.CurrentCultureIgnoreCase))
        {
            CredList.Items.Add(new Row
            {
                Scope = scope, ScopeKey = key, Credential = c,
                Title = c.Title, User = c.User, Url = c.Url,
                Project = project, Module = module, ModuleBrush = brush,
            });
        }
    }

    /// <summary>
    /// ¿La credencial matchea la búsqueda? Sólo sobre lo que NO es secreto: título, usuario, URL, el
    /// Espacio/Contexto dueño y los NOMBRES de los campos. Ni la contraseña, ni el valor de un campo,
    /// ni la nota (es texto libre: ahí la gente pega cosas que no debería, y la búsqueda no tiene
    /// que convertirse en un oráculo de "¿qué credencial contiene X?").
    /// </summary>
    private static bool Matches(Credential c, string project, string module, string filter)
    {
        if (filter == "") return true;
        bool Has(string s) => s.Contains(filter, StringComparison.CurrentCultureIgnoreCase);
        return Has(c.Title) || Has(c.User) || Has(c.Url) || Has(project) || Has(module)
            || c.Fields.Any(f => Has(f.Name));
    }

    /// <summary>
    /// Vacío-real y vacío-por-búsqueda dicen cosas distintas: el primero cómo cargar la primera, el
    /// segundo qué mira la búsqueda (para que nadie crea que falló por no encontrar una contraseña).
    /// </summary>
    private void UpdateEmptyState(string filter)
    {
        bool any = CredList.Items.OfType<Row>().Any(r => !r.IsSeparator);
        if (any) { EmptyText.Visibility = Visibility.Collapsed; return; }

        EmptyText.Text = filter != ""
            ? string.Format(Loc.T("Creds.EmptyFilter"), filter)
            : string.Format(Loc.T("Creds.EmptyScope"), ScopePicker.LabelOf(_scopeKey));
        EmptyText.Visibility = Visibility.Visible;
    }

    /// <summary>Re-selecciona por id (el orden cambia al editar); si ya no está, la primera fila real.</summary>
    private void Reselect(Guid? id)
    {
        var rows = CredList.Items.OfType<Row>().Where(r => !r.IsSeparator).ToList();
        var target = (id is { } g ? rows.FirstOrDefault(r => r.Credential?.Id == g) : null) ?? rows.FirstOrDefault();
        CredList.SelectedItem = target;
        if (target is not null) CredList.ScrollIntoView(target);
        OnSelectionChanged();
    }

    private Row? SelectedRow => CredList.SelectedItem is Row { IsSeparator: false } r ? r : null;

    private void ToggleAllProjects()
    {
        if (AllProjectsBtn.Visibility != Visibility.Visible) return; // no hay otros espacios
        _showAllProjects = !_showAllProjects;
        RefreshList();
    }

    // ── Detalle ────────────────────────────────────────────────────────────────

    private Guid? _detailFor;

    private void OnSelectionChanged()
    {
        // Cambiar de fila TAPA lo revelado. Un refresh que vuelve a seleccionar la MISMA credencial
        // (p. ej. tras editarla) no cuenta como cambio: revelaste ésa y sigue siendo ésa.
        Guid? now = SelectedRow?.Credential?.Id;
        if (now != _detailFor) _revealed = false;
        _detailFor = now;
        RenderDetail();
    }

    private void RenderDetail()
    {
        FieldsPanel.Children.Clear();
        RevealBtn.Content = Loc.T(_revealed ? "Creds.BtnHide" : "Creds.BtnReveal");

        if (SelectedRow is not { Credential: { } c } row)
        {
            DetailPanel.Visibility = Visibility.Collapsed;
            NoSelectionText.Visibility = Visibility.Visible;
            DetailPassword.Text = "";
            return;
        }
        DetailPanel.Visibility = Visibility.Visible;
        NoSelectionText.Visibility = Visibility.Collapsed;

        DetailTitle.Text = c.Title;
        DetailScope.Text = string.Format(Loc.T("Scope.SavedIn"), ScopePicker.LabelOf(row.ScopeKey));
        DetailUser.Text = Or(c.User);
        DetailPassword.Text = c.Password == "" ? Loc.T("Creds.Empty") : _revealed ? c.Password : Mask;
        RevealedText.Visibility = _revealed && c.Password != "" ? Visibility.Visible : Visibility.Collapsed;
        DetailUrl.Text = Or(c.Url);

        FieldsSection.Visibility = c.Fields.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        for (int i = 0; i < c.Fields.Count; i++)
            FieldsPanel.Children.Add(BuildFieldRow(c.Fields[i], i));

        NoteSection.Visibility = c.Note.Trim() == "" ? Visibility.Collapsed : Visibility.Visible;
        DetailNote.Text = c.Note;
    }

    private static string Or(string s) => s.Trim() == "" ? Loc.T("Creds.Empty") : s;

    /// <summary>
    /// Un campo extra: nombre, valor (enmascarado si es secreto y la fila no está revelada) y su
    /// botón de copiar con atajo Ctrl+1..9 — el botón está en el MISMO renglón que el valor que
    /// copia, así no hay que contar campos para saber qué número es cuál.
    /// </summary>
    private UIElement BuildFieldRow(CustomField f, int index)
    {
        var grid = new Grid { Margin = new Thickness(0, 4, 0, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var text = new StackPanel();
        text.Children.Add(new TextBlock
        {
            Text = f.Name.Trim() == "" ? Loc.T("Creds.Empty") : f.Name,
            Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0x8A, 0x92)), FontSize = 11,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        text.Children.Add(new TextBlock
        {
            Text = f.Value == "" ? Loc.T("Creds.Empty") : f.IsSecret && !_revealed ? Mask : f.Value,
            Foreground = new SolidColorBrush(Color.FromRgb(0xE6, 0xE6, 0xEA)), FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = f.IsSecret ? new FontFamily("Consolas") : FontFamily,
        });
        grid.Children.Add(text);

        var btn = new Button
        {
            Content = Loc.T("Creds.BtnCopyField"),
            Tag = index < 9 ? $"Ctrl+{index + 1}" : null,
            Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(btn, 1);
        btn.Click += (_, _) => CopyField(index);
        grid.Children.Add(btn);
        return grid;
    }

    // ── Teclado ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Atajos de TODA la ventana, con la misma cesión por TAREA que Variables: el filtro se queda
    /// Supr sólo si tiene texto, y Ctrl+C sólo si tiene texto seleccionado; con el foco en un botón,
    /// Enter es "apretar ese botón". Ver <see cref="ProjectPathsWindow"/> para la historia completa.
    /// </summary>
    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        bool inFilter = FilterBox.IsKeyboardFocused;
        bool onButton = Keyboard.FocusedElement is Button;
        bool filterCopies = inFilter && FilterBox.SelectionLength > 0;
        bool filterDeletes = inFilter && FilterBox.Text.Length > 0;
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;

        if (ctrl && FieldIndexOf(e.Key) is { } fi) { CopyField(fi); e.Handled = true; return; }

        switch (e.Key)
        {
            case Key.Enter when !onButton:          CopyPassword();      break;
            case Key.Escape:                        Close();             break;
            case Key.F2:                            EditSelected();      break;
            case Key.N when ctrl:                   AddNew();            break;
            case Key.P when ctrl:                   ToggleAllProjects(); break;
            case Key.U when ctrl:                   CopyUser();          break;
            case Key.L when ctrl:                   CopyUrl();           break;
            case Key.O when ctrl:                   OpenUrl();           break;
            case Key.R when ctrl:                   ToggleReveal();      break;
            // Alias por reflejo: Ctrl+C sobre una credencial copia su contraseña, como Enter.
            case Key.C when ctrl && !filterCopies:  CopyPassword();      break;
            case Key.Delete when !filterDeletes:    DeleteSelected();    break;

            case Key.Down when inFilter && CredList.Items.Count > 0:
                if (CredList.SelectedIndex < 0) CredList.SelectedIndex = 0;
                (CredList.ItemContainerGenerator.ContainerFromItem(CredList.SelectedItem) as UIElement ?? CredList).Focus();
                break;

            default: return;
        }
        e.Handled = true;
    }

    /// <summary>Ctrl+1..9 (fila superior o numpad) → índice del campo extra; null si no es esa tecla.</summary>
    private static int? FieldIndexOf(Key key) => key switch
    {
        >= Key.D1 and <= Key.D9 => key - Key.D1,
        >= Key.NumPad1 and <= Key.NumPad9 => key - Key.NumPad1,
        _ => null,
    };

    // ── Acciones ───────────────────────────────────────────────────────────────
    // Ninguna acción queda muda: sin fila elegida o sin el dato pedido, el flash dice POR QUÉ no
    // pasó nada (un botón que no hace nada se lee igual que un botón roto).

    private bool TryGetSelected(out Row row, out Credential c)
    {
        if (SelectedRow is { Credential: { } cred } r) { row = r; c = cred; return true; }
        ShowFlash(Loc.T("Creds.NoSelection"));
        row = null!; c = null!;
        return false;
    }

    private void CopyPassword()
    {
        if (!TryGetSelected(out _, out var c)) return;
        if (c.Password == "") { ShowFlash(string.Format(Loc.T("Creds.NoPassword"), c.Title)); return; }
        Copy(c.Password, secret: true, Loc.T("Creds.DetailPassword"), c.Title);
    }

    private void CopyUser()
    {
        if (!TryGetSelected(out _, out var c)) return;
        if (c.User.Trim() == "") { ShowFlash(string.Format(Loc.T("Creds.NoUser"), c.Title)); return; }
        Copy(c.User, secret: false, c.User, c.Title);
    }

    private void CopyUrl()
    {
        if (!TryGetSelected(out _, out var c)) return;
        if (c.Url.Trim() == "") { ShowFlash(string.Format(Loc.T("Creds.NoUrl"), c.Title)); return; }
        Copy(c.Url.Trim(), secret: false, c.Url.Trim(), c.Title);
    }

    private void CopyField(int index)
    {
        if (!TryGetSelected(out _, out var c)) return;
        if (index < 0 || index >= c.Fields.Count)
        {
            ShowFlash(string.Format(Loc.T("Creds.NoField"), index + 1, c.Title));
            return;
        }
        var f = c.Fields[index];
        if (f.Value == "") { ShowFlash(string.Format(Loc.T("Creds.EmptyField"), f.Name)); return; }
        Copy(f.Value, secret: f.IsSecret, f.IsSecret ? f.Name : f.Value, c.Title);
    }

    /// <summary>
    /// Copia y avisa. <paramref name="what"/> es lo que el flash NOMBRA: para un secreto, el nombre
    /// del campo ("Contraseña"), nunca el valor; para lo visible, el valor mismo (confirma que copiaste
    /// la fila que creías, como en Variables).
    /// </summary>
    private void Copy(string value, bool secret, string what, string title)
    {
        if (!SecureClipboard.CopySecret(value, secret)) { ShowFlash(Loc.T("Creds.ClipboardBusy")); return; }
        ShowFlash(secret
            ? string.Format(Loc.T("Creds.CopiedSecret"), what, title, (int)SecureClipboard.SecretLifetime.TotalSeconds)
            : string.Format(Loc.T("Creds.CopiedPlain"), what));
    }

    private void OpenUrl()
    {
        if (!TryGetSelected(out _, out var c)) return;
        string url = c.Url.Trim();
        if (url == "") { ShowFlash(string.Format(Loc.T("Creds.NoUrl"), c.Title)); return; }
        if (!UrlHelper.IsUrl(url)) { ShowFlash(string.Format(Loc.T("Creds.NotAUrl"), c.Title)); return; }
        try
        {
            // Vía explorer.exe: browser por defecto y, como la app corre elevada, DE-elevado (un
            // browser con token de admin es justo lo que no querés para loguearte en un sitio).
            UnelevatedLauncher.StartShellTarget(UrlHelper.Normalize(url));
        }
        catch { return; }
        Close(); // ya cumplió: lo que sigue es el browser, y la ventana se cerraría igual al perder el foco
    }

    private void ToggleReveal()
    {
        if (!TryGetSelected(out _, out _)) return;
        _revealed = !_revealed;
        RenderDetail();
    }

    private void DeleteSelected()
    {
        if (!TryGetSelected(out var row, out var c)) return;
        var answer = MessageBox.Show(this,
            string.Format(Loc.T("Creds.DeleteConfirm"), c.Title, ScopePicker.LabelOf(row.ScopeKey)),
            Loc.T("Creds.WindowTitle"), MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;

        _creds.Delete(row.ScopeKey, c.Id);
        RefreshList();
    }

    private void AddNew() => OpenEditor(null);

    private void EditSelected()
    {
        if (!TryGetSelected(out var row, out _)) return;
        OpenEditor(row);
    }

    /// <summary>Alta (row null) o edición de la fila. El editor llega con su propia ventana.</summary>
    private void OpenEditor(Row? row) => ShowFlash(Loc.T("Creds.EditorPending"));

    // ── Feedback ───────────────────────────────────────────────────────────────

    /// <summary>Flash sobre la lista (mismo que Variables): el aviso aparece donde estás mirando.</summary>
    private void ShowFlash(string text)
    {
        CopyToastText.Text = text;
        if (TryFindResource("CopyToastAnim") is Storyboard sb) sb.Begin(this);
    }

    /// <summary>Host (con puerto si lo trae) de una URL; si no se puede parsear, el texto tal cual.</summary>
    private static string HostOf(string url)
    {
        url = url.Trim();
        if (url == "") return "";
        string candidate = UrlHelper.IsUrl(url) ? UrlHelper.Normalize(url) : url;
        return Uri.TryCreate(candidate, UriKind.Absolute, out var uri) && uri.Host != ""
            ? (uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}")
            : url;
    }
}
