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
        RemoveBtn.Click += (_, _) => ResetAndClose();
        CloseBtn.Click += (_, _) => Close();

        Loaded += (_, _) => { FilterBox.Focus(); FilterBox.SelectAll(); };
    }

    /// <summary>
    /// Reset del desk: saca el proyecto de la sesión y cierra. Lo dispara tanto el botón "Quitar"
    /// como el re-press del hotkey Win+NumpadEnter (instancia única en el router) — un solo camino,
    /// sin duplicar lógica. No toca historial ni catálogo (eso es RemoveDeskProject, no DeleteFromHistory).
    /// </summary>
    public void ResetAndClose()
    {
        _store.RemoveDeskProject(_deskIdx);
        _onChanged();
        Close();
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

        // El nombre de proyecto no puede pasar de 23 caracteres. El textbox ya lo frena con MaxLength,
        // pero una fila del historial creada antes de esta regla podría superarlo → la cortamos acá.
        if (name.Length > 23)
        {
            MessageBox.Show(
                $"El nombre del proyecto no puede tener más de 23 caracteres (tiene {name.Length}).",
                "Nombre demasiado largo", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

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
