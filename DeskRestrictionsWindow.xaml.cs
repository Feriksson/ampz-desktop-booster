using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;
using AmpzDesktopBooster.Desktops;
using AmpzDesktopBooster.Services;

namespace AmpzDesktopBooster;

/// <summary>
/// Proteger escritorios (Win+F8): activa/desactiva la restricción en los desks protegibles
/// (no MAIN, no DESK+). Enter/doble-clic togglea. La whitelist se llena con Win+F9.
/// </summary>
public partial class DeskRestrictionsWindow : Window
{
    private sealed record Row(string Name)
    {
        public string? Status { get; set; }
        public override string ToString() => $"{Name}   —   {Status}";
    }

    private readonly RestrictionStore _restrictions;
    private readonly DesktopService _desktops;
    private readonly List<Row> _rows = new();

    public DeskRestrictionsWindow(RestrictionStore restrictions, DesktopService desktops)
    {
        InitializeComponent();
        _restrictions = restrictions;
        _desktops = desktops;
        Icon = AppIcon.TryLoadForWindow();

        for (int i = 0; i < _desktops.Count; i++)
        {
            string name = _desktops.GetName(i);
            if (RestrictionStore.IsRestrictable(name))
                _rows.Add(new Row(name));
        }
        Refresh();

        DeskList.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { ToggleSelected(); e.Handled = true; }
            else if (e.Key == Key.Escape) { Close(); e.Handled = true; }
        };
        DeskList.MouseDoubleClick += (_, _) => ToggleSelected();
        ToggleBtn.Click += (_, _) => ToggleSelected();
        CloseBtn.Click += (_, _) => Close();
        Loaded += (_, _) => DeskList.Focus();
    }

    private void Refresh()
    {
        int sel = DeskList.SelectedIndex;
        DeskList.Items.Clear();
        foreach (var r in _rows)
        {
            int wl = _restrictions.WhitelistCount(r.Name);
            r.Status = _restrictions.IsRestricted(r.Name) ? $"🔒 Protegido ({wl} apps)" : "— Libre";
            DeskList.Items.Add(r);
        }
        EmptyHint.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        DeskList.SelectedIndex = sel >= 0 && sel < DeskList.Items.Count ? sel : (DeskList.Items.Count > 0 ? 0 : -1);
    }

    private void ToggleSelected()
    {
        if (DeskList.SelectedItem is not Row row) return;
        bool nowOn = !_restrictions.IsRestricted(row.Name);
        _restrictions.SetRestricted(row.Name, nowOn);
        if (nowOn) Services.Toasts.ProtectionOn(row.Name);
        else Services.Toasts.ProtectionOff(row.Name);
        Refresh();
    }
}
