using System;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using AmpzDesktopBooster.Desktops;
using AmpzDesktopBooster.Services;
using AmpzDesktopBooster.Services.Localization;

namespace AmpzDesktopBooster;

/// <summary>
/// Selector de CONTEXTO (sub-scope) del espacio cargado en el desk. Se llega por dos caminos:
///   · 2do paso del setter (Win+NumpadEnter): confirmás espacio → aparece esta ventana.
///   · Win+NumpadDot (Del): cambia SÓLO el contexto, sin re-elegir espacio. El flujo rápido.
///
/// Mismo lenguaje que el setter de espacio (textbox filtro + lista + Enter confirma / Supr borra),
/// para que no haya nada nuevo que aprender. Lo único propio es el COLOR: cada contexto nace con uno
/// de la paleta y F3 lo cicla — la señal cromática es el motivo de existir de toda la feature.
/// </summary>
public partial class ModulePickerWindow : Window
{
    /// <summary>Fila del listado. <c>Accent</c> alimenta el chip de color del DataTemplate.</summary>
    private sealed record Row(string Name, string Color)
    {
        public Brush Accent => new SolidColorBrush(ModulePalette.Parse(Color));
    }

    private readonly int _deskIdx;
    private readonly string _project;
    private readonly ProjectStore _store;
    private readonly Action _onChanged;

    public ModulePickerWindow(int deskIdx, string deskName, string project, ProjectStore store, Action onChanged)
    {
        InitializeComponent();

        _deskIdx = deskIdx;
        _project = project;
        _store = store;
        _onChanged = onChanged;

        Icon = AppIcon.TryLoadForWindow();
        HeaderText.Text = string.Format(Loc.T("Modules.Header"), project);
        SubHeaderText.Text = deskName;

        // Pre-cargar con el contexto activo o, si no hay, la sugerencia persistida — misma regla que
        // el setter de espacio: la sesión manda, el INI sólo pre-llena.
        string seed = store.GetDeskModule(deskIdx);
        if (seed == "") seed = store.GetModuleSuggestion(deskIdx);
        FilterBox.Text = seed;

        RefreshList();

        FilterBox.TextChanged += (_, _) => RefreshList();
        FilterBox.PreviewKeyDown += OnFilterKeyDown;
        ModuleList.PreviewKeyDown += OnListKeyDown;
        ModuleList.MouseDoubleClick += (_, _) => Confirm();
        NoModuleBtn.Click += (_, _) => ClearAndClose();
        CloseBtn.Click += (_, _) => Close();

        Loaded += (_, _) => { FilterBox.Focus(); FilterBox.SelectAll(); };
    }

    /// <summary>
    /// Deja el desk en el espacio PELADO (sin contexto) y cierra. Lo disparan el botón "Sin contexto"
    /// y el re-press del hotkey — un solo camino, igual que el ResetAndClose del setter.
    /// </summary>
    public void ClearAndClose()
    {
        _store.SetDeskModule(_deskIdx, "");
        _onChanged();
        Close();
    }

    private void RefreshList()
    {
        string filter = FilterBox.Text.Trim();
        ModuleList.Items.Clear();

        var modules = _store.GetModules(_project);
        foreach (var m in modules.OrderBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase))
            if (filter == "" || m.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                ModuleList.Items.Add(new Row(m.Name, m.Color));

        // El hint de "todavía no hay contextos" mira el CATÁLOGO, no el filtro: si tenés contextos pero
        // el filtro no matchea, no hace falta explicarte qué es un contexto — ya lo sabés.
        EmptyHint.Visibility = modules.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnFilterKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)       { Confirm(); e.Handled = true; }
        else if (e.Key == Key.Escape) { Close();   e.Handled = true; }
        else if (e.Key == Key.Down && ModuleList.Items.Count > 0)
        {
            ModuleList.SelectedIndex = 0;
            ModuleList.Focus();
            e.Handled = true;
        }
    }

    private void OnListKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:  Confirm();       e.Handled = true; break;
            case Key.Escape: Close();         e.Handled = true; break;
            case Key.Delete: DeleteSelected(); e.Handled = true; break;
            case Key.F3:     CycleColor();     e.Handled = true; break;
        }
    }

    /// <summary>
    /// Confirma el contexto. Misma prioridad que el setter de espacio: fila seleccionada → único
    /// resultado visible → texto del textbox (contexto NUEVO, que se da de alta con color automático).
    /// Con el textbox vacío y nada seleccionado equivale a "sin contexto": no te obliga a apuntarle
    /// al botón para volver al espacio pelado.
    /// </summary>
    private void Confirm()
    {
        string name = (ModuleList.SelectedItem as Row)?.Name ?? "";
        if (name == "" && ModuleList.Items.Count == 1)
            name = ((Row)ModuleList.Items[0]).Name;
        if (name == "")
            name = ProjectStore.Sanitize(FilterBox.Text);

        _store.SetDeskModule(_deskIdx, name);
        _onChanged();
        Close();
    }

    /// <summary>F3: cicla el color del contexto seleccionado por la paleta y repinta sin cerrar.</summary>
    private void CycleColor()
    {
        if (ModuleList.SelectedItem is not Row row) return;

        _store.SetModuleColor(_project, row.Name, ModulePalette.Next(row.Color));
        RefreshList();

        // Re-seleccionar por NOMBRE: RefreshList reconstruye las filas, así que la referencia vieja
        // ya no está en la lista y la selección se perdería justo cuando querés seguir ciclando.
        ModuleList.SelectedItem = ModuleList.Items.OfType<Row>()
            .FirstOrDefault(r => string.Equals(r.Name, row.Name, StringComparison.OrdinalIgnoreCase));

        // El feedback ya lo da la barra/overlay del desk actual si este contexto está activo ahí.
        _onChanged();
    }

    /// <summary>Supr: borra el contexto del catálogo EN CASCADA (sus variables y notas se van con él).</summary>
    private void DeleteSelected()
    {
        if (ModuleList.SelectedItem is not Row row) return;

        var resp = MessageBox.Show(
            string.Format(Loc.T("Modules.DeleteConfirm"), row.Name),
            Loc.T("Modules.DeleteTitle"), MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (resp != MessageBoxResult.Yes) return;

        _store.DeleteModule(_project, row.Name);
        _onChanged();
        RefreshList();
    }
}
