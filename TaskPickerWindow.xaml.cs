using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using AmpzDesktopBooster.Services.Tasks;

namespace AmpzDesktopBooster;

/// <summary>
/// Picker de tareas (Win+NumLock): lista las tareas ABIERTAS que devolvió el aggregator, con filtro
/// en vivo. Enter / doble-click ancla la tarea al desk actual.
///
/// ABRE INSTANTE + LOADER: el picker NO espera al fetch para mostrarse. Se construye con cabecera y
/// loader visible, el llamador (HotkeyRouter) lo muestra de inmediato y después dispara el fetch en
/// background; cuando termina, llama a SetItems / SetError vía Dispatcher. La razón: con varias
/// cuentas + Vikunja haciendo N fetches anidados, la espera previa al picker se sentía como freeze.
///
/// La ventana NO sabe de HTTP ni de providers: recibe items o un error ya armados y devuelve la
/// elegida por callback. Separación de capas — el router orquesta el fetch, la ventana sólo muestra.
/// </summary>
public partial class TaskPickerWindow : Window
{
    /// <summary>
    /// Una fila del picker. Expone trozos PRE-FORMATEADOS que el DataTemplate (TaskPickerWindow.xaml)
    /// pinta en colores distintos. Por qué no un solo ToString: con todo en un string se pierde la
    /// jerarquía visual (título vs metadata vs columna vs board, todo gris). Separar por TextBlock
    /// permite color por trozo y que los vacíos se Colapsen vía EmptyStringToCollapsedConverter sin
    /// dejar huecos.
    /// </summary>
    private sealed record Row(TaskItem Task)
    {
        public string AccountChip   => string.IsNullOrEmpty(Task.AccountName) ? "" : $"[{Task.AccountName}]";
        public string IdLabel       => string.IsNullOrEmpty(Task.Identifier)  ? "" : $"{Task.Identifier}  —";
        public string Title         => Task.Title;
        public string StageInParens => string.IsNullOrEmpty(Task.Stage)   ? "" : $"({Task.Stage})";
        public string ProjectName   => Task.Project ?? "";
    }

    private readonly Action<TaskItem> _onPick;
    private readonly List<Row> _all = new();

    public TaskPickerWindow(string deskName, Action<TaskItem> onPick)
    {
        InitializeComponent();

        _onPick = onPick;
        SubHeaderText.Text = deskName;

        FilterBox.TextChanged += (_, _) => RefreshList();
        FilterBox.PreviewKeyDown += OnFilterKeyDown;
        TaskList.PreviewKeyDown += OnListKeyDown;
        TaskList.MouseDoubleClick += (_, _) => PickSelected();
        CloseBtn.Click += (_, _) => Close();

        // Cierre al clickear afuera o cambiar de desktop (se arma a los 700ms, ver CloseOnDeactivate).
        this.CloseOnDeactivate();

        Loaded += (_, _) => FilterBox.Focus();
    }

    /// <summary>Inyecta las tareas ya traídas. Oculta el loader y arma la lista filtrable.</summary>
    public void SetItems(IReadOnlyList<TaskItem> tasks)
    {
        _all.Clear();
        foreach (var t in tasks) _all.Add(new Row(t));
        LoadingOverlay.Visibility = Visibility.Collapsed;
        RefreshList();
    }

    /// <summary>Reemplaza el loader por un mensaje de error. La lista queda vacía.</summary>
    public void SetError(string message)
    {
        LoadingText.Text = message;
        LoadingHint.Text = "Cerrá y volvé a intentar, o revisá Config → Tareas.";
    }

    /// <summary>Loader con mensaje custom (ej. "Sin tareas abiertas").</summary>
    public void SetEmpty(string title, string hint)
    {
        LoadingText.Text = title;
        LoadingHint.Text = hint;
    }

    private void RefreshList()
    {
        string filter = FilterBox.Text.Trim();
        TaskList.Items.Clear();
        foreach (var r in _all)
        {
            if (filter == ""
                || r.Task.Title.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || r.Task.Identifier.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || r.Task.AccountName.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || (r.Task.Project ?? "").Contains(filter, StringComparison.OrdinalIgnoreCase)
                || (r.Task.Stage   ?? "").Contains(filter, StringComparison.OrdinalIgnoreCase))
                TaskList.Items.Add(r);
        }
    }

    private void OnFilterKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)       { PickSelected(); e.Handled = true; }
        else if (e.Key == Key.Escape) { Close();        e.Handled = true; }
        else if (e.Key == Key.Down && TaskList.Items.Count > 0)
        {
            TaskList.SelectedIndex = 0;
            TaskList.Focus();
            e.Handled = true;
        }
    }

    private void OnListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)       { PickSelected(); e.Handled = true; }
        else if (e.Key == Key.Escape) { Close();        e.Handled = true; }
    }

    private void PickSelected()
    {
        // Fila seleccionada, o el único resultado visible desde el searchbox (igual que el DeskPicker).
        var row = TaskList.SelectedItem as Row;
        if (row is null && TaskList.Items.Count == 1)
            row = (Row)TaskList.Items[0];
        if (row is null)
            return;

        _onPick(row.Task);
        Close();
    }
}
