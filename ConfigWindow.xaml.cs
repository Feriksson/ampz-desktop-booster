using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AmpzDesktopBooster.Apps;
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

        // ── Pestaña Variables ── (después de Espacios: se apoya en su misma lectura del catálogo)
        InitVarsTab();
        InitCmdsTab();

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
        ScopeDuplicateBtn.Click   += (_, _) => DuplicateSpace();
        ScopeDuplicateToBtn.Click += (_, _) => DuplicateContextToTarget();
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

        // Las pestañas Variables y Comandos leen el MISMO catálogo: renombrar, mover o borrar un
        // scope acá cambia su panel izquierdo. Sin esto, quedarían mostrando espacios que ya no
        // existen hasta reabrir la ventana — y peor, con un scope fantasma seleccionado listo para
        // recibir un drop.
        RefreshVarScopes();
        RefreshCmdScopes();
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

        // Cada duplicar aplica a UN nivel, por el mismo motivo que el resto: el de espacio crea un
        // espacio nuevo (sin destino), el de contexto aterriza EN un espacio (con destino). Un solo
        // botón que hiciera las dos cosas dejaría al combo de destino significando algo distinto
        // según qué fila tocaste.
        ScopeDuplicateBtn.IsEnabled   = isSpace;
        ScopeDuplicateToBtn.IsEnabled = isContext;
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
            ScopeOpResult.NameTaken     => string.Format(Loc.T("Config.ScopeErrNameTaken"), subject),
            ScopeOpResult.EmptyName     => Loc.T("Config.ScopeErrEmptyName"),
            ScopeOpResult.WouldNest     => string.Format(Loc.T("Config.ScopeErrWouldNest"), subject),
            ScopeOpResult.SameTarget    => Loc.T("Config.ScopeErrSameTarget"),
            ScopeOpResult.DuplicatePath => string.Format(Loc.T("Config.ScopeErrDuplicatePath"), subject),
            _                           => Loc.T("Config.ScopeErrNotFound"),
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

    /// <summary>
    /// Duplica el espacio seleccionado: sus variables, comandos, predeterminados y TODOS sus
    /// contextos con lo suyo. Las notas quedan afuera (ver el bloque de duplicación en ProjectStore).
    /// </summary>
    private void DuplicateSpace()
    {
        var row = SelectedScope;
        if (row is null || row.IsContext) return;

        // El nombre se pide SIEMPRE, nunca se autogenera y listo: el duplicado existe para
        // convertirse en otra cosa, así que nombrarlo es el primer paso del trabajo, no un trámite.
        // Se pre-llena con "(copia)" para que aceptar sin pensar tampoco choque con el original.
        string? name = PromptDialog.Show(this, Loc.T("Config.ScopesDupSpaceTitle"),
            Loc.T("Config.ScopesNameLabel"), string.Format(Loc.T("Config.ScopesDupSuffix"), row.Space));
        if (name is null) return;

        if (!ReportScopeResult(_projects.DuplicateProject(row.Space, name, out var ports), name)) return;

        string final = ProjectStore.TitleCase(ProjectStore.Sanitize(name));
        ReportReassignedPorts(ports);
        RefreshScopes(final);
        _onApplied();
    }

    /// <summary>
    /// Duplica el contexto seleccionado dentro del espacio elegido en el combo. Eligiendo su PROPIO
    /// espacio, la copia queda al lado del original; eligiendo otro, te llevás el contexto armado a
    /// un cliente nuevo sin tocar el de acá.
    /// </summary>
    private void DuplicateContextToTarget()
    {
        var row = SelectedScope;
        if (row?.Context is null || ScopeTargetCombo.SelectedItem is not string target) return;

        // Duplicando DENTRO del mismo espacio el nombre no puede repetirse → se propone "(copia)".
        // Duplicando a OTRO espacio el nombre original está libre y es el que querés (llevarte
        // "Plataforma" tal cual), así que se propone ése.
        bool sameSpace = string.Equals(target, row.Space, StringComparison.OrdinalIgnoreCase);
        string seed = sameSpace ? string.Format(Loc.T("Config.ScopesDupSuffix"), row.Context) : row.Context;

        string? name = PromptDialog.Show(this,
            string.Format(Loc.T("Config.ScopesDupContextTitle"), target),
            Loc.T("Config.ScopesNameLabel"), seed);
        if (name is null) return;

        if (!ReportScopeResult(
                _projects.DuplicateModule(row.Space, row.Context, target, name, out var ports), name))
            return;

        string final = ProjectStore.TitleCase(ProjectStore.Sanitize(name));

        // Duplicar a OTRO espacio cambia de quién HEREDA la copia, y eso no se ve en la lista: el
        // contexto nuevo deja de ver las variables del espacio original y pasa a ver las del destino.
        // Es lo correcto, pero sorprende — mismo aviso que ya lleva Mover, y por lo mismo.
        if (!sameSpace)
            MessageBox.Show(this,
                string.Format(Loc.T("Config.ScopesDupInherit"), final, target, row.Space),
                Loc.T("Config.ScopesDupTitle"), MessageBoxButton.OK, MessageBoxImage.Information);

        ReportReassignedPorts(ports);
        RefreshScopes(target, final);
        _onApplied();
    }

    /// <summary>
    /// Avisa qué puertos salieron cambiados. NUNCA se calla: un puerto que se mueve solo y en
    /// silencio te deja lanzando el comando y mirando el puerto de siempre, que ahora sirve otra
    /// cosa. Es el mismo aviso que ya da la copia de un comando, por el mismo motivo.
    /// </summary>
    private void ReportReassignedPorts(List<int> ports)
    {
        if (ports.Count == 0) return;
        MessageBox.Show(this,
            string.Format(Loc.T("Config.ScopesDupPorts"), string.Join(", ", ports)),
            Loc.T("Config.ScopesDupTitle"), MessageBoxButton.OK, MessageBoxImage.Information);
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

    // ── Pestaña Variables ──────────────────────────────────────────────────────────────────────
    //
    // Hermana de la de Espacios y con el mismo motivo de existir: aquella arregla la JERARQUÍA, ésta
    // arregla el CONTENIDO. Hasta acá una variable cargada en el scope equivocado —el repo del cliente
    // metido en un contexto en vez de en su espacio, o al revés— sólo se podía "mover" borrándola y
    // re-tipeándola del otro lado, con el predeterminado perdido en el camino.
    //
    // Por qué DOS paneles y no una lista sola: mover es una operación con ORIGEN y DESTINO, y los dos
    // tienen que estar a la vista o el gesto es a ciegas. El panel izquierdo es además el blanco de
    // los drops — el mapa de scopes ES la superficie de destino, no un combo escondido.

    /// <summary>Un scope en el panel izquierdo: la GLOBAL, un espacio o un contexto suyo.</summary>
    /// <remarks><see cref="Key"/> es la key REAL del catálogo ("" global, "Espacio", "Espacio/Contexto").</remarks>
    private sealed record VarScope(string Key, string Label, string Color, bool IsContext, bool IsGlobal)
    {
        /// <summary>Lo que muestra el ComboBox de destino (que renderiza por ToString).</summary>
        public override string ToString() => Label;
    }

    /// <summary>Una variable del scope elegido. <see cref="PoolIndex"/> es su índice REAL en la pool.</summary>
    private sealed record VarRow(int PoolIndex, string Title, string Path, bool IsDefault, bool IsBroken);

    /// <summary>Formato privado del portapapeles de drag. No se comparte con nadie: es intra-ventana.</summary>
    private const string VarDragFormat = "AmpzDesktopBooster.VariableDrag";

    /// <summary>Scope cuyo contenido muestra el panel derecho. "" = la pool GLOBAL compartida.</summary>
    private string _varScope = ProjectStore.GlobalScope;

    private System.Windows.Point _varDragOrigin;

    /// <summary>
    /// Selección capturada en el mouse-down, ANTES de que el ListBox la colapse. Sin esto, apretar
    /// sobre una fila que ya era parte de una multi-selección la deja SOLA (WPF selecciona en el
    /// down), y arrastrar cinco variables juntas se vuelve imposible.
    /// </summary>
    private List<int>? _varDragSnapshot;

    /// <summary>Fila del panel izquierdo resaltada como destino del drop en curso (o null).</summary>
    private ListBoxItem? _varDropTarget;

    /// <summary>Lo que dejó cargado el último Ctrl+C / Ctrl+X de esta pestaña (null = vacío).</summary>
    private ScopeClipboard? _varClip;

    private void InitVarsTab()
    {
        VarScopeList.SelectionChanged += (_, _) => OnVarScopeChanged();
        VarList.SelectionChanged += (_, _) => UpdateVarButtons();
        VarList.MouseDoubleClick += (_, _) => EditVariable();
        VarList.PreviewKeyDown += OnVarListKeyDown;
        // Ctrl+V también con el foco en el MAPA de scopes: seleccionar el destino ahí y tener que
        // volver a la lista de la derecha para poder pegar sería pedirle al usuario que deshaga el
        // gesto que acaba de hacer. Copiar/cortar no se enganchan acá — en este panel no hay
        // variables seleccionadas, hay scopes.
        VarScopeList.PreviewKeyDown += OnVarScopeListKeyDown;
        VarFilterBox.TextChanged += (_, _) => RefreshVars();

        VarNewBtn.Click     += (_, _) => NewVariable();
        VarEditBtn.Click    += (_, _) => EditVariable();
        VarDeleteBtn.Click  += (_, _) => DeleteVariables();
        VarDefaultBtn.Click += (_, _) => ToggleVarDefault();
        VarMoveBtn.Click    += (_, _) => MoveVarsToComboTarget(copy: false);
        VarCopyBtn.Click    += (_, _) => MoveVarsToComboTarget(copy: true);

        // Drag & drop: se arrastra DESDE la lista de variables y se suelta SOBRE una fila de scope.
        VarList.PreviewMouseLeftButtonDown += OnVarDragPress;
        VarList.MouseMove += OnVarDragMove;
        VarScopeList.DragOver += OnVarScopeDragOver;
        VarScopeList.Drop += OnVarScopeDrop;
        VarScopeList.DragLeave += (_, _) => HighlightDropTarget(null);

        RefreshVarScopes();
    }

    /// <summary>
    /// Todos los scopes en orden de lectura: la global primero (es la raíz de la herencia), después
    /// cada espacio con sus contextos debajo. Mismo criterio de orden que la pestaña de Espacios.
    /// </summary>
    private List<VarScope> AllVarScopes()
    {
        var list = new List<VarScope>
        {
            new(ProjectStore.GlobalScope, Loc.T("Config.VarsGlobal"), "", false, true),
        };

        foreach (var space in _projects.GetHistory().OrderBy(s => s, StringComparer.CurrentCultureIgnoreCase))
        {
            list.Add(new VarScope(space, space, "", false, false));
            foreach (var m in _projects.GetModules(space).OrderBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase))
                list.Add(new VarScope(ProjectStore.ScopeKey(space, m.Name), m.Name, m.Color, true, false));
        }
        return list;
    }

    /// <summary>
    /// Repinta el panel de scopes (con el conteo de variables PROPIAS de cada uno) y repone la
    /// selección. El conteo no es decorativo: es la confirmación de que el drop aterrizó — ves el
    /// número del destino subir sin tener que ir a mirar.
    /// </summary>
    private void RefreshVarScopes(string? select = null)
    {
        // Se llama también desde la pestaña de Espacios, que corre ANTES de que esta pestaña se
        // inicialice (InitScopesTab → RefreshScopes). En esa primera pasada todavía no hay handlers
        // enganchados, por eso el refresco del panel derecho se dispara explícito al final y no
        // confiando en el SelectionChanged.
        string wanted = select ?? _varScope;
        VarScopeList.Items.Clear();

        var scopes = AllVarScopes();
        foreach (var s in scopes)
            VarScopeList.Items.Add(BuildVarScopeItem(s, _projects.PeekVariables(s.Key).Count));

        // Si el scope que estaba elegido ya no existe (lo borraron desde la otra pestaña), caemos a
        // la global — que siempre existe — en vez de quedar apuntando a un scope fantasma.
        if (!scopes.Any(s => string.Equals(s.Key, wanted, StringComparison.OrdinalIgnoreCase)))
            wanted = ProjectStore.GlobalScope;

        VarScopeList.SelectedItem = VarScopeList.Items.OfType<ListBoxItem>().FirstOrDefault(i =>
            i.Tag is VarScope s && string.Equals(s.Key, wanted, StringComparison.OrdinalIgnoreCase));

        OnVarScopeChanged();
    }

    /// <summary>Fila de scope. El contexto va indentado y con su chip de color, igual que en Espacios.</summary>
    private ListBoxItem BuildVarScopeItem(VarScope scope, int count)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };

        if (scope.IsContext)
            panel.Children.Add(new Border
            {
                Width = 10,
                Height = 10,
                CornerRadius = new CornerRadius(3),
                Margin = new Thickness(18, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Background = new System.Windows.Media.SolidColorBrush(ModulePalette.Parse(scope.Color)),
            });

        panel.Children.Add(new TextBlock
        {
            Text = scope.Label,
            VerticalAlignment = VerticalAlignment.Center,
            FontWeight = scope.IsContext ? FontWeights.Normal : FontWeights.SemiBold,
            FontStyle = scope.IsGlobal ? FontStyles.Italic : FontStyles.Normal,
        });

        panel.Children.Add(new TextBlock
        {
            Text = count.ToString(),
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 10,
            Foreground = (System.Windows.Media.Brush)FindResource("FgMuted"),
        });

        // El contenido va envuelto en un Border PROPIO porque el template del ListBoxItem pinta su
        // fondo en duro (Transparent) e ignora el Background del item: sin esta capa no hay dónde
        // pintar el resaltado del drop, que es el único feedback de "acá cae".
        return new ListBoxItem
        {
            Content = new Border
            {
                Background = System.Windows.Media.Brushes.Transparent,
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(2),
                Child = panel,
            },
            Tag = scope,
        };
    }

    private VarScope? SelectedVarScope => (VarScopeList.SelectedItem as ListBoxItem)?.Tag as VarScope;

    /// <summary>Cambió el scope elegido: se recarga el contenido y se recalcula el combo de destino.</summary>
    private void OnVarScopeChanged()
    {
        _varScope = SelectedVarScope?.Key ?? ProjectStore.GlobalScope;
        RefreshVarTargets();
        RefreshVars();
    }

    /// <summary>
    /// Contenido del scope elegido. Muestra SÓLO lo propio, no lo heredado: ésta es la superficie
    /// donde se MUTA, y una fila heredada que no se puede tocar (o que al tocarla cambiaría el scope
    /// padre sin avisar) sería una trampa. Para VER la herencia está el Paths Manager del atajo.
    /// </summary>
    private void RefreshVars()
    {
        string filter = VarFilterBox.Text.Trim();
        var entries = _projects.PeekVariables(_varScope);
        string? def = _projects.GetScopeDefault(_varScope);

        VarList.Items.Clear();

        var rows = new List<VarRow>();
        for (int i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            if (filter != "" &&
                !e.Title.Contains(filter, StringComparison.OrdinalIgnoreCase) &&
                !e.Path.Contains(filter, StringComparison.OrdinalIgnoreCase))
                continue;

            rows.Add(new VarRow(i, e.Title, e.Path,
                def is not null && string.Equals(e.Path, def, StringComparison.OrdinalIgnoreCase),
                ProjectPathsWindow.IsBrokenPath(e.Path)));
        }

        foreach (var r in rows.OrderBy(r => r.Title, StringComparer.CurrentCultureIgnoreCase))
            VarList.Items.Add(BuildVarItem(r));

        VarScopeHeader.Text = string.Format(Loc.T("Config.VarsScopeHeader"),
            ProjectStore.PrettyScope(_varScope) is { Length: > 0 } label ? label : Loc.T("Config.VarsGlobal"),
            entries.Count);

        // El estado vacío distingue "no hay nada cargado" de "el filtro no matcheó": son dos
        // situaciones con salidas distintas y un mismo cartel para las dos no orienta a nadie.
        VarEmptyHint.Text = entries.Count == 0 ? Loc.T("Config.VarsEmpty") : Loc.T("Config.VarsNoMatch");
        VarEmptyHint.Visibility = VarList.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        UpdateVarButtons();
    }

    /// <summary>Fila de variable: título arriba (con ⭐/⚠) y el path completo abajo, atenuado.</summary>
    private ListBoxItem BuildVarItem(VarRow row)
    {
        var panel = new StackPanel();

        panel.Children.Add(new TextBlock
        {
            // Mismas marcas que el Paths Manager, para que la fila se lea IGUAL en las dos ventanas:
            // ⚠ adelante (la señal más importante), ⭐ al final (no desplaza el título).
            Text = (row.IsBroken ? "⚠ " : "") + row.Title + (row.IsDefault ? " ⭐" : ""),
            FontSize = 13,
            Foreground = row.IsBroken
                ? (System.Windows.Media.Brush)FindResource("Danger")
                : (System.Windows.Media.Brush)FindResource("Fg"),
        });

        panel.Children.Add(new TextBlock
        {
            Text = row.Path,
            FontSize = 10,
            Foreground = (System.Windows.Media.Brush)FindResource("FgMuted"),
            TextTrimming = TextTrimming.CharacterEllipsis,
        });

        return new ListBoxItem { Content = panel, Tag = row };
    }

    /// <summary>Llena el combo de destino con TODOS los scopes menos el actual (mover a sí mismo no existe).</summary>
    private void RefreshVarTargets()
    {
        string? prev = (VarTargetCombo.SelectedItem as VarScope)?.Key;
        VarTargetCombo.Items.Clear();

        foreach (var s in AllVarScopes())
        {
            if (string.Equals(s.Key, _varScope, StringComparison.OrdinalIgnoreCase)) continue;
            // El contexto se muestra con su espacio adelante: "Plataforma" solo es ambiguo en cuanto
            // dos espacios tienen un contexto con el mismo nombre — y ése es el caso NORMAL.
            VarTargetCombo.Items.Add(s.IsContext ? s with { Label = ProjectStore.PrettyScope(s.Key) } : s);
        }

        var keep = VarTargetCombo.Items.OfType<VarScope>()
            .FirstOrDefault(s => string.Equals(s.Key, prev, StringComparison.OrdinalIgnoreCase));
        if (keep is not null) VarTargetCombo.SelectedItem = keep;
        else if (VarTargetCombo.Items.Count > 0) VarTargetCombo.SelectedIndex = 0;
    }

    /// <summary>Índices de pool de las filas seleccionadas (los separadores no existen en esta lista).</summary>
    private List<int> SelectedVarIndices() =>
        VarList.SelectedItems.OfType<ListBoxItem>()
            .Select(i => i.Tag).OfType<VarRow>()
            .Select(r => r.PoolIndex).ToList();

    private VarRow? SingleSelectedVar =>
        VarList.SelectedItems.Count == 1
            ? (VarList.SelectedItems[0] as ListBoxItem)?.Tag as VarRow
            : null;

    private void UpdateVarButtons()
    {
        int n = VarList.SelectedItems.Count;
        VarEditBtn.IsEnabled    = n == 1;
        VarDefaultBtn.IsEnabled = n == 1;
        VarDeleteBtn.IsEnabled  = n >= 1;
        VarMoveBtn.IsEnabled    = n >= 1 && VarTargetCombo.SelectedItem is VarScope;
        VarCopyBtn.IsEnabled    = VarMoveBtn.IsEnabled;

        VarDefaultBtn.Content = Loc.T(SingleSelectedVar?.IsDefault == true
            ? "Config.VarsDefaultOff" : "Config.VarsDefault");
    }

    private void OnVarListKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Delete: DeleteVariables();  e.Handled = true; break;
            case Key.F2:     EditVariable();     e.Handled = true; break;
            case Key.F3:     ToggleVarDefault(); e.Handled = true; break;
            case Key.C when Ctrl: CopyVars(cut: false); e.Handled = true; break;
            case Key.X when Ctrl: CopyVars(cut: true);  e.Handled = true; break;
            case Key.V when Ctrl: PasteVars();          e.Handled = true; break;
        }
    }

    /// <summary>En el panel de scopes sólo se PEGA: es un destino, no una fuente de selección.</summary>
    private void OnVarScopeListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.V || !Ctrl) return;
        PasteVars();
        e.Handled = true;
    }

    private static bool Ctrl => (Keyboard.Modifiers & ModifierKeys.Control) != 0;

    // ── Acciones sobre variables ───────────────────────────────────────────────

    /// <summary>Etiqueta legible de un scope para los mensajes ("Global" o "Espacio / Contexto").</summary>
    private static string VarScopeLabel(string key) =>
        key == ProjectStore.GlobalScope ? Loc.T("Config.VarsGlobal") : ProjectStore.PrettyScope(key);

    private void NewVariable()
    {
        var entry = VariableEditWindow.Show(this, Loc.T("Config.VarsDlgNew"), VarScopeLabel(_varScope));
        if (entry is null) return;

        _projects.GetPoolFor(_varScope).Add(entry.Title, entry.Path);
        RefreshVarScopes(_varScope);
    }

    private void EditVariable()
    {
        if (SingleSelectedVar is not { } row) return;

        var entry = VariableEditWindow.Show(this, Loc.T("Config.VarsDlgEdit"), VarScopeLabel(_varScope),
            new PathEntry { Title = row.Title, Path = row.Path });
        if (entry is null) return;

        _projects.UpdateVariable(_varScope, row.PoolIndex, entry.Title, entry.Path);
        RefreshVars();
    }

    private void DeleteVariables()
    {
        var indices = SelectedVarIndices();
        if (indices.Count == 0) return;

        string msg = indices.Count == 1
            ? string.Format(Loc.T("Config.VarsDeleteOneConfirm"), SingleSelectedVar?.Title ?? "")
            : string.Format(Loc.T("Config.VarsDeleteManyConfirm"), indices.Count);

        if (MessageBox.Show(this, msg, Loc.T("Config.VarsDeleteTitle"),
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

        _projects.DeleteVariables(_varScope, indices);
        RefreshVarScopes(_varScope);
    }

    /// <summary>
    /// Marca/desmarca el predeterminado DEL SCOPE que estás viendo apuntando a esta variable. Es la
    /// misma operación que el F3 del Paths Manager y escribe en el mismo lugar (el store) — acá
    /// alcanza a las propias porque esta lista sólo muestra propias.
    /// </summary>
    private void ToggleVarDefault()
    {
        if (SingleSelectedVar is not { } row) return;
        _projects.SetScopeDefault(_varScope, row.IsDefault ? null : row.Path);
        RefreshVars();
    }

    private void MoveVarsToComboTarget(bool copy)
    {
        if (VarTargetCombo.SelectedItem is not VarScope target) return;
        MoveVars(target.Key, SelectedVarIndices(), copy);
    }

    /// <summary>Botón y drag&amp;drop: el origen es SIEMPRE el scope que estás viendo.</summary>
    private void MoveVars(string targetKey, IEnumerable<int> indices, bool copy) =>
        MoveVarsFrom(_varScope, targetKey, indices, copy);

    /// <summary>
    /// Ejecuta el movimiento/copia y repinta. Un fallo SIEMPRE dice por qué (nunca silencio).
    ///
    /// El origen va EXPLÍCITO y no se asume <see cref="_varScope"/> porque al PEGAR ya no coinciden:
    /// el usuario copió en un scope, navegó a otro, y el destino es el que está viendo ahora.
    /// </summary>
    private bool MoveVarsFrom(string fromScope, string targetKey, IEnumerable<int> indices, bool copy)
    {
        var list = indices.ToList();
        if (list.Count == 0) return false;

        if (!ReportScopeResult(_projects.MoveVariables(fromScope, targetKey, list, copy),
                VarScopeLabel(targetKey)))
            return false;

        // Se repinta dejando seleccionado el scope que el usuario ESTÁ MIRANDO, sea cual sea la vía:
        // arrastrando o con el botón está parado en el ORIGEN (el caso real es vaciar un scope mal
        // cargado moviendo varias seguidas, y saltar al destino en cada drop obligaría a volver a mano
        // cada vez); pegando está parado en el DESTINO y ve aparecer las filas donde las mandó.
        RefreshVarScopes(_varScope);
        return true;
    }

    // ── Portapapeles de variables (Ctrl+C / Ctrl+X / Ctrl+V) ───────────────────

    /// <summary>Huella de una variable para revalidar el portapapeles. Ver <see cref="ScopeClipboard"/>.</summary>
    private static string VarFingerprint(PathEntry e) => ScopeClipboard.Fingerprint(e.Title, e.Path);

    private static List<string> VarFingerprints(IReadOnlyList<PathEntry> entries) =>
        entries.Select(VarFingerprint).ToList();

    /// <summary>
    /// Carga la selección en el portapapeles. No muta nada todavía —ni siquiera con Ctrl+X— porque
    /// cortar sin pegar tiene que poder abandonarse sin consecuencias: si el corte borrara acá, un
    /// Ctrl+X seguido de un Escape te habría comido las variables.
    /// </summary>
    private void CopyVars(bool cut)
    {
        var entries = _projects.PeekVariables(_varScope);
        // El filtro de rango es defensivo: las filas se construyen de esta misma pool, así que el
        // índice SIEMPRE debería existir. Pero una lista repintada a destiempo dejaría acá un
        // IndexOutOfRange que voltea la ventana, y en esta app la persistencia y la UI nunca voltean.
        var indices = SelectedVarIndices().Where(i => i >= 0 && i < entries.Count).ToList();
        if (indices.Count == 0) return;

        var fps = indices.Select(i => VarFingerprint(entries[i])).ToList();

        _varClip = new ScopeClipboard(_varScope, indices, fps, cut);
        UpdateVarClipHint();
    }

    private void PasteVars()
    {
        // Portapapeles vacío → silencio, como cualquier app: un cartel de "no hay nada" ante un atajo
        // que se aprieta de más es ruido, no información.
        if (_varClip is not { } clip) return;

        if (clip.IsStale(VarFingerprints(_projects.PeekVariables(clip.SourceScope))))
        {
            _varClip = null;
            UpdateVarClipHint();
            MessageBox.Show(this, Loc.T("Config.ClipStale"), Loc.T("Config.ClipTitle"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!MoveVarsFrom(clip.SourceScope, _varScope, clip.Indices, copy: !clip.IsCut)) return;

        // Un CORTE consumido vacía el portapapeles: las entradas ya no están en el origen, así que sus
        // índices quedaron corridos y un segundo pegado traería OTRAS filas. Una COPIA sobrevive — el
        // caso de uso es justamente pegar lo mismo en varios scopes seguidos.
        if (clip.IsCut) _varClip = null;
        UpdateVarClipHint();
    }

    /// <summary>
    /// Qué hay cargado, al lado del combo de destino. Sin esto el Ctrl+C es MUDO y no hay forma de
    /// distinguir "copié cuatro" de "no copié nada" hasta que el pegado sale mal.
    /// </summary>
    private void UpdateVarClipHint() =>
        VarClipHint.Text = _varClip is not { } c
            ? ""
            : string.Format(Loc.T(c.IsCut ? "Config.ClipCut" : "Config.ClipCopy"),
                            c.Count, VarScopeLabel(c.SourceScope));

    // ── Drag & drop de variables sobre el panel de scopes ──────────────────────

    private void OnVarDragPress(object sender, MouseButtonEventArgs e)
    {
        _varDragOrigin = e.GetPosition(null);

        var pressed = ItemUnder(VarList, e.OriginalSource as DependencyObject);
        _varDragSnapshot = pressed is not null && VarList.SelectedItems.Contains(pressed)
            ? SelectedVarIndices()
            : null;
    }

    private void OnVarDragMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            _varDragSnapshot = null;
            return;
        }

        // Umbral del sistema: sin esto, cualquier temblor de la mano al hacer click arranca un drag
        // y el usuario pierde la selección sin haber querido arrastrar nada.
        var pos = e.GetPosition(null);
        if (Math.Abs(pos.X - _varDragOrigin.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(pos.Y - _varDragOrigin.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        var indices = _varDragSnapshot ?? SelectedVarIndices();
        _varDragSnapshot = null;
        if (indices.Count == 0) return;

        // El payload viaja como STRING a propósito, no como int[]: el DataObject de un drag pasa por
        // la capa OLE del sistema, que serializa el valor — y desde .NET Core la serialización binaria
        // de tipos arbitrarios está desactivada. Un string siempre viaja; una lista de índices "linda"
        // explotaría recién al soltar, en runtime, sin que el compilador diga una palabra.
        DragDrop.DoDragDrop(VarList, new DataObject(VarDragFormat, string.Join(",", indices)),
            DragDropEffects.Move | DragDropEffects.Copy);
        HighlightDropTarget(null); // el drop pudo caer fuera: el resaltado no puede quedar colgado
    }

    private void OnVarScopeDragOver(object sender, DragEventArgs e)
    {
        var item = ItemUnder(VarScopeList, ItemHitTest(VarScopeList, e));
        bool ok = e.Data.GetDataPresent(VarDragFormat)
                  && item?.Tag is VarScope s
                  && !string.Equals(s.Key, _varScope, StringComparison.OrdinalIgnoreCase);

        // Ctrl = COPIAR, como en todo Windows. Sin modificador, MOVER.
        e.Effects = !ok ? DragDropEffects.None
            : (e.KeyStates & DragDropKeyStates.ControlKey) != 0 ? DragDropEffects.Copy
            : DragDropEffects.Move;

        HighlightDropTarget(ok ? item : null);
        e.Handled = true;
    }

    private void OnVarScopeDrop(object sender, DragEventArgs e)
    {
        HighlightDropTarget(null);
        e.Handled = true;

        if (ItemUnder(VarScopeList, ItemHitTest(VarScopeList, e))?.Tag is not VarScope target) return;
        if (e.Data.GetData(VarDragFormat) is not string payload) return;

        var indices = payload.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => int.TryParse(s, out int i) ? i : -1)
            .Where(i => i >= 0).ToList();

        MoveVars(target.Key, indices, (e.KeyStates & DragDropKeyStates.ControlKey) != 0);
    }

    /// <summary>Elemento visual bajo el puntero durante un drag (el hit-test normal no aplica en drop).</summary>
    private static DependencyObject? ItemHitTest(ListBox list, DragEventArgs e) =>
        list.InputHitTest(e.GetPosition(list)) as DependencyObject;

    /// <summary>Sube por el árbol visual hasta la fila (<see cref="ListBoxItem"/>) que contiene al elemento.</summary>
    private static ListBoxItem? ItemUnder(ListBox list, DependencyObject? hit)
    {
        while (hit is not null && hit is not ListBoxItem)
        {
            if (ReferenceEquals(hit, list)) return null;
            hit = System.Windows.Media.VisualTreeHelper.GetParent(hit);
        }
        return hit as ListBoxItem;
    }

    /// <summary>
    /// Pinta (o despinta) la fila de destino. Es el ÚNICO feedback de "acá cae": el cursor de Windows
    /// dice si va a mover o copiar, pero no sobre QUÉ scope — y con espacios y contextos apilados y
    /// filas de 24px, errarle por uno es lo más fácil del mundo.
    /// </summary>
    private void HighlightDropTarget(ListBoxItem? item)
    {
        if (ReferenceEquals(_varDropTarget, item)) return;

        if (_varDropTarget?.Content is Border old)
            old.Background = System.Windows.Media.Brushes.Transparent;

        _varDropTarget = item;

        // Acento TRANSLÚCIDO, no el acento pleno: la fila tiene que seguir leyéndose mientras la
        // resaltás (un relleno opaco celeste deja el texto claro ilegible justo cuando más importa).
        if (item?.Content is Border border)
            border.Background = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromArgb(0x70, 0x4F, 0xC3, 0xF7));
    }

    // ── Pestaña Comandos (servicios) ───────────────────────────────────────────────────────────
    //
    // Trilliza de Espacios y Variables. Reusa a propósito TODO lo que ya existe de aquélla —
    // AllVarScopes, BuildVarScopeItem, ItemUnder/ItemHitTest, HighlightDropTarget — y no una copia
    // propia: si el mapa de scopes se dibujara distinto en cada pestaña, el usuario tendría que
    // aprender dos veces la misma pantalla. Lo único propio es la fila del panel derecho, porque un
    // servicio tiene cuatro campos y una variable dos.
    //
    // Qué NO hay acá, y por qué: no se LANZA nada. Config es donde se define lo que hay; lanzar es
    // la ventana del atajo (Win+Numpad+), que además tiene el estado vivo 🟢/⚪ para saber qué está
    // arriba. Un botón "lanzar" acá te haría arrancar servicios sin ver si ya corrían.

    /// <summary>Un servicio del scope elegido. <see cref="PoolIndex"/> es su índice REAL en la pool.</summary>
    private sealed record CmdRow(int PoolIndex, string Title, string Command, string WorkDir,
                                 int Port, bool AutoStarts, bool IsBroken, bool PortDuplicated);

    private const string CmdDragFormat = "AmpzDesktopBooster.CommandDrag";

    private string _cmdScope = ProjectStore.GlobalScope;
    private System.Windows.Point _cmdDragOrigin;
    private List<int>? _cmdDragSnapshot;

    /// <summary>Portapapeles PROPIO, separado del de variables — ver <see cref="PasteCmds"/>.</summary>
    private ScopeClipboard? _cmdClip;

    private void InitCmdsTab()
    {
        CmdScopeList.SelectionChanged += (_, _) => OnCmdScopeChanged();
        CmdList.SelectionChanged += (_, _) => UpdateCmdButtons();
        CmdList.MouseDoubleClick += (_, _) => EditCommand();
        CmdList.PreviewKeyDown += OnCmdListKeyDown;
        CmdScopeList.PreviewKeyDown += OnCmdScopeListKeyDown;
        CmdFilterBox.TextChanged += (_, _) => RefreshCmds();

        CmdNewBtn.Click    += (_, _) => NewCommand();
        CmdEditBtn.Click   += (_, _) => EditCommand();
        CmdDeleteBtn.Click += (_, _) => DeleteCommands();
        CmdAutoBtn.Click   += (_, _) => ToggleCmdAutoStart();
        CmdMoveBtn.Click   += (_, _) => MoveCmdsToComboTarget(copy: false);
        CmdCopyBtn.Click   += (_, _) => MoveCmdsToComboTarget(copy: true);

        CmdList.PreviewMouseLeftButtonDown += OnCmdDragPress;
        CmdList.MouseMove += OnCmdDragMove;
        CmdScopeList.DragOver += OnCmdScopeDragOver;
        CmdScopeList.Drop += OnCmdScopeDrop;
        CmdScopeList.DragLeave += (_, _) => HighlightDropTarget(null);

        RefreshCmdScopes();
    }

    /// <summary>Repinta el panel de scopes con el conteo de comandos PROPIOS de cada uno.</summary>
    private void RefreshCmdScopes(string? select = null)
    {
        // Igual que RefreshVarScopes: la pestaña de Espacios la llama ANTES de que ésta se
        // inicialice, así que el refresco del panel derecho se dispara explícito al final en vez de
        // confiar en el SelectionChanged (que todavía no está enganchado).
        string wanted = select ?? _cmdScope;
        CmdScopeList.Items.Clear();

        var scopes = AllVarScopes();
        foreach (var s in scopes)
            CmdScopeList.Items.Add(BuildVarScopeItem(s, _projects.PeekServices(s.Key).Count));

        if (!scopes.Any(s => string.Equals(s.Key, wanted, StringComparison.OrdinalIgnoreCase)))
            wanted = ProjectStore.GlobalScope;

        CmdScopeList.SelectedItem = CmdScopeList.Items.OfType<ListBoxItem>().FirstOrDefault(i =>
            i.Tag is VarScope s && string.Equals(s.Key, wanted, StringComparison.OrdinalIgnoreCase));

        OnCmdScopeChanged();
    }

    private VarScope? SelectedCmdScope => (CmdScopeList.SelectedItem as ListBoxItem)?.Tag as VarScope;

    private void OnCmdScopeChanged()
    {
        _cmdScope = SelectedCmdScope?.Key ?? ProjectStore.GlobalScope;
        RefreshCmdTargets();
        RefreshCmds();
    }

    /// <summary>
    /// Contenido del scope elegido. SÓLO lo propio, no lo heredado — mismo criterio que Variables:
    /// ésta es la superficie donde se MUTA, y una fila heredada que al tocarla cambia el scope padre
    /// sin avisar sería una trampa. Para VER la herencia está la ventana del atajo.
    /// </summary>
    private void RefreshCmds()
    {
        string filter = CmdFilterBox.Text.Trim();
        var entries = _projects.PeekServices(_cmdScope);
        var duplicated = _projects.Ports.Duplicates();

        CmdList.Items.Clear();

        var rows = new List<CmdRow>();
        for (int i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            if (filter != "" &&
                !e.Title.Contains(filter, StringComparison.OrdinalIgnoreCase) &&
                !e.Command.Contains(filter, StringComparison.OrdinalIgnoreCase) &&
                !e.WorkDir.Contains(filter, StringComparison.OrdinalIgnoreCase) &&
                !e.Port.ToString().Contains(filter))
                continue;

            rows.Add(new CmdRow(i, e.Title, e.Command, e.WorkDir, e.Port,
                ServiceLauncher.IsGroupLaunchable(e),
                ServicesWindow.IsBrokenDir(e),
                e.Port > 0 && duplicated.Contains(e.Port)));
        }

        foreach (var r in rows.OrderBy(r => r.Title, StringComparer.CurrentCultureIgnoreCase))
            CmdList.Items.Add(BuildCmdItem(r));

        CmdScopeHeader.Text = string.Format(Loc.T("Config.CmdsScopeHeader"),
            ProjectStore.PrettyScope(_cmdScope) is { Length: > 0 } label ? label : Loc.T("Config.VarsGlobal"),
            entries.Count);

        CmdEmptyHint.Text = entries.Count == 0 ? Loc.T("Config.CmdsEmpty") : Loc.T("Config.VarsNoMatch");
        CmdEmptyHint.Visibility = CmdList.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        UpdateCmdButtons();
    }

    /// <summary>
    /// Fila de comando: título arriba con las MISMAS marcas que la ventana del atajo (⚠ directorio
    /// roto, ⏩ entra en "levantar todo", ⛔ puerto duplicado) y el comando abajo, atenuado, con el
    /// puerto adelante. Que se lea igual en las dos superficies no es cosmética: es lo que evita
    /// tener que re-aprender la lista al cambiar de ventana.
    /// </summary>
    private ListBoxItem BuildCmdItem(CmdRow row)
    {
        var panel = new StackPanel();

        string marks = (row.AutoStarts ? " ⏩" : "") + (row.PortDuplicated ? " ⛔" : "");
        panel.Children.Add(new TextBlock
        {
            Text = (row.IsBroken ? "⚠ " : "") + row.Title + marks,
            FontSize = 13,
            Foreground = row.IsBroken
                ? (System.Windows.Media.Brush)FindResource("Danger")
                : (System.Windows.Media.Brush)FindResource("Fg"),
        });

        // El puerto va DELANTE del comando y no en una columna aparte: es el campo que más se
        // consulta de un vistazo (es la identidad del servidor y la clave del registro de puertos),
        // y una columna propia lo alejaría del texto que lo explica.
        string sub = row.Port > 0 ? $":{row.Port}  ·  " : "";
        sub += row.Command == "" ? Loc.T("Config.CmdsMonitorOnly") : row.Command;
        if (row.WorkDir != "") sub += "  ·  " + row.WorkDir;

        panel.Children.Add(new TextBlock
        {
            Text = sub,
            FontSize = 10,
            Foreground = (System.Windows.Media.Brush)FindResource("FgMuted"),
            TextTrimming = TextTrimming.CharacterEllipsis,
        });

        return new ListBoxItem { Content = panel, Tag = row };
    }

    private void RefreshCmdTargets()
    {
        string? prev = (CmdTargetCombo.SelectedItem as VarScope)?.Key;
        CmdTargetCombo.Items.Clear();

        foreach (var s in AllVarScopes())
        {
            if (string.Equals(s.Key, _cmdScope, StringComparison.OrdinalIgnoreCase)) continue;
            CmdTargetCombo.Items.Add(s.IsContext ? s with { Label = ProjectStore.PrettyScope(s.Key) } : s);
        }

        var keep = CmdTargetCombo.Items.OfType<VarScope>()
            .FirstOrDefault(s => string.Equals(s.Key, prev, StringComparison.OrdinalIgnoreCase));
        if (keep is not null) CmdTargetCombo.SelectedItem = keep;
        else if (CmdTargetCombo.Items.Count > 0) CmdTargetCombo.SelectedIndex = 0;
    }

    private List<int> SelectedCmdIndices() =>
        CmdList.SelectedItems.OfType<ListBoxItem>()
            .Select(i => i.Tag).OfType<CmdRow>()
            .Select(r => r.PoolIndex).ToList();

    private CmdRow? SingleSelectedCmd =>
        CmdList.SelectedItems.Count == 1
            ? (CmdList.SelectedItems[0] as ListBoxItem)?.Tag as CmdRow
            : null;

    private void UpdateCmdButtons()
    {
        int n = CmdList.SelectedItems.Count;
        CmdEditBtn.IsEnabled   = n == 1;
        CmdAutoBtn.IsEnabled   = n == 1;
        CmdDeleteBtn.IsEnabled = n >= 1;
        CmdMoveBtn.IsEnabled   = n >= 1 && CmdTargetCombo.SelectedItem is VarScope;
        CmdCopyBtn.IsEnabled   = CmdMoveBtn.IsEnabled;

        CmdAutoBtn.Content = Loc.T(SingleSelectedCmd?.AutoStarts == true
            ? "Config.CmdsAutoOff" : "Config.CmdsAutoOn");
    }

    private void OnCmdListKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Delete: DeleteCommands();      e.Handled = true; break;
            case Key.F2:     EditCommand();         e.Handled = true; break;
            case Key.F3:     ToggleCmdAutoStart();  e.Handled = true; break;
            case Key.C when Ctrl: CopyCmds(cut: false); e.Handled = true; break;
            case Key.X when Ctrl: CopyCmds(cut: true);  e.Handled = true; break;
            case Key.V when Ctrl: PasteCmds();          e.Handled = true; break;
        }
    }

    /// <summary>En el panel de scopes sólo se PEGA (mismo criterio que Variables).</summary>
    private void OnCmdScopeListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.V || !Ctrl) return;
        PasteCmds();
        e.Handled = true;
    }

    // ── Acciones sobre comandos ────────────────────────────────────────────────

    private void NewCommand()
    {
        var entry = ServiceEditWindow.Show(this, Loc.T("Services.DlgNewTitle"),
                                           VarScopeLabel(_cmdScope), ports: _projects.Ports);
        if (entry is null) return;

        _projects.GetServicePoolFor(_cmdScope)
                 .Add(entry.Title, entry.Command, entry.WorkDir, entry.Port, entry.AutoStart);
        RefreshCmdScopes(_cmdScope);
    }

    private void EditCommand()
    {
        if (SingleSelectedCmd is not { } row) return;

        // Se pasa la entry VIVA de la pool (no una copia armada con los campos de la fila): el
        // registro de puertos la excluye POR REFERENCIA, así que una copia se chocaría consigo misma
        // y no te dejaría guardar sin cambiarle el puerto.
        var live = _projects.PeekServices(_cmdScope);
        if (row.PoolIndex < 0 || row.PoolIndex >= live.Count) return;

        var entry = ServiceEditWindow.Show(this, Loc.T("Services.DlgEditTitle"),
                                           VarScopeLabel(_cmdScope), live[row.PoolIndex],
                                           _projects.Ports);
        if (entry is null) return;

        _projects.UpdateService(_cmdScope, row.PoolIndex, entry);
        RefreshCmds();
    }

    private void DeleteCommands()
    {
        var indices = SelectedCmdIndices();
        if (indices.Count == 0) return;

        string msg = indices.Count == 1
            ? string.Format(Loc.T("Config.CmdsDeleteOneConfirm"), SingleSelectedCmd?.Title ?? "")
            : string.Format(Loc.T("Config.CmdsDeleteManyConfirm"), indices.Count);

        if (MessageBox.Show(this, msg, Loc.T("Config.VarsDeleteTitle"),
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

        _projects.DeleteServices(_cmdScope, indices);
        RefreshCmdScopes(_cmdScope);
    }

    /// <summary>
    /// Mete o saca el servicio de "levantar todo". Es el análogo del ⭐ de Variables: la acción de un
    /// solo gesto sobre la fila elegida. Escribe un valor EXPLÍCITO (nunca vuelve al automático) —
    /// ver ProjectStore.SetServiceAutoStart.
    /// </summary>
    private void ToggleCmdAutoStart()
    {
        if (SingleSelectedCmd is not { } row) return;
        _projects.SetServiceAutoStart(_cmdScope, row.PoolIndex, !row.AutoStarts);
        RefreshCmds();
    }

    private void MoveCmdsToComboTarget(bool copy)
    {
        if (CmdTargetCombo.SelectedItem is not VarScope target) return;
        MoveCmds(target.Key, SelectedCmdIndices(), copy);
    }

    /// <summary>Botón y drag&amp;drop: el origen es SIEMPRE el scope que estás viendo.</summary>
    private void MoveCmds(string targetKey, IEnumerable<int> indices, bool copy) =>
        MoveCmdsFrom(_cmdScope, targetKey, indices, copy);

    /// <summary>Origen explícito por el mismo motivo que <see cref="MoveVarsFrom"/>: al pegar difiere.</summary>
    private bool MoveCmdsFrom(string fromScope, string targetKey, IEnumerable<int> indices, bool copy)
    {
        var list = indices.ToList();
        if (list.Count == 0) return false;

        if (!ReportScopeResult(_projects.MoveServices(fromScope, targetKey, list, copy, out var reassigned),
                VarScopeLabel(targetKey)))
            return false;

        // La copia NO se lleva el puerto (duplicarlo es justo lo que el registro prohíbe): sale con
        // el primer libre. Se AVISA siempre — un campo que cambia solo y en silencio es peor que la
        // duplicación, porque después lanzás el comando creyendo que apunta al puerto de siempre.
        if (reassigned.Count > 0)
            MessageBox.Show(this,
                string.Format(Loc.T("Config.CmdsCopyPortReassigned"), string.Join(", ", reassigned)),
                Loc.T("Config.CmdsTitle"), MessageBoxButton.OK, MessageBoxImage.Information);

        // Queda seleccionado el scope que el usuario ESTÁ MIRANDO, mismo criterio que Variables:
        // arrastrando o con el botón es el ORIGEN (vaciar un scope mal cargado moviendo varios
        // seguidos); pegando es el DESTINO, y ve aparecer las filas donde las mandó.
        RefreshCmdScopes(_cmdScope);
        return true;
    }

    // ── Portapapeles de comandos (Ctrl+C / Ctrl+X / Ctrl+V) ────────────────────

    /// <summary>
    /// Huella de un servicio: sus CINCO campos. Van todos y no sólo el título porque el título es
    /// justo el que más se re-tipea sin que la entrada cambie de identidad, y editar el puerto o el
    /// directorio de lo que tenés copiado es exactamente el caso que la revalidación debe cazar.
    /// </summary>
    private static string CmdFingerprint(ServiceEntry e) => ScopeClipboard.Fingerprint(
        e.Title, e.Command, e.WorkDir, e.Port.ToString(), e.AutoStart?.ToString() ?? "");

    private static List<string> CmdFingerprints(IReadOnlyList<ServiceEntry> entries) =>
        entries.Select(CmdFingerprint).ToList();

    private void CopyCmds(bool cut)
    {
        var entries = _projects.PeekServices(_cmdScope);
        var indices = SelectedCmdIndices().Where(i => i >= 0 && i < entries.Count).ToList(); // ver CopyVars
        if (indices.Count == 0) return;

        var fps = indices.Select(i => CmdFingerprint(entries[i])).ToList();

        _cmdClip = new ScopeClipboard(_cmdScope, indices, fps, cut);
        UpdateCmdClipHint();
    }

    /// <summary>
    /// Pega los comandos del portapapeles en el scope que estás viendo.
    ///
    /// El portapapeles de comandos es SEPARADO del de variables a propósito: un servicio tiene cinco
    /// campos y una variable dos, así que "pegar" de una lista en la otra no tiene un significado que
    /// se pueda definir sin inventarlo. Con dos portapapeles, Ctrl+V en cada pestaña pega lo que esa
    /// pestaña copió, y nunca hay que explicar por qué el atajo no hizo nada.
    /// </summary>
    private void PasteCmds()
    {
        if (_cmdClip is not { } clip) return;

        if (clip.IsStale(CmdFingerprints(_projects.PeekServices(clip.SourceScope))))
        {
            _cmdClip = null;
            UpdateCmdClipHint();
            MessageBox.Show(this, Loc.T("Config.ClipStale"), Loc.T("Config.ClipTitle"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // copy: !IsCut — o sea que Ctrl+C hereda la regla del puerto tal cual está escrita en
        // MoveServices: la copia NO se lleva el puerto y sale con el primero libre, avisando. Ctrl+X
        // sí lo conserva, porque la entrada se va del origen y nunca hay dos dueños del mismo número.
        if (!MoveCmdsFrom(clip.SourceScope, _cmdScope, clip.Indices, copy: !clip.IsCut)) return;

        if (clip.IsCut) _cmdClip = null;
        UpdateCmdClipHint();
    }

    private void UpdateCmdClipHint() =>
        CmdClipHint.Text = _cmdClip is not { } c
            ? ""
            : string.Format(Loc.T(c.IsCut ? "Config.ClipCut" : "Config.ClipCopy"),
                            c.Count, VarScopeLabel(c.SourceScope));

    // ── Drag & drop de comandos sobre el panel de scopes ───────────────────────

    private void OnCmdDragPress(object sender, MouseButtonEventArgs e)
    {
        _cmdDragOrigin = e.GetPosition(null);

        var pressed = ItemUnder(CmdList, e.OriginalSource as DependencyObject);
        _cmdDragSnapshot = pressed is not null && CmdList.SelectedItems.Contains(pressed)
            ? SelectedCmdIndices()
            : null;
    }

    private void OnCmdDragMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            _cmdDragSnapshot = null;
            return;
        }

        var pos = e.GetPosition(null);
        if (Math.Abs(pos.X - _cmdDragOrigin.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(pos.Y - _cmdDragOrigin.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        var indices = _cmdDragSnapshot ?? SelectedCmdIndices();
        _cmdDragSnapshot = null;
        if (indices.Count == 0) return;

        // Payload como STRING por el mismo motivo que en Variables: el DataObject pasa por la capa
        // OLE y desde .NET Core la serialización binaria de tipos arbitrarios está desactivada.
        DragDrop.DoDragDrop(CmdList, new DataObject(CmdDragFormat, string.Join(",", indices)),
            DragDropEffects.Move | DragDropEffects.Copy);
        HighlightDropTarget(null);
    }

    private void OnCmdScopeDragOver(object sender, DragEventArgs e)
    {
        var item = ItemUnder(CmdScopeList, ItemHitTest(CmdScopeList, e));
        bool ok = e.Data.GetDataPresent(CmdDragFormat)
                  && item?.Tag is VarScope s
                  && !string.Equals(s.Key, _cmdScope, StringComparison.OrdinalIgnoreCase);

        e.Effects = !ok ? DragDropEffects.None
            : (e.KeyStates & DragDropKeyStates.ControlKey) != 0 ? DragDropEffects.Copy
            : DragDropEffects.Move;

        HighlightDropTarget(ok ? item : null);
        e.Handled = true;
    }

    private void OnCmdScopeDrop(object sender, DragEventArgs e)
    {
        HighlightDropTarget(null);
        e.Handled = true;

        if (ItemUnder(CmdScopeList, ItemHitTest(CmdScopeList, e))?.Tag is not VarScope target) return;
        if (e.Data.GetData(CmdDragFormat) is not string payload) return;

        var indices = payload.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => int.TryParse(s, out int i) ? i : -1)
            .Where(i => i >= 0).ToList();

        MoveCmds(target.Key, indices, (e.KeyStates & DragDropKeyStates.ControlKey) != 0);
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
