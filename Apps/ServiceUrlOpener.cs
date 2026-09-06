using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
using AmpzDesktopBooster.Desktops;
using AmpzDesktopBooster.Interop;

namespace AmpzDesktopBooster.Apps;

/// <summary>
/// Abre las URLs de un servicio (<c>ServiceEntry.Url</c>) — la contracara de
/// <see cref="ServiceLauncher"/>: aquél levanta procesos, éste abre pestañas.
///
/// EL PROBLEMA QUE RESUELVE, Y POR QUÉ NO ES UN <c>PathOpener.Open</c> PELADO: en "levantar todo" la
/// URL y el comando que la sirve salen en el MISMO gesto, y un dev server tarda entre 3 y 15 segundos
/// en escuchar. Abrir la pestaña en el instante 0 te deja SIEMPRE con un ERR_CONNECTION_REFUSED que
/// hay que refrescar a mano — o sea, la feature andaría "bien" y en la práctica nunca serviría.
///
/// La cura NO es esperar N segundos fijos: eso es arreglar una carrera con timing, que en este repo
/// ya sabemos que no se sostiene (ver el bug del z-order en el CLAUDE.md). Se espera contra una SEÑAL
/// REAL —que el puerto empiece a escuchar—, que es exactamente la misma que ya alimenta el 🟢/⚪ de la
/// ventana de Servicios.
///
/// A qué puerto esperarle lo decide <see cref="WaitPort"/>, y el caso que hay que tener en la cabeza
/// es el de la entrada de SOLO URL: no declara puerto (el servidor que la sirve es OTRA entrada), así
/// que el puerto sale de la propia dirección. Sin eso la feature entera no sirve — ver ahí el porqué.
///
/// POR QUÉ ES ESTÁTICO Y NO VIVE EN LA VENTANA: el re-press del atajo LANZA Y CIERRA la ventana de
/// Servicios (ver HotkeyRouter.ShowServices). Una espera que colgara de la ventana moriría con ella,
/// justo en el camino más usado. El timer cuelga del Dispatcher de la aplicación y sobrevive.
/// </summary>
public static class ServiceUrlOpener
{
    /// <summary>Cada cuánto se re-pregunta qué puertos escuchan. Barato: no toca red.</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(600);

    /// <summary>
    /// Cuánto se le da a un servidor para levantar antes de abrir igual. Pasado el plazo la URL se
    /// abre AUNQUE el puerto siga mudo, a propósito: rendirse en silencio dejaría "levantar todo"
    /// incompleto sin que nada lo diga, y la página de error del browser explica que el servicio no
    /// levantó mejor que cualquier cartel nuestro (mismo criterio que el botón Visitar).
    /// </summary>
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(90);

    /// <summary>
    /// La URL LISTA para abrir: expande los tokens (<c>{port}</c> / <c>{ip}</c>, los MISMOS del
    /// comando) y le antepone el esquema si no lo trae. "" si la entrada no tiene URL.
    ///
    /// Los tokens valen acá por la misma razón que en el comando y contra la misma deriva: el puerto
    /// ya vive en el servicio, así que re-tipearlo dentro de la URL crea una segunda fuente de verdad
    /// que el día que muevas el puerto te va a abrir el server viejo. Con <c>localhost:{port}/platform</c>
    /// la URL sigue al puerto sola.
    ///
    /// La IP se resuelve sólo si de verdad hay tokens: escanear las interfaces de red para armar una
    /// URL que no los usa sería trabajo al pedo (mismo cuidado que en ServiceLauncher).
    /// </summary>
    public static string Resolve(string url, int port)
    {
        url = url.Trim();
        if (url == "") return "";

        if (CommandTokens.HasTokens(url))
        {
            string? ip = Services.LocalIp.Get();
            CommandTokens.Expand(url, port, ip, out url);
        }

        // Un token que no se pudo resolver deja "{port}" adentro del texto y eso ya no parsea como
        // URL: se devuelve tal cual, sin esquema. Abrirla va a fallar visiblemente, que es lo que
        // corresponde — mejor eso que inventar un http:// delante de algo que no es una dirección.
        return UrlHelper.IsUrl(url) ? UrlHelper.Normalize(url) : url;
    }

    /// <summary>
    /// A QUÉ PUERTO hay que esperarle antes de abrir esta URL — 0 = no hay nada que esperar, se abre ya.
    ///
    /// ⚠ ESTE MÉTODO ES EL CORAZÓN DE LA FEATURE, no un detalle: sin él la URL se abre contra un
    /// servidor que todavía está booteando y te comés un ERR_CONNECTION_REFUSED en cada "levantar todo".
    ///
    /// El puerto sale de DOS fuentes, en este orden:
    ///   1. El que DECLARA la entrada. Es el más explícito y el que además le da el estado 🟢/⚪.
    ///   2. Si no declara ninguno, el de la PROPIA URL. Y éste es el caso que importa: la entrada de
    ///      SOLO URL ("abrime localhost:6080/platform") no tiene por qué repetir el 6080 en el campo
    ///      Puerto —el servidor que lo sirve es OTRA entrada, la que trae el comando—, así que si nos
    ///      quedáramos en el punto 1 la URL saldría disparada en el instante 0. La dirección YA dice
    ///      contra qué puerto vas a golpear: usarla es leer el dato que el usuario ya escribió, no
    ///      adivinarlo.
    ///
    /// Sólo se espera si el host es LOCAL (esta máquina). Una URL a un Jira o a un staging remoto no
    /// depende de nada que estemos levantando: esperarle a su :443 sería quedarse mirando un puerto
    /// que en esta máquina no va a abrir NUNCA, y abrir recién al vencer el plazo.
    /// </summary>
    public static int WaitPort(string resolvedUrl, int declaredPort)
    {
        if (declaredPort > 0) return declaredPort;
        if (!Uri.TryCreate(resolvedUrl, UriKind.Absolute, out var uri)) return 0;
        return IsLocalHost(uri.Host) ? uri.Port : 0;  // Uri completa el 80/443 del esquema
    }

    /// <summary>¿El host apunta a ESTA máquina? (loopback, comodín, o su propia IP de LAN)</summary>
    private static bool IsLocalHost(string host) =>
        host is "localhost" or "127.0.0.1" or "0.0.0.0" or "::1" or "[::1]"
        || string.Equals(host, Services.LocalIp.Get(), StringComparison.Ordinal);

    /// <summary>
    /// Abre <paramref name="url"/>, esperándole al puerto que corresponda (ver <see cref="WaitPort"/>)
    /// o hasta el <see cref="Deadline"/>.
    /// </summary>
    public static void Open(string url, int port)
    {
        url = Resolve(url, port);
        if (url == "") return;

        int wait = WaitPort(url, port);
        if (wait <= 0 || TcpPortInfo.ListeningPorts().Contains(wait))
        {
            PathOpener.Open(url);
            return;
        }

        WaitAndOpen(new List<(string Url, int Port)> { (url, wait) });
    }

    /// <summary>
    /// Abre VARIAS de una (el arranque grupal). Las que ya tienen su servidor arriba —o que no
    /// dependen de ninguno— salen ahora mismo; el resto queda en UNA sola espera compartida.
    ///
    /// Una espera compartida y no una por URL no es un ahorro de timers: con N timers cada uno
    /// consultaría la tabla TCP por su cuenta, y sobre todo cada uno abriría su pestaña en un
    /// instante distinto — las pestañas terminarían en un orden que no es el que declaraste.
    ///
    /// El ORDEN es el del catálogo, y se respeta también entre las que esperan: el tick abre lo que
    /// esté listo recorriendo la lista de arriba a abajo, así que dos servidores que levanten en el
    /// mismo tick abren sus pestañas en el orden en que los cargaste, no en el que ganaron la carrera.
    /// </summary>
    public static void OpenAll(IEnumerable<(string Url, int Port)> urls)
    {
        var pending = new List<(string Url, int Port)>();
        var listening = TcpPortInfo.ListeningPorts();

        foreach (var (rawUrl, port) in urls)
        {
            string url = Resolve(rawUrl, port);
            if (url == "") continue;

            int wait = WaitPort(url, port);
            if (wait <= 0 || listening.Contains(wait)) PathOpener.Open(url);
            else pending.Add((url, wait));
        }

        if (pending.Count > 0) WaitAndOpen(pending);
    }

    /// <summary>
    /// Poll del listado de puertos hasta que cada URL pendiente tenga el suyo arriba. Corre en el
    /// thread de UI (el Dispatcher de la app): <c>GetActiveTcpListeners</c> es barato y sincrónico,
    /// igual que en el timer de estado de la ventana de Servicios.
    /// </summary>
    private static void WaitAndOpen(List<(string Url, int Port)> pending)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            // Sin aplicación WPF viva no hay dónde colgar la espera: abrimos ya y que sea lo que sea.
            foreach (var (url, _) in pending) PathOpener.Open(url);
            return;
        }

        var start = DateTime.UtcNow;
        var timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
        {
            Interval = PollInterval,
        };

        timer.Tick += (_, _) =>
        {
            bool expired = DateTime.UtcNow - start >= Deadline;
            var listening = TcpPortInfo.ListeningPorts();

            // Se recorre en orden y se abre lo que esté listo: el que levanta primero abre primero.
            var ready = pending.Where(p => expired || listening.Contains(p.Port)).ToList();
            foreach (var (url, _) in ready) PathOpener.Open(url);
            pending.RemoveAll(p => ready.Contains(p));

            if (pending.Count == 0) timer.Stop();
        };

        timer.Start();
    }
}
