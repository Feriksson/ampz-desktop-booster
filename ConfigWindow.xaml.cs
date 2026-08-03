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
using AmpzDesktopBooster.Services.Localization;
using AmpzDesktopBooster.Services.Browser;
using AmpzDesktopBooster.Services.Tasks;

namespace AmpzDesktopBooster;

/// <summary>
/// Ventana de configuración con pestañas — el hogar de todo lo ajustable de la app.
/// Por ahora trae la pestaña DESKTOPS (gestionar el set de escritorios virtuales);
/// las próximas fases sumarán pestañas (Widgets, Espacios, Pins, etc.) al mismo TabControl.
/// </summary>
public partial class ConfigWindow : Window
{
    private readonly DesktopConfig _config;
    private readonly Apps.AppsConfig _apps;
    private readonly DesktopService _desktops;
    private readonly ProjectStore _projects;
    private readonly RestrictionStore _restrictions;
    private readonly PinStore _pins;
    private readonly Action _onApplied;

    // Config del proveedor de tareas. La pestaña Tareas la maneja por su cuenta (Load/Save) — NO
    // pasa por el constructor ni por App.OnStartup, para no tocar el arranque core (hooks sensibles).
    private readonly TasksSettings _tasks;

    public ConfigWindow(DesktopConfig config, Apps.AppsConfig apps, DesktopService desktops,
        ProjectStore projects, RestrictionStore restrictions, PinStore pins, Action onApplied)
    {
        InitializeComponent();

        _config = config;
        _apps = apps;
        _desktops = desktops;
        _projects = projects;
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

        // ── Pestaña Espacios y Contextos ──
        InitScopesTab();

        // ── Pestaña General ──
        DataPathText.Text = AppPaths.DataDir;
        ResetAllBtn.Click += (_, _) => ResetAll();
        SetupLanguageSelector();

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

        // ── Pestaña Navegador ──
        InitBrowserTab();
    }

    // ── Pestaña Navegador ───────────────────────────────────────────────────────
    // Shim de navegador: registrar la app como navegador candidato del SO + reenviar links al
    // navegador real con --new-window (abren en el desk actual, sin catapulteo). Config propia
    // (browser.json), igual que Atención y Tareas: NO toca el arranque core. El registro en HKCU lo
    // hace BrowserShim; acá solo cableamos los botones y reflejamos el estado real.

    private void InitBrowserTab()
    {
        var s = BrowserSettings.Load();
        BrowserEnabledChk.IsChecked = s.Enabled;
        BrowserPathBox.Text = s.RealBrowserPath;

        BrowserBrowseBtn.Click += (_, _) => BrowseRealBrowser();
        BrowserOpenSettingsBtn.Click += (_, _) => BrowserShim.OpenWindowsDefaultApps();
        BrowserSaveBtn.Click += (_, _) => SaveBrowser();

        RefreshBrowserStatus();
    }

    /// <summary>Pinta el estado REAL: registrado como candidato, y si es el default elegido hoy.</summary>
    private void RefreshBrowserStatus()
    {
        bool registered = BrowserShim.IsRegistered();
        bool isDefault = BrowserShim.IsDefault();
        string browser = BrowserSettings.Load().EffectiveBrowserPath();
        string browserName = string.IsNullOrEmpty(browser) ? Loc.T("Config.BrowserNoneDetected") : System.IO.Path.GetFileName(browser);

        if (isDefault)
        {
            BrowserStatusText.Foreground = (System.Windows.Media.Brush)FindResource("Accent");
            BrowserStatusText.Text = Loc.T("Config.BrowserIsDefault");
        }
        else if (registered)
        {
            BrowserStatusText.Foreground = (System.Windows.Media.Brush)FindResource("Fg");
            BrowserStatusText.Text = Loc.T("Config.BrowserRegisteredNotDefault");
        }
        else
        {
            BrowserStatusText.Foreground = (System.Windows.Media.Brush)FindResource("FgMuted");
            BrowserStatusText.Text = Loc.T("Config.BrowserDisabled");
        }
        BrowserStatusHint.Text = $"{Loc.T("Config.BrowserRealPrefix")}{browserName}.";
    }

    private void BrowseRealBrowser()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = Loc.T("Config.BrowseBrowserTitle"),
            Filter = Loc.T("Config.BrowseBrowserFilter"),
            CheckFileExists = true,
        };
        if (dlg.ShowDialog() == true)
            BrowserPathBox.Text = dlg.FileName;
    }

    /// <summary>
    /// Persiste browser.json y aplica el registro: si está activado → Register() (aparece en Apps
    /// predeterminadas); si no → Unregister() (Windows vuelve a pedir navegador). Refresca el estado.
    /// </summary>
    private void SaveBrowser()
    {
        var s = new BrowserSettings
        {
            Enabled = BrowserEnabledChk.IsChecked == true,
            RealBrowserPath = BrowserPathBox.Text.Trim(),
        };
        s.Save();

        if (s.Enabled) BrowserShim.Register();
        else BrowserShim.Unregister();

        RefreshBrowserStatus();
        Toasts.Saved(Loc.T("Config.BrowserTab"));

        if (s.Enabled && !BrowserShim.IsDefault())
            MessageBox.Show(
                Loc.T("Config.BrowserSavePrompt"),
                Loc.T("Config.BrowserTab"), MessageBoxButton.OK, MessageBoxImage.Information);
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
            Title = Loc.T("Config.BrowseWavTitle"),
            Filter = Loc.T("Config.BrowseWavFilter"),
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
        Toasts.Saved(Loc.T("Config.AttentionTab"));
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
            MessageBox.Show(string.Format(Loc.T("Config.PinBlockedMsg"), proc), Loc.T("Config.Pin"),
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
        if (MessageBox.Show(Loc.T("Config.UnpinAllConfirm"), Loc.T("Config.PinsTab"),
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
    /// <summary>
    /// Llena el combo de idioma y cablea el cambio. El modelo es por REINICIO: al elegir un idioma
    /// distinto, persistimos la preferencia y avisamos por toast que hay que reiniciar para verlo
    /// aplicado en toda la UI. Enganchamos el handler DESPUÉS de fijar la selección inicial para no
    /// dispararlo en el armado (y, por las dudas, el guard <c>lang != Loc.Current</c> lo blinda).
    /// </summary>
    private void SetupLanguageSelector()
    {
        LanguageCombo.Items.Clear();
        LanguageCombo.Items.Add(new ComboBoxItem { Content = Loc.T("Language.Spanish"), Tag = AppLanguage.Spanish });
        LanguageCombo.Items.Add(new ComboBoxItem { Content = Loc.T("Language.English"), Tag = AppLanguage.English });
        LanguageCombo.SelectedIndex = Loc.Current == AppLanguage.English ? 1 : 0;

        LanguageCombo.SelectionChanged += (_, _) =>
        {
            if (LanguageCombo.SelectedItem is ComboBoxItem { Tag: AppLanguage lang } && lang != Loc.Current)
            {
                Loc.SetAndPersist(lang);
                Toasts.Info(Loc.T("General.Language"), Loc.T("General.LanguageHint"));
            }
        };
    }

    private void ResetAll()
    {
        var r = MessageBox.Show(
            Loc.T("Config.ResetAllConfirm"),
            Loc.T("Config.ResetAllTitle"),
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
            MessageBox.Show(Loc.T("Config.AppRequiredFields"), Loc.T("Config.AppsTab"),
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
            string status = exists ? Loc.T("Config.DeskExists") : Loc.T("Config.DeskMissing");
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
            created > 0 ? string.Format(Loc.T("Config.DesktopsCreated"), created) : Loc.T("Config.DesktopsNoneMissing"),
            Loc.T("Config.DesktopsTab"), MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void SaveAndApply()
    {
        _config.AutoCreate = AutoCreateChk.IsChecked == true;
        _config.Save();
        if (_config.AutoCreate)
            DesktopBootstrapper.Ensure(_config, _desktops);
        RefreshList();
        _onApplied();
        Toasts.Saved(Loc.T("Config.DesktopsTab"));
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
            string nm = string.IsNullOrWhiteSpace(Account.DisplayName) ? Loc.T("Config.AccountNoName") : Account.DisplayName;
            return Account.Enabled ? nm : $"{nm}   · {Loc.T("Config.AccountDisabled")}";
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

    // ── Pestaña Espacios y Contextos ───────────────────────────────────────────────────────────
    //
    // La única superficie donde se puede REORGANIZAR el catálogo. El setter y el picker sólo saben
    // crear; borrar existía sin dónde verlo. Mover un contexto de espacio, promoverlo o degradar un
    // espacio no se podía hacer de ninguna forma que no fuera editar el JSON a mano.

    /// <summary>Una fila de la lista: un ESPACIO (<c>Context</c> null) o un CONTEXTO suyo.</summary>
    private sealed record ScopeRow(string Space, string? Context)
    {
        public bool IsContext => Context is not null;
        public string Name => Context ?? Space;
    }

    /// <summary>La fila seleccionada, o null (los separadores "(sin contextos)" no son seleccionables).</summary>
    private ScopeRow? SelectedScope => (ScopeList.SelectedItem as ListBoxItem)?.Tag as ScopeRow;

    private void InitScopesTab()
    {
        ScopeList.SelectionChanged += (_, _) => UpdateScopeButtons();
        ScopeRenameBtn.Click  += (_, _) => RenameScope();
        ScopeColorBtn.Click   += (_, _) => CycleScopeColor();
        ScopePromoteBtn.Click += (_, _) => PromoteScope();
        ScopeMoveBtn.Click    += (_, _) => MoveScopeToTarget();
        ScopeDemoteBtn.Click  += (_, _) => DemoteScopeToTarget();
        ScopeDeleteBtn.Click  += (_, _) => DeleteScope();
        RefreshScopes();
    }

    /// <summary>
    /// Repinta la lista entera y REPONE la selección en el scope indicado. Reponerla no es cosmético:
    /// después de mover o renombrar, perder la selección te obliga a buscar de nuevo la fila que
    /// acabás de tocar — justo cuando querés encadenar otra operación sobre ella.
    /// </summary>
    private void RefreshScopes(string? selectSpace = null, string? selectContext = null)
    {
        ScopeList.Items.Clear();

        var session = _projects.SessionEntries().ToList();
        var spaces = _projects.GetHistory()
            .OrderBy(s => s, StringComparer.CurrentCultureIgnoreCase).ToList();

        foreach (var space in spaces)
        {
            bool spaceInUse = session.Any(e => string.Equals(e.Project, space, StringComparison.OrdinalIgnoreCase));
            ScopeList.Items.Add(BuildScopeRow(new ScopeRow(space, null), "", spaceInUse));

            var mods = _projects.GetModules(space);
            if (mods.Count == 0)
            {
                ScopeList.Items.Add(BuildNoContextsRow());
                continue;
            }

            foreach (var m in mods.OrderBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                bool inUse = session.Any(e =>
                    string.Equals(e.Project, space, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(e.Module, m.Name, StringComparison.OrdinalIgnoreCase));
                ScopeList.Items.Add(BuildScopeRow(new ScopeRow(space, m.Name), m.Color, inUse));
            }
        }

        ScopeEmptyHint.Visibility = spaces.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        RefreshScopeTargets();

        if (selectSpace is not null)
        {
            foreach (var item in ScopeList.Items.OfType<ListBoxItem>())
            {
                if (item.Tag is ScopeRow r
                    && string.Equals(r.Space, selectSpace, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(r.Context ?? "", selectContext ?? "", StringComparison.OrdinalIgnoreCase))
                {
                    ScopeList.SelectedItem = item;
                    break;
                }
            }
        }

        UpdateScopeButtons();
    }

    /// <summary>Fila de espacio o de contexto. El contexto va indentado y con su chip de color.</summary>
    private ListBoxItem BuildScopeRow(ScopeRow row, string color, bool inUse)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };

        if (row.IsContext)
            panel.Children.Add(new Border
            {
                Width = 10,
                Height = 10,
                CornerRadius = new CornerRadius(3),
                Margin = new Thickness(22, 0, 10, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Background = new System.Windows.Media.SolidColorBrush(ModulePalette.Parse(color)),
            });

        panel.Children.Add(new TextBlock
        {
            Text = row.Name,
            VerticalAlignment = VerticalAlignment.Center,
            FontWeight = row.IsContext ? FontWeights.Normal : FontWeights.SemiBold,
        });

        // "en uso" = hay un desk con este scope cargado AHORA. Avisa antes de borrar algo que estás usando.
        if (inUse)
            panel.Children.Add(new TextBlock
            {
                Text = "· " + Loc.T("Config.ScopesInUse"),
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 10,
                Foreground = (System.Windows.Media.Brush)FindResource("Accent"),
            });

        return new ListBoxItem { Content = panel, Tag = row };
    }

    /// <summary>
    /// Marca de "este espacio no tiene contextos". NO es seleccionable: no representa ninguna entidad,
    /// y dejar que se seleccione haría que los botones actuaran sobre algo que no existe.
    /// </summary>
    private ListBoxItem BuildNoContextsRow() => new()
    {
        Content = new TextBlock
        {
            Text = Loc.T("Config.ScopesNoContexts"),
            Margin = new Thickness(42, 0, 0, 0),
            FontSize = 11,
            FontStyle = FontStyles.Italic,
            Foreground = (System.Windows.Media.Brush)FindResource("FgMuted"),
        },
        IsHitTestVisible = false,
        Focusable = false,
    };

    /// <summary>Llena el combo de destino con los espacios, conservando el elegido si sigue existiendo.</summary>
    private void RefreshScopeTargets()
    {
        string? prev = ScopeTargetCombo.SelectedItem as string;
        ScopeTargetCombo.Items.Clear();
        foreach (var s in _projects.GetHistory().OrderBy(s => s, StringComparer.CurrentCultureIgnoreCase))
            ScopeTargetCombo.Items.Add(s);

        if (prev is not null && ScopeTargetCombo.Items.Contains(prev)) ScopeTargetCombo.SelectedItem = prev;
        else if (ScopeTargetCombo.Items.Count > 0) ScopeTargetCombo.SelectedIndex = 0;
    }

    /// <summary>
    /// Habilita sólo lo que APLICA al tipo de fila seleccionada. Un botón que significara una cosa
    /// sobre un espacio y otra sobre un contexto sería ambiguo por diseño: promover y degradar son
    /// operaciones OPUESTAS y viven en niveles distintos.
    /// </summary>
    private void UpdateScopeButtons()
    {
        var row = SelectedScope;
        bool isContext = row?.IsContext == true;
        bool isSpace = row is not null && !row.IsContext;

        ScopeRenameBtn.IsEnabled  = row is not null;
        ScopeDeleteBtn.IsEnabled  = row is not null;
        ScopeColorBtn.IsEnabled   = isContext;
        ScopePromoteBtn.IsEnabled = isContext;
        ScopeMoveBtn.IsEnabled    = isContext;
        ScopeDemoteBtn.IsEnabled  = isSpace;
    }

    /// <summary>
    /// Traduce el motivo del store a un mensaje que ORIENTA. Una operación que no puede avanzar
    /// NUNCA se queda muda: un botón que no hace nada se lee igual que un botón roto.
    /// </summary>
    private bool ReportScopeResult(ScopeOpResult result, string subject)
    {
        if (result == ScopeOpResult.Ok) return true;

        string msg = result switch
        {
            ScopeOpResult.NameTaken  => string.Format(Loc.T("Config.ScopeErrNameTaken"), subject),
            ScopeOpResult.EmptyName  => Loc.T("Config.ScopeErrEmptyName"),
            ScopeOpResult.WouldNest  => string.Format(Loc.T("Config.ScopeErrWouldNest"), subject),
            ScopeOpResult.SameTarget => Loc.T("Config.ScopeErrSameTarget"),
            _                        => Loc.T("Config.ScopeErrNotFound"),
        };
        MessageBox.Show(this, msg, Loc.T("Config.ScopeErrTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
        return false;
    }

    private void RenameScope()
    {
        var row = SelectedScope;
        if (row is null) return;

        string? name = PromptDialog.Show(this,
            row.IsContext ? Loc.T("Config.ScopesRenameContextTitle") : Loc.T("Config.ScopesRenameSpaceTitle"),
            Loc.T("Config.ScopesNameLabel"), row.Name);
        if (name is null || name.Trim() == row.Name) return;

        var res = row.IsContext
            ? _projects.RenameModule(row.Space, row.Context!, name)
            : _projects.RenameProject(row.Space, name);
        if (!ReportScopeResult(res, name)) return;

        string final = ProjectStore.TitleCase(ProjectStore.Sanitize(name));
        RefreshScopes(row.IsContext ? row.Space : final, row.IsContext ? final : null);
        _onApplied();
    }

    /// <summary>Cicla la paleta — mismo gesto que el F3 del picker de contextos, misma fuente de verdad.</summary>
    private void CycleScopeColor()
    {
        var row = SelectedScope;
        if (row?.Context is null) return;

        _projects.SetModuleColor(row.Space, row.Context,
            ModulePalette.Next(_projects.GetModuleColor(row.Space, row.Context)));
        RefreshScopes(row.Space, row.Context);
        _onApplied();
    }

    private void MoveScopeToTarget()
    {
        var row = SelectedScope;
        if (row?.Context is null || ScopeTargetCombo.SelectedItem is not string target) return;

        if (MessageBox.Show(this,
                string.Format(Loc.T("Config.ScopesMoveConfirm"), row.Context, target, row.Space),
                Loc.T("Config.ScopesMoveTitle"),
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        if (!ReportScopeResult(_projects.MoveModule(row.Space, row.Context, target), row.Context)) return;
        RefreshScopes(target, row.Context);
        _onApplied();
    }

    private void PromoteScope()
    {
        var row = SelectedScope;
        if (row?.Context is null) return;

        if (MessageBox.Show(this,
                string.Format(Loc.T("Config.ScopesPromoteConfirm"), row.Context, row.Space),
                Loc.T("Config.ScopesPromoteTitle"),
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        if (!ReportScopeResult(_projects.PromoteModule(row.Space, row.Context), row.Context)) return;
        RefreshScopes(row.Context);
        _onApplied();
    }

    private void DemoteScopeToTarget()
    {
        var row = SelectedScope;
        if (row is null || row.IsContext || ScopeTargetCombo.SelectedItem is not string target) return;

        if (MessageBox.Show(this,
                string.Format(Loc.T("Config.ScopesDemoteConfirm"), row.Space, target),
                Loc.T("Config.ScopesDemoteTitle"),
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        if (!ReportScopeResult(_projects.DemoteProject(row.Space, target), row.Space)) return;
        RefreshScopes(target, row.Space);
        _onApplied();
    }

    private void DeleteScope()
    {
        var row = SelectedScope;
        if (row is null) return;

        string msg = row.IsContext
            ? string.Format(Loc.T("Config.ScopesDeleteContextConfirm"), row.Context)
            : string.Format(Loc.T("Config.ScopesDeleteSpaceConfirm"), row.Space);

        if (MessageBox.Show(this, msg, Loc.T("Config.ScopesDeleteTitle"),
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

        if (row.IsContext) _projects.DeleteModule(row.Space, row.Context!);
        else _projects.DeleteFromHistory(row.Space);

        RefreshScopes(row.IsContext ? row.Space : null);
        _onApplied();
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
            DisplayName = Loc.T("Config.NewAccount"),
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
        TaskTestStatus.Text = string.Format(Loc.T("Config.TasksSaved"), _tasks.Accounts.Count);
        RefreshAccountsList(preserveSelection: true); // los DisplayName trimeados se ven en la lista
        Toasts.Saved(Loc.T("Config.TasksTab"));
    }

    /// <summary>Prueba SOLO la cuenta seleccionada con los valores actuales del editor.</summary>
    private async Task TestSelectedAccount()
    {
        if (_currentAccount is null) return;

        var provider = TasksService.CreateProvider(_currentAccount);
        if (provider is null)
        {
            TaskTestStatus.Foreground = (System.Windows.Media.Brush)FindResource("FgMuted");
            TaskTestStatus.Text = Loc.T("Config.TaskNoCredentials");
            return;
        }

        TaskTestBtn.IsEnabled = false;
        TaskTestStatus.Foreground = (System.Windows.Media.Brush)FindResource("FgMuted");
        TaskTestStatus.Text = string.Format(Loc.T("Config.TaskTesting"), _currentAccount.DisplayName);
        try
        {
            var result = await provider.GetOpenTasksAsync();
            if (result.Ok)
            {
                TaskTestStatus.Foreground = (System.Windows.Media.Brush)FindResource("Accent");
                TaskTestStatus.Text = string.Format(Loc.T("Config.TaskTestOk"), _currentAccount.DisplayName, result.Items.Count);
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
