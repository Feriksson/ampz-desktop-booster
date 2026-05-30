using System;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using AmpzDesktopBooster.Desktops;

namespace AmpzDesktopBooster;

/// <summary>
/// Diálogo de Win+NumpadEnter: setea el proyecto del desk actual (sólo "DESK +N").
/// Textbox pre-cargado con la sugerencia/proyecto actual + lista del historial filtrable.
/// Enter prioriza: (1) fila seleccionada, (2) único resultado visible, (3) texto del textbox
/// como proyecto NUEVO. Supr sobre una fila → borrado en cascada del historial.
/// </summary>
public partial class ProjectSetterWindow : Window
{
    private readonly int _deskIdx;
    private readonly ProjectStore _store;
    private readonly Action _onChanged;

    public ProjectSetterWindow(int deskIdx, string deskName, ProjectStore store, Action onChanged)
    {
        InitializeComponent();

        _deskIdx = deskIdx;
        _store = store;
        _onChanged = onChanged;

        HeaderText.Text = "Proyecto para este escritorio";
        SubHeaderText.Text = deskName;

        // Pre-cargar con el proyecto activo (sesión) o, si no hay, la sugerencia persistida.
        string seed = store.GetDeskProject(deskIdx);
        if (seed == "") seed = store.GetSuggestion(deskIdx);
        FilterBox.Text = seed;

        RefreshList();

        FilterBox.TextChanged += (_, _) => RefreshList();
        FilterBox.PreviewKeyDown += OnFilterKeyDown;
        HistoryList.PreviewKeyDown += OnListKeyDown;
        HistoryList.MouseDoubleClick += (_, _) => Confirm();
        RemoveBtn.Click += (_, _) => { _store.RemoveDeskProject(_deskIdx); _onChanged(); Close(); };
        CloseBtn.Click += (_, _) => Close();

        Loaded += (_, _) => { FilterBox.Focus(); FilterBox.SelectAll(); };
    }

    private void RefreshList()
    {
        string filter = FilterBox.Text.Trim();
        HistoryList.Items.Clear();
        foreach (var p in _store.GetHistory())
        {
            if (filter == "" || p.Contains(filter, StringComparison.OrdinalIgnoreCase))
                HistoryList.Items.Add(p);
        }
    }

    private void OnFilterKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)      { Confirm(); e.Handled = true; }
        else if (e.Key == Key.Escape) { Close();  e.Handled = true; }
        else if (e.Key == Key.Down && HistoryList.Items.Count > 0)
        {
            HistoryList.SelectedIndex = 0;
            HistoryList.Focus();
            e.Handled = true;
        }
    }

    private void OnListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)       { Confirm(); e.Handled = true; }
        else if (e.Key == Key.Escape) { Close();   e.Handled = true; }
        else if (e.Key == Key.Delete) { DeleteSelectedFromHistory(); e.Handled = true; }
    }

    private void Confirm()
    {
        // Prioridad: fila seleccionada → único resultado visible → texto del textbox (nuevo).
        string name = HistoryList.SelectedItem as string ?? "";
        if (name == "" && HistoryList.Items.Count == 1)
            name = (string)HistoryList.Items[0];
        if (name == "")
            name = FilterBox.Text.Trim();

        if (name == "")
            return;

        _store.SetDeskProject(_deskIdx, name);
        _onChanged();
        Close();
    }

    private void DeleteSelectedFromHistory()
    {
        if (HistoryList.SelectedItem is not string name)
            return;

        var resp = MessageBox.Show(
            $"¿Borrar '{name}' del historial?\nSe eliminan también sus paths y notas.",
            "Borrar proyecto", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (resp != MessageBoxResult.Yes)
            return;

        _store.DeleteFromHistory(name);
        _onChanged();
        RefreshList();
    }
}
