using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using AmpzDesktopBooster.Credentials;
using AmpzDesktopBooster.Services.Localization;

namespace AmpzDesktopBooster;

/// <summary>
/// Picker con buscador para "Inicia sesión con" (lo abre <see cref="CredentialEditWindow"/>). Es un
/// picker con buscador y no un ComboBox porque recorre TODAS las credenciales de todos los scopes:
/// con un catálogo que crece, una lista desplegable sin filtro obliga a scrollear a ciegas.
///
/// Se tipea sin salir del buscador: ↑↓ mueven la selección de la lista con el foco quieto en el
/// texto, Enter vincula, Esc cancela. Elige UNA cuenta y cierra (lo normal es una identidad por
/// cuenta; para otra, se vuelve a abrir).
///
/// El filtro mira título, usuario, proveedor y Espacio/Contexto — nunca contraseñas ni valores de
/// campos, por lo mismo que la búsqueda del gestor (ver <see cref="CredentialsWindow"/>).
/// </summary>
public partial class CredentialLinkPickerWindow : Window
{
    private sealed class Item
    {
        public required Guid Id { get; init; }
        public required string Title { get; init; }
        public required string Detail { get; init; }
        public required string Haystack { get; init; }
        public required bool IsIdentity { get; init; }
    }

    private readonly List<Item> _all;
    private Guid? _result;

    private CredentialLinkPickerWindow(CredentialStore creds, ICollection<Guid> exclude, string forTitle)
    {
        InitializeComponent();
        HeaderText.Text = forTitle.Trim() == ""
            ? Loc.T("CredPick.HeaderNew")
            : string.Format(Loc.T("CredPick.Header"), forTitle.Trim());

        // Las cuentas de IDENTIDAD (con proveedor) van primero: son a las que uno vincula casi
        // siempre, y así "gm" + Enter elige la de Google sin bajar. Dentro, por título.
        _all = creds.GetAll()
            .SelectMany(kv => kv.Value.Select(c => (Scope: kv.Key, C: c)))
            .Where(x => !exclude.Contains(x.C.Id))
            .Select(x =>
            {
                string where = CredentialLabels.Where(x.Scope, x.C);
                string detail = x.C.User.Trim() == "" ? where : $"{x.C.User} · {where}";
                return new Item
                {
                    Id = x.C.Id, Title = x.C.Title, Detail = detail,
                    Haystack = $"{x.C.Title}\n{x.C.User}\n{where}",
                    IsIdentity = x.C.Provider != CredentialProvider.None,
                };
            })
            .OrderByDescending(i => i.IsIdentity)
            .ThenBy(i => i.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        Refresh();

        FilterBox.TextChanged += (_, _) => Refresh();
        ResultList.MouseDoubleClick += (_, _) => Pick();
        PickBtn.Click += (_, _) => Pick();
        CancelBtn.Click += (_, _) => DialogResult = false;
        PreviewKeyDown += OnKeyDown;
        Loaded += (_, _) => FilterBox.Focus();
    }

    /// <summary>
    /// Abre el picker. <paramref name="exclude"/> = la propia credencial y las ya vinculadas (no se
    /// ofrecen: vincular dos veces la misma no agrega nada). Devuelve el id elegido o null.
    /// </summary>
    public static Guid? Show(Window owner, CredentialStore creds, ICollection<Guid> exclude, string forTitle)
    {
        var dlg = new CredentialLinkPickerWindow(creds, exclude, forTitle) { Owner = owner };
        return dlg.ShowDialog() == true ? dlg._result : null;
    }

    private void Refresh()
    {
        string filter = FilterBox.Text.Trim();
        var shown = filter == ""
            ? _all
            : _all.Where(i => i.Haystack.Contains(filter, StringComparison.CurrentCultureIgnoreCase)).ToList();

        ResultList.ItemsSource = shown;
        if (shown.Count > 0) ResultList.SelectedIndex = 0;
        StatusText.Visibility = Visibility.Collapsed;

        if (shown.Count > 0) { EmptyText.Visibility = Visibility.Collapsed; return; }
        EmptyText.Text = _all.Count == 0 ? Loc.T("CredPick.Empty") : string.Format(Loc.T("CredPick.EmptyFilter"), filter);
        EmptyText.Visibility = Visibility.Visible;
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down: Move(+1); break;
            case Key.Up: Move(-1); break;
            // Con el foco en Cancelar, Enter es "apretar Cancelar" (lo maneja el botón).
            case Key.Enter when !CancelBtn.IsKeyboardFocused: Pick(); break;
            case Key.Escape: DialogResult = false; break;
            default: return;
        }
        e.Handled = true;
    }

    /// <summary>Mueve la selección con el foco quieto en el buscador (se sigue tipeando sin volver).</summary>
    private void Move(int delta)
    {
        int n = ResultList.Items.Count;
        if (n == 0) return;
        int at = Math.Clamp(ResultList.SelectedIndex + delta, 0, n - 1);
        ResultList.SelectedIndex = at;
        ResultList.ScrollIntoView(ResultList.SelectedItem);
    }

    private void Pick()
    {
        if (ResultList.SelectedItem is not Item it)
        {
            // Enter / Vincular sin nada elegido dice por qué (lista vacía o sin selección).
            StatusText.Text = Loc.T("CredPick.NoPick");
            StatusText.Visibility = Visibility.Visible;
            FilterBox.Focus();
            return;
        }
        _result = it.Id;
        DialogResult = true;
    }
}
