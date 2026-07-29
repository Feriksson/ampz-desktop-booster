using System;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace AmpzDesktopBooster.Services;

/// <summary>Foto inmutable de las IPs. null = todavía sin resolver o sin conectividad.</summary>
public readonly record struct IpSnapshot(string? Local, string? Public)
{
    public static readonly IpSnapshot Empty = new(null, null);
}

/// <summary>
/// Vigila las dos IPs de la máquina y AVISA cuando cambian:
///   · LOCAL  — la IPv4 de la LAN (<see cref="LocalIp"/>). Cambia al saltar de WiFi a cable, de red,
///              o cuando el DHCP te renueva. Es local y barata: se resuelve sincrónica.
///   · PÚBLICA — la que ve internet. NO se puede saber sin PREGUNTARLE A ALGUIEN de afuera, así que
///              sale de un GET a un servicio de eco de IP. Cambia al prender/apagar la VPN, al
///              reconectar el módem, o cuando el ISP te reasigna.
///
/// Diseño de la cadencia (importa): la IP pública NO se pollea seguido. Un timer corto contra un
/// servicio de terceros es maleducado y te puede ganar un rate-limit — y además la IP pública casi
/// nunca cambia SOLA. Lo que sí es una señal fuerte de cambio es que el SO reporte un cambio de
/// direccionamiento: por eso el disparador principal es <see cref="NetworkChange.NetworkAddressChanged"/>
/// (VPN arriba/abajo, cambio de red) y el timer de <see cref="PollInterval"/> queda sólo de RED, para
/// cazar el caso raro en que el ISP rota la IP sin que la interfaz local se entere.
///
/// El evento de red se DEBOUNCEA: al conectar una VPN el SO dispara varias notificaciones seguidas y
/// el direccionamiento tarda un momento en asentar — consultar en la primera nos daría la IP VIEJA.
///
/// Todo el trabajo de red corre fuera del hilo de UI; <see cref="Changed"/> se marshalea al
/// Dispatcher que se le pasa, así el consumidor (la barra) puede tocar controles sin pensarlo.
/// </summary>
public sealed class IpMonitor : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan SettleDelay = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan HttpTimeout = TimeSpan.FromSeconds(6);

    /// <summary>
    /// Servicios de eco de IP, en orden de preferencia. Devuelven la IP en TEXTO PLANO (no JSON), que
    /// es justo lo que queremos: cero parsing, cero dependencia de un schema ajeno. Hay más de uno a
    /// propósito — si el primero está caído o te rate-limitea, seguimos con el siguiente en vez de
    /// mostrar "sin datos". Sólo se manda el GET: no viaja NINGÚN dato nuestro.
    /// </summary>
    private static readonly string[] Endpoints =
    {
        "https://api.ipify.org",
        "https://icanhazip.com",
        "https://ifconfig.me/ip",
    };

    private readonly HttpClient _http = new() { Timeout = HttpTimeout };
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _poll;
    private readonly DispatcherTimer _settle;

    // Serializa los refresh: sin esto, el timer y una ráfaga de eventos de red podrían disparar
    // varios GET simultáneos al mismo endpoint (justo lo que dispara un rate-limit).
    private readonly SemaphoreSlim _gate = new(1, 1);

    private bool _started;
    private bool _disposed;

    /// <summary>Último estado conocido. Arranca vacío: la primera resolución es asíncrona.</summary>
    public IpSnapshot Current { get; private set; } = IpSnapshot.Empty;

    /// <summary>
    /// Cambió alguna de las dos IPs. Trae (anterior, actual) para que el consumidor pueda decidir
    /// QUÉ cambió y avisar en consecuencia. Ojo: la PRIMERA resolución también dispara este evento
    /// (pasa de null a un valor) — el consumidor distingue ese caso mirando si el anterior era null,
    /// porque "recién arrancó la app" no es una novedad que valga un aviso.
    /// </summary>
    public event Action<IpSnapshot, IpSnapshot>? Changed;

    public IpMonitor(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _poll = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = PollInterval };
        _poll.Tick += (_, _) => _ = RefreshAsync();

        // One-shot: lo re-arma cada notificación de red; sólo corre cuando la ráfaga se calmó.
        _settle = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = SettleDelay };
        _settle.Tick += (_, _) => { _settle.Stop(); _ = RefreshAsync(); };
    }

    public void Start()
    {
        if (_started || _disposed) return;
        _started = true;

        NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
        _poll.Start();
        _ = RefreshAsync(); // primera resolución YA: la barra no espera 15 minutos para mostrar algo
    }

    /// <summary>
    /// El SO reportó un cambio de direccionamiento. Corre en un hilo del pool, así que TODO lo que
    /// toque timers de UI se marshalea. Re-armamos el debounce: si vienen cinco notificaciones
    /// seguidas (típico al levantar una VPN), consultamos UNA sola vez, cuando dejaron de llegar.
    /// </summary>
    private void OnNetworkAddressChanged(object? sender, EventArgs e)
    {
        if (_disposed) return;
        _dispatcher.BeginInvoke(() =>
        {
            if (_disposed) return;
            // La LOCAL se actualiza YA (es sincrónica y gratis): el feedback inmediato es el que
            // importa cuando enchufás el cable. La pública espera a que la red se asiente.
            Apply(Current with { Local = LocalIp.Get() });
            _settle.Stop();
            _settle.Start();
        });
    }

    /// <summary>
    /// Resuelve ambas IPs y publica el resultado. Best-effort de punta a punta: si la red está
    /// caída, la pública queda como estaba (NO la borramos — mostrar la última conocida es más útil
    /// que un hueco durante un microcorte) y la local pasa a null, que es la verdad.
    /// </summary>
    public async Task RefreshAsync()
    {
        if (_disposed) return;
        if (!await _gate.WaitAsync(0).ConfigureAwait(false)) return; // ya hay uno en curso → este sobra

        try
        {
            string? local = LocalIp.Get();
            string? pub = await FetchPublicAsync().ConfigureAwait(false);

            var next = new IpSnapshot(local, pub ?? Current.Public);
            _ = _dispatcher.BeginInvoke(() => Apply(next)); // fire-and-forget explícito: no esperamos al pintado
        }
        catch
        {
            // Nada de red puede voltear la app ni dejarla sin barra. Reintentamos al próximo tick.
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Publica el snapshot y dispara <see cref="Changed"/> SÓLO si de verdad cambió algo.</summary>
    private void Apply(IpSnapshot next)
    {
        if (_disposed || next == Current) return;
        var prev = Current;
        Current = next;
        Changed?.Invoke(prev, next);
    }

    /// <summary>
    /// Primer endpoint que conteste algo con forma de IP. Validamos el texto (largo acotado + que
    /// parsee como IP) porque un portal cautivo de WiFi público devuelve un HTML de login con 200 OK:
    /// sin este chequeo pintaríamos ese HTML en la barra.
    /// </summary>
    private async Task<string?> FetchPublicAsync()
    {
        foreach (var url in Endpoints)
        {
            try
            {
                var text = (await _http.GetStringAsync(url).ConfigureAwait(false)).Trim();
                if (text.Length is > 0 and <= 45 && System.Net.IPAddress.TryParse(text, out var ip))
                    return ip.ToString();
            }
            catch
            {
                // endpoint caído / sin red / timeout → probamos el siguiente
            }
        }
        return null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_started)
            NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;

        _poll.Stop();
        _settle.Stop();
        _http.Dispose();
        _gate.Dispose();
    }
}
