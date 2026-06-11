using System;
using System.IO;
using System.Windows;
using System.Windows.Input;
using AmpzDesktopBooster.Desktops;
using AmpzDesktopBooster.Services;

namespace AmpzDesktopBooster;

/// <summary>
/// Notas del proyecto / globales — la Win+Numpad/ del legacy (Notes Editor).
/// Textarea grande (Consolas) con el mismo dual-scope que las variables: notas del proyecto
/// activo en un DESK +N, o las globales en cualquier otro desk.
///
/// Además, un SEGUNDO panel abajo: notas ligadas a la CARPETA activa del Explorer (la que estaba
/// en foreground al abrir). Sirve para anotarle detalles a un repo/carpeta puntual, atado al disco
/// y no al desk. Si al abrir no había un Explorer con carpeta, el panel inferior se COLAPSA y queda
/// sólo el de proyecto (idéntico al comportamiento de antes). Cada panel se guarda por su cuenta.
///
/// Flujo "abrir, editar, cerrar y listo": NO hay botón Guardar. Auto-guarda al cerrar (Esc/X)
/// si el texto cambió respecto al inicial; Ctrl+S guarda explícito sin cerrar. Como el legacy.
/// </summary>
public partial class ProjectNotesWindow : Window
{
    private readonly ProjectStore _store;
    private readonly string _deskName;
    private readonly int _deskIdx;
    private string _initial;

    // Notas de carpeta: path capturado al abrir, su texto inicial, y si hay carpeta del todo.
    private readonly string _activeFolder;
    private readonly bool _hasFolder;
    private string _folderInitial = "";

    public ProjectNotesWindow(ProjectStore store, string deskName, int deskIdx, string? activeFolder)
    {
        InitializeComponent();

        _store = store;
        _deskName = deskName;
        _deskIdx = deskIdx;

        Icon = AppIcon.TryLoadForWindow();
        HeaderText.Text = store.ScopeLabel(deskName, deskIdx); // "<Proyecto>" o "Global"
        SubHeaderText.Text = $"Notas del proyecto del desk · {deskName}";

        _initial = store.GetNotes(deskName, deskIdx);
        NotesBox.Text = _initial;

        // ── Panel de carpeta: sólo si había un Explorer con carpeta real en foreground ──
        _activeFolder = activeFolder ?? "";
        _hasFolder = _activeFolder.Length > 0;
        if (_hasFolder)
        {
            // Header = nombre de la carpeta (lo que identifica la nota); subheader = path completo,
            // para que se vea de qué carpeta exacta son estas notas (la key es sólo el nombre).
            FolderHeaderText.Text = Path.GetFileName(_activeFolder.TrimEnd('\\', '/', ' '));
            FolderSubHeaderText.Text = $"Notas de la carpeta · {_activeFolder}";
            _folderInitial = store.GetFolderNotes(_activeFolder);
            FolderNotesBox.Text = _folderInitial;
        }
        else
        {
            // Sin carpeta → colapsamos el panel inferior y su splitter, y devolvemos TODO el alto
            // al panel de proyecto (la ventana queda igual que antes de esta feature).
            FolderPanel.Visibility = Visibility.Collapsed;
            PanelSplitter.Visibility = Visibility.Collapsed;
            SplitterRow.Height = new GridLength(0);
            FolderRow.Height = new GridLength(0);
        }

        SizeToWorkArea();

        PreviewKeyDown += OnKeyDown;
        Closing += (_, _) => AutoSave();
        Loaded += (_, _) => { NotesBox.Focus(); NotesBox.CaretIndex = NotesBox.Text.Length; };
    }

    /// <summary>90% × 80% del área de trabajo (excluye la taskbar), centrada sobre ella — como el legacy.</summary>
    private void SizeToWorkArea()
    {
        var wa = SystemParameters.WorkArea;
        Width = wa.Width * 0.90;
        Height = wa.Height * 0.80;
        Left = wa.Left + (wa.Width - Width) / 2;
        Top = wa.Top + (wa.Height - Height) / 2;
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.S && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            SaveExplicit();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            Close(); // el auto-save corre en Closing
            e.Handled = true;
        }
    }

    /// <summary>Ctrl+S: guarda ambos paneles sin cerrar y reancla los "inicial" para no re-guardar de gusto.</summary>
    private void SaveExplicit()
    {
        _store.SetNotes(_deskName, _deskIdx, NotesBox.Text);
        _initial = NotesBox.Text;

        if (_hasFolder)
        {
            _store.SetFolderNotes(_activeFolder, FolderNotesBox.Text);
            _folderInitial = FolderNotesBox.Text;
        }
    }

    /// <summary>Guarda cada panel sólo si su texto cambió respecto al valor con el que se abrió.</summary>
    private void AutoSave()
    {
        if (NotesBox.Text != _initial)
            _store.SetNotes(_deskName, _deskIdx, NotesBox.Text);

        if (_hasFolder && FolderNotesBox.Text != _folderInitial)
            _store.SetFolderNotes(_activeFolder, FolderNotesBox.Text);
    }
}
