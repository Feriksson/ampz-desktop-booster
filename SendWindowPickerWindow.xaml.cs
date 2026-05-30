using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;
using AmpzDesktopBooster.Desktops;
using AmpzDesktopBooster.Services;

namespace AmpzDesktopBooster;

/// <summary>
/// Enviar la ventana activa a un escritorio elegido (Win+NumpadDel). Lista todos los desks con
/// filtro; Enter mueve la ventana (capturada antes de abrir) y sigue. Útil cuando no te acordás
/// el atajo directo del desk destino.
/// </summary>
public partial class SendWindowPickerWindow : Window
{
    private sealed record Row(int Idx, string Name)
    {
        public string? Project { get; init; }
        public override string ToString() =>
            string.IsNullOrEmpty(Project) ? Name : $"{Name}   —   {Project}";
    }

    private readonly DesktopService _desktops;
    private readonly IntPtr _hwnd;
    private readonly List<Row> _all = new();

    public SendWindowPickerWindow(DesktopService desktops, IntPtr hwnd, string activeTitle)
    {
        InitializeComponent();
        _desktops = desktops;
        _hwnd = hwnd;
        Icon = AppIcon.TryLoadForWindow();

        HeaderText.Text = string.IsNullOrEmpty(activeTitle) ? "Enviar ventana a…" : $"Enviar: {activeTitle}";

        int current = _desktops.Current;
        for (int i = 0; i < _desktops.Count; i++)
        {
            if (i == current) continue; // no tiene sentido enviar al mismo desk
            _all.Add(new Row(i, _desktops.GetName(i)) { Project = _desktops.GetProject(i) });
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
                        || (r.Project ?? "").Contains(f, StringComparison.OrdinalIgnoreCase))
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
