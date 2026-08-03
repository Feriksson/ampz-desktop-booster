using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using AmpzDesktopBooster.Desktops;
using AmpzDesktopBooster.Services.Localization;

namespace AmpzDesktopBooster;

/// <summary>
/// Diálogo de NumpadClear (pelado): lista los desks que tienen un espacio activo en la sesión,
/// con filtro en vivo. Enter salta al desk seleccionado; Supr lo quita de la sesión.
/// </summary>
public partial class DeskPickerWindow : Window
{
    /// <summary>
    /// Una fila del picker: desk + espacio + contexto. Las props derivadas (Accent/Visibility) las
    /// bindea el DataTemplate — así el XAML no necesita converters para pintar el color del contexto.
    /// </summary>
    private sealed record Row(int Idx, string Name, string Project, string Module, string ModuleColor)
    {
        public Brush ModuleAccent => new SolidColorBrush(ModulePalette.Parse(ModuleColor));
        public Visibility ModuleVisibility => Module == "" ? Visibility.Collapsed : Visibility.Visible;
    }

    private readonly DesktopService _desktops;
    private readonly ProjectStore _store;
    private readonly Action<int> _onJump;
    private List<Row> _all = new();

    public DeskPickerWindow(DesktopService desktops, ProjectStore store, Action<int> onJump)
    {
        InitializeComponent();

        _desktops = desktops;
        _store = store;
        _onJump = onJump;

        LoadRows();

        FilterBox.TextChanged += (_, _) => RefreshList();
        FilterBox.PreviewKeyDown += OnFilterKeyDown;
        DeskList.PreviewKeyDown += OnListKeyDown;
        DeskList.MouseDoubleClick += (_, _) => JumpSelected();
        ClearAllBtn.Click += (_, _) => ClearAll();
        CloseBtn.Click += (_, _) => Close();

        Loaded += (_, _) => FilterBox.Focus();
    }

    private void LoadRows()
    {
        _all = _store.SessionEntries()
            .Select(e => new Row(e.Idx, _desktops.GetName(e.Idx), e.Project, e.Module,
                                 _store.GetModuleColor(e.Project, e.Module)))
            .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        RefreshList();
    }

    private void RefreshList()
    {
        string filter = FilterBox.Text.Trim();
        DeskList.Items.Clear();
        foreach (var r in _all)
        {
            if (filter == ""
                || r.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || r.Project.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || r.Module.Contains(filter, StringComparison.OrdinalIgnoreCase))
                DeskList.Items.Add(r);
        }
        EmptyHint.Visibility = _all.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnFilterKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)       { JumpSelected(); e.Handled = true; }
        else if (e.Key == Key.Escape) { Close();        e.Handled = true; }
        else if (e.Key == Key.Down && DeskList.Items.Count > 0)
        {
            DeskList.SelectedIndex = 0;
            DeskList.Focus();
            e.Handled = true;
        }
    }

    private void OnListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)       { JumpSelected(); e.Handled = true; }
        else if (e.Key == Key.Escape) { Close();        e.Handled = true; }
        else if (e.Key == Key.Delete) { RemoveSelected(); e.Handled = true; }
    }

    private void JumpSelected()
    {
        // Fila seleccionada, o el único resultado visible desde el searchbox.
        var row = DeskList.SelectedItem as Row;
        if (row is null && DeskList.Items.Count == 1)
            row = (Row)DeskList.Items[0];
        if (row is null)
            return;

        _onJump(row.Idx);
        Close();
    }

    private void RemoveSelected()
    {
        if (DeskList.SelectedItem is not Row row)
            return;
        _store.RemoveDeskProject(row.Idx);
        LoadRows();
        if (_all.Count == 0)
            Close();
    }

    private void ClearAll()
    {
        if (_all.Count == 0)
            return;
        var resp = MessageBox.Show(
            Loc.T("DeskPicker.ClearAllConfirm"),
            Loc.T("DeskPicker.BtnClearAll"), MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (resp != MessageBoxResult.Yes)
            return;
        _store.ClearAllSession();
        Close();
    }
}
