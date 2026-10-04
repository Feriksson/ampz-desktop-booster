using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AmpzDesktopBooster.Apps;
using AmpzDesktopBooster.Services.Localization;

namespace AmpzDesktopBooster;

/// <summary>
/// Shortcuts Helper (Win+/). Arriba un BUSCADOR que tiene el foco al abrir; abajo, con el buscador
/// vacío, el estado principal a dos columnas: IZQUIERDA la cheatsheet global del app (estática, con
/// los bindings REALES de hoy — no los del legacy), DERECHA la cheatsheet editable de la app que tenía
/// el foco al abrir (atajos per-app + alias) y los atajos LIBRES agrupados por su título ("PowerToys",
/// "Windows"). Apenas se tipea, el estado principal se reemplaza por una lista plana de resultados
/// que busca en TODO: los del app, los libres y la cheatsheet de CUALQUIER proceso del store.
///
/// Por qué los libres: lo que más se olvida es justo lo que nunca tiene el foco cuando abrís el
/// helper (PowerToys corre atrás; los de Windows no son de ninguna ventana), y la cheatsheet per-proc
/// no tenía dónde guardarlo. Y por qué el buscador: con todo junto, scrollear para encontrar un atajo
/// hacía el panel inutilizable — el helper es un ayuda-memoria, se usa tipeando lo que querés hacer.
///
/// El proc/título de la app con foco se capturan ANTES de abrir (lo hace el router): una vez abierto,
/// el foreground pasa a ser este panel.
/// </summary>
public partial class ShortcutsHelperWindow : Window
{
    private readonly AppShortcutStore _store;
    private readonly string _proc;
    private readonly string _title;

    // Etiqueta del grupo de los atajos propios en los resultados (nombre de producto: no se traduce).
    private const string OwnAppName = "Ampz Desktop Booster";

    // ── Paleta (misma que el legacy) ──
    private static readonly Brush Cyan      = Frozen("#00D4FF"); // combinación
    private static readonly Brush LightGray = Frozen("#CCCCCC"); // descripción
    private static readonly Brush Gray666   = Frozen("#666666"); // headers de sección / vacíos
    private static readonly Brush Gold      = Frozen("#FFD700"); // app con foco / grupo de resultados
    private static readonly Brush Dim555    = Frozen("#555555"); // proceso
    private static readonly Brush Dim777    = Frozen("#777777"); // título
    private static readonly Brush Blue57    = Frozen("#5577AA"); // hint de title-scope
    private static readonly Brush Sep333    = Frozen("#333333");
    private static readonly Brush Sep252    = Frozen("#252525");
    private static readonly FontFamily Mono = new("Consolas");

    public ShortcutsHelperWindow(AppShortcutStore store, string activeProc, string activeTitle)
    {
        InitializeComponent();
        _store = store;
        _proc = activeProc;
        _title = activeTitle;

        // Tamaño FIJO relativo a la pantalla. Antes no tenía Width/Height: el SizeToContent implícito
        // hacía que la ventana creciera con el contenido hasta salirse del monitor y los ScrollViewer
        // nunca scrolleaban (con alto infinito no hay nada que scrollear). Con alto fijo, cada columna
        // scrollea sola y el buscador queda siempre a la vista.
        var wa = SystemParameters.WorkArea;
        Width = Math.Min(1180, wa.Width * 0.85);
        Height = Math.Min(860, wa.Height * 0.85);

        CloseBtn.Click += (_, _) => Close();
        NewBtn.Click += (_, _) => OnAddCustom();
        SearchBox.TextChanged += (_, _) => OnSearchChanged();
        PreviewKeyDown += OnPreviewKeyDown;

        // El foco al buscador: FocusedElement (XAML) cubre la activación; esto lo fuerza también a nivel
        // de TECLADO por si la ventana se activó antes de que el árbol estuviera listo (ShowFocused
        // machaca el foreground con reintentos, y la ventana restaura el FocusedElement al reactivarse).
        Loaded += (_, _) => { SearchBox.Focus(); Keyboard.Focus(SearchBox); };

        BuildLeftColumn();
        RenderRightColumn();
    }

    private bool IsSearching => SearchBox.Text.Trim() != "";

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            // Esc en dos tiempos: primero limpia la búsqueda (volvés al estado principal), recién
            // con el buscador vacío cierra. Cerrar de una con texto tipeado se siente como perderlo.
            e.Handled = true;
            if (SearchBox.Text != "")
            {
                SearchBox.Clear();
                SearchBox.Focus();
            }
            else
                Close();
        }
        else if (e.Key == Key.N && Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            OnAddCustom();
        }
    }

    // ──────────────────────────── Columna izquierda (estática) ────────────────────────────

    /// <summary>
    /// Atajos GLOBALES del app — reflejan los bindings actuales (HotkeyRouter), no el legacy.
    /// Las teclas de los escritorios son configurables (Config → Escritorios) y los dinámicos toman
    /// la que el launcher reparte: por eso NO se listan "Numpad 2 → X" con nombres fijos, que quedaban
    /// mintiendo apenas el usuario renombraba o reasignaba un desk.
    /// </summary>
    private static (string key, string desc)[] GetGlobalShortcuts() =>
    [
        ($"§{Loc.T("Helper.SectionDesktops")}", ""),
        ("Win + Numpad 1..9",     Loc.T("Helper.GoDeskKey")),
        (Loc.T("Helper.KeyCurrentDesk"), Loc.T("Helper.GoPrevious")),
        ("NumpadClear",           Loc.T("Helper.DeskPicker")),
        ("", ""),
        ($"§{Loc.T("Helper.SectionMoveWindow")}", ""),
        ("Win + Shift + (nav)",   Loc.T("Helper.SendAndFollow")),
        ("Win + Numpad −",        Loc.T("Helper.SendPicker")),
        ("", ""),
        ($"§{Loc.T("Helper.SectionProjects")}", ""),
        ("Win + NumpadEnter",     Loc.T("Helper.LauncherOpen")),
        ("Win + Numpad *",        Loc.T("Helper.Variables")),
        ("Win + Numpad /",        Loc.T("Helper.Notes")),
        ("Win + Numpad +",        Loc.T("Helper.Services")),
        ("Win + Numpad 0",        Loc.T("Helper.Tasks")),
        ("", ""),
        ($"§{Loc.T("Helper.SectionExplorer")}", ""),
        ("Win + `",               Loc.T("Helper.Terminal")),
        ("Win + F2",              Loc.T("Helper.OpenWith")),
        ("Win + F11",             Loc.T("Helper.Downloads")),
        ("", ""),
        ($"§{Loc.T("Helper.SectionSystem")}", ""),
        ("Win + D",               Loc.T("Helper.ShowDesktop")),
        ("Win + F3",              Loc.T("Helper.EnvVars")),
        ("Win + F5",              Loc.T("Helper.Docker")),
        ("Win + F6",              Loc.T("Helper.PinToggle")),
        ("Win + F9",              Loc.T("Helper.Whitelist")),
        ("Win + F12",             Loc.T("Helper.Hz")),
        ("", ""),
        ("Win + /",               Loc.T("Helper.TogglePanel")),
    ];

    private void BuildLeftColumn()
    {
        foreach (var (key, desc) in GetGlobalShortcuts())
        {
            if (key == "" && desc == "")
                AddSpacer(LeftColumn);
            else if (key.StartsWith('§'))
                AddSectionHeader(LeftColumn, key[1..]);
            else
                AddShortcutRow(LeftColumn, key, desc);
        }
    }

    // ──────────────────── Columna derecha (app con foco + atajos libres, editable) ────────────────────

    private void RenderRightColumn()
    {
        RightColumn.Children.Clear();
        RenderFocusedApp();
        RenderCustom();
    }

    private void RenderFocusedApp()
    {
        AddSectionHeader(RightColumn, Loc.T("Helper.FocusedAppSection"));

        // Encabezado: 📌 alias/proc + botón de alias.
        string alias = _store.GetAlias(_proc);
        string label = _proc == "" ? Loc.T("Helper.NoApp") : (alias != "" ? alias : _proc);

        var headerRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        headerRow.Children.Add(new TextBlock
        {
            Text = "📌 " + label, Foreground = Gold, FontSize = 16, FontWeight = FontWeights.Bold,
            VerticalAlignment = VerticalAlignment.Center,
        });
        if (_proc != "")
        {
            var btnAlias = new Button { Content = Loc.T("Helper.AliasBtn"), Height = 26, Margin = new Thickness(10, 0, 0, 0), Padding = new Thickness(8, 0, 8, 0) };
            btnAlias.Click += (_, _) => OnEditAlias();
            headerRow.Children.Add(btnAlias);
        }
        RightColumn.Children.Add(headerRow);

        if (alias != "" && _proc != "")
            RightColumn.Children.Add(new TextBlock { Text = Loc.T("Helper.ProcLabel") + _proc, Foreground = Dim555, FontFamily = Mono, FontSize = 11, Margin = new Thickness(0, 2, 0, 0) });

        if (_title != "")
        {
            var t = _title.Length > 90 ? _title[..87] + "…" : _title;
            RightColumn.Children.Add(new TextBlock { Text = Loc.T("Helper.TitleLabel") + t, Foreground = Dim777, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 2, 0, 0) });
        }

        RightColumn.Children.Add(new Border { Height = 1, Background = Sep252, Margin = new Thickness(0, 10, 0, 8) });

        if (_proc == "")
        {
            AddEmptyHint(RightColumn, Loc.T("Helper.NoFocusedApp"));
            return;
        }

        // Filas filtradas por title-scope: title vacío = siempre; con texto = sólo si el título lo contiene.
        var items = _store.GetShortcuts(_proc);
        int shown = 0;
        foreach (var it in items)
        {
            bool visible = it.Title == "" || (_title != "" && _title.Contains(it.Title, StringComparison.OrdinalIgnoreCase));
            if (!visible) continue;
            AddProcRow(RightColumn, _proc, it);
            shown++;
        }

        if (items.Count == 0)
        {
            AddEmptyHint(RightColumn, string.Format(Loc.T("Helper.NoShortcutsForProc"), _proc));
            AddEmptyHint(RightColumn, Loc.T("Helper.AddShortcutHint"));
        }
        else if (shown == 0)
        {
            AddEmptyHint(RightColumn, Loc.T("Helper.NoShortcutsForTitle"));
        }

        // Botón de alta.
        var btnAdd = new Button { Content = Loc.T("Helper.AddShortcutBtn"), Height = 32, Margin = new Thickness(0, 12, 0, 0), HorizontalContentAlignment = HorizontalAlignment.Center };
        btnAdd.Click += (_, _) => OnAddShortcut();
        RightColumn.Children.Add(btnAdd);
    }

    /// <summary>Atajos LIBRES agrupados por su título (PowerToys, Windows…), editables.</summary>
    private void RenderCustom()
    {
        AddSectionHeader(RightColumn, Loc.T("Helper.CustomSection"));

        var all = _store.GetCustom();
        if (all.Count == 0)
        {
            AddEmptyHint(RightColumn, Loc.T("Helper.NoCustom"));
            return;
        }

        // GetCustom ya viene ordenado por App: agrupar ignorando mayúsculas para que "powertoys" y
        // "PowerToys" no aparezcan como dos grupos.
        foreach (var group in all.GroupBy(c => c.App, StringComparer.CurrentCultureIgnoreCase))
        {
            AddGroupTitle(RightColumn, group.First().App, null);
            foreach (var c in group)
                AddCustomRow(RightColumn, c);
        }
    }

    // ──────────────────────────── Búsqueda ────────────────────────────

    /// <summary>Un atajo de CUALQUIER origen, normalizado para buscar y pintar.</summary>
    private sealed record Entry(string Group, string Source, string Key, string Desc, string TitleScope,
        string Haystack, Action? Edit, Action? Delete);

    private void OnSearchChanged()
    {
        SearchPlaceholder.Visibility = SearchBox.Text == "" ? Visibility.Visible : Visibility.Collapsed;

        if (IsSearching)
        {
            MainView.Visibility = Visibility.Collapsed;
            ResultsScroll.Visibility = Visibility.Visible;
            RenderResults();
        }
        else
        {
            // Volver al estado principal re-renderiza la derecha: una edición hecha desde los
            // resultados (alias, alta, borrado) tiene que verse acá también.
            ResultsScroll.Visibility = Visibility.Collapsed;
            MainView.Visibility = Visibility.Visible;
            RenderRightColumn();
        }
    }

    /// <summary>Re-pinta la vista activa tras una edición (los resultados se recalculan con el store nuevo).</summary>
    private void Refresh()
    {
        if (IsSearching) RenderResults();
        else RenderRightColumn();
    }

    private void RenderResults()
    {
        ResultsPanel.Children.Clear();
        ResultsScroll.ScrollToTop();

        // Cada término (separado por espacios) tiene que aparecer en algún lado — app, descripción o
        // combinación —, en cualquier orden: "power color" encuentra el Color Picker de PowerToys.
        var terms = Fold(SearchBox.Text).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var hits = CollectEntries().Where(e => terms.All(t => e.Haystack.Contains(t, StringComparison.Ordinal))).ToList();

        if (hits.Count == 0)
        {
            AddEmptyHint(ResultsPanel, string.Format(Loc.T("Helper.NoResults"), SearchBox.Text.Trim()));
            AddEmptyHint(ResultsPanel, Loc.T("Helper.NoResultsHint"));
            return;
        }

        // CollectEntries ya devuelve el orden de grupos deseado; GroupBy preserva el de primera aparición.
        foreach (var group in hits.GroupBy(h => (h.Group, h.Source)))
        {
            AddGroupTitle(ResultsPanel, group.Key.Group, group.Key.Source);
            foreach (var h in group)
                AddEditableRow(ResultsPanel, h.Key, h.Desc, h.TitleScope, h.Edit, h.Delete);
        }
    }

    /// <summary>
    /// Todos los atajos buscables, en el orden en que se muestran: los del app, los libres y la
    /// cheatsheet de TODOS los procesos del store (la app con foco primero). No sólo la con foco: el
    /// caso de uso es justamente acordarse del atajo de una app que no tenés delante.
    /// </summary>
    private IEnumerable<Entry> CollectEntries()
    {
        // 1) Los del app. La sección entra al haystack: buscar "escritorio" trae toda la navegación.
        string section = "";
        foreach (var (key, desc) in GetGlobalShortcuts())
        {
            if (key.StartsWith('§')) { section = key[1..]; continue; }
            if (key == "") continue;
            yield return new Entry(OwnAppName, Loc.T("Helper.SourceOwn"), key, desc, "",
                Fold($"{OwnAppName} {section} {key} {desc}"), null, null);
        }

        // 2) Los libres.
        foreach (var c in _store.GetCustom())
        {
            var it = c; // captura estable para los handlers
            yield return new Entry(it.App, Loc.T("Helper.SourceCustom"), it.Key, it.Desc, "",
                Fold($"{it.App} {it.Key} {it.Desc}"), () => OnEditCustom(it), () => OnDeleteCustom(it));
        }

        // 3) Las cheatsheets per-proc. Se busca por alias Y por proc: el usuario recuerda "Notion",
        //    no necesariamente "Notion.exe". El filtro de título NO filtra acá (no hay ventana contra
        //    la cual comparar); se muestra como hint para que se entienda cuándo aplica.
        var procs = _store.Apps.Keys
            .OrderBy(p => string.Equals(p, _proc, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(p => _store.GetAlias(p) is { Length: > 0 } a ? a : p, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        foreach (var proc in procs)
        {
            string alias = _store.GetAlias(proc);
            string group = alias != "" ? $"{alias}  ·  {proc}" : proc;
            foreach (var s in _store.GetShortcuts(proc))
            {
                var it = s;
                yield return new Entry(group, Loc.T("Helper.SourceApp"), it.Key, it.Desc, it.Title,
                    Fold($"{alias} {proc} {it.Key} {it.Desc} {it.Title}"),
                    () => OnEditShortcut(proc, it), () => OnDeleteShortcut(proc, it));
            }
        }
    }

    /// <summary>
    /// Normaliza para comparar: minúsculas y SIN tildes ("búsqueda" == "busqueda"). Se descompone a
    /// FormD y se descartan las marcas combinantes — el usuario tipea rápido y sin acentos.
    /// </summary>
    private static string Fold(string s)
    {
        var d = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(d.Length);
        foreach (var ch in d)
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
                sb.Append(char.ToLowerInvariant(ch));
        return sb.ToString();
    }

    // ──────────────────────────── Handlers ────────────────────────────

    private void OnAddShortcut()
    {
        var r = AppShortcutDialog.Show(this, _proc, _title);
        if (r is null) return;
        _store.AddOrUpdate(_proc, new AppShortcut { Id = _store.NextId(_proc), Key = r.Key, Desc = r.Desc, Title = r.Title });
        Refresh();
    }

    private void OnEditShortcut(string proc, AppShortcut it)
    {
        // El título activo sólo es contexto útil si estamos editando la app que tenía el foco.
        string activeTitle = string.Equals(proc, _proc, StringComparison.OrdinalIgnoreCase) ? _title : "";
        var r = AppShortcutDialog.Show(this, proc, activeTitle, it);
        if (r is null) return;
        _store.AddOrUpdate(proc, new AppShortcut { Id = it.Id, Key = r.Key, Desc = r.Desc, Title = r.Title });
        Refresh();
    }

    private void OnDeleteShortcut(string proc, AppShortcut it)
    {
        if (!ConfirmDelete(it.Key, it.Desc)) return;
        _store.Delete(proc, it.Id);
        Refresh();
    }

    private void OnAddCustom()
    {
        // Con una búsqueda en curso el texto suele ser el nombre de la app que no encontraste
        // ("powertoys") → se precarga como título. Si no, vacío y el diálogo arranca en ese campo.
        var r = AppShortcutDialog.ShowCustom(this, _store.CustomApps(), SearchBox.Text.Trim());
        if (r is null) return;
        _store.AddOrUpdateCustom(new CustomShortcut { Id = _store.NextCustomId(), App = r.App, Key = r.Key, Desc = r.Desc });
        Refresh();
        SearchBox.Focus();
    }

    private void OnEditCustom(CustomShortcut it)
    {
        var r = AppShortcutDialog.ShowCustom(this, _store.CustomApps(), it.App, it);
        if (r is null) return;
        _store.AddOrUpdateCustom(new CustomShortcut { Id = it.Id, App = r.App, Key = r.Key, Desc = r.Desc });
        Refresh();
    }

    private void OnDeleteCustom(CustomShortcut it)
    {
        if (!ConfirmDelete(it.Key, it.Desc)) return;
        _store.DeleteCustom(it.Id);
        Refresh();
    }

    private bool ConfirmDelete(string key, string desc)
        => MessageBox.Show(this, string.Format(Loc.T("Helper.DeleteConfirmMsg"), key, desc),
               Loc.T("Helper.DeleteConfirmTitle"), MessageBoxButton.YesNo, MessageBoxImage.Warning)
           == MessageBoxResult.Yes;

    private void OnEditAlias()
    {
        // Reusamos PromptDialog (input de una línea). "" = volver a mostrar el process name.
        var alias = PromptDialog.Show(this, Loc.T("Helper.AliasDialogTitle") + _proc,
            Loc.T("Helper.AliasDialogLabel"), _store.GetAlias(_proc));
        if (alias is null) return; // cancelado
        _store.SetAlias(_proc, alias);
        Refresh();
    }

    // ──────────────────────────── Builders comunes ────────────────────────────

    private void AddProcRow(StackPanel panel, string proc, AppShortcut it)
        => AddEditableRow(panel, it.Key, it.Desc, it.Title, () => OnEditShortcut(proc, it), () => OnDeleteShortcut(proc, it));

    private void AddCustomRow(StackPanel panel, CustomShortcut it)
        => AddEditableRow(panel, it.Key, it.Desc, "", () => OnEditCustom(it), () => OnDeleteCustom(it));

    /// <summary>
    /// Fila combinación + descripción, con ✎/✕ sólo si el origen es editable (los atajos del app son
    /// fijos: no tienen acciones). Sirve igual al estado principal y a los resultados.
    /// </summary>
    private static void AddEditableRow(StackPanel panel, string keyText, string descText, string titleScope,
        Action? onEdit, Action? onDelete)
    {
        var g = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(180) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var key = new TextBlock { Text = keyText, Foreground = Cyan, FontFamily = Mono, FontSize = 13, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        var desc = new TextBlock { Text = descText, Foreground = LightGray, FontSize = 13, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 6, 0) };
        Grid.SetColumn(key, 0); Grid.SetColumn(desc, 1);
        g.Children.Add(key); g.Children.Add(desc);

        if (onEdit is not null)
        {
            var btnEdit = new Button { Content = "✎", Width = 28, Height = 24, Margin = new Thickness(2, 0, 0, 0) };
            btnEdit.Click += (_, _) => onEdit();
            Grid.SetColumn(btnEdit, 2);
            g.Children.Add(btnEdit);
        }
        if (onDelete is not null)
        {
            var btnDel = new Button { Content = "✕", Width = 28, Height = 24, Margin = new Thickness(4, 0, 0, 0) };
            btnDel.Click += (_, _) => onDelete();
            Grid.SetColumn(btnDel, 3);
            g.Children.Add(btnDel);
        }
        panel.Children.Add(g);

        // Hint de title-scope, igual que el legacy ("↳ solo en títulos con 'X'").
        if (titleScope != "")
            panel.Children.Add(new TextBlock { Text = string.Format(Loc.T("Helper.TitleScope"), titleScope), Foreground = Blue57, FontStyle = FontStyles.Italic, FontSize = 11, Margin = new Thickness(6, 0, 0, 4) });
    }

    /// <summary>Título de grupo (nombre de app) con el origen opcional en gris al lado.</summary>
    private static void AddGroupTitle(StackPanel panel, string title, string? source)
    {
        var tb = new TextBlock { Margin = new Thickness(0, 12, 0, 4), TextTrimming = TextTrimming.CharacterEllipsis };
        tb.Inlines.Add(new System.Windows.Documents.Run(title) { Foreground = Gold, FontSize = 14, FontWeight = FontWeights.Bold });
        if (!string.IsNullOrEmpty(source))
            tb.Inlines.Add(new System.Windows.Documents.Run("   " + source) { Foreground = Dim777, FontSize = 11 });
        panel.Children.Add(tb);
    }

    private static void AddSectionHeader(StackPanel panel, string title)
    {
        panel.Children.Add(new Border { Height = 1, Background = Sep333, Margin = new Thickness(0, 12, 0, 6) });
        panel.Children.Add(new TextBlock { Text = title, Foreground = Gray666, FontSize = 11, FontWeight = FontWeights.Bold });
    }

    private static void AddShortcutRow(StackPanel panel, string key, string desc)
    {
        var g = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(180) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var k = new TextBlock { Text = key, Foreground = Cyan, FontFamily = Mono, FontSize = 13, TextWrapping = TextWrapping.Wrap };
        var d = new TextBlock { Text = desc, Foreground = LightGray, FontSize = 13, TextWrapping = TextWrapping.Wrap };
        Grid.SetColumn(k, 0); Grid.SetColumn(d, 1);
        g.Children.Add(k); g.Children.Add(d);
        panel.Children.Add(g);
    }

    private static void AddSpacer(StackPanel panel)
        => panel.Children.Add(new Border { Height = 6, Background = Brushes.Transparent });

    private static void AddEmptyHint(StackPanel panel, string text)
        => panel.Children.Add(new TextBlock { Text = text, Foreground = Gray666, FontStyle = FontStyles.Italic, FontSize = 13, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 2) });

    private static SolidColorBrush Frozen(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }
}
