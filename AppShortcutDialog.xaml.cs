using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AmpzDesktopBooster.Apps;
using AmpzDesktopBooster.Services.Localization;

namespace AmpzDesktopBooster;

/// <summary>
/// Modal de alta/edición de un atajo per-app (columna derecha del Shortcuts Helper). Campos:
/// combinación (con captura en vivo), descripción y filtro de título opcional. Porta el
/// _ShowAppShortcutForm + _CaptureKeyCombo del legacy: el botón "Capturar" entra en modo captura
/// y arma el string "Ctrl+Shift+T" leyendo los modificadores físicos al soltar la tecla final.
///
/// Tiene un SEGUNDO modo (<see cref="ShowCustom"/>) para los atajos LIBRES — no atados a un proceso:
/// suma el campo "App" (título simple) y esconde el filtro de título, que sólo tiene sentido contra la
/// ventana con foco. Es el mismo diálogo a propósito: la captura en vivo es la parte delicada y no
/// queremos dos copias que se desincronicen.
/// </summary>
public partial class AppShortcutDialog : Window
{
    /// <summary>Lo que el usuario confirmó. null si canceló.</summary>
    public sealed record Result(string Key, string Desc, string Title);

    /// <summary>Lo que el usuario confirmó en modo atajo libre. null si canceló.</summary>
    public sealed record CustomResult(string App, string Key, string Desc);

    private bool _capturing;
    // Modo atajo libre: el campo App es obligatorio (sin él el atajo no tendría grupo donde aparecer).
    private bool _customMode;

    private AppShortcutDialog(string proc, string activeTitle, AppShortcut? existing)
    {
        InitializeComponent();

        bool isEdit = existing is not null;
        HeaderText.Text = isEdit ? $"{Loc.T("Shortcut.HeaderEdit")} · {proc}" : $"{Loc.T("Shortcut.HeaderNew")} · {proc}";
        SaveBtn.Content = isEdit ? Loc.T("Shortcut.SaveBtn") : Loc.T("Shortcut.CreateBtn");

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
            CurrentTitleHint.Text = $"{Loc.T("Shortcut.CurrentTitle")}: {t}";
            CurrentTitleHint.Visibility = Visibility.Visible;
        }

        CaptureBtn.Click += (_, _) => BeginCapture();
        SaveBtn.Click += (_, _) => OnSave();
        CancelBtn.Click += (_, _) => { DialogResult = false; };

        PreviewKeyDown += OnPreviewKeyDown;
        Loaded += (_, _) => { KeyBox.Focus(); KeyBox.SelectAll(); };
    }

    /// <summary>
    /// Modo atajo LIBRE. <paramref name="knownApps"/> se ofrecen como chips para no duplicar grupos por
    /// una mayúscula; <paramref name="prefillApp"/> precarga el título (p.ej. lo tipeado en el buscador).
    /// </summary>
    private AppShortcutDialog(IReadOnlyList<string> knownApps, string prefillApp, CustomShortcut? existing)
        : this("", "", null)
    {
        _customMode = true;
        bool isEdit = existing is not null;
        HeaderText.Text = isEdit ? Loc.T("Shortcut.HeaderEditCustom") : Loc.T("Shortcut.HeaderNewCustom");
        SaveBtn.Content = isEdit ? Loc.T("Shortcut.SaveBtn") : Loc.T("Shortcut.CreateBtn");
        AppPanel.Visibility = Visibility.Visible;
        TitlePanel.Visibility = Visibility.Collapsed;

        AppBox.Text = existing?.App ?? prefillApp;
        if (existing is not null)
        {
            KeyBox.Text = existing.Key;
            DescBox.Text = existing.Desc;
        }

        // Tope de chips: con muchos grupos el diálogo crecería sin límite; los más usados ya se ven.
        foreach (var app in knownApps.Take(16))
        {
            var chip = new Button
            {
                Content = app, Height = 24, FontSize = 12, Padding = new Thickness(8, 0, 8, 0),
                Margin = new Thickness(0, 0, 6, 6), Focusable = false,
            };
            chip.Click += (_, _) => { AppBox.Text = app; KeyBox.Focus(); };
            AppChips.Children.Add(chip);
        }

        // El constructor base ya enganchó Loaded (foco al KeyBox). Si falta el título, el foco va ahí:
        // es lo primero que hay que completar. Corre DESPUÉS del handler base → gana.
        Loaded += (_, _) =>
        {
            if (AppBox.Text.Trim() == "") { AppBox.Focus(); }
        };
    }

    /// <summary>Abre el modal de atajo LIBRE. Devuelve los datos, o null si canceló.</summary>
    public static CustomResult? ShowCustom(Window owner, IReadOnlyList<string> knownApps, string prefillApp,
        CustomShortcut? existing = null)
    {
        var dlg = new AppShortcutDialog(knownApps, prefillApp, existing) { Owner = owner };
        if (dlg.ShowDialog() != true)
            return null;
        return new CustomResult(dlg.AppBox.Text.Trim(), dlg.KeyBox.Text.Trim(), dlg.DescBox.Text.Trim());
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
        CaptureBtn.Content = Loc.T("Shortcut.CapturingPrompt");
        // Sacamos el foco del TextBox para que las teclas NO se tipeen ahí; el window las captura.
        Focus();
        Keyboard.Focus(this);
    }

    private void EndCapture()
    {
        _capturing = false;
        CaptureBtn.Content = Loc.T("Shortcut.CaptureBtn");
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
        bool missing = KeyBox.Text.Trim() == "" || DescBox.Text.Trim() == ""
                       || (_customMode && AppBox.Text.Trim() == "");
        if (missing)
        {
            MessageBox.Show(Loc.T(_customMode ? "Shortcut.ValidationMessageCustom" : "Shortcut.ValidationMessage"),
                Loc.T("Shortcut.ValidationTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
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
