using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AmpzDesktopBooster.Interop;
using AmpzDesktopBooster.Services;
using AmpzDesktopBooster.Services.Localization;

namespace AmpzDesktopBooster;

/// <summary>
/// Cambiar la frecuencia de refresco (Win+F12). Interacción tipo Alt+Tab: al abrir, la selección
/// arranca en la frecuencia SIGUIENTE a la actual; re-presionar Win+F12 (sin soltar Win) cicla; al
/// soltar Win se aplica la seleccionada. El ciclado y el "soltar Win" los maneja el HotkeyRouter
/// vía el hook global — esta ventana solo expone <see cref="CycleNext"/> y <see cref="ApplySelected"/>.
///
/// Enumera las frecuencias reales que soporta la resolución actual (no los 60/240 hardcodeados del
/// legacy). La actual queda marcada con ✓; la seleccionada se resalta con borde/fondo de acento.
/// </summary>
public partial class HzWindow : Window
{
    private static readonly Brush AccentBrush  = new SolidColorBrush(Color.FromRgb(0x4F, 0xC3, 0xF7));
    private static readonly Brush SelectedBack = new SolidColorBrush(Color.FromRgb(0x2E, 0x3E, 0x46));
    private static readonly Brush NormalBack   = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2A));
    private static readonly Brush NormalBorder = new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x44));
    private static readonly Brush NormalFg     = new SolidColorBrush(Color.FromRgb(0xF2, 0xF2, 0xF2));

    private readonly List<(int Hz, Button Button)> _options = new();
    private readonly int _current;
    private int _selected; // índice dentro de _options

    public HzWindow()
    {
        InitializeComponent();
        Icon = AppIcon.TryLoadForWindow();

        _current = DisplaySettings.CurrentRate();
        CurrentText.Text = $"{Loc.T("Hz.Current")} {_current} Hz";

        var rates = DisplaySettings.AvailableRates();
        int currentIdx = 0;
        for (int i = 0; i < rates.Count; i++)
        {
            int hz = rates[i];
            bool isCurrent = hz == _current;
            if (isCurrent) currentIdx = i;

            var btn = new Button { Content = isCurrent ? $"{hz} Hz  ✓" : $"{hz} Hz" };
            int captured = hz;
            // El mouse sigue siendo un atajo directo: clic = aplicar esa y cerrar (sin esperar al Win-up).
            btn.Click += (_, _) => Apply(captured);
            RatesPanel.Children.Add(btn);
            _options.Add((hz, btn));
        }

        // Arranca seleccionando la SIGUIENTE a la actual (wrap). Con una sola opción, queda en la misma.
        _selected = _options.Count > 0 ? (currentIdx + 1) % _options.Count : 0;
        UpdateHighlight();

        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
    }

    /// <summary>Avanza la selección a la siguiente opción (wrap). Lo llama el router en cada Win+F12.</summary>
    public void CycleNext()
    {
        if (_options.Count == 0) return;
        _selected = (_selected + 1) % _options.Count;
        UpdateHighlight();
    }

    /// <summary>Aplica la opción seleccionada y cierra. Lo llama el router al soltar la Win.</summary>
    public void ApplySelected()
    {
        if (_options.Count == 0) { Close(); return; }
        Apply(_options[_selected].Hz);
    }

    private void Apply(int hz)
    {
        // Si la seleccionada es la actual (p. ej. se cicló hasta volver a ella), no tocamos el modo:
        // evitamos el parpadeo de pantalla de un ChangeDisplaySettings inútil.
        if (hz != _current && !DisplaySettings.SetRate(hz))
            MessageBox.Show(string.Format(Loc.T("Hz.ErrorMsg"), hz), Loc.T("Hz.ErrorTitle"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
        Close();
    }

    /// <summary>Repinta el resalte: ✓ en la actual, borde/fondo de acento en la seleccionada.</summary>
    private void UpdateHighlight()
    {
        for (int i = 0; i < _options.Count; i++)
        {
            var (hz, btn) = _options[i];
            bool sel = i == _selected;
            bool isCurrent = hz == _current;

            btn.Background = sel ? SelectedBack : NormalBack;
            btn.BorderBrush = sel ? AccentBrush : NormalBorder;
            btn.BorderThickness = new Thickness(sel ? 2 : 1);
            btn.Foreground = isCurrent ? AccentBrush : NormalFg;
        }
    }
}
