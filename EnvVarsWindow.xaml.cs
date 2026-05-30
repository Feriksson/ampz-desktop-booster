using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using AmpzDesktopBooster.Services;

namespace AmpzDesktopBooster;

/// <summary>
/// Variables de entorno (Win+F3): lista buscable de las env vars del proceso. Enter abre el
/// directorio si el valor es un path existente; Ctrl+C copia el valor. Sin nada hardcodeado.
/// </summary>
public partial class EnvVarsWindow : Window
{
    private sealed record Pair(string Key, string Value);

    private readonly List<Pair> _all;

    public EnvVarsWindow()
    {
        InitializeComponent();
        Icon = AppIcon.TryLoadForWindow();

        _all = Environment.GetEnvironmentVariables()
            .Cast<DictionaryEntry>()
            .Select(e => new Pair((string)e.Key, e.Value?.ToString() ?? ""))
            .OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();

        RefreshList();

        FilterBox.TextChanged += (_, _) => RefreshList();
        FilterBox.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { Close(); e.Handled = true; }
            else if (e.Key == Key.Enter) { OpenSelected(); e.Handled = true; }
            else if (e.Key == Key.Down && VarsList.Items.Count > 0) { VarsList.SelectedIndex = 0; VarsList.Focus(); e.Handled = true; }
        };
        VarsList.PreviewKeyDown += OnListKeyDown;
        VarsList.MouseDoubleClick += (_, _) => OpenSelected();

        Loaded += (_, _) => FilterBox.Focus();
    }

    private void RefreshList()
    {
        string f = FilterBox.Text.Trim();
        VarsList.Items.Clear();
        foreach (var p in _all)
        {
            if (f == "" || p.Key.Contains(f, StringComparison.OrdinalIgnoreCase)
                        || p.Value.Contains(f, StringComparison.OrdinalIgnoreCase))
                VarsList.Items.Add(p);
        }
        if (VarsList.Items.Count > 0) VarsList.SelectedIndex = 0;
    }

    private void OnListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)       { OpenSelected(); e.Handled = true; }
        else if (e.Key == Key.Escape) { Close();        e.Handled = true; }
        else if (e.Key == Key.C && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            if (VarsList.SelectedItem is Pair p) { try { Clipboard.SetText(p.Value); } catch { } }
            e.Handled = true;
        }
    }

    private void OpenSelected()
    {
        if (VarsList.SelectedItem is not Pair p) return;
        if (Directory.Exists(p.Value))
        {
            try { Process.Start(new ProcessStartInfo("explorer.exe") { Arguments = $"\"{p.Value}\"", UseShellExecute = true }); } catch { }
            Close();
        }
        else
        {
            MessageBox.Show("El valor no es un directorio válido.", "Variables de entorno",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }
}
