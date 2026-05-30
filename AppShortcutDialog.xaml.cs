using System.Windows;
using System.Windows.Input;
using AmpzDesktopBooster.Apps;

namespace AmpzDesktopBooster;

/// <summary>
/// Modal de alta/edición de un atajo per-app (columna derecha del Shortcuts Helper). Campos:
/// combinación (con captura en vivo), descripción y filtro de título opcional. Porta el
/// _ShowAppShortcutForm + _CaptureKeyCombo del legacy: el botón "Capturar" entra en modo captura
/// y arma el string "Ctrl+Shift+T" leyendo los modificadores físicos al soltar la tecla final.
/// </summary>
public partial class AppShortcutDialog : Window
{
    /// <summary>Lo que el usuario confirmó. null si canceló.</summary>
    public sealed record Result(string Key, string Desc, string Title);

    private bool _capturing;

    private AppShortcutDialog(string proc, string activeTitle, AppShortcut? existing)
    {
        InitializeComponent();

        bool isEdit = existing is not null;
        HeaderText.Text = isEdit ? $"Editar shortcut · {proc}" : $"Nuevo shortcut · {proc}";
        SaveBtn.Content = isEdit ? "Guardar cambios" : "Crear shortcut";

        if (existing is not null)
        {
            KeyBox.Text = existing.Key;
            DescBox.Text = existing.Desc;
            TitleBox.Text = existing.Title;
        }

        // Mostrar el título actual para que el usuario sepa contra qué se va a matchear el filtro.
        if (!string.IsNullOrEmpty(activeTitle))
        {
            var t = activeTitle.Length > 80 ? activeTitle[..77] + "…" : activeTitle;
            CurrentTitleHint.Text = "Título actual: " + t;
            CurrentTitleHint.Visibility = Visibility.Visible;
        }

        CaptureBtn.Click += (_, _) => BeginCapture();
        SaveBtn.Click += (_, _) => OnSave();
        CancelBtn.Click += (_, _) => { DialogResult = false; };

        PreviewKeyDown += OnPreviewKeyDown;
        Loaded += (_, _) => { KeyBox.Focus(); KeyBox.SelectAll(); };
    }

    /// <summary>Abre el modal sobre <paramref name="owner"/>. Devuelve los datos, o null si canceló.</summary>
    public static Result? Show(Window owner, string proc, string activeTitle, AppShortcut? existing = null)
    {
        var dlg = new AppShortcutDialog(proc, activeTitle, existing) { Owner = owner };
        if (dlg.ShowDialog() != true)
            return null;
        return new Result(dlg.KeyBox.Text.Trim(), dlg.DescBox.Text.Trim(), dlg.TitleBox.Text.Trim());
    }

    private void BeginCapture()
    {
        _capturing = true;
        CaptureBtn.Content = "Presioná… (Esc cancela)";
        // Sacamos el foco del TextBox para que las teclas NO se tipeen ahí; el window las captura.
        Focus();
        Keyboard.Focus(this);
    }

    private void EndCapture()
    {
        _capturing = false;
        CaptureBtn.Content = "🎯 Capturar";
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_capturing)
        {
            e.Handled = true; // en modo captura tragamos TODO

            // Alt llega como Key.System; la tecla real está en SystemKey.
            var key = e.Key == Key.System ? e.SystemKey : e.Key;

            if (IsModifier(key))
                return; // esperamos la tecla final (no-modificador)

            if (key == Key.Escape)
            {
                EndCapture(); // Esc en captura: cancela SOLO la captura, no el diálogo
                return;
            }

            KeyBox.Text = BuildCombo(key);
            EndCapture();
            KeyBox.CaretIndex = KeyBox.Text.Length;
            return;
        }

        // Fuera de captura: Esc cierra el diálogo.
        if (e.Key == Key.Escape)
        {
            DialogResult = false;
            e.Handled = true;
        }
    }

    private void OnSave()
    {
        if (KeyBox.Text.Trim() == "" || DescBox.Text.Trim() == "")
        {
            MessageBox.Show("La combinación y la descripción son obligatorias.",
                "Datos faltantes", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        DialogResult = true;
    }

    private static bool IsModifier(Key k) => k is
        Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or
        Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin or Key.System;

    /// <summary>Arma "Win+Ctrl+Alt+Shift+Tecla" desde los modificadores físicos vigentes.</summary>
    private static string BuildCombo(Key key)
    {
        var mods = Keyboard.Modifiers;
        var combo = "";
        if (mods.HasFlag(ModifierKeys.Windows)) combo += "Win+";
        if (mods.HasFlag(ModifierKeys.Control)) combo += "Ctrl+";
        if (mods.HasFlag(ModifierKeys.Alt))     combo += "Alt+";
        if (mods.HasFlag(ModifierKeys.Shift))   combo += "Shift+";
        return combo + KeyName(key);
    }

    /// <summary>Nombre legible de una tecla para el cheatsheet (no necesita ser canónico de AHK).</summary>
    private static string KeyName(Key key) => key switch
    {
        >= Key.A and <= Key.Z => key.ToString(),
        >= Key.D0 and <= Key.D9 => ((char)('0' + (key - Key.D0))).ToString(),
        >= Key.NumPad0 and <= Key.NumPad9 => "Numpad" + (key - Key.NumPad0),
        >= Key.F1 and <= Key.F24 => key.ToString(),
        Key.Return => "Enter",
        Key.Escape => "Esc",
        Key.Space => "Space",
        Key.Tab => "Tab",
        Key.Back => "Backspace",
        Key.Delete => "Del",
        Key.Insert => "Ins",
        Key.Home => "Home",
        Key.End => "End",
        Key.PageUp => "PgUp",
        Key.PageDown => "PgDn",
        Key.Left => "Left",
        Key.Right => "Right",
        Key.Up => "Up",
        Key.Down => "Down",
        Key.OemQuestion => "/",
        Key.OemComma => ",",
        Key.OemPeriod => ".",
        Key.OemMinus => "-",
        Key.OemPlus => "+",
        Key.OemOpenBrackets => "[",
        Key.Oem6 => "]",
        Key.Oem5 => "\\",
        Key.Oem1 => ";",
        Key.OemQuotes => "'",
        Key.OemTilde => "`",
        Key.Add => "Numpad+",
        Key.Subtract => "Numpad-",
        Key.Multiply => "Numpad*",
        Key.Divide => "Numpad/",
        _ => key.ToString(),
    };
}
