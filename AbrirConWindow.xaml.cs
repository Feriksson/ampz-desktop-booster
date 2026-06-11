using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AmpzDesktopBooster.Apps;
using AmpzDesktopBooster.Services;
using AmpzDesktopBooster.Services.Localization;

namespace AmpzDesktopBooster;

/// <summary>
/// "Abrir con…" (Win+F2): toma las carpetas del Explorer activo y ofrece un botón por cada app
/// DISPONIBLE (auto-detectadas + las del usuario). Click abre los targets con esa app y cierra.
/// </summary>
public partial class AbrirConWindow : Window
{
    private readonly IReadOnlyList<string> _targets;

    public AbrirConWindow(IReadOnlyList<string> targets, AppsConfig userApps)
    {
        InitializeComponent();
        _targets = targets;

        Icon = AppIcon.TryLoadForWindow();
        TargetText.Text = DescribeTargets(targets);

        var apps = AppCatalog.GetAvailable(userApps);
        EmptyHint.Visibility = apps.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        foreach (var app in apps)
        {
            var btn = new Button { Content = app.Name };
            btn.Click += (_, _) =>
            {
                // Mostramos el error REAL en vez de tragarlo: si algo falla al abrir, queremos
                // verlo, no que "no pase nada". La ventana NO se cierra si hubo error, así el
                // mensaje queda a la vista.
                try
                {
                    app.Launch(_targets);
                    Close();
                }
                catch (System.Exception ex)
                {
                    MessageBox.Show(
                        $"{string.Format(Loc.T("OpenWith.LaunchError"), app.Name)}\n\n{ex.GetType().Name}: {ex.Message}",
                        Loc.T("OpenWith.Title"), MessageBoxButton.OK, MessageBoxImage.Error);
                }
            };
            ButtonsPanel.Children.Add(btn);
        }

        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };

        // El foco de teclado necesita que la ventana esté ACTIVA primero; con sólo .Show() no
        // agarra. Activamos y enfocamos el primer botón → Enter lo dispara sin tocar el mouse.
        Loaded += (_, _) =>
        {
            Activate();
            if (ButtonsPanel.Children.Count > 0)
            {
                var first = (Button)ButtonsPanel.Children[0];
                first.Focus();
                Keyboard.Focus(first);
            }
        };
    }

    private static string DescribeTargets(IReadOnlyList<string> targets)
    {
        if (targets.Count == 1)
            return $"Target:\n{targets[0]}\n\n{Loc.T("OpenWith.PromptSingle")}";
        return $"{string.Format(Loc.T("OpenWith.PromptMultiple"), targets.Count)}";
    }
}
