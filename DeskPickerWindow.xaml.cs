using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using AmpzDesktopBooster.Desktops;

namespace AmpzDesktopBooster;

/// <summary>
/// Diálogo de NumpadClear (pelado): lista los desks que tienen un proyecto activo en la sesión,
/// con filtro en vivo. Enter salta al desk seleccionado; Supr lo quita de la sesión.
/// </summary>
public partial class DeskPickerWindow : Window
{
    /// <summary>Una fila del picker. ToString define lo que muestra el ListBox.</summary>
    private sealed record Row(int Idx, string Name, string Project)
    {
        public override string ToString() => $"{Name}   —   {Project}";
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
            .Select(e => new Row(e.Idx, _desktops.GetName(e.Idx), e.Project))
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
                || r.Project.Contains(filter, StringComparison.OrdinalIgnoreCase))
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
            "¿Quitar todos los proyectos de la sesión?",
            "Limpiar todo", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (resp != MessageBoxResult.Yes)
            return;
        _store.ClearAllSession();
        Close();
    }
}
