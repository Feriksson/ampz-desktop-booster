using System.Windows;

namespace AmpzDesktopBooster;

/// <summary>
/// Mini-diálogo de una sola línea (WPF no trae InputBox). Devuelve el texto, o null si se canceló.
/// Lo usa el Paths Manager para "agregar" y "renombrar".
/// </summary>
public partial class PromptDialog : Window
{
    private PromptDialog(string title, string label, string initial)
    {
        InitializeComponent();
        TitleText.Text = title;
        LabelText.Text = label;
        InputBox.Text = initial;

        OkBtn.Click += (_, _) => { DialogResult = true; };
        CancelBtn.Click += (_, _) => { DialogResult = false; };
        Loaded += (_, _) => { InputBox.Focus(); InputBox.SelectAll(); };
    }

    /// <summary>Muestra el prompt modal sobre <paramref name="owner"/>. null si el usuario cancela.</summary>
    public static string? Show(Window owner, string title, string label, string initial = "")
    {
        var dlg = new PromptDialog(title, label, initial) { Owner = owner };
        return dlg.ShowDialog() == true ? dlg.InputBox.Text : null;
    }
}
