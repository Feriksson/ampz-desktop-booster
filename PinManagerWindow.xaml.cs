using System;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using AmpzDesktopBooster.Desktops;
using AmpzDesktopBooster.Services;

namespace AmpzDesktopBooster;

/// <summary>Gestor de anclajes (Win+F7): lista proceso → desk, Supr desancla, botón desanclar todo.</summary>
public partial class PinManagerWindow : Window
{
    private sealed record Row(string Proc, string DeskName)
    {
        public override string ToString() => $"{Proc}   →   {DeskName}";
    }

    private readonly PinStore _pins;

    public PinManagerWindow(PinStore pins)
    {
        InitializeComponent();
        _pins = pins;
        Icon = AppIcon.TryLoadForWindow();

        Refresh();

        PinList.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Delete) { UnpinSelected(); e.Handled = true; }
            else if (e.Key == Key.Escape) { Close(); e.Handled = true; }
        };
        UnpinAllBtn.Click += (_, _) => UnpinAll();
        CloseBtn.Click += (_, _) => Close();
        Loaded += (_, _) => PinList.Focus();
    }

    private void Refresh()
    {
        PinList.Items.Clear();
        foreach (var kv in _pins.All.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
            PinList.Items.Add(new Row(kv.Key, kv.Value)); // kv.Value YA es el nombre del desk anclado
        EmptyHint.Visibility = PinList.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (PinList.Items.Count > 0) PinList.SelectedIndex = 0;
    }

    private void UnpinSelected()
    {
        if (PinList.SelectedItem is not Row row) return;
        _pins.Unpin(row.Proc);
        Refresh();
        if (PinList.Items.Count == 0) Close();
    }

    private void UnpinAll()
    {
        if (_pins.All.Count == 0) return;
        if (MessageBox.Show("¿Desanclar todos los procesos?", "Anclajes",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _pins.Clear();
        Close();
    }
}
