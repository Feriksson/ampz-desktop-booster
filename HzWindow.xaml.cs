using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AmpzDesktopBooster.Interop;
using AmpzDesktopBooster.Services;

namespace AmpzDesktopBooster;

/// <summary>
/// Cambiar la frecuencia de refresco (Win+F12). En vez de los 60/240 hardcodeados del legacy,
/// ENUMERA las frecuencias reales que soporta la resolución actual y muestra un botón por cada
/// una. La actual queda marcada y deshabilitada.
/// </summary>
public partial class HzWindow : Window
{
    public HzWindow()
    {
        InitializeComponent();
        Icon = AppIcon.TryLoadForWindow();

        int current = DisplaySettings.CurrentRate();
        CurrentText.Text = $"Frecuencia actual: {current} Hz";

        foreach (int hz in DisplaySettings.AvailableRates())
        {
            bool isCurrent = hz == current;
            var btn = new Button
            {
                Content = isCurrent ? $"{hz} Hz  ✓" : $"{hz} Hz",
                IsEnabled = !isCurrent,
            };
            if (isCurrent)
                btn.Foreground = new SolidColorBrush(Color.FromRgb(0x4F, 0xC3, 0xF7));
            int captured = hz;
            btn.Click += (_, _) => Apply(captured);
            RatesPanel.Children.Add(btn);
        }

        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
    }

    private void Apply(int hz)
    {
        bool ok = DisplaySettings.SetRate(hz);
        if (!ok)
            MessageBox.Show($"No se pudo cambiar a {hz} Hz.", "Frecuencia",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        Close();
    }
}
