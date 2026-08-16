using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using AmpzDesktopBooster.Apps;
using AmpzDesktopBooster.Desktops;
using AmpzDesktopBooster.Interop;
using AmpzDesktopBooster.Persistence;
using AmpzDesktopBooster.Services;
using AmpzDesktopBooster.Services.Localization;

namespace AmpzDesktopBooster;

/// <summary>
/// Servicios del espacio/contexto — la Win+Numpad+ (Add). Qué hay que levantar para laburar acá, y
/// qué está levantado AHORA.
///
/// Sucede al viejo popup de Puertos, que modelaba el PUERTO (la consecuencia) en vez del SERVICIO, y
/// que sabía decirte si algo corría pero no hacerlo correr. Hoy:
///   Enter        → acción primaria: LANZAR (o abrir en el browser si la entrada no tiene comando)
///   Shift+Enter  → abrir http://localhost:PUERTO en el browser
///   Ctrl+Enter   → LEVANTAR TODO lo que falta (los que declaran puerto y no están escuchando)
///   F2 / ✎       → editar los cuatro campos
///   Supr         → borrar
///   Ctrl+C       → copiar la URL con localhost   ·   Ctrl+Shift+C → con la IP de red
///   🔳 QR        → QR de la URL-de-red (escaneás con el celu y entrás)
///
/// HERENCIA: igual que Variables — contexto → espacio → global. Las heredadas se ven atenuadas y SE
/// PUEDEN LANZAR (heredar un servicio es justamente poder levantarlo desde el contexto), pero no se
/// editan ni se borran desde acá: se tocan parándote en el scope donde viven, para que no puedas
/// romperle el servicio a otro contexto sin darte cuenta.
///
/// POR QUÉ EL ESTADO ES EL PUERTO Y NO EL PID (limitación asumida, no la re-pelees): guardar el PID
/// que lanzamos sería exacto en teoría e inútil acá — se lanza vía wt.exe, que delega en el proceso
/// MONARCA de Windows Terminal, así que el PID que devuelve Process.Start NO es el del dev server; y
/// además moriría al reiniciar la app. El puerto es barato, sobrevive a que cierres la app, y es
/// AMBIGUO a propósito: te dice "hay algo escuchando en ese puerto", no "es el tuyo". Con un puerto
/// por servicio alcanza; el caso que no desambigua (Expo/Metro clava 8081) se resuelve fijando el
/// puerto en el comando.
/// </summary>
public partial class ServicesWindow : Window
{
    /// <summary>De qué pool viene la fila: define si se puede editar y cómo se pinta.</summary>
    private enum RowScope { Own, Parent, Global, Separator }

    /// <summary>Fila visible. Observable: el timer de estado actualiza el puntito en el lugar.</summary>
    private sealed class Row : INotifyPropertyChanged
    {
        public required RowScope Scope { get; init; }
        /// <summary>Índice a la entry real en SU pool (-1 en separadores).</summary>
        public required int PoolIndex { get; init; }
        public required string Title { get; init; }
        public required string Command { get; init; }
        public required string WorkDir { get; init; }
        public required int Port { get; init; }
        public required bool IsBroken { get; init; }
        /// <summary>Su puerto lo declara además OTRA entrada del catálogo (choque preexistente).</summary>
        public required bool IsPortDuplicated { get; init; }
        /// <summary>Entra en "levantar todo". Se pinta en la fila para que se lea sin abrir el editor.</summary>
        public required bool AutoStarts { get; init; }

        private bool _isListening;
        public bool IsListening
        {
            get => _isListening;
            set
            {
                if (_isListening == value) return;
                _isListening = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsListening)));
            }
        }

        public bool IsSeparator => Scope == RowScope.Separator;
        public bool IsOwn => Scope == RowScope.Own;
        public bool IsInherited => Scope is RowScope.Parent or RowScope.Global;

        /// <summary>Declara puerto → es un SERVIDOR y tiene estado que mostrar. Ver ServiceEntry.</summary>
        public bool HasPort => Port > 0;
        public bool HasCommand => Command.Trim() != "";

        public string PortText => Port > 0 ? Port.ToString() : "";
        public string LocalhostUrl => $"http://localhost:{Port}";

        /// <summary>
        /// ⚠ delante cuando el directorio ya no existe (la señal más importante de la fila) y ⏩ atrás
        /// cuando el servicio entra en "levantar todo" — mismo ícono que el botón, para que de un
        /// vistazo sepas QUÉ va a arrancar sin tener que abrir el editor de cada fila.
        ///
        /// ⛔ atrás cuando el puerto está duplicado en el catálogo. Va acá y no en un cartel aparte
        /// porque el choque es INVISIBLE por naturaleza: la otra entrada vive en un scope que no
        /// estás mirando, y el 🟢 se pone verde igual (mira el puerto, no el proceso) — o sea que sin
        /// esta marca el estado te MIENTE con cara de éxito. El registro impide los nuevos; esta
        /// marca es para los que ya estaban guardados cuando la regla llegó.
        /// </summary>
        public string Display
        {
            get
            {
                string t = AutoStarts ? Title + " ⏩" : Title;
                if (IsPortDuplicated) t += " ⛔";
                return IsBroken ? "⚠ " + t : t;
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private readonly ServicePool _pool;          // scope primario: el único EDITABLE desde acá
    private readonly ServicePool? _parentPool;   // el espacio, si estás parado en un contexto
    private readonly ServicePool? _globalPool;
    private readonly ObservableCollection<Row> _rows = new();
    private readonly DispatcherTimer _statusTimer;
    private string? _networkIp;

    /// <summary>
    /// Registro de puertos de TODO el catálogo (no sólo de las tres pools visibles acá): es lo que
    /// hace que el alta pueda avisar de un choque contra un scope que ni siquiera está en pantalla.
    /// </summary>
    private readonly PortRegistry? _ports;

    /// <summary>
    /// Servicios SIN puerto que este arranque grupal ya disparó (key = comando + directorio). Existe
    /// sólo para que el re-press no te apile workers duplicados — ver <see cref="LaunchMissing"/>.
    /// </summary>
    private readonly HashSet<string> _groupLaunchedPortless = new(StringComparer.OrdinalIgnoreCase);

    public ServicesWindow(ServicePool pool, string deskName, ServicePool? parentPool = null,
                          ServicePool? globalPool = null, PortRegistry? ports = null)
    {
        InitializeComponent();
        _pool = pool;
        _parentPool = parentPool;
        _globalPool = globalPool;
        _ports = ports;

        Icon = AppIcon.TryLoadForWindow();

        // 90% x 80% del área de trabajo, como Notas. Antes era 1020 fijo, y con eso el comando —que
        // es lo que más se necesita leer de un vistazo, porque ahí están los parámetros— entraba en
        // 230px y se cortaba SIEMPRE. Va antes de RefreshList para que el reparto de columnas se
        // calcule sobre el ancho definitivo.
        this.SizeToWorkArea();
        LayoutColumns();

        _networkIp = LocalIp.Get();
        HeaderText.Text = $"{pool.Label} — {Loc.T("Services.HeaderSuffix")}";
        SubHeaderText.Text = _networkIp is null
            ? $"{deskName}    ·    {Loc.T("Services.NoNetwork")}"
            : $"{deskName}    ·    {string.Format(Loc.T("Services.NetworkIp"), _networkIp)}";

        ServiceList.ItemsSource = _rows;
        RefreshList();

        FilterBox.TextChanged += (_, _) => RefreshList();
        FilterBox.PreviewKeyDown += OnFilterKeyDown;
        ServiceList.PreviewKeyDown += OnListKeyDown;
        ServiceList.MouseDoubleClick += (_, _) => PrimaryAction();

        LaunchBtn.Click += (_, _) => PrimaryAction();
        VisitBtn.Click += (_, _) => OpenInBrowser();
        LaunchAllBtn.Click += (_, _) => LaunchMissingWithFeedback();
        AddBtn.Click += (_, _) => AddNew();
        EditBtn.Click += (_, _) => EditSelected();
        DeleteBtn.Click += (_, _) => DeleteSelected();
        CopyLocalBtn.Click += (_, _) => CopyLocalhost();
        QrBtn.Click += (_, _) => ShowQr();
        CloseBtn.Click += (_, _) => Close();

        // Estado vivo: cada 2.5s recomputamos qué puertos escuchan y actualizamos los puntitos.
        // GetActiveTcpListeners es barato y no toca red → corre en el UI thread sin drama. NO
        // rebuildeamos la lista (pisaría selección y filtro): sólo tocamos IsListening de cada fila.
        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
        _statusTimer.Tick += (_, _) => RefreshStatus();
        _statusTimer.Start();

        Closed += (_, _) => _statusTimer.Stop();
        Loaded += (_, _) => FilterBox.Focus();
    }

    /// <summary>
    /// Reparte el ancho disponible entre Título, Comando y Directorio. Existe porque un
    /// <c>GridViewColumn</c> con <c>Width</c> fijo NO crece con la ventana y GridView no tiene
    /// star-sizing: sin esto, agrandar la ventana sólo agregaba un hueco a la derecha y el comando
    /// se seguía cortando exactamente igual que antes.
    ///
    /// Estado y Puerto quedan FIJOS a propósito: uno es un puntito y el otro cuatro dígitos: darles
    /// ancho proporcional sería regalarle a un círculo el espacio que necesita un comando.
    ///
    /// El DIRECTORIO se lleva la tajada más grande, y no por gusto: se midió contra el catálogo real.
    /// Un comando típico ronda los 60 caracteres ("php artisan queue:work --queue=default --tries=3
    /// --timeout=60"), pero un directorio es un path ABSOLUTO de Windows con repos anidados y se va a
    /// los 86 ("C:\...\Repos clientes\Geocontrol\geoplataform -dev\worktrees\wt-desk-01"). Los paths
    /// son estructuralmente más largos que los comandos, así que darle la mayor al comando —que es lo
    /// que parecía obvio— dejaba al directorio cortándose igual.
    ///
    /// El TÍTULO es el que mejor tolera quedarse corto: lo escribiste vos y lo reconocés por el
    /// principio, mientras que en un comando y en un path lo que se necesita leer está al FINAL.
    /// </summary>
    private void LayoutColumns()
    {
        const double fixedCols = 60 + 80;   // Estado + Puerto (ver los Width del XAML)
        const double chrome = 34 + 4 + 24;  // borde+padding de la ventana, padding del panel, scrollbar

        double free = Width - fixedCols - chrome;
        if (free <= 0) return; // pantalla absurdamente chica: dejamos los anchos de arranque del XAML

        TitleCol.Width   = free * 0.24;
        CommandCol.Width = free * 0.36;
        WorkDirCol.Width = free * 0.40;
    }

    // ── Lista ───────────────────────────────────────────────────────────────────

    private void RefreshList()
    {
        string filter = FilterBox.Text.Trim();
        var listening = TcpPortInfo.ListeningPorts();
        // Se recalcula en cada rebuild y no una vez al abrir: si arreglás un duplicado desde acá, el
        // ⛔ tiene que irse de las DOS filas — también de la que no tocaste.
        var duplicated = _ports?.Duplicates() ?? new HashSet<int>();

        _rows.Clear();
        AddSection(_pool, RowScope.Own, filter, listening, duplicated, header: null);
        // El orden de las secciones ES el orden de cercanía (contexto → espacio → global), igual que
        // en Variables: lo primero que ves es lo tuyo.
        AddSection(_parentPool, RowScope.Parent, filter, listening, duplicated, _parentPool?.Label);
        AddSection(_globalPool, RowScope.Global, filter, listening, duplicated, _globalPool?.Label);

        SelectFirstSelectable();
    }

    /// <summary>Agrega las filas de una pool, con su rótulo de sección si es heredada.</summary>
    private void AddSection(ServicePool? pool, RowScope scope, string filter,
                            HashSet<int> listening, HashSet<int> duplicated, string? header)
    {
        if (pool is null) return;

        var matches = pool.Entries
            .Select((e, i) => (e, i))
            .Where(t => Matches(t.e, filter))
            .ToList();
        if (matches.Count == 0) return;

        if (header is not null)
        {
            _rows.Add(new Row
            {
                Scope = RowScope.Separator, PoolIndex = -1,
                Title = string.Format(Loc.T("Services.SectionInherited"), header),
                Command = "", WorkDir = "", Port = 0, IsBroken = false, AutoStarts = false,
                IsPortDuplicated = false,
            });
        }

        foreach (var (e, i) in matches.OrderBy(t => t.e.Title, StringComparer.CurrentCultureIgnoreCase))
        {
            _rows.Add(new Row
            {
                Scope = scope,
                PoolIndex = i,
                Title = e.Title,
                Command = e.Command,
                WorkDir = e.WorkDir,
                Port = e.Port,
                IsBroken = IsBrokenDir(e),
                IsPortDuplicated = e.Port > 0 && duplicated.Contains(e.Port),
                AutoStarts = ServiceLauncher.IsGroupLaunchable(e),
                IsListening = e.Port > 0 && listening.Contains(e.Port),
            });
        }
    }

    private static bool Matches(ServiceEntry e, string filter) =>
        filter == ""
        || e.Title.Contains(filter, StringComparison.OrdinalIgnoreCase)
        || e.Command.Contains(filter, StringComparison.OrdinalIgnoreCase)
        || e.WorkDir.Contains(filter, StringComparison.OrdinalIgnoreCase)
        || e.Port.ToString().Contains(filter);

    /// <summary>
    /// El directorio configurado ya no existe. Sólo aplica a entradas CON comando: una de sólo
    /// monitoreo (migrada del viejo ports.json) no tiene directorio y no está rota por eso.
    /// Público porque la pestaña Comandos de la config pinta el mismo ⚠: el criterio de "roto" vive
    /// acá y no duplicado, o las dos superficies terminarían discrepando sobre la misma fila
    /// (mismo precedente que <see cref="ProjectPathsWindow.IsBrokenPath"/>).
    /// </summary>
    public static bool IsBrokenDir(ServiceEntry e)
    {
        if (e.Command.Trim() == "") return false;
        string dir = e.WorkDir.Trim();
        return dir == "" || !Directory.Exists(dir);
    }

    /// <summary>Sólo actualiza los puntitos en el lugar — NO rebuildea (no pisa selección/filtro).</summary>
    private void RefreshStatus()
    {
        var listening = TcpPortInfo.ListeningPorts();
        foreach (var row in _rows)
            row.IsListening = row.Port > 0 && listening.Contains(row.Port);
    }

    private void SelectFirstSelectable()
    {
        var first = _rows.FirstOrDefault(r => !r.IsSeparator);
        if (first is not null) ServiceList.SelectedItem = first;
    }

    private Row? Selected => ServiceList.SelectedItem as Row;

    /// <summary>La pool a la que pertenece una fila (para editar/borrar sobre la correcta).</summary>
    private ServicePool? PoolOf(Row row) => row.Scope switch
    {
        RowScope.Own => _pool,
        RowScope.Parent => _parentPool,
        RowScope.Global => _globalPool,
        _ => null,
    };

    // ── Teclado ─────────────────────────────────────────────────────────────────

    private static bool Shift => (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
    private static bool Ctrl => (Keyboard.Modifiers & ModifierKeys.Control) != 0;

    private void OnFilterKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            if (Ctrl) LaunchMissingWithFeedback(); else PrimaryAction();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape) { Close(); e.Handled = true; }
        else if (e.Key == Key.Down && _rows.Count > 0)
        {
            SelectFirstSelectable();
            ServiceList.Focus();
            e.Handled = true;
        }
    }

    private void OnListKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter when Ctrl:  LaunchMissingWithFeedback(); e.Handled = true; break;
            case Key.Enter when Shift: OpenInBrowser();             e.Handled = true; break;
            case Key.Enter:            PrimaryAction();             e.Handled = true; break;
            case Key.Escape:           Close();                     e.Handled = true; break;
            case Key.Delete:           DeleteSelected();            e.Handled = true; break;
            case Key.F2:               EditSelected();              e.Handled = true; break;
            case Key.C when Ctrl && Shift: CopyNetwork();   e.Handled = true; break;
            case Key.C when Ctrl:          CopyLocalhost(); e.Handled = true; break;
        }
    }

    // ── Acciones ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Acción primaria del servicio. NO depende del estado volátil (si está escuchando o no) sino de
    /// su DEFINICIÓN, que es estable: si sabe cómo levantarse, lo levanta; si no tiene comando —una
    /// entrada de sólo monitoreo— lo único que puede hacer es abrirte el browser. Que dependa de la
    /// definición y no del estado es lo que la hace predecible: la misma fila hace siempre lo mismo.
    /// </summary>
    private void PrimaryAction()
    {
        if (Selected is not { } row || row.IsSeparator) return;
        if (row.HasCommand) LaunchRow(row);
        else if (row.HasPort) OpenInBrowser();
    }

    private void LaunchRow(Row row)
    {
        if (PoolOf(row) is not { } pool) return;
        if (row.PoolIndex < 0 || row.PoolIndex >= pool.Entries.Count) return;

        var result = ServiceLauncher.Launch(pool.Entries[row.PoolIndex]);
        if (result == LaunchResult.Ok) return;

        MessageBox.Show(LaunchErrorText(result, row), Loc.T("Services.WindowTitle"),
            MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private static string LaunchErrorText(LaunchResult result, Row row) => result switch
    {
        LaunchResult.NoCommand => Loc.T("Services.ErrNoCommand"),
        LaunchResult.NoWorkDir => Loc.T("Services.ErrNoWorkDir"),
        LaunchResult.WorkDirMissing => string.Format(Loc.T("Services.ErrWorkDirMissing"), row.WorkDir),
        LaunchResult.NoNetwork => Loc.T("Services.ErrTokenNoIp"),
        LaunchResult.NoPortToken => Loc.T("Services.ErrTokenNoPort"),
        _ => Loc.T("Services.ErrNoCommand"),
    };

    /// <summary>
    /// "Levantar lo básico": dispara los SERVIDORES (los que declaran puerto) que no estén escuchando
    /// ya. Las tareas sueltas (sin puerto) quedan afuera a propósito — ver ServiceEntry: un `npm ci`
    /// no tiene que salir disparado porque re-presionaste el atajo.
    /// Incluye las HEREDADAS: si el espacio define el docker compartido, "levantar lo básico" del
    /// contexto tiene que levantarlo — para eso se hereda.
    /// Devuelve cuántos lanzó (0 = ya estaba todo arriba, o no hay nada que levantar).
    ///
    /// Se juntan TODOS primero y se lanzan de UNA (<see cref="ServiceLauncher.LaunchMany"/>), no de a
    /// uno adentro del lazo. No es un detalle de estilo: uno por uno abría una VENTANA de terminal por
    /// servicio en vez de una ventana con una pestaña por servicio — el porqué (la creación asíncrona
    /// de ventanas de wt.exe) está en LaunchMany.
    /// </summary>
    public int LaunchMissing()
    {
        var listening = TcpPortInfo.ListeningPorts();
        var pending = new List<ServiceEntry>();

        foreach (var pool in new[] { _pool, _parentPool, _globalPool })
        {
            if (pool is null) continue;
            foreach (var s in pool.Entries)
            {
                if (!ServiceLauncher.IsGroupLaunchable(s)) continue;

                if (s.Port > 0)
                {
                    if (listening.Contains(s.Port)) continue;  // ya está arriba → no duplicamos
                }
                else
                {
                    // SIN PUERTO no hay forma de saber si ya corre (es la misma limitación de siempre:
                    // el PID no sirve, wt.exe delega en su monarca). Si no hiciéramos nada, machacar
                    // el atajo te spawnearía un `queue:work` nuevo por cada pulsación.
                    // Mitigación: recordamos lo que ya disparó ESTA ventana. Cubre el caso real
                    // (re-press seguido); cerrar y reabrir vuelve a permitirlo, que es lo que querés
                    // si cerraste a propósito para relevantar.
                    string key = s.Command.Trim() + " " + s.WorkDir.Trim();
                    if (!_groupLaunchedPortless.Add(key)) continue;
                }

                pending.Add(s);
            }
        }

        return ServiceLauncher.LaunchMany(pending);
    }

    private void LaunchMissingWithFeedback()
    {
        int n = LaunchMissing();
        // Silencio cuando SÍ hizo algo: las terminales que aparecen ya son el feedback. El aviso es
        // sólo para el caso mudo — si no, "no pasó nada" se lee igual que "está roto".
        if (n == 0)
            MessageBox.Show(Loc.T("Services.NothingToLaunch"), Loc.T("Services.WindowTitle"),
                MessageBoxButton.OK, MessageBoxImage.Information);
    }

    /// <summary>
    /// VISIT: abre http://localhost:PUERTO en el browser (botón, Shift+Enter, y el Enter de una
    /// entrada sin comando).
    ///
    /// Se abre SIN chequear que el puerto esté escuchando, a propósito. El caso más común de "visit"
    /// es justo después de lanzar, cuando el server TODAVÍA está booteando: bloquear ahí con un
    /// "no está escuchando" sería un falso negativo cada vez. Y si de verdad no levantó, el error del
    /// browser lo dice mejor que un MessageBox nuestro.
    /// </summary>
    private void OpenInBrowser()
    {
        if (Selected is not { } row || row.IsSeparator) return;
        if (!row.HasPort) { NeedsPort(); return; }

        IntPtr monitor = WindowMethods.MonitorOf(new WindowInteropHelper(this).Handle);
        PathOpener.Open(row.LocalhostUrl, monitor);
        Close();
    }

    /// <summary>Sin puerto no hay URL que abrir — se explica en vez de dejar el botón mudo.</summary>
    private void NeedsPort() =>
        MessageBox.Show(Loc.T("Services.NeedsPort"), Loc.T("Services.WindowTitle"),
            MessageBoxButton.OK, MessageBoxImage.Information);

    private void CopyLocalhost()
    {
        if (Selected is not { } row || !row.HasPort) return;
        TryCopy(row.LocalhostUrl);
    }

    private void CopyNetwork()
    {
        if (Selected is not { } row || !row.HasPort) return;
        if (_networkIp is null) { NoNetwork(); return; }
        TryCopy(NetworkUrl(row));
    }

    private void ShowQr()
    {
        if (Selected is not { } row || !row.HasPort) return;
        if (_networkIp is null) { NoNetwork(); return; }
        string url = NetworkUrl(row);
        new QrWindow(url, $"{row.Title} — {url}") { Owner = this }.ShowDialog();
    }

    private void NoNetwork() =>
        MessageBox.Show(Loc.T("Services.NoNetworkMsg"), Loc.T("Services.WindowTitle"),
            MessageBoxButton.OK, MessageBoxImage.Warning);

    /// <summary>URL del servicio con la IP de red (para entrar desde otro dispositivo de la LAN).</summary>
    private string NetworkUrl(Row row) => $"http://{_networkIp}:{row.Port}";

    private static void TryCopy(string text)
    {
        try { Clipboard.SetText(text); } catch { /* el portapapeles a veces está tomado por otro proceso */ }
    }

    // ── Alta / edición / borrado (SÓLO sobre el scope primario) ────────────────

    private void AddNew()
    {
        var entry = ServiceEditWindow.Show(this, Loc.T("Services.DlgNewTitle"), _pool.Label,
                                           ports: _ports);
        if (entry is null) return;
        _pool.Add(entry.Title, entry.Command, entry.WorkDir, entry.Port, entry.AutoStart);
        RefreshList();
    }

    private void EditSelected()
    {
        if (Selected is not { } row || row.IsSeparator) return;
        if (!row.IsOwn) { InheritedReadOnly(); return; }
        if (row.PoolIndex < 0 || row.PoolIndex >= _pool.Entries.Count) return;

        // Se pasa la entry VIVA de la pool (no una copia): el registro de puertos la excluye por
        // referencia para que re-guardar sin tocar el puerto no se choque consigo mismo.
        var entry = ServiceEditWindow.Show(this, Loc.T("Services.DlgEditTitle"), _pool.Label,
                                           _pool.Entries[row.PoolIndex], _ports);
        if (entry is null) return;
        _pool.Update(row.PoolIndex, entry.Title, entry.Command, entry.WorkDir, entry.Port, entry.AutoStart);
        RefreshList();
    }

    private void DeleteSelected()
    {
        if (Selected is not { } row || row.IsSeparator) return;
        if (!row.IsOwn) { InheritedReadOnly(); return; }
        _pool.Delete(row.PoolIndex);
        RefreshList();
    }

    /// <summary>
    /// Editar/borrar una heredada desde acá le cambiaría el servicio a TODOS los contextos que la ven
    /// — la misma trampa que el predeterminado por entrada que ya se sacó del modelo. Se explica en
    /// vez de dejar el botón mudo: un botón que no hace nada se lee igual que un botón roto.
    /// </summary>
    private void InheritedReadOnly() =>
        MessageBox.Show(Loc.T("Services.InheritedReadOnly"), Loc.T("Services.WindowTitle"),
            MessageBoxButton.OK, MessageBoxImage.Information);
}
