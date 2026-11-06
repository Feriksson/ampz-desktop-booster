using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AmpzDesktopBooster.Credentials;
using AmpzDesktopBooster.Desktops;
using AmpzDesktopBooster.Services;
using AmpzDesktopBooster.Services.Localization;

namespace AmpzDesktopBooster;

/// <summary>
/// Alta / edición de una credencial. Se abre como diálogo HIJO de <see cref="CredentialsWindow"/>
/// (Owner + ShowDialog, el mismo patrón que Variables con <see cref="VariableEditWindow"/>): así la
/// guarda de <see cref="WindowActivation.CloseOnDeactivate"/> ve que el foco se fue a una ventana
/// NUESTRA y el gestor, que es efímero, no se cierra debajo del diálogo.
///
/// Guarda ÉL MISMO contra el store (Add / Update / Move) y no le devuelve el objeto al caller para
/// que lo guarde: así el "no se pudo" (la credencial se borró o se movió mientras editabas) se dice
/// acá, con el formulario todavía abierto y lo tipeado intacto.
///
/// A diferencia de <see cref="VariableEditWindow"/>, el selector de scope está también en la EDICIÓN:
/// mover una credencial es una operación del store (<see cref="CredentialStore.Move"/>), sin la
/// cascada de keys que obliga a mover variables desde la config.
/// </summary>
public partial class CredentialEditWindow : Window
{
    /// <summary>
    /// Un campo de texto que puede ir ENMASCARADO: un PasswordBox (nunca deja ver ni copiar lo que
    /// tiene) y un TextBox para cuando el usuario pide verlo. Sólo uno de los dos tiene el valor a la
    /// vez: el oculto queda vacío, para no dejar el secreto duplicado en un control que no se ve.
    /// Lo usan la contraseña y cada campo extra secreto — una sola implementación del "mostrar".
    /// </summary>
    private sealed class SecretInput
    {
        public Grid Root { get; } = new();
        private readonly PasswordBox _hidden = new();
        private readonly TextBox _plain = new();
        private bool _masked = true;

        public SecretInput(string value, bool masked)
        {
            Root.Children.Add(_hidden);
            Root.Children.Add(_plain);
            _masked = masked;
            Apply(value);
        }

        public string Value
        {
            get => _masked ? _hidden.Password : _plain.Text;
            set => Apply(value);
        }

        public void SetMasked(bool masked)
        {
            if (masked == _masked) return;
            string v = Value;
            _masked = masked;
            Apply(v);
        }

        public void Focus() { if (_masked) _hidden.Focus(); else _plain.Focus(); }

        private void Apply(string value)
        {
            _hidden.Password = _masked ? value : "";
            _plain.Text = _masked ? "" : value;
            _hidden.Visibility = _masked ? Visibility.Visible : Visibility.Collapsed;
            _plain.Visibility = _masked ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    /// <summary>Fila editable de un campo extra.</summary>
    private sealed class FieldRow
    {
        public required Grid Root { get; init; }
        public required TextBox NameBox { get; init; }
        public required SecretInput ValueBox { get; init; }
        public required CheckBox SecretBox { get; init; }
    }

    private readonly CredentialStore _creds;
    private readonly ScopePicker _picker;
    private readonly Credential? _editing;     // null = alta
    private readonly string _originalScope;    // scope donde vive (edición) o el default (alta)
    private readonly SecretInput _password;
    private readonly List<FieldRow> _fields = new();
    private readonly string _snapshot;         // huella de lo cargado al abrir, para "¿hay cambios?"

    /// <summary>
    /// ¿Lo secreto se ve en claro? UNO para todo el diálogo (contraseña y campos secretos): estás
    /// editando ESTA credencial, y revelar de a un campo sería un gesto por campo sin ganar nada.
    /// </summary>
    private bool _revealed;

    private CredentialEditWindow(ProjectStore store, string scopeKey, Credential? editing)
    {
        InitializeComponent();
        _creds = store.Credentials;
        _editing = editing?.Clone();
        _originalScope = scopeKey;

        HeaderText.Text = Loc.T(editing is null ? "CredEdit.HeaderNew" : "CredEdit.HeaderEdit");
        _picker = new ScopePicker(store, SpaceCombo, ContextCombo, scopeKey, UpdateScopeText);
        UpdateScopeText();

        var c = _editing ?? new Credential();
        TitleBox.Text = c.Title;
        UserBox.Text = c.User;
        UrlBox.Text = c.Url;
        NoteBox.Text = c.Note;
        _password = new SecretInput(c.Password, masked: true);
        PasswordHost.Content = _password.Root;
        foreach (var f in c.Fields) AddFieldRow(f);
        LengthBox.Text = PasswordGenerator.DefaultLength.ToString();

        _snapshot = Fingerprint();

        RevealBtn.Click += (_, _) => ToggleReveal();
        GenerateBtn.Click += (_, _) => Generate();
        AddFieldBtn.Click += (_, _) => AddFieldRow(null, focus: true);
        OkBtn.Click += (_, _) => Save();
        CancelBtn.Click += (_, _) => Cancel();
        PreviewKeyDown += OnKeyDown;

        // Foco en el título: es lo único obligatorio y, en un alta, lo primero que se escribe. Los
        // combos de scope ya traen el default correcto (el desk donde estás), como en Variables.
        Loaded += (_, _) => { TitleBox.Focus(); TitleBox.SelectAll(); };
        Closing += (_, _) => { _password.Value = ""; foreach (var r in _fields) r.ValueBox.Value = ""; };
    }

    /// <summary>
    /// Abre el editor (alta si <paramref name="editing"/> es null) y guarda contra el store.
    /// <paramref name="scopeKey"/> es el scope del desk (alta) o donde vive la credencial (edición).
    /// Devuelve el id y el scope FINAL de lo guardado, o null si se canceló.
    /// </summary>
    public static (Guid Id, string ScopeKey)? Show(Window owner, ProjectStore store, string scopeKey,
                                                   Credential? editing)
    {
        var dlg = new CredentialEditWindow(store, scopeKey, editing) { Owner = owner };
        return dlg.ShowDialog() == true ? dlg._result : null;
    }

    private (Guid Id, string ScopeKey)? _result;

    // ── Scope ──────────────────────────────────────────────────────────────────

    private void UpdateScopeText()
    {
        if (_picker is null) return; // el ctor del picker avisa antes de que exista la referencia
        string label = _picker.Label;
        bool moving = _editing is not null && !Same(_picker.ScopeKey, _originalScope);
        ScopeText.Text = string.Format(Loc.T(_editing is null ? "Scope.SavesIn"
                                            : moving ? "CredEdit.MovesTo" : "Scope.SavedIn"), label);
        ScopeText.Foreground = moving ? new SolidColorBrush(Color.FromRgb(0xE5, 0xC0, 0x7B))
                                      : new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0x9A));
    }

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    // ── Campos extra ───────────────────────────────────────────────────────────

    private void AddFieldRow(CustomField? f, bool focus = false)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var name = new TextBox { Text = f?.Name ?? "" };
        bool secret = f?.IsSecret ?? false;
        var value = new SecretInput(f?.Value ?? "", masked: secret && !_revealed);
        var secretBox = new CheckBox { Content = Loc.T("CredEdit.FldSecret"), IsChecked = secret, Margin = new Thickness(10, 0, 0, 0) };
        // Copiar el VALOR desde el editor (pedido del usuario): al cargar una key nueva se la quiere
        // pegar en otro lado sin cerrar el diálogo e ir a buscarla al gestor. Copia lo que está
        // escrito AHORA, guardado o no.
        var copy = new Button { Content = Loc.T("Creds.BtnCopyField"), Margin = new Thickness(10, 0, 0, 0) };
        var remove = new Button { Content = Loc.T("CredEdit.BtnRemoveField"), Margin = new Thickness(6, 0, 0, 0) };

        // Los rótulos visibles son el encabezado "Nombre | Valor" de arriba (FieldsHeader); acá va el
        // nombre accesible, que un lector de pantalla no saca de un encabezado suelto.
        System.Windows.Automation.AutomationProperties.SetName(name, Loc.T("CredEdit.FieldName"));
        System.Windows.Automation.AutomationProperties.SetName(value.Root, Loc.T("CredEdit.FieldValue"));
        Grid.SetColumn(name, 0);
        Grid.SetColumn(value.Root, 2);
        Grid.SetColumn(secretBox, 3);
        Grid.SetColumn(copy, 4);
        Grid.SetColumn(remove, 5);
        grid.Children.Add(name);
        grid.Children.Add(value.Root);
        grid.Children.Add(secretBox);
        grid.Children.Add(copy);
        grid.Children.Add(remove);

        var row = new FieldRow { Root = grid, NameBox = name, ValueBox = value, SecretBox = secretBox };
        secretBox.Checked += (_, _) => value.SetMasked(!_revealed);
        secretBox.Unchecked += (_, _) => value.SetMasked(false);
        copy.Click += (_, _) => FlashCopy(copy, value.Value);
        remove.Click += (_, _) => { _fields.Remove(row); FieldsPanel.Children.Remove(grid); UpdateFieldsHeader(); };

        _fields.Add(row);
        FieldsPanel.Children.Add(grid);
        UpdateFieldsHeader();
        if (focus) name.Focus();
    }

    /// <summary>
    /// Copia y lo confirma EN el botón (el diálogo no tiene flash propio): «Copiado» ~1,5s y vuelve.
    /// Vacío o portapapeles tomado también lo dice ahí — un botón que no hace nada se lee como roto.
    /// </summary>
    private static void FlashCopy(Button btn, string text)
    {
        string label = Loc.T("Creds.BtnCopyField");
        btn.Content = text == "" ? Loc.T("CredEdit.CopyEmpty")
                    : ClipboardCopy.TryCopy(text) ? Loc.T("CredEdit.Copied")
                    : Loc.T("Creds.ClipboardBusy");
        var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
        t.Tick += (_, _) => { t.Stop(); btn.Content = label; };
        t.Start();
    }

    /// <summary>El encabezado de columnas sólo tiene sentido con al menos una fila debajo.</summary>
    private void UpdateFieldsHeader() =>
        FieldsHeader.Visibility = _fields.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

    // ── Revelar / generar ──────────────────────────────────────────────────────

    private void ToggleReveal()
    {
        _revealed = !_revealed;
        _password.SetMasked(!_revealed);
        foreach (var r in _fields) r.ValueBox.SetMasked(r.SecretBox.IsChecked == true && !_revealed);
        RevealBtn.Content = Loc.T(_revealed ? "Creds.BtnHide" : "Creds.BtnReveal");
    }

    private void Generate()
    {
        if (!int.TryParse(LengthBox.Text.Trim(), out int length)
            || length < PasswordGenerator.MinLength || length > PasswordGenerator.MaxLength)
        {
            ShowGenStatus(string.Format(Loc.T("CredEdit.GenBadLength"), PasswordGenerator.MinLength, PasswordGenerator.MaxLength), error: true);
            LengthBox.Focus();
            LengthBox.SelectAll();
            return;
        }

        string? pwd = PasswordGenerator.Generate(length, UpperBox.IsChecked == true, LowerBox.IsChecked == true,
                                                 DigitsBox.IsChecked == true, SymbolsBox.IsChecked == true);
        if (pwd is null)
        {
            ShowGenStatus(Loc.T("CredEdit.GenNoSet"), error: true);
            UpperBox.Focus();
            return;
        }

        // Cae en el campo como esté (enmascarado salvo que hayas pedido verlo): generar no es revelar.
        _password.Value = pwd;
        ShowGenStatus(string.Format(Loc.T("CredEdit.GenDone"), length), error: false);
    }

    private void ShowGenStatus(string text, bool error)
    {
        GenStatus.Text = text;
        GenStatus.Foreground = new SolidColorBrush(error ? Color.FromRgb(0xE0, 0x6C, 0x75) : Color.FromRgb(0x7A, 0xC4, 0x8A));
        GenStatus.Visibility = Visibility.Visible;
    }

    // ── Teclado ────────────────────────────────────────────────────────────────

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        var mods = Keyboard.Modifiers;
        bool ctrl = (mods & ModifierKeys.Control) != 0;
        bool shift = (mods & ModifierKeys.Shift) != 0;

        switch (e.Key)
        {
            // Ctrl+Enter y no Enter pelado: la nota es multilínea y un Enter ahí es un salto de
            // renglón; con Enter = guardar, cerrarías el diálogo a mitad de una frase.
            case Key.Enter when ctrl:            Save();          break;
            case Key.R when ctrl:                ToggleReveal();  break;
            case Key.G when ctrl:                Generate();      break;
            case Key.N when ctrl && shift:       AddFieldRow(null, focus: true); break;
            // Esc con un combo desplegado es del combo (cerrarlo), no del diálogo.
            case Key.Escape when !SpaceCombo.IsDropDownOpen && !ContextCombo.IsDropDownOpen:
                Cancel();
                break;
            default: return;
        }
        e.Handled = true;
    }

    // ── Guardar / cancelar ─────────────────────────────────────────────────────

    /// <summary>
    /// Huella de TODO lo editable, para decidir si cancelar pierde algo. Vive sólo en memoria y
    /// mientras el diálogo está abierto (lleva secretos adentro: nunca se loguea ni se persiste).
    /// </summary>
    private string Fingerprint() => string.Join("\u001F", new[]
    {
        _picker.ScopeKey, TitleBox.Text, UserBox.Text, _password.Value, UrlBox.Text, NoteBox.Text,
    }.Concat(_fields.SelectMany(r => new[] { r.NameBox.Text, r.ValueBox.Value, r.SecretBox.IsChecked == true ? "1" : "0" })));

    /// <summary>
    /// Esc / Cancelar cierran sin preguntar si no hay nada que perder; con cambios, confirman — un Esc
    /// de más no puede tirarte una credencial a medio cargar (la contraseña generada no se repite).
    /// </summary>
    private void Cancel()
    {
        if (Fingerprint() != _snapshot)
        {
            var answer = MessageBox.Show(this, Loc.T("CredEdit.DiscardConfirm"), Loc.T("CredEdit.DlgTitle"),
                                         MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes) return;
        }
        DialogResult = false;
    }

    private void ShowError(string text, Action focus)
    {
        ErrorText.Text = text;
        ErrorText.Visibility = Visibility.Visible;
        focus();
    }

    private void Save()
    {
        ErrorText.Visibility = Visibility.Collapsed;

        string title = TitleBox.Text.Trim();
        if (title == "")
        {
            ShowError(Loc.T("CredEdit.NeedTitle"), () => TitleBox.Focus());
            return;
        }

        // Campos: las filas vacías del todo se descartan (agregar una y no usarla no es un error);
        // una fila con valor pero sin nombre sí lo es — se guardaría un dato que nadie sabe qué es.
        var fields = new List<CustomField>();
        for (int i = 0; i < _fields.Count; i++)
        {
            var r = _fields[i];
            string name = r.NameBox.Text.Trim();
            string value = r.ValueBox.Value;
            if (name == "" && value == "") continue;
            if (name == "")
            {
                var box = r.NameBox;
                ShowError(string.Format(Loc.T("CredEdit.NeedFieldName"), i + 1), () => box.Focus());
                return;
            }
            fields.Add(new CustomField { Name = name, Value = value, IsSecret = r.SecretBox.IsChecked == true });
        }

        var c = _editing?.Clone() ?? new Credential();
        c.Title = title;
        c.User = UserBox.Text.Trim();
        c.Password = _password.Value; // tal cual: un espacio al borde puede ser parte de la contraseña
        c.Url = UrlBox.Text.Trim();
        c.Note = NoteBox.Text;
        c.Fields = fields;

        string target = _picker.ScopeKey;
        Guid id;
        if (_editing is null)
        {
            id = _creds.Add(target, c).Id;
        }
        else
        {
            // Primero el contenido, en el scope donde vive; después, si cambió, la mudanza. Si la
            // credencial ya no está ahí (se borró o se movió desde otra ventana mientras editabas),
            // se dice y el formulario queda abierto con lo tuyo: nada se guarda "en algún lado".
            if (!_creds.Update(_originalScope, c) || !_creds.Move(c.Id, _originalScope, target))
            {
                ShowError(Loc.T("CredEdit.Gone"), () => TitleBox.Focus());
                return;
            }
            id = c.Id;
        }

        _result = (id, target);
        DialogResult = true;
    }
}
