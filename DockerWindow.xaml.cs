using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using AmpzDesktopBooster.Apps;
using AmpzDesktopBooster.Services;
using AmpzDesktopBooster.Services.Localization;

namespace AmpzDesktopBooster;

/// <summary>
/// Panel Docker (Win+F5): lista de contenedores (docker ps -a) con start/stop multi-selección,
/// copiar puerto expuesto y filtro. Las filas "running" se pintan verdes (DataTrigger sobre
/// IsRunning). Sin libs: usa el CLI de docker del PATH.
/// </summary>
public partial class DockerWindow : Window
{
    private List<DockerContainer> _all = new();

    public DockerWindow()
    {
        InitializeComponent();
        Icon = AppIcon.TryLoadForWindow();

        RefreshBtn.Click += (_, _) => Reload();
        StartBtn.Click += (_, _) => Act(start: true);
        StopBtn.Click += (_, _) => Act(start: false);
        CopyPortBtn.Click += (_, _) => CopyPort();
        FilterBox.TextChanged += (_, _) => ApplyFilter();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { Close(); e.Handled = true; }
            else if (e.Key == Key.C && (Keyboard.Modifiers & ModifierKeys.Control) != 0) { CopyPort(); e.Handled = true; }
        };

        Loaded += (_, _) => Reload();
    }

    private void Reload()
    {
        if (!DockerCli.IsAvailable)
        {
            StatusText.Text = $"⚠ {Loc.T("Docker.NotAvailable")}";
            _all = new();
            ApplyFilter();
            return;
        }

        _all = DockerCli.List();
        int running = _all.Count(c => c.IsRunning);
        StatusText.Text = _all.Count == 0
            ? Loc.T("Docker.NoContainers")
            : $"{_all.Count} {Loc.T("Docker.ContainersSuffix")} · {running} {Loc.T("Docker.RunningLabel")}";
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        string f = FilterBox.Text.Trim();
        ContainersList.ItemsSource = f == ""
            ? _all
            : _all.Where(c =>
                c.Name.Contains(f, System.StringComparison.OrdinalIgnoreCase) ||
                c.Image.Contains(f, System.StringComparison.OrdinalIgnoreCase) ||
                c.Status.Contains(f, System.StringComparison.OrdinalIgnoreCase) ||
                c.ExposedPorts.Contains(f, System.StringComparison.OrdinalIgnoreCase)).ToList();
        ContainersList.Items.Refresh();
    }

    private void Act(bool start)
    {
        var names = ContainersList.SelectedItems.Cast<DockerContainer>().Select(c => c.Name).ToList();
        if (names.Count == 0)
        {
            StatusText.Text = Loc.T("Docker.SelectAtLeastOne");
            return;
        }
        if (start) DockerCli.Start(names); else DockerCli.Stop(names);
        Reload();
    }

    private void CopyPort()
    {
        if (ContainersList.SelectedItem is not DockerContainer c) return;
        if (string.IsNullOrEmpty(c.ExposedPorts))
        {
            StatusText.Text = $"'{c.Name}' {Loc.T("Docker.NoExposedPort")}";
            return;
        }
        try { Clipboard.SetText(c.ExposedPorts); StatusText.Text = $"{Loc.T("Docker.Copied")}: {c.ExposedPorts}"; } catch { }
    }
}
