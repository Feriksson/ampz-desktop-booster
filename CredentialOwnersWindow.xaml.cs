using System;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using AmpzDesktopBooster.Credentials;
using AmpzDesktopBooster.Services.Localization;

namespace AmpzDesktopBooster;

/// <summary>
/// Dueños de credenciales (Ctrl+D desde <see cref="CredentialsWindow"/>): la lista GLOBAL que
/// alimenta "Pertenece a". Acá se ARREGLA la lista — renombrar una grafía, borrar a quien ya no
/// corresponde, cargar uno de antemano —; asignar un dueño a una credencial es del editor.
///
/// Cada botón es UNA operación del store que se guarda sola, y después se relee la lista: no hay
/// "Guardar" ni estado a medias (mismo criterio que Config → Conocimiento). El gestor refresca al
/// volver, así que un renombre se ve en todas las filas sin más.
/// </summary>
public partial class CredentialOwnersWindow : Window
{
    private sealed class Item
    {
        public required Guid Id { get; init; }
        public required string Name { get; init; }
        public required string Detail { get; init; }
    }

    private readonly CredentialStore _creds;

    private CredentialOwnersWindow(CredentialStore creds)
    {
        InitializeComponent();
        _creds = creds;
        Refresh(null);

        OwnerList.MouseDoubleClick += (_, _) => Rename();
        AddBtn.Click += (_, _) => Add();
        RenameBtn.Click += (_, _) => Rename();
        DeleteBtn.Click += (_, _) => Delete();
        CloseBtn.Click += (_, _) => Close();
        PreviewKeyDown += OnKeyDown;
        Loaded += (_, _) => FocusList();
    }

    /// <summary>Abre el diálogo modal sobre el gestor. Las mutaciones ya quedaron guardadas al volver.</summary>
    public static void Show(Window owner, CredentialStore creds) =>
        new CredentialOwnersWindow(creds) { Owner = owner }.ShowDialog();

    private Item? Selected => OwnerList.SelectedItem as Item;

    private void Refresh(Guid? keep)
    {
        var items = _creds.Owners().Select(o =>
        {
            int n = _creds.OwnerUsage(o.Id);
            return new Item
            {
                Id = o.Id, Name = o.Name,
                Detail = n == 0 ? Loc.T("CredOwners.UsageNone")
                       : string.Format(Loc.T(n == 1 ? "CredOwners.UsageOne" : "CredOwners.UsageMany"), n),
            };
        }).ToList();

        OwnerList.ItemsSource = items;
        OwnerList.SelectedItem = items.FirstOrDefault(i => i.Id == keep) ?? items.FirstOrDefault();
        EmptyText.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>El foco a la fila elegida: así ↑↓ / F2 / Supr andan sin tocar el mouse.</summary>
    private void FocusList()
    {
        OwnerList.UpdateLayout();
        (OwnerList.ItemContainerGenerator.ContainerFromItem(OwnerList.SelectedItem) as UIElement ?? OwnerList).Focus();
    }

    private void ShowStatus(string? text)
    {
        StatusText.Text = text ?? "";
        StatusText.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        bool onButton = Keyboard.FocusedElement is System.Windows.Controls.Button;
        switch (e.Key)
        {
            case Key.N when ctrl:             Add();    break;
            case Key.F2:                      Rename(); break;
            // Con el foco en un botón, Enter es "apretar ese botón" (lo maneja el botón).
            case Key.Enter when !onButton:    Rename(); break;
            case Key.Delete:                  Delete(); break;
            case Key.Escape:                  Close();  break;
            default: return;
        }
        e.Handled = true;
    }

    private void Add()
    {
        ShowStatus(null);
        string? name = PromptDialog.Show(this, Loc.T("CredOwners.AddTitle"), Loc.T("CredOwners.NameLabel"));
        if (name is null) { FocusList(); return; }
        if (name.Trim() == "") { ShowStatus(Loc.T("CredOwners.NeedName")); FocusList(); return; }

        // EnsureOwner y no un "Add" a ciegas: si ya existe (con otras mayúsculas) se elige ése y se
        // dice, en vez de abrir la segunda grafía que la lista vino a evitar.
        bool existed = _creds.Owners().Any(o => string.Equals(o.Name, name.Trim(), StringComparison.CurrentCultureIgnoreCase));
        var id = _creds.EnsureOwner(name);
        Refresh(id);
        if (existed) ShowStatus(string.Format(Loc.T("CredOwners.AlreadyExists"), Selected?.Name ?? name.Trim()));
        FocusList();
    }

    private void Rename()
    {
        ShowStatus(null);
        if (Selected is not { } it) { ShowStatus(Loc.T("CredOwners.NoSelection")); return; }

        string? name = PromptDialog.Show(this, Loc.T("CredOwners.RenameTitle"), Loc.T("CredOwners.NameLabel"), it.Name);
        if (name is null) { FocusList(); return; }

        string? reason = _creds.RenameOwner(it.Id, name) switch
        {
            OwnerOpResult.Ok => null,
            OwnerOpResult.EmptyName => Loc.T("CredOwners.NeedName"),
            OwnerOpResult.DuplicateName => string.Format(Loc.T("CredOwners.Duplicate"), name.Trim()),
            _ => Loc.T("CredOwners.Gone"),
        };
        Refresh(it.Id);
        ShowStatus(reason);
        FocusList();
    }

    /// <summary>
    /// Borrar pide confirmación diciendo CUÁNTAS credenciales se quedan sin dueño (mismo criterio que
    /// el aviso de "La usan" al borrar una cuenta de identidad): el "sí" se da sabiendo qué cambia.
    /// Las credenciales NO se borran — sólo pierden el dueño.
    /// </summary>
    private void Delete()
    {
        ShowStatus(null);
        if (Selected is not { } it) { ShowStatus(Loc.T("CredOwners.NoSelection")); return; }

        int n = _creds.OwnerUsage(it.Id);
        string message = string.Format(Loc.T("CredOwners.DeleteConfirm"), it.Name);
        if (n > 0)
            message += "\n\n" + string.Format(Loc.T(n == 1 ? "CredOwners.DeleteAffectsOne" : "CredOwners.DeleteAffectsMany"), n);

        var answer = MessageBox.Show(this, message, Loc.T("CredOwners.WindowTitle"),
                                     MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) { FocusList(); return; }

        if (_creds.DeleteOwner(it.Id) is null) ShowStatus(Loc.T("CredOwners.Gone"));
        Refresh(null);
        FocusList();
    }
}
