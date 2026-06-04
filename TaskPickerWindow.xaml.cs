using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using AmpzDesktopBooster.Services.Tasks;

namespace AmpzDesktopBooster;

/// <summary>
/// Picker de tareas (Win+NumLock): lista las tareas ABIERTAS que trajo el provider, con filtro en
/// vivo. Enter / doble-click ancla la tarea al desk actual. Calcado del DeskPickerWindow — mismo
/// look, mismo manejo de teclas (Enter confirma fila o único resultado, Esc cierra).
///
/// La ventana NO sabe de HTTP ni de providers: recibe las tareas ya traídas y devuelve la elegida
/// por callback. Separación de capas — el router orquesta el fetch, la ventana sólo muestra/elige.
/// </summary>
public partial class TaskPickerWindow : Window
{
    /// <summary>Una fila del picker. ToString define lo que muestra el ListBox.</summary>
    private sealed record Row(TaskItem Task)
    {
        public override string ToString() =>
            string.IsNullOrEmpty(Task.Identifier) ? Task.Title : $"{Task.Identifier}   —   {Task.Title}";
    }

    private readonly Action<TaskItem> _onPick;
    private readonly List<Row> _all;

    public TaskPickerWindow(IReadOnlyList<TaskItem> tasks, string deskName, Action<TaskItem> onPick)
    {
        InitializeComponent();

        _onPick = onPick;
        _all = tasks.Select(t => new Row(t)).ToList();

        SubHeaderText.Text = deskName;
        RefreshList();

        FilterBox.TextChanged += (_, _) => RefreshList();
        FilterBox.PreviewKeyDown += OnFilterKeyDown;
        TaskList.PreviewKeyDown += OnListKeyDown;
        TaskList.MouseDoubleClick += (_, _) => PickSelected();
        CloseBtn.Click += (_, _) => Close();

        // Cierre al clickear afuera o cambiar de desktop (se arma a los 700ms, ver CloseOnDeactivate).
        this.CloseOnDeactivate();

        Loaded += (_, _) => FilterBox.Focus();
    }

    private void RefreshList()
    {
        string filter = FilterBox.Text.Trim();
        TaskList.Items.Clear();
        foreach (var r in _all)
        {
            if (filter == ""
                || r.Task.Title.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || r.Task.Identifier.Contains(filter, StringComparison.OrdinalIgnoreCase))
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
