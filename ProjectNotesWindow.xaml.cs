using System;
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
/// Flujo "abrir, editar, cerrar y listo": NO hay botón Guardar. Auto-guarda al cerrar (Esc/X)
/// si el texto cambió respecto al inicial; Ctrl+S guarda explícito sin cerrar. Como el legacy.
/// </summary>
public partial class ProjectNotesWindow : Window
{
    private readonly ProjectStore _store;
    private readonly string _deskName;
    private readonly int _deskIdx;
    private string _initial;

    public ProjectNotesWindow(ProjectStore store, string deskName, int deskIdx)
    {
        InitializeComponent();

        _store = store;
        _deskName = deskName;
        _deskIdx = deskIdx;

        Icon = AppIcon.TryLoadForWindow();
        HeaderText.Text = store.ScopeLabel(deskName, deskIdx); // "<Proyecto>" o "Global"
        SubHeaderText.Text = $"{deskName} — Notas";

        _initial = store.GetNotes(deskName, deskIdx);
        NotesBox.Text = _initial;

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

    /// <summary>Ctrl+S: guarda sin cerrar y reancla el "inicial" para no re-guardar de gusto.</summary>
    private void SaveExplicit()
    {
        _store.SetNotes(_deskName, _deskIdx, NotesBox.Text);
        _initial = NotesBox.Text;
    }

    /// <summary>Guarda sólo si el texto cambió respecto al valor con el que se abrió.</summary>
    private void AutoSave()
    {
        if (NotesBox.Text != _initial)
            _store.SetNotes(_deskName, _deskIdx, NotesBox.Text);
    }
}
