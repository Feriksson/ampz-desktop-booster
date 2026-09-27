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
/// Paso 2 del LAUNCHER de Win+NumpadEnter: elegís el CONTEXTO (sub-scope) del espacio ya confirmado
/// en el paso 1 (<see cref="ProjectSetterWindow"/>). Ya NO es alcanzable por Win+NumpadDot —ese
/// atajo se RETIRÓ con la reforma de escritorios dinámicos (ver CLAUDE.md)—, así que su única
/// entrada es la que encadena el setter.
///
/// Mismo lenguaje que el paso 1 (textbox filtro + lista + Enter confirma / Supr borra). Lo propio es
/// el COLOR: cada contexto nace con uno de la paleta y F3 lo cicla. A diferencia de la versión vieja,
/// esta ventana NO asigna nada a ningún desk — sólo devuelve el contexto elegido (o "" = ninguno) por
/// <c>onCompleted</c>; es el orquestador del launcher el que crea el escritorio dinámico y lo asienta.
/// </summary>
public partial class ModulePickerWindow : Window
{
    /// <summary>Fila del listado. <c>Accent</c> alimenta el chip de color del DataTemplate.</summary>
    private sealed record Row(string Name, string Color)
    {
        public Brush Accent => new SolidColorBrush(ModulePalette.Parse(Color));
    }

    private readonly string _project;
    private readonly ProjectStore _store;
    private readonly Action<string> _onCompleted;

    public ModulePickerWindow(string project, ProjectStore store, Action<string> onCompleted)
    {
        InitializeComponent();

        _project = project;
        _store = store;
        _onCompleted = onCompleted;

        Icon = AppIcon.TryLoadForWindow();
        HeaderText.Text = string.Format(Loc.T("Modules.Header"), project);
        // No hay "desk actual" que mostrar en el subtítulo: el desk todavía no existe, lo crea el
        // launcher recién al completar este paso.
        SubHeaderText.Text = "";

        // El filtro arranca vacío y la lista se muestra ENTERA (sin contexto "actual" preseleccionado
        // — no hay ningún desk activo del que partir en este flujo).
        RefreshList();

        FilterBox.TextChanged += (_, _) => RefreshList();
        FilterBox.PreviewKeyDown += OnFilterKeyDown;
        ModuleList.PreviewKeyDown += OnListKeyDown;
        ModuleList.MouseDoubleClick += (_, _) => Confirm();
        NoModuleBtn.Click += (_, _) => Complete("");
        CloseBtn.Click += (_, _) => Close();

        Loaded += (_, _) => FilterBox.Focus();
    }

    private void RefreshList()
    {
        string filter = FilterBox.Text.Trim();
        ModuleList.Items.Clear();

        var modules = _store.GetModules(_project);
        foreach (var m in modules.OrderBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase))
            if (filter == "" || m.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                ModuleList.Items.Add(new Row(m.Name, m.Color));

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
    /// Confirma el contexto. Misma prioridad que el setter: fila seleccionada → único resultado
    /// visible → texto del textbox (contexto NUEVO). Textbox vacío y nada seleccionado equivale a
    /// "sin contexto" — no obliga a apuntarle al botón para seguir sin sub-scope.
    /// </summary>
    private void Confirm()
    {
        string name = (ModuleList.SelectedItem as Row)?.Name ?? "";
        if (name == "" && ModuleList.Items.Count == 1)
            name = ((Row)ModuleList.Items[0]).Name;
        if (name == "")
            name = ProjectStore.Sanitize(FilterBox.Text);

        Complete(name);
    }

    private void Complete(string module)
    {
        // Da de alta el contexto en el catálogo (color automático) si es nuevo. No toca sesión: el
        // desk que lo va a usar todavía no existe.
        if (module != "") _store.EnsureModule(_project, module);
        _onCompleted(module);
        Close();
    }

    /// <summary>F3: cicla el color del contexto seleccionado por la paleta y repinta sin cerrar.</summary>
    private void CycleColor()
    {
        if (ModuleList.SelectedItem is not Row row) return;

        _store.SetModuleColor(_project, row.Name, ModulePalette.Next(row.Color));
        RefreshList();

        ModuleList.SelectedItem = ModuleList.Items.OfType<Row>()
            .FirstOrDefault(r => string.Equals(r.Name, row.Name, StringComparison.OrdinalIgnoreCase));
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
        RefreshList();
    }
}
