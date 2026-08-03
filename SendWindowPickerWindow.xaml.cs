using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;
using AmpzDesktopBooster.Desktops;
using AmpzDesktopBooster.Services;
using AmpzDesktopBooster.Services.Localization;
using AmpzDesktopBooster.Services.Tasks;

namespace AmpzDesktopBooster;

/// <summary>
/// Enviar la ventana activa a un escritorio elegido (Win+NumpadDel). Lista todos los desks con
/// filtro; Enter mueve la ventana (capturada antes de abrir) y sigue. Útil cuando no te acordás
/// el atajo directo del desk destino.
///
/// Cada fila muestra DOS datos del desk para reconocerlo de un vistazo: el espacio activo (1ra
/// línea, junto al nombre) y la TAREA activa (2da línea, con el mismo lenguaje visual que el widget
/// de tarea de la barra: ícono + identifier celeste + título). Ambos salen de la SESIÓN efímera
/// (espacio de ProjectStore, tarea de TaskSessionStore) — no de disco.
/// </summary>
public partial class SendWindowPickerWindow : Window
{
    private sealed record Row(int Idx, string Name)
    {
        public string? Project { get; init; }
        public string? TaskId { get; init; }    // identifier de la tarea (ej. "VKJ-123"), o "" si no hay
        public string? TaskTitle { get; init; } // título de la tarea, o null si el desk no tiene tarea

        /// <summary>1ra línea: nombre del desk y, si hay, el espacio activo.</summary>
        public string Line1 => string.IsNullOrEmpty(Project) ? Name : $"{Name}   —   {Project}";

        public bool HasTask   => !string.IsNullOrEmpty(TaskTitle);
        public bool HasTaskId => !string.IsNullOrEmpty(TaskId);

        /// <summary>Texto plano para el filtro y el fallback de ToString (incluye la tarea).</summary>
        public override string ToString() => HasTask ? $"{Line1}   ·   {TaskTitle}" : Line1;
    }

    private readonly DesktopService _desktops;
    private readonly IntPtr _hwnd;
    private readonly List<Row> _all = new();

    public SendWindowPickerWindow(DesktopService desktops, TaskSessionStore taskSession, IntPtr hwnd, string activeTitle)
    {
        InitializeComponent();
        _desktops = desktops;
        _hwnd = hwnd;
        Icon = AppIcon.TryLoadForWindow();

        HeaderText.Text = string.IsNullOrEmpty(activeTitle) ? Loc.T("SendPicker.Title") : $"{Loc.T("SendPicker.SendPrefix")}: {activeTitle}";

        int current = _desktops.Current;
        for (int i = 0; i < _desktops.Count; i++)
        {
            if (i == current) continue; // no tiene sentido enviar al mismo desk
            var task = taskSession.GetDeskTask(i); // tarea activa de ESE desk (o null)
            _all.Add(new Row(i, _desktops.GetName(i))
            {
                Project = _desktops.GetProject(i),
                TaskId = task?.Identifier ?? "",
                TaskTitle = task?.Title,
            });
        }
        Refresh();

        FilterBox.TextChanged += (_, _) => Refresh();
        FilterBox.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { SendSelected(); e.Handled = true; }
            else if (e.Key == Key.Escape) { Close(); e.Handled = true; }
            else if (e.Key == Key.Down && DeskList.Items.Count > 0) { DeskList.SelectedIndex = 0; DeskList.Focus(); e.Handled = true; }
        };
        DeskList.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { SendSelected(); e.Handled = true; }
            else if (e.Key == Key.Escape) { Close(); e.Handled = true; }
        };
        DeskList.MouseDoubleClick += (_, _) => SendSelected();
        Loaded += (_, _) => FilterBox.Focus();
    }

    private void Refresh()
    {
        string f = FilterBox.Text.Trim();
        DeskList.Items.Clear();
        foreach (var r in _all)
        {
            if (f == "" || r.Name.Contains(f, StringComparison.OrdinalIgnoreCase)
                        || (r.Project ?? "").Contains(f, StringComparison.OrdinalIgnoreCase)
                        || (r.TaskTitle ?? "").Contains(f, StringComparison.OrdinalIgnoreCase)
                        || (r.TaskId ?? "").Contains(f, StringComparison.OrdinalIgnoreCase))
                DeskList.Items.Add(r);
        }
        if (DeskList.Items.Count > 0) DeskList.SelectedIndex = 0;
    }

    private void SendSelected()
    {
        var row = DeskList.SelectedItem as Row ?? (DeskList.Items.Count == 1 ? (Row)DeskList.Items[0] : null);
        if (row is null) return;
        _desktops.SendWindowTo(_hwnd, row.Idx, follow: true);
        Close();
    }
}
