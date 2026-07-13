using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using AmpzDesktopBooster.Desktops;
using AmpzDesktopBooster.Interop;
using AmpzDesktopBooster.Persistence;
using AmpzDesktopBooster.Services;
using AmpzDesktopBooster.Services.Localization;

namespace AmpzDesktopBooster;

/// <summary>
/// Puertos / Servicios locales — la Win+Numpad+ (Add). Lista GLOBAL única de apps web que corrés
/// en la máquina (título + puerto). Yo (la app) me encargo del resto: armo las URLs, detecto si el
/// puerto está ESCUCHANDO ahora (🟢/⚪, en vivo), auto-completo el título con el proceso dueño, y
/// dejo copiar/abrir en dos formas:
///   Enter / doble-clic → abrir http://localhost:PUERTO en el browser
///   Ctrl+C             → copiar la URL con localhost
///   Ctrl+Shift+C       → copiar la URL con la IP de red (para entrar desde otro dispositivo)
///   F2 / ✎             → renombrar el título
///   Supr               → borrar la entrada
///   🔳 QR              → QR de la URL-de-red (escaneás con el celu y entrás)
///
/// Instancia única (la maneja el router): re-presionar Win++ con la ventana abierta la trae al frente.
/// El estado vivo se refresca solo cada pocos segundos SIN rebuildear la lista (para no pisar la
/// selección ni el filtro): el timer sólo toca la propiedad IsListening de cada fila, que dispara el
/// binding del puntito. Mismo criterio que el resto del repo: barato, sin red, sin bloquear la UI.
/// </summary>
public partial class PortsWindow : Window
{
    /// <summary>Fila visible. Observable: el timer de estado actualiza el puntito en el lugar.</summary>
    private sealed class Row : INotifyPropertyChanged
    {
        public required int StoreIndex { get; init; } // índice a la entry real en el PortStore
        public required string Title { get; init; }
        public required int Port { get; init; }

        private bool _isListening;
        public bool IsListening
        {
            get => _isListening;
            set
            {
                if (_isListening == value) return;
                _isListening = value;
                // El puntito de estado (Ellipse en el XAML) liga a IsListening por DataTrigger.
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsListening)));
            }
        }

        public string LocalhostUrl => $"http://localhost:{Port}";

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private readonly PortStore _store;
    private readonly ObservableCollection<Row> _rows = new();
    private readonly DispatcherTimer _statusTimer;
    private string? _networkIp; // IP LAN cacheada; se resuelve al abrir (puede ser null: sin red)

    public PortsWindow(PortStore store)
    {
        InitializeComponent();
        _store = store;

        Icon = AppIcon.TryLoadForWindow();
        _networkIp = LocalIp.Get();
        SubHeaderText.Text = _networkIp is null
            ? Loc.T("Ports.NoNetwork")
            : string.Format(Loc.T("Ports.NetworkIp"), _networkIp);

        PortList.ItemsSource = _rows;
        RefreshList();

        FilterBox.TextChanged += (_, _) => RefreshList();
        FilterBox.PreviewKeyDown += OnFilterKeyDown;
        PortList.PreviewKeyDown += OnListKeyDown;
        PortList.MouseDoubleClick += (_, _) => OpenSelected();

        AddBtn.Click += (_, _) => AddNew();
        EditBtn.Click += (_, _) => RenameSelected();
        DeleteBtn.Click += (_, _) => DeleteSelected();
        CopyLocalBtn.Click += (_, _) => CopyLocalhost();
        CopyIpBtn.Click += (_, _) => CopyNetwork();
        QrBtn.Click += (_, _) => ShowQr();
        CloseBtn.Click += (_, _) => Close();

        // Estado vivo: cada 2.5s recomputamos qué puertos escuchan y actualizamos los puntitos.
        // GetActiveTcpListeners es barato y no toca red → lo podemos correr en el UI thread sin drama.
        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
        _statusTimer.Tick += (_, _) => RefreshStatus();
        _statusTimer.Start();

        Closed += (_, _) => _statusTimer.Stop();
        Loaded += (_, _) => FilterBox.Focus();
    }

    // ── Lista ───────────────────────────────────────────────────────────────────

    /// <summary>Reconstruye las filas desde el store (add/delete/rename/filtro). Recalcula el estado.</summary>
    private void RefreshList()
    {
        string filter = FilterBox.Text.Trim();
        var listening = TcpPortInfo.ListeningPorts();

        _rows.Clear();
        var entries = _store.Entries;
        // Orden alfabético por título (mismo criterio que Variables) para que la lista sea estable.
        var indexed = entries
            .Select((e, i) => (e, i))
            .Where(t => filter == "" || t.e.Title.Contains(filter, StringComparison.OrdinalIgnoreCase)
                        || t.e.Port.ToString().Contains(filter))
            .OrderBy(t => t.e.Title, StringComparer.CurrentCultureIgnoreCase);

        foreach (var (e, i) in indexed)
            _rows.Add(new Row { StoreIndex = i, Title = e.Title, Port = e.Port, IsListening = listening.Contains(e.Port) });

        if (_rows.Count > 0)
            PortList.SelectedIndex = 0;
    }

    /// <summary>Sólo actualiza los puntitos 🟢/⚪ en el lugar — NO rebuildea (no pisa selección/filtro).</summary>
    private void RefreshStatus()
    {
        var listening = TcpPortInfo.ListeningPorts();
        foreach (var row in _rows)
            row.IsListening = listening.Contains(row.Port);
    }

    private Row? Selected => PortList.SelectedItem as Row;

    // ── Teclado ─────────────────────────────────────────────────────────────────

    private void OnFilterKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)       { OpenSelected(); e.Handled = true; }
        else if (e.Key == Key.Escape) { Close(); e.Handled = true; }
        else if (e.Key == Key.Down && _rows.Count > 0)
        {
            PortList.SelectedIndex = 0;
            PortList.Focus();
            e.Handled = true;
        }
    }

    private void OnListKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:  OpenSelected();   e.Handled = true; break;
            case Key.Escape: Close();          e.Handled = true; break;
            case Key.Delete: DeleteSelected(); e.Handled = true; break;
            case Key.F2:     RenameSelected(); e.Handled = true; break;
            // Ctrl+C copia localhost; Ctrl+Shift+C copia la URL con la IP de red.
            case Key.C when Ctrl && Shift: CopyNetwork();   e.Handled = true; break;
            case Key.C when Ctrl:          CopyLocalhost(); e.Handled = true; break;
        }
    }

    private static bool Shift => (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
    private static bool Ctrl  => (Keyboard.Modifiers & ModifierKeys.Control) != 0;

    // ── Acciones ──────────────────────────────────────────────────────────────

    private void OpenSelected()
    {
        if (Selected is not { } row) return;
        // Reusamos el abridor de URLs del repo (browser en el monitor de ESTA ventana), como Variables.
        IntPtr monitor = WindowMethods.MonitorOf(new WindowInteropHelper(this).Handle);
        PathOpener.Open(row.LocalhostUrl, monitor);
        Close();
    }

    private void CopyLocalhost()
    {
        if (Selected is not { } row) return;
        TryCopy(row.LocalhostUrl);
    }

    private void CopyNetwork()
    {
        if (Selected is not { } row) return;
        if (_networkIp is null)
        {
            MessageBox.Show(Loc.T("Ports.NoNetworkMsg"), Loc.T("Ports.WindowTitle"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        TryCopy(NetworkUrl(row));
    }

    private void ShowQr()
    {
        if (Selected is not { } row) return;
        if (_networkIp is null)
        {
            MessageBox.Show(Loc.T("Ports.NoNetworkMsg"), Loc.T("Ports.WindowTitle"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        string url = NetworkUrl(row);
        new QrWindow(url, $"{row.Title} — {url}") { Owner = this }.ShowDialog();
    }

    /// <summary>URL del servicio con la IP de red (para acceder desde otro dispositivo de la LAN).</summary>
    private string NetworkUrl(Row row) => $"http://{_networkIp}:{row.Port}";

    private static void TryCopy(string text)
    {
        try { Clipboard.SetText(text); } catch { /* el portapapeles a veces está tomado por otro proceso */ }
    }

    private void DeleteSelected()
    {
        if (Selected is not { } row) return;
        _store.Delete(row.StoreIndex);
        RefreshList();
    }

    private void RenameSelected()
    {
        if (Selected is not { } row) return;
        string? title = PromptDialog.Show(this, Loc.T("Ports.DlgRenameTitle"), Loc.T("Ports.DlgTitleLabel"), row.Title);
        if (title is null) return;
        if (title.Trim() == "") return; // título vacío no aporta nada al renombrar
        _store.UpdateTitle(row.StoreIndex, title);
        RefreshList();
    }

    /// <summary>
    /// Sugiere el primer puerto LIBRE desde 6000 para arriba: ni escuchando ahora ni ya registrado en
    /// la lista (así no duplicás una entrada). Es sólo una sugerencia pre-cargada en el input — el
    /// usuario la puede pisar. 6000+ es el rango típico de dev servers (el legacy arrancaba ahí).
    /// </summary>
    private int SuggestNextFreePort()
    {
        var listening = TcpPortInfo.ListeningPorts();
        var used = new HashSet<int>(_store.Entries.Select(e => e.Port));
        for (int p = 6000; p <= 65535; p++)
            if (!listening.Contains(p) && !used.Contains(p))
                return p;
        return 6000; // caso imposible en la práctica (todos los puertos 6000+ ocupados)
    }

    private void AddNew()
    {
        // 1) Puerto (obligatorio). Vos sólo aportás puerto + título; el resto lo armo yo. El input
        //    viene pre-cargado con el próximo puerto libre desde 6000 (podés pisarlo).
        string? portText = PromptDialog.Show(this, Loc.T("Ports.DlgNewTitle"), Loc.T("Ports.DlgPortLabel"),
            SuggestNextFreePort().ToString());
        if (portText is null) return;
        if (!int.TryParse(portText.Trim(), out int port) || port < 1 || port > 65535)
        {
            MessageBox.Show(Loc.T("Ports.InvalidPort"), Loc.T("Ports.WindowTitle"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // 2) Título (opcional). Si lo dejás vacío, lo AUTO-completo con el proceso que escucha ese
        //    puerto ahora mismo (ej. "node", "dotnet", "Code"); si nadie escucha, "Puerto N".
        string? title = PromptDialog.Show(this, Loc.T("Ports.DlgNewTitle"), Loc.T("Ports.DlgTitleOptionalLabel"), "");
        if (title is null) return;
        title = title.Trim();
        if (title == "")
            title = TcpPortInfo.ProcessNameForPort(port) ?? string.Format(Loc.T("Ports.AutoTitle"), port);

        _store.Add(title, port);
        RefreshList();
    }
}
