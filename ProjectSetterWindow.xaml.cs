using System;
using System.Windows;
using System.Windows.Input;
using AmpzDesktopBooster.Desktops;
using AmpzDesktopBooster.Services.Localization;

namespace AmpzDesktopBooster;

/// <summary>
/// Paso 1 del LAUNCHER de Win+NumpadEnter: elegís un ESPACIO. A diferencia de la versión vieja
/// (que seteaba el espacio del desk actual, sólo en desks de rol "DESK +N"), esta ventana ya no
/// pertenece a ningún desk — se abre desde CUALQUIER escritorio y su único trabajo es devolver un
/// espacio+contexto elegidos; es <see cref="Hotkeys.HotkeyRouter"/> (vía el orquestador no-UI del
/// launcher) el que después crea el escritorio DINÁMICO, lo asigna y lo navega. Ver CLAUDE.md.
///
/// Textbox filtrable + lista del historial (igual UX que antes). Enter prioriza: (1) fila
/// seleccionada, (2) único resultado visible, (3) texto del textbox como espacio NUEVO. Supr sobre
/// una fila → borrado en cascada del historial. Al confirmar, encadena <see cref="ModulePickerWindow"/>
/// (paso 2, contexto) ANTES de cerrarse — mismo timing que antes, para no abrir un hueco de foco
/// huérfano entre una ventana real y otra (ver CLAUDE.md, "el FOCO HUÉRFANO cuelga el hook").
/// </summary>
public partial class ProjectSetterWindow : Window
{
    private readonly ProjectStore _store;
    private readonly Action<string, string> _onCompleted;

    /// <summary>
    /// Se dispara cuando el flujo pasa al paso 2 (picker de contexto), con la ventana nueva. El
    /// router necesita saber CUÁL ventana está activa para poder cerrar "lo que sea que esté abierto
    /// del launcher" ante un re-press de Win+NumpadEnter, sin importar en qué paso quedó parado.
    /// </summary>
    public event Action<Window>? StageChanged;

    /// <param name="onCompleted">(espacio, contexto) YA normalizados/catalogados — contexto puede ser "".</param>
    public ProjectSetterWindow(ProjectStore store, Action<string, string> onCompleted)
    {
        InitializeComponent();

        _store = store;
        _onCompleted = onCompleted;

        HeaderText.Text = Loc.T("Setter.Header");
        // Ya no hay un "desk dueño" del launcher (Win+NumpadEnter funciona desde cualquier
        // escritorio y SIEMPRE crea uno nuevo), así que el subtítulo que mostraba el desk actual
        // pierde sentido — queda vacío en vez de un dato que confundiría ("¿por qué dice MAIN si
        // el espacio va a abrir en un desk nuevo?").
        SubHeaderText.Text = "";

        // Sin sugerencia de desk: el textbox arranca vacío. Antes se pre-cargaba con la asignación
        // del desk donde estabas parado, pero el launcher ya no pertenece a un desk — heredar la de
        // aquél donde diste Win+NumpadEnter sería arbitrario.
        FilterBox.Text = "";

        RefreshList();

        FilterBox.TextChanged += (_, _) => RefreshList();
        FilterBox.PreviewKeyDown += OnFilterKeyDown;
        HistoryList.PreviewKeyDown += OnListKeyDown;
        HistoryList.MouseDoubleClick += (_, _) => Confirm();
        // "Quitar del desk" no aplica más: no hay ningún desk activo del que sacar nada en este paso.
        RemoveBtn.Visibility = Visibility.Collapsed;
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

        if (name.Length > 23)
        {
            MessageBox.Show(
                string.Format(Loc.T("Setter.NameTooLong"), name.Length),
                Loc.T("Setter.NameTooLongTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // Normaliza (Sanitize + TitleCase) y da de alta en el historial si es nuevo — SIN tocar
        // sesión de ningún desk: todavía no existe el escritorio al que asignarle esto.
        name = _store.RegisterProjectName(name);

        // ── PASO 2: el contexto ── Se abre ANTES de cerrar esta ventana (ver el doc de la clase).
        var picker = new ModulePickerWindow(name, _store, module => _onCompleted(name, module));
        StageChanged?.Invoke(picker);
        picker.ShowFocused();
        Close();
    }

    private void DeleteSelectedFromHistory()
    {
        if (HistoryList.SelectedItem is not string name)
            return;

        var resp = MessageBox.Show(
            string.Format(Loc.T("Setter.DeleteConfirm"), name),
            Loc.T("Setter.DeleteTitle"), MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (resp != MessageBoxResult.Yes)
            return;

        _store.DeleteFromHistory(name);
        RefreshList();
    }
}
