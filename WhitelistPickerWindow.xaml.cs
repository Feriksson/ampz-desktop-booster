using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;
using AmpzDesktopBooster.Desktops;
using AmpzDesktopBooster.Services;
using AmpzDesktopBooster.Services.Localization;

namespace AmpzDesktopBooster;

/// <summary>
/// Permitir app en escritorio protegido (Win+F9): agrega el proceso de la ventana activa a la
/// whitelist del desk elegido y la mueve ahí. El proc + hwnd se capturan ANTES de abrir (si no,
/// el foreground pasa a ser esta ventana).
/// </summary>
public partial class WhitelistPickerWindow : Window
{
    private sealed record Row(int Idx, string Name, bool Already)
    {
        public override string ToString() => $"{Name}   —   {(Already ? Loc.T("Whitelist.RowAlready") : Loc.T("Whitelist.RowAdd"))}";
    }

    private readonly RestrictionStore _restrictions;
    private readonly DesktopService _desktops;
    private readonly string _proc;
    private readonly IntPtr _hwnd;

    public WhitelistPickerWindow(RestrictionStore restrictions, DesktopService desktops, string proc, IntPtr hwnd)
    {
        InitializeComponent();
        _restrictions = restrictions;
        _desktops = desktops;
        _proc = proc;
        _hwnd = hwnd;
        Icon = AppIcon.TryLoadForWindow();

        HeaderText.Text = $"App: {proc}";

        for (int i = 0; i < _desktops.Count; i++)
        {
            string name = _desktops.GetName(i);
            if (_restrictions.IsRestricted(name))
                DeskList.Items.Add(new Row(i, name, _restrictions.IsWhitelisted(name, proc)));
        }

        EmptyHint.Visibility = DeskList.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (DeskList.Items.Count > 0) DeskList.SelectedIndex = 0;

        DeskList.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { AddAndMove(); e.Handled = true; }
            else if (e.Key == Key.Escape) { Close(); e.Handled = true; }
        };
        DeskList.MouseDoubleClick += (_, _) => AddAndMove();
        CloseBtn.Click += (_, _) => Close();
        Loaded += (_, _) => DeskList.Focus();
    }

    private void AddAndMove()
    {
        if (DeskList.SelectedItem is not Row row) return;
        _restrictions.AddToWhitelist(row.Name, _proc);
        _desktops.SendWindowTo(_hwnd, row.Idx, follow: true); // el move SÍ usa el índice live
        Services.Toasts.Whitelisted(_proc, row.Name);
        Close();
    }
}
