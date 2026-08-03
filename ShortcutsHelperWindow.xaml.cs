using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AmpzDesktopBooster.Apps;
using AmpzDesktopBooster.Services.Localization;

namespace AmpzDesktopBooster;

/// <summary>
/// Shortcuts Helper (Win+/). Panel overlay a dos columnas: IZQUIERDA la cheatsheet global del app
/// (estática, con los bindings REALES de hoy — no los del legacy), DERECHA la cheatsheet editable
/// de la app que tenía el foco al abrir (atajos per-app + alias, con add/edit/delete). Porta el
/// ShowShortcutsHelper del legacy, pero en vez de destruir/recrear todo en cada edición, sólo
/// re-renderiza la columna derecha (RenderRightColumn) — mismo comportamiento, más prolijo.
///
/// El proc/título de la app con foco se capturan ANTES de abrir (lo hace el router): una vez abierto,
/// el foreground pasa a ser este panel.
/// </summary>
public partial class ShortcutsHelperWindow : Window
{
    private readonly AppShortcutStore _store;
    private readonly string _proc;
    private readonly string _title;

    // ── Paleta (misma que el legacy) ──
    private static readonly Brush Cyan      = Frozen("#00D4FF"); // combinación
    private static readonly Brush LightGray = Frozen("#CCCCCC"); // descripción
    private static readonly Brush Gray666   = Frozen("#666666"); // headers de sección / vacíos
    private static readonly Brush Gold      = Frozen("#FFD700"); // app con foco
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

        CloseBtn.Click += (_, _) => Close();
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };

        BuildLeftColumn();
        RenderRightColumn();
    }

    // ──────────────────────────── Columna izquierda (estática) ────────────────────────────

    /// <summary>Atajos GLOBALES del app — reflejan los bindings actuales (HotkeyRouter), no el legacy.</summary>
    private static (string key, string desc)[] GetGlobalShortcuts() =>
    [
        ($"§{Loc.T("Helper.SectionDesktops")}", ""),
        ("Win + Numpad 1",        Loc.T("Helper.GoMain")),
        ("Win + Numpad 2",        Loc.T("Helper.GoMails")),
        ("Win + Numpad 3",        Loc.T("Helper.GoMiscs")),
        ("Win + Numpad 4..9",     Loc.T("Helper.GoDeskN")),
        ("NumpadClear",           Loc.T("Helper.DeskPicker")),
        ("", ""),
        ($"§{Loc.T("Helper.SectionMoveWindow")}", ""),
        ("Win + Shift + (nav)",   Loc.T("Helper.SendAndFollow")),
        ("Win + Numpad −",        Loc.T("Helper.SendPicker")),
        ("", ""),
        ($"§{Loc.T("Helper.SectionProjects")}", ""),
        ("Win + NumpadEnter",     Loc.T("Helper.SetProject")),
        ("Win + Numpad .",        Loc.T("Helper.SetModule")),
        ("Win + Numpad *",        Loc.T("Helper.Variables")),
        ("Win + Numpad /",        Loc.T("Helper.Notes")),
        ("", ""),
        ($"§{Loc.T("Helper.SectionExplorer")}", ""),
        ("Win + `",               Loc.T("Helper.Terminal")),
        ("Win + F2",              Loc.T("Helper.OpenWith")),
        ("Win + F11",             Loc.T("Helper.Downloads")),
        ("", ""),
        ($"§{Loc.T("Helper.SectionSystem")}", ""),
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

    // ──────────────────────────── Columna derecha (per-app, editable) ─────────────────────

    private void RenderRightColumn()
    {
        RightColumn.Children.Clear();

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
            AddEmptyHint(Loc.T("Helper.NoFocusedApp"));
            return;
        }

        // Filas filtradas por title-scope: title vacío = siempre; con texto = sólo si el título lo contiene.
        var items = _store.GetShortcuts(_proc);
        int shown = 0;
        foreach (var it in items)
        {
            bool visible = it.Title == "" || (_title != "" && _title.Contains(it.Title, StringComparison.OrdinalIgnoreCase));
            if (!visible) continue;
            AddEditableRow(it);
            shown++;
        }

        if (items.Count == 0)
        {
            AddEmptyHint(string.Format(Loc.T("Helper.NoShortcutsForProc"), _proc));
            AddEmptyHint(Loc.T("Helper.AddShortcutHint"));
        }
        else if (shown == 0)
        {
            AddEmptyHint(Loc.T("Helper.NoShortcutsForTitle"));
        }

        // Botón de alta.
        var btnAdd = new Button { Content = Loc.T("Helper.AddShortcutBtn"), Height = 32, Margin = new Thickness(0, 12, 0, 0), HorizontalContentAlignment = HorizontalAlignment.Center };
        btnAdd.Click += (_, _) => OnAddShortcut();
        RightColumn.Children.Add(btnAdd);
    }

    private void AddEditableRow(AppShortcut it)
    {
        var g = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var key = new TextBlock { Text = it.Key, Foreground = Cyan, FontFamily = Mono, FontSize = 13, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        var desc = new TextBlock { Text = it.Desc, Foreground = LightGray, FontSize = 13, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 6, 0) };

        var btnEdit = new Button { Content = "✎", Width = 28, Height = 24, Margin = new Thickness(2, 0, 0, 0) };
        var btnDel  = new Button { Content = "✕", Width = 28, Height = 24, Margin = new Thickness(4, 0, 0, 0) };
        btnEdit.Click += (_, _) => OnEditShortcut(it);
        btnDel.Click  += (_, _) => OnDeleteShortcut(it);

        Grid.SetColumn(key, 0); Grid.SetColumn(desc, 1); Grid.SetColumn(btnEdit, 2); Grid.SetColumn(btnDel, 3);
        g.Children.Add(key); g.Children.Add(desc); g.Children.Add(btnEdit); g.Children.Add(btnDel);
        RightColumn.Children.Add(g);

        // Hint de title-scope, igual que el legacy ("↳ solo en títulos con 'X'").
        if (it.Title != "")
            RightColumn.Children.Add(new TextBlock { Text = string.Format(Loc.T("Helper.TitleScope"), it.Title), Foreground = Blue57, FontStyle = FontStyles.Italic, FontSize = 11, Margin = new Thickness(6, 0, 0, 4) });
    }

    // ──────────────────────────── Handlers ────────────────────────────

    private void OnAddShortcut()
    {
        var r = AppShortcutDialog.Show(this, _proc, _title);
        if (r is null) return;
        _store.AddOrUpdate(_proc, new AppShortcut { Id = _store.NextId(_proc), Key = r.Key, Desc = r.Desc, Title = r.Title });
        RenderRightColumn();
    }

    private void OnEditShortcut(AppShortcut it)
    {
        var r = AppShortcutDialog.Show(this, _proc, _title, it);
        if (r is null) return;
        _store.AddOrUpdate(_proc, new AppShortcut { Id = it.Id, Key = r.Key, Desc = r.Desc, Title = r.Title });
        RenderRightColumn();
    }

    private void OnDeleteShortcut(AppShortcut it)
    {
        var res = MessageBox.Show(string.Format(Loc.T("Helper.DeleteConfirmMsg"), it.Key, it.Desc),
            Loc.T("Helper.DeleteConfirmTitle"), MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (res == MessageBoxResult.Yes)
        {
            _store.Delete(_proc, it.Id);
            RenderRightColumn();
        }
    }

    private void OnEditAlias()
    {
        // Reusamos PromptDialog (input de una línea). "" = volver a mostrar el process name.
        var alias = PromptDialog.Show(this, Loc.T("Helper.AliasDialogTitle") + _proc,
            Loc.T("Helper.AliasDialogLabel"), _store.GetAlias(_proc));
        if (alias is null) return; // cancelado
        _store.SetAlias(_proc, alias);
        RenderRightColumn();
    }

    // ──────────────────────────── Builders comunes ────────────────────────────

    private void AddSectionHeader(StackPanel panel, string title)
    {
        panel.Children.Add(new Border { Height = 1, Background = Sep333, Margin = new Thickness(0, 12, 0, 6) });
        panel.Children.Add(new TextBlock { Text = title, Foreground = Gray666, FontSize = 11, FontWeight = FontWeights.Bold });
    }

    private void AddShortcutRow(StackPanel panel, string key, string desc)
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

    private void AddEmptyHint(string text)
        => RightColumn.Children.Add(new TextBlock { Text = text, Foreground = Gray666, FontStyle = FontStyles.Italic, FontSize = 13, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 2) });

    private static SolidColorBrush Frozen(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }
}
