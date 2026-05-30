using System;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace AmpzDesktopBooster.Services.Usage;

/// <summary>
/// Dueño del polling de uso de tokens de IA. Crea el provider según settings, dispara el PRIMER
/// fetch ("el tiro") apenas arranca la app y refresca en un timer propio (es red → lento).
///
/// Vive en el ARRANQUE CORE (App.OnStartup), NO en la UI: así el tiro inicial está garantizado
/// aunque la barra tarde, falle, o un día no exista. La UI (BarWindow) es un mero CONSUMIDOR —
/// se suscribe a <see cref="Updated"/> y lee <see cref="Latest"/> para pintar. Mismo espíritu que
/// SystemMonitor o DesktopChangeListener: el dato lo produce un servicio, la barra sólo lo muestra.
///
/// Se construye en el hilo de UI (OnStartup corre ahí): el DispatcherTimer queda atado al
/// Dispatcher de UI y el evento <see cref="Updated"/> se dispara en ese hilo → los consumidores
/// pueden tocar controles sin marshalling.
/// </summary>
public sealed class UsageService : IDisposable
{
    private readonly IUsageProvider _provider;
    private readonly DispatcherTimer _timer;

    /// <summary>
    /// Último snapshot recibido (null hasta el primer fetch). Permite que un consumidor que se
    /// engancha DESPUÉS del primer tiro pinte de una, sin esperar al próximo refresco.
    /// </summary>
    public UsageSnapshot? Latest { get; private set; }

    /// <summary>Se dispara en el hilo de UI cada vez que llega un snapshot nuevo.</summary>
    public event Action<UsageSnapshot>? Updated;

    public UsageService(UsageSettings? settings = null)
    {
        settings ??= UsageSettings.Load();

        _provider = settings.Provider switch
        {
            // Hoy sólo Claude. Cuando haya más proveedores, el factory crece acá (un case por id).
            _ => new ClaudeUsageProvider(),
        };

        _timer = new DispatcherTimer
        {
            // El endpoint tiene rate limit (429 si abusás) y el uso no cambia rápido → mínimo 15s.
            Interval = TimeSpan.FromSeconds(Math.Max(15, settings.RefreshSeconds)),
        };
        _timer.Tick += async (_, _) => await RefreshAsync();
    }

    /// <summary>Arranca el timer y dispara el PRIMER fetch inmediato (el "tiro" del arranque).</summary>
    public void Start()
    {
        _timer.Start();
        _ = RefreshAsync(); // primera carga ya, sin esperar el primer tick
    }

    /// <summary>
    /// Trae el snapshot (el provider NUNCA tira: ante fallo devuelve UsageSnapshot.Failed) y avisa.
    /// El await vuelve al hilo de UI (SynchronizationContext del Dispatcher capturado al invocar
    /// desde Start/Tick), así <see cref="Updated"/> se dispara en UI y el consumidor toca controles.
    /// </summary>
    public async Task RefreshAsync()
    {
        var snap = await _provider.GetUsageAsync();
        Latest = snap;
        Updated?.Invoke(snap);
    }

    public void Dispose() => _timer.Stop();
}
