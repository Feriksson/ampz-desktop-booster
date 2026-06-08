using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using AmpzDesktopBooster.Desktops;
using AmpzDesktopBooster.Interop;
using AmpzDesktopBooster.Persistence;
using AmpzDesktopBooster.Services;
using AmpzDesktopBooster.Services.Attention;
using AmpzDesktopBooster.Services.Tasks;

namespace AmpzDesktopBooster;

/// <summary>
/// Ventana de configuración con pestañas — el hogar de todo lo ajustable de la app.
/// Por ahora trae la pestaña DESKTOPS (gestionar el set de escritorios virtuales);
/// las próximas fases sumarán pestañas (Widgets, Proyectos, Pins, etc.) al mismo TabControl.
/// </summary>
public partial class ConfigWindow : Window
{
    private readonly DesktopConfig _config;
    private readonly Apps.AppsConfig _apps;
    private readonly DesktopService _desktops;
    private readonly RestrictionStore _restrictions;
    private readonly PinStore _pins;
    private readonly Action _onApplied;

    // Config del proveedor de tareas. La pestaña Tareas la maneja por su cuenta (Load/Save) — NO
    // pasa por el constructor ni por App.OnStartup, para no tocar el arranque core (hooks sensibles).
    private readonly TasksSettings _tasks;

    public ConfigWindow(DesktopConfig config, Apps.AppsConfig apps, DesktopService desktops,
        RestrictionStore restrictions, PinStore pins, Action onApplied)
    {
        InitializeComponent();

        _config = config;
        _apps = apps;
        _desktops = desktops;
        _restrictions = restrictions;
        _pins = pins;
        _onApplied = onApplied;

        Icon = AppIcon.TryLoadForWindow(); // ícono real en taskbar / Alt-Tab / título

        AutoCreateChk.IsChecked = _config.AutoCreate;
        RefreshList();

        UpBtn.Click += (_, _) => Move(-1);
        DownBtn.Click += (_, _) => Move(+1);
        RenameBtn.Click += (_, _) => RenameSelected();
        RemoveBtn.Click += (_, _) => RemoveSelected();
        AddBtn.Click += (_, _) => AddNew();
        ApplyNowBtn.Click += (_, _) => CreateMissingNow();
        SaveBtn.Click += (_, _) => SaveAndApply();
        DeskList.SelectionChanged += (_, _) => SyncNameBox();

        // ── Pestaña Aplicaciones ──
        RefreshApps();
        AppAddBtn.Click += (_, _) => AddApp();
        AppRemoveBtn.Click += (_, _) => RemoveApp();

        // ── Pestaña Protecciones ──
        RefreshProtDesks();
        ProtDeskCombo.SelectionChanged += (_, _) => RefreshProtState();
        ProtectedChk.Click += (_, _) => ToggleProtected();             // Click: NO se dispara al setear IsChecked por código
        WlRemoveBtn.Click += (_, _) => RemoveFromWhitelist();
        WlAddFromRunningBtn.Click += (_, _) => AddFromRunning();
        RunningRefreshBtn.Click += (_, _) => RefreshRunning();
        WlAddManualBtn.Click += (_, _) => AddManual();
        RefreshRunning();
        if (ProtDeskCombo.Items.Count > 0) ProtDeskCombo.SelectedIndex = 0; // dispara RefreshProtState

        // ── Pestaña Anclajes ──
        RefreshPinDesks();
        RefreshPins();
        RefreshPinRunning();
        UnpinBtn.Click += (_, _) => UnpinSelected();
        UnpinAllBtn.Click += (_, _) => UnpinAll();
        PinAddFromRunningBtn.Click += (_, _) => PinFromRunning();
        PinRunningRefreshBtn.Click += (_, _) => RefreshPinRunning();
        PinAddManualBtn.Click += (_, _) => PinManual();

        // ── Pestaña General ──
        DataPathText.Text = AppPaths.DataDir;
        ResetAllBtn.Click += (_, _) => ResetAll();

        // ── Pestaña Tareas ──
        // OJO orden: cableamos handlers ANTES de InitTasksTab. Si init seteara SelectedIndex con el
        // handler aún sin enganchar, OnAccountSelectionChanged NUNCA corre → _currentAccount queda
        // null → Quitar/Probar no responden hasta que el usuario fuerza una selección manual o
        // agrega una cuenta nueva (AddAccount sí setea _currentAccount a mano). Bug visto en vivo.
        _tasks = TasksSettings.Load();
        AccountsList.SelectionChanged += (_, _) => OnAccountSelectionChanged();
        AccountAddBtn.Click += (_, _) => AddAccount();
        AccountRemoveBtn.Click += (_, _) => RemoveSelectedAccount();
        AcctKindCombo.SelectionChanged += (_, _) => OnKindChanged();
        // CRÍTICO: RefreshAccountsList sólo va si el cambio vino del usuario (no de RefreshFields).
        // Si no, RefreshFields → SetText → TextChanged → RefreshAccountsList → Items.Clear →
        // SelectionChanged → RefreshFields → recursión infinita (la app exploto exactamente por esto).
        AcctNameBox.TextChanged += (_, _) =>
        {
            if (_currentAccount == null || _loadingFields) return;
            _currentAccount.DisplayName = AcctNameBox.Text;
            RefreshAccountsList(preserveSelection: true);
        };
        AcctEnabledChk.Checked += (_, _) =>
        {
            if (_currentAccount == null || _loadingFields) return;
            _currentAccount.Enabled = true;
            RefreshAccountsList(preserveSelection: true);
        };
        AcctEnabledChk.Unchecked += (_, _) =>
        {
            if (_currentAccount == null || _loadingFields) return;
            _currentAccount.Enabled = false;
            RefreshAccountsList(preserveSelection: true);
        };
        VkUrlBox.TextChanged     += (_, _) => { if (_currentAccount?.Vikunja != null && !_loadingFields) _currentAccount.Vikunja.BaseUrl  = VkUrlBox.Text; };
        VkUserBox.TextChanged    += (_, _) => { if (_currentAccount?.Vikunja != null && !_loadingFields) _currentAccount.Vikunja.Username = VkUserBox.Text; };
        VkTokenBox.TextChanged   += (_, _) => { if (_currentAccount?.Vikunja != null && !_loadingFields) _currentAccount.Vikunja.Token    = VkTokenBox.Text; };
        JiraUrlBox.TextChanged   += (_, _) => { if (_currentAccount?.Jira    != null && !_loadingFields) _currentAccount.Jira.BaseUrl     = JiraUrlBox.Text; };
        JiraEmailBox.TextChanged += (_, _) => { if (_currentAccount?.Jira    != null && !_loadingFields) _currentAccount.Jira.Email       = JiraEmailBox.Text; };
        JiraTokenBox.TextChanged += (_, _) => { if (_currentAccount?.Jira    != null && !_loadingFields) _currentAccount.Jira.Token       = JiraTokenBox.Text; };
        TrelloKeyBox.TextChanged          += (_, _) => { if (_currentAccount?.Trello != null && !_loadingFields) _currentAccount.Trello.ApiKey          = TrelloKeyBox.Text; };
        TrelloTokenBox.TextChanged        += (_, _) => { if (_currentAccount?.Trello != null && !_loadingFields) _currentAccount.Trello.Token           = TrelloTokenBox.Text; };
        TrelloIgnoredListsBox.TextChanged += (_, _) => { if (_currentAccount?.Trello != null && !_loadingFields) _currentAccount.Trello.IgnoredListsRaw = TrelloIgnoredListsBox.Text; };
        TaskTestBtn.Click += async (_, _) => await TestSelectedAccount();
        TaskSaveBtn.Click += (_, _) => SaveTasksSettings();
        InitTasksTab(); // ahora sí: con todos los handlers ya cableados, SelectedIndex=0 dispara la selección

        // ── Pestaña Atención ──
        InitAttentionTab();
    }

    // ── Pestaña Atención ────────────────────────────────────────────────────────
    // Config propia (Load/Save acá dentro), igual que Tareas: NO toca el arranque core (hooks
    // sensibles). El AttentionService relee attention.json en cada señal, así que "Guardar" impacta
    // al instante, sin reiniciar la app.

    private void InitAttentionTab()
    {
        // Combos con los .wav del sistema (listas independientes — no compartir ItemsSource entre dos).
        AttnSoundUrgentCombo.ItemsSource = AttentionSettings.AvailableSounds();
        AttnSoundDoneCombo.ItemsSource = AttentionSettings.AvailableSounds();

        var s = AttentionSettings.Load();
        AttnEnabledChk.IsChecked = s.Enabled;
        AttnSoundEnabledChk.IsChecked = s.SoundEnabled;
        AttnSameDeskToastChk.IsChecked = s.ToastOnSameDesk;
        AttnSameDeskSoundChk.IsChecked = s.SoundOnSameDesk;
        AttnVolumeSlider.Value = s.Volume;
        AttnVolumeText.Text = $"{s.Volume}%";
        SelectSound(AttnSoundUrgentCombo, s.SoundActionNeeded);
        SelectSound(AttnSoundDoneCombo, s.SoundCompleted);

        AttnVolumeSlider.ValueChanged += (_, _) => AttnVolumeText.Text = $"{(int)AttnVolumeSlider.Value}%";
        AttnTestUrgentBtn.Click += (_, _) => AttentionSound.Play(SelectedSound(AttnSoundUrgentCombo), (int)AttnVolumeSlider.Value);
        AttnTestDoneBtn.Click += (_, _) => AttentionSound.Play(SelectedSound(AttnSoundDoneCombo), (int)AttnVolumeSlider.Value);
        AttnBrowseUrgentBtn.Click += (_, _) => BrowseCustomWav(AttnSoundUrgentCombo);
        AttnBrowseDoneBtn.Click += (_, _) => BrowseCustomWav(AttnSoundDoneCombo);
        AttnSaveBtn.Click += (_, _) => SaveAttention();
    }

    /// <summary>
    /// Abre un selector de archivo .wav y agrega el elegido al combo como item seleccionado (con su
    /// path completo). Así el usuario usa SU sonido, no solo los del sistema. AttentionSound.Play
    /// distingue path-completo de nombre-del-sistema solo, así que persistir el path alcanza.
    /// </summary>
    private static void BrowseCustomWav(ComboBox combo)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Elegí un sonido (.wav)",
            Filter = "Audio WAV (*.wav)|*.wav",
            CheckFileExists = true,
        };
        if (dlg.ShowDialog() != true) return;

        var items = (combo.ItemsSource as IEnumerable<string>)?.ToList() ?? new List<string>();
        if (!items.Contains(dlg.FileName, StringComparer.OrdinalIgnoreCase))
            items.Add(dlg.FileName);
        combo.ItemsSource = items;
        combo.SelectedItem = items.First(x => string.Equals(x, dlg.FileName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Selecciona en el combo el sonido guardado. Si es un wav PROPIO (path completo) que no está en
    /// la lista del sistema pero existe en disco, lo agrega y lo selecciona. Si no existe nada, cae a
    /// "(Ninguno)".
    /// </summary>
    private static void SelectSound(ComboBox combo, string wav)
    {
        var items = (combo.ItemsSource as IEnumerable<string>)?.ToList() ?? new List<string>();
        var match = items.FirstOrDefault(x => string.Equals(x, wav, StringComparison.OrdinalIgnoreCase));

        if (match is null && !string.IsNullOrEmpty(wav) && wav != AttentionSettings.NoneSound
            && System.IO.File.Exists(wav))
        {
            // wav propio (path completo) guardado de antes → lo reincorporamos al combo.
            items.Add(wav);
            combo.ItemsSource = items;
            match = wav;
        }

        if (match is not null) combo.SelectedItem = match;
        else combo.SelectedIndex = 0; // "(Ninguno)"
    }

    private static string SelectedSound(ComboBox combo) =>
        combo.SelectedItem as string ?? AttentionSettings.NoneSound;

    private void SaveAttention()
    {
        new AttentionSettings
        {
            Enabled = AttnEnabledChk.IsChecked == true,
            SoundEnabled = AttnSoundEnabledChk.IsChecked == true,
            ToastOnSameDesk = AttnSameDeskToastChk.IsChecked == true,
            SoundOnSameDesk = AttnSameDeskSoundChk.IsChecked == true,
            Volume = (int)AttnVolumeSlider.Value,
            SoundActionNeeded = SelectedSound(AttnSoundUrgentCombo),
            SoundCompleted = SelectedSound(AttnSoundDoneCombo),
        }.Save();
    }

    // ── Pestaña Anclajes ───────────────────────────────────────────────────────
    // Los pins se guardan por NOMBRE de desk (igual que protecciones); reordenar escritorios no los
    // rompe. A diferencia de las protecciones, un pin puede ir a CUALQUIER desk, no solo a los
    // restringibles → el combo lista todos. Se persiste al instante (sin botón Guardar).

    /// <summary>Una app anclada en el listado: muestra "proc.exe → NombreDesk".</summary>
    private sealed record PinRow(string Proc, string Desk)
    {
        public override string ToString() => $"{Proc}   →   {Desk}";
    }

    /// <summary>Combo de destino del pin: TODOS los escritorios actuales (por nombre).</summary>
    private void RefreshPinDesks()
    {
        string? prev = PinDeskCombo.SelectedItem as string;
        PinDeskCombo.Items.Clear();
        for (int i = 0; i < _desktops.Count; i++)
            PinDeskCombo.Items.Add(_desktops.GetName(i));
        if (prev is not null && PinDeskCombo.Items.Contains(prev)) PinDeskCombo.SelectedItem = prev;
        else if (PinDeskCombo.Items.Count > 0) PinDeskCombo.SelectedIndex = 0;
    }

    private void RefreshPins()
    {
        PinnedBox.Items.Clear();
        foreach (var kv in _pins.All.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
            PinnedBox.Items.Add(new PinRow(kv.Key, kv.Value));
    }

    /// <summary>Apps abiertas para anclar, salteando las bloqueadas (sistema + la propia app).</summary>
    private void RefreshPinRunning()
    {
        PinRunningBox.Items.Clear();
        foreach (var (proc, title) in WindowMethods.RunningTopLevelApps())
        {
            if (_pins.IsBlocked(proc)) continue;
            PinRunningBox.Items.Add(new RunningRow(proc, title));
        }
    }

    private void PinFromRunning()
    {
        if (PinDeskCombo.SelectedItem is not string desk) return;
        if (PinRunningBox.SelectedItem is not RunningRow row) return;
        _pins.Pin(row.Proc, desk); // re-anclar el mismo proc solo actualiza su desk destino
        RefreshPins();
    }

    private void PinManual()
    {
        if (PinDeskCombo.SelectedItem is not string desk) return;
        string proc = PinManualBox.Text.Trim();
        if (proc == "") return;
        if (!proc.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) proc += ".exe"; // normalizamos al .exe
        if (_pins.IsBlocked(proc))
        {
            MessageBox.Show($"'{proc}' no puede anclarse (proceso del sistema).", "Anclar",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        _pins.Pin(proc, desk);
        PinManualBox.Clear();
        RefreshPins();
    }

    private void UnpinSelected()
    {
        if (PinnedBox.SelectedItem is not PinRow row) return;
        _pins.Unpin(row.Proc);
        RefreshPins();
    }

    private void UnpinAll()
    {
        if (_pins.All.Count == 0) return;
        if (MessageBox.Show("¿Desanclar todas las apps?", "Anclajes",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _pins.Clear();
        RefreshPins();
    }

    // ── Pestaña General ───────────────────────────────────────────────────────

    /// <summary>
    /// Borra TODA la config y reinicia la app. Operación destructiva → confirmación explícita que
    /// enumera qué se pierde. El borrado real + el reinicio (con su relauncher anti-mutex) viven en
    /// AppPaths.ResetAllData() y App.RestartApplication().
    /// </summary>
    private void ResetAll()
    {
        var r = MessageBox.Show(
            "Esto borra TODA la configuración y NO se puede deshacer:\n\n" +
            "• Escritorios gestionados\n" +
            "• Proyectos (historial, paths, notas)\n" +
            "• Pins (anclajes)\n" +
            "• Protecciones y whitelists\n" +
            "• Apps de “Abrir con” y sus atajos\n" +
            "• Widgets de la barra y panel de uso\n\n" +
            "La app se reinicia con la configuración por defecto.\n\n¿Continuar?",
            "Restablecer todo",
            MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (r != MessageBoxResult.Yes) return;

        AppPaths.ResetAllData();
        (Application.Current as App)?.RestartApplication();
    }

    // ── Pestaña Protecciones ───────────────────────────────────────────────────
    // La whitelist se indexa por NOMBRE de desk (igual que pins/restricciones). Solo se administra
    // con el desk PROTEGIDO: RestrictionStore.Load() únicamente restaura whitelists de desks
    // restringidos, así que editar una de un desk libre no persistiría al reiniciar. Lo gateamos.

    /// <summary>Representa una app abierta en el picker: muestra "Título (proc.exe)".</summary>
    private sealed record RunningRow(string Proc, string Title)
    {
        public override string ToString() => $"{Title}   ({Proc})";
    }

    private string? SelectedProtDesk => ProtDeskCombo.SelectedItem as string;

    /// <summary>Carga el combo solo con los desks restringibles (ni MAIN ni DESK+).</summary>
    private void RefreshProtDesks()
    {
        ProtDeskCombo.Items.Clear();
        for (int i = 0; i < _desktops.Count; i++)
        {
            string name = _desktops.GetName(i);
            if (RestrictionStore.IsRestrictable(name))
                ProtDeskCombo.Items.Add(name);
        }
    }

    /// <summary>Sincroniza el toggle, habilita/deshabilita la edición y repinta la whitelist.</summary>
    private void RefreshProtState()
    {
        string? desk = SelectedProtDesk;
        bool isDesk = desk is not null;
        bool prot = isDesk && _restrictions.IsRestricted(desk!);

        ProtectedChk.IsChecked = prot;       // setear por código NO dispara Click → sin recursión
        ProtectedChk.IsEnabled = isDesk;

        bool canEdit = prot;                 // solo se edita la whitelist si el desk está protegido
        WhitelistBox.IsEnabled = canEdit;
        RunningBox.IsEnabled = canEdit;
        WlRemoveBtn.IsEnabled = canEdit;
        WlAddFromRunningBtn.IsEnabled = canEdit;
        WlManualBox.IsEnabled = canEdit;
        WlAddManualBtn.IsEnabled = canEdit;
        ProtHint.Visibility = isDesk && !prot ? Visibility.Visible : Visibility.Collapsed;

        RefreshWhitelist();
    }

    private void RefreshWhitelist()
    {
        WhitelistBox.Items.Clear();
        string? desk = SelectedProtDesk;
        if (desk is null || !_restrictions.IsRestricted(desk)) return;
        foreach (var proc in _restrictions.Whitelist(desk).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            WhitelistBox.Items.Add(proc);
    }

    /// <summary>Lista las apps con ventana abierta ahora, salteando las exentas (sistema + la propia app).</summary>
    private void RefreshRunning()
    {
        RunningBox.Items.Clear();
        foreach (var (proc, title) in WindowMethods.RunningTopLevelApps())
        {
            if (_restrictions.IsExempt(proc)) continue;
            RunningBox.Items.Add(new RunningRow(proc, title));
        }
    }

    private void ToggleProtected()
    {
        string? desk = SelectedProtDesk;
        if (desk is null) return;
        _restrictions.SetRestricted(desk, ProtectedChk.IsChecked == true);
        RefreshProtState();
    }

    private void AddFromRunning()
    {
        string? desk = SelectedProtDesk;
        if (desk is null || RunningBox.SelectedItem is not RunningRow row) return;
        _restrictions.AddToWhitelist(desk, row.Proc);
        RefreshWhitelist();
    }

    private void AddManual()
    {
        string? desk = SelectedProtDesk;
        if (desk is null) return;
        string proc = WlManualBox.Text.Trim();
        if (proc == "") return;
        if (!proc.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) proc += ".exe"; // normalizamos al .exe
        _restrictions.AddToWhitelist(desk, proc);
        WlManualBox.Clear();
        RefreshWhitelist();
    }

    private void RemoveFromWhitelist()
    {
        string? desk = SelectedProtDesk;
        if (desk is null || WhitelistBox.SelectedItem is not string proc) return;
        _restrictions.RemoveFromWhitelist(desk, proc);
        RefreshWhitelist();
    }

    // ── Pestaña Aplicaciones ──────────────────────────────────────────────────

    private void RefreshApps()
    {
        AppsList.Items.Clear();
        foreach (var a in _apps.Apps)
        {
            string args = string.IsNullOrWhiteSpace(a.Args) ? "" : $"   ({a.Args})";
            AppsList.Items.Add($"{a.Name}  →  {a.ExePath}{args}");
        }
    }

    private void AddApp()
    {
        string name = AppNameBox.Text.Trim();
        string exe = AppExeBox.Text.Trim();
        if (name == "" || exe == "")
        {
            MessageBox.Show("Completá al menos Nombre y Ruta al ejecutable.", "Aplicaciones",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        _apps.Apps.Add(new Apps.UserApp { Name = name, ExePath = exe, Args = AppArgsBox.Text.Trim() });
        _apps.Save();
        AppNameBox.Clear(); AppExeBox.Clear(); AppArgsBox.Clear();
        RefreshApps();
    }

    private void RemoveApp()
    {
        int i = AppsList.SelectedIndex;
        if (i < 0 || i >= _apps.Apps.Count) return;
        _apps.Apps.RemoveAt(i);
        _apps.Save();
        RefreshApps();
    }

    /// <summary>Repinta la lista con el estado real (existe / falta) de cada escritorio gestionado.</summary>
    private void RefreshList()
    {
        int sel = DeskList.SelectedIndex;
        DeskList.Items.Clear();

        for (int i = 0; i < _config.Managed.Count; i++)
        {
            string name = _config.Managed[i];
            bool exists = _desktops.FindExact(name) >= 0;
            string status = exists ? "✓ existe" : "✗ falta";
            DeskList.Items.Add($"{i + 1}.  {name}      —  {status}");
        }

        if (sel >= 0 && sel < DeskList.Items.Count)
            DeskList.SelectedIndex = sel;
    }

    private void SyncNameBox()
    {
        int i = DeskList.SelectedIndex;
        if (i >= 0 && i < _config.Managed.Count)
            NameBox.Text = _config.Managed[i];
    }

    private void Move(int dir)
    {
        int i = DeskList.SelectedIndex;
        int j = i + dir;
        if (i < 0 || j < 0 || j >= _config.Managed.Count)
            return;
        (_config.Managed[i], _config.Managed[j]) = (_config.Managed[j], _config.Managed[i]);
        RefreshList();
        DeskList.SelectedIndex = j;
    }

    private void RenameSelected()
    {
        int i = DeskList.SelectedIndex;
        string name = NameBox.Text.Trim();
        if (i < 0 || name == "")
            return;
        _config.Managed[i] = name;
        RefreshList();
        DeskList.SelectedIndex = i;
    }

    private void RemoveSelected()
    {
        int i = DeskList.SelectedIndex;
        if (i < 0)
            return;
        _config.Managed.RemoveAt(i);
        RefreshList();
    }

    private void AddNew()
    {
        string name = NameBox.Text.Trim();
        if (name == "")
            return;
        _config.Managed.Add(name);
        NameBox.Clear();
        RefreshList();
        DeskList.SelectedIndex = DeskList.Items.Count - 1;
    }

    private void CreateMissingNow()
    {
        _config.AutoCreate = AutoCreateChk.IsChecked == true;
        int created = DesktopBootstrapper.Ensure(_config, _desktops);
        RefreshList();
        _onApplied();
        MessageBox.Show(
            created > 0 ? $"Listo. Escritorios creados: {created}." : "Listo. No faltaba ninguno (sólo se renombraron los que diferían).",
            "Desktops", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void SaveAndApply()
    {
        _config.AutoCreate = AutoCreateChk.IsChecked == true;
        _config.Save();
        if (_config.AutoCreate)
            DesktopBootstrapper.Ensure(_config, _desktops);
        RefreshList();
        _onApplied();
    }

    // ── Pestaña Tareas ─────────────────────────────────────────────────────────
    // Multi-cuenta: la pestaña edita una LISTA de TaskAccount. La izquierda muestra las cuentas
    // (Add/Quitar), la derecha edita la seleccionada. El binding es directo sobre el modelo:
    // cada keystroke muta _currentAccount → no hay "Aplicar". El botón Guardar sólo persiste a disco.

    /// <summary>Opción del combo de Kind: id estable + etiqueta para mostrar.</summary>
    private sealed record KindChoice(string Id, string Label)
    {
        public override string ToString() => Label;
    }

    /// <summary>Una fila de la ListBox de cuentas. Marca el estado Enabled para que sea obvio.</summary>
    private sealed record AccountRow(TaskAccount Account)
    {
        public override string ToString()
        {
            string nm = string.IsNullOrWhiteSpace(Account.DisplayName) ? "(sin nombre)" : Account.DisplayName;
            return Account.Enabled ? nm : $"{nm}   · apagada";
        }
    }

    // La cuenta actualmente seleccionada en la ListBox; los TextChanged escriben en ESTA referencia.
    private TaskAccount? _currentAccount;
    // Flag para distinguir "el usuario está tipeando" de "estoy refrescando los campos desde el modelo":
    // sin esto, RefreshFields() dispararía TextChanged y se haría lío con _currentAccount.
    private bool _loadingFields;

    /// <summary>Llena el combo de Kind y la lista de cuentas a partir de _tasks.</summary>
    private void InitTasksTab()
    {
        AcctKindCombo.Items.Add(new KindChoice("vikunja", "Vikunja"));
        AcctKindCombo.Items.Add(new KindChoice("jira",    "JIRA (en preparación)"));
        AcctKindCombo.Items.Add(new KindChoice("trello",  "Trello"));

        RefreshAccountsList(preserveSelection: false);
        if (AccountsList.Items.Count > 0)
            AccountsList.SelectedIndex = 0;
        else
            UpdateEditorVisibility(); // sin cuentas → editor oculto
    }

    /// <summary>
    /// Redibuja la lista de cuentas. SUPRIME OnAccountSelectionChanged durante el rebuild — sin esto,
    /// el Clear+Add dispara SelectionChanged dos veces (a null y a la cuenta preservada), cada una
    /// llama a RefreshFields que reasigna AcctNameBox.Text al MISMO valor, y WPF resetea el caret en
    /// medio de la edición del usuario.
    /// </summary>
    private void RefreshAccountsList(bool preserveSelection)
    {
        bool prev = _loadingFields;
        _loadingFields = true;
        try
        {
            TaskAccount? selected = preserveSelection ? _currentAccount : null;
            AccountsList.Items.Clear();
            foreach (var a in _tasks.Accounts)
                AccountsList.Items.Add(new AccountRow(a));
            if (selected != null)
            {
                for (int i = 0; i < AccountsList.Items.Count; i++)
                {
                    if (AccountsList.Items[i] is AccountRow r && ReferenceEquals(r.Account, selected))
                    {
                        AccountsList.SelectedIndex = i;
                        return;
                    }
                }
            }
        }
        finally { _loadingFields = prev; }
    }

    private void OnAccountSelectionChanged()
    {
        // Las SelectionChanged disparadas por rebuilds internos NO son cambios reales — las ignoramos.
        if (_loadingFields) return;
        _currentAccount = (AccountsList.SelectedItem as AccountRow)?.Account;
        RefreshFields();
        UpdateEditorVisibility();
    }

    /// <summary>
    /// Vuelca _currentAccount a los TextBoxes con _loadingFields=true para no auto-dispararse.
    ///
    /// Los TextBox van por SetIfDifferent: asignar .Text = mismo-valor IGUAL dispara TextChanged en
    /// WPF y reposiciona el caret al inicio → la edición del usuario se ve "saltada" en pleno tipeo
    /// cuando este RefreshFields corre desde un RefreshAccountsList encadenado al propio TextChanged.
    /// El IsChecked del CheckBox no necesita guard: WPF NO refire Checked/Unchecked si el valor
    /// asignado es el actual.
    /// </summary>
    private void RefreshFields()
    {
        bool prev = _loadingFields;
        _loadingFields = true;
        try
        {
            if (_currentAccount is null)
            {
                SetIfDifferent(AcctNameBox, "");
                AcctEnabledChk.IsChecked = false;
                SetIfDifferent(VkUrlBox, ""); SetIfDifferent(VkUserBox, ""); SetIfDifferent(VkTokenBox, "");
                SetIfDifferent(JiraUrlBox, ""); SetIfDifferent(JiraEmailBox, ""); SetIfDifferent(JiraTokenBox, "");
                SetIfDifferent(TrelloKeyBox, ""); SetIfDifferent(TrelloTokenBox, ""); SetIfDifferent(TrelloIgnoredListsBox, "");
                return;
            }

            SetIfDifferent(AcctNameBox, _currentAccount.DisplayName);
            AcctEnabledChk.IsChecked = _currentAccount.Enabled;
            SelectKindCombo(_currentAccount.Kind);

            // Aseguramos que el sub-objeto del Kind exista — si la cuenta nació recién, falta.
            EnsureCredentialsBlock(_currentAccount);

            SetIfDifferent(VkUrlBox,   _currentAccount.Vikunja?.BaseUrl  ?? "");
            SetIfDifferent(VkUserBox,  _currentAccount.Vikunja?.Username ?? "");
            SetIfDifferent(VkTokenBox, _currentAccount.Vikunja?.Token    ?? "");

            SetIfDifferent(JiraUrlBox,   _currentAccount.Jira?.BaseUrl ?? "");
            SetIfDifferent(JiraEmailBox, _currentAccount.Jira?.Email   ?? "");
            SetIfDifferent(JiraTokenBox, _currentAccount.Jira?.Token   ?? "");

            SetIfDifferent(TrelloKeyBox,          _currentAccount.Trello?.ApiKey          ?? "");
            SetIfDifferent(TrelloTokenBox,        _currentAccount.Trello?.Token           ?? "");
            SetIfDifferent(TrelloIgnoredListsBox, _currentAccount.Trello?.IgnoredListsRaw ?? "");
        }
        finally { _loadingFields = prev; }
    }

    private static void SetIfDifferent(System.Windows.Controls.TextBox tb, string value)
    {
        if (tb.Text != value) tb.Text = value;
    }

    private void SelectKindCombo(string kind)
    {
        foreach (var item in AcctKindCombo.Items)
            if (item is KindChoice kc && kc.Id == kind) { AcctKindCombo.SelectedItem = item; return; }
        if (AcctKindCombo.Items.Count > 0) AcctKindCombo.SelectedIndex = 0;
    }

    /// <summary>El Kind cambió desde la UI — actualiza la cuenta y muestra el panel que toca.</summary>
    private void OnKindChanged()
    {
        if (_loadingFields || _currentAccount is null) { UpdateEditorVisibility(); return; }
        if (AcctKindCombo.SelectedItem is KindChoice kc && _currentAccount.Kind != kc.Id)
        {
            _currentAccount.Kind = kc.Id;
            EnsureCredentialsBlock(_currentAccount);
            // refrescamos los textboxes del nuevo Kind (los del anterior quedan con su valor — no se borran)
            RefreshFields();
        }
        UpdateEditorVisibility();
    }

    /// <summary>Materializa el sub-objeto de credenciales del Kind activo si está null.</summary>
    private static void EnsureCredentialsBlock(TaskAccount a)
    {
        switch (a.Kind)
        {
            case "vikunja": a.Vikunja ??= new VikunjaSettings(); break;
            case "jira":    a.Jira    ??= new JiraSettings();    break;
            case "trello":  a.Trello  ??= new TrelloSettings();  break;
        }
    }

    /// <summary>Muestra el panel del Kind seleccionado; oculta el editor entero si no hay cuenta.</summary>
    private void UpdateEditorVisibility()
    {
        bool hasAccount = _currentAccount != null;
        AccountEditorPanel.Visibility = hasAccount ? Visibility.Visible : Visibility.Collapsed;
        AccountRemoveBtn.IsEnabled = hasAccount;
        TaskTestBtn.IsEnabled = hasAccount;

        string kind = (AcctKindCombo.SelectedItem as KindChoice)?.Id ?? "";
        VikunjaPanel.Visibility = kind == "vikunja" ? Visibility.Visible : Visibility.Collapsed;
        JiraPanel.Visibility    = kind == "jira"    ? Visibility.Visible : Visibility.Collapsed;
        TrelloPanel.Visibility  = kind == "trello"  ? Visibility.Visible : Visibility.Collapsed;
    }

    private void AddAccount()
    {
        var a = new TaskAccount
        {
            Kind = "vikunja",
            DisplayName = "Cuenta nueva",
            Enabled = true,
            Vikunja = new VikunjaSettings(),
        };
        _tasks.Accounts.Add(a);
        _currentAccount = a;
        RefreshAccountsList(preserveSelection: true); // suprime SelectionChanged → sincronizamos editor a mano
        RefreshFields();
        UpdateEditorVisibility();
        AcctNameBox.Focus();
        AcctNameBox.SelectAll();
    }

    private void RemoveSelectedAccount()
    {
        if (_currentAccount is null) return;
        _tasks.Accounts.Remove(_currentAccount);
        _currentAccount = _tasks.Accounts.Count > 0 ? _tasks.Accounts[0] : null;
        RefreshAccountsList(preserveSelection: true); // suprime SelectionChanged → sincronizamos editor a mano
        RefreshFields();
        UpdateEditorVisibility();
    }

    /// <summary>Persiste tasks.json. NO valida credenciales — eso es Probar cuenta.</summary>
    private void SaveTasksSettings()
    {
        // Pasamos los strings por Trim antes de guardar: los TextChanged ya copiaron los valores tal
        // cual; el trim final evita persistir espacios en blanco que rompen el adapter.
        foreach (var a in _tasks.Accounts)
        {
            a.DisplayName = a.DisplayName.Trim();
            if (a.Vikunja != null)
            {
                a.Vikunja.BaseUrl = a.Vikunja.BaseUrl.Trim();
                a.Vikunja.Username = a.Vikunja.Username.Trim();
                a.Vikunja.Token = a.Vikunja.Token.Trim();
            }
            if (a.Jira != null)
            {
                a.Jira.BaseUrl = a.Jira.BaseUrl.Trim();
                a.Jira.Email = a.Jira.Email.Trim();
                a.Jira.Token = a.Jira.Token.Trim();
            }
            if (a.Trello != null)
            {
                a.Trello.ApiKey = a.Trello.ApiKey.Trim();
                a.Trello.Token = a.Trello.Token.Trim();
            }
        }
        _tasks.Save();

        TaskTestStatus.Foreground = (System.Windows.Media.Brush)FindResource("FgMuted");
        TaskTestStatus.Text = $"Guardado en tasks.json. {_tasks.Accounts.Count} cuenta(s).";
        RefreshAccountsList(preserveSelection: true); // los DisplayName trimeados se ven en la lista
    }

    /// <summary>Prueba SOLO la cuenta seleccionada con los valores actuales del editor.</summary>
    private async Task TestSelectedAccount()
    {
        if (_currentAccount is null) return;

        var provider = TasksService.CreateProvider(_currentAccount);
        if (provider is null)
        {
            TaskTestStatus.Foreground = (System.Windows.Media.Brush)FindResource("FgMuted");
            TaskTestStatus.Text = "Esta cuenta no tiene credenciales del tipo elegido.";
            return;
        }

        TaskTestBtn.IsEnabled = false;
        TaskTestStatus.Foreground = (System.Windows.Media.Brush)FindResource("FgMuted");
        TaskTestStatus.Text = $"Probando '{_currentAccount.DisplayName}'…";
        try
        {
            var result = await provider.GetOpenTasksAsync();
            if (result.Ok)
            {
                TaskTestStatus.Foreground = (System.Windows.Media.Brush)FindResource("Accent");
                TaskTestStatus.Text = $"✓ Conectado a '{_currentAccount.DisplayName}'. Tareas abiertas: {result.Items.Count}.";
            }
            else
            {
                TaskTestStatus.Foreground = (System.Windows.Media.Brush)FindResource("Danger");
                TaskTestStatus.Text = "✗ " + result.Error;
            }
        }
        finally
        {
            TaskTestBtn.IsEnabled = true;
        }
    }
}
