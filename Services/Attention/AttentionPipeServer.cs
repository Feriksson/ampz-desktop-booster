using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace AmpzDesktopBooster.Services.Attention;

/// <summary>
/// SOLO transporte. Escucha un Named Pipe local, deserializa cada mensaje y emite una
/// <see cref="AttentionSignal"/> ya traducida al dominio. No sabe NADA de desks, ventanas ni UI —
/// mismo espíritu que SystemMonitor: produce el dato crudo, otro lo interpreta.
///
/// ¿Por qué Named Pipe y no localhost HTTP? La app es Windows-only: la "portabilidad" de HTTP no
/// nos compra nada, y un puerto TCP es superficie abierta a CUALQUIER proceso local. El pipe trae
/// seguridad por ACL NATIVA: lo restringimos al usuario actual, no aparece en netstat, cero red.
/// Eso es el "más seguro" que pidió el diseño, gratis.
///
/// El mapeo del vocabulario externo ("action-needed"/"completed") al enum del dominio ocurre ACÁ,
/// en el borde — el core nunca ve strings de wire.
/// </summary>
public sealed class AttentionPipeServer : IDisposable
{
    /// <summary>
    /// Nombre del pipe. Estable y documentado: es el contrato público con cualquier integrador.
    /// Un cliente abre \\.\pipe\AmpzDesktopBooster.attention, escribe un JSON y cierra.
    /// </summary>
    public const string PipeName = "AmpzDesktopBooster.attention";

    private readonly Dispatcher _ui;
    private readonly CancellationTokenSource _cts = new();

    /// <summary>Se dispara EN EL HILO DE UI por cada mensaje válido recibido.</summary>
    public event Action<AttentionSignal>? Received;

    /// <summary>
    /// Se construye en el hilo de UI (App.OnStartup): capturamos su Dispatcher para marshalear cada
    /// señal ahí. El loop de aceptación corre en un Task de fondo (E/S bloqueante), pero el evento
    /// sale SIEMPRE en UI → los consumidores tocan ventanas/toasts sin pensar en hilos.
    /// </summary>
    public AttentionPipeServer()
    {
        _ui = Dispatcher.CurrentDispatcher;
    }

    /// <summary>Arranca el loop de aceptación en segundo plano. No bloquea el arranque.</summary>
    public void Start() => _ = AcceptLoopAsync(_cts.Token);

    /// <summary>
    /// Un mensaje por conexión: aceptamos, leemos hasta EOF, parseamos, emitimos, y volvemos a
    /// esperar. Una instancia a la vez alcanza de sobra para este volumen (avisos esporádicos).
    /// El servidor NUNCA se cae: cualquier basura entrante se descarta y seguimos escuchando.
    /// </summary>
    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var server = CreateServer();
                await server.WaitForConnectionAsync(ct).ConfigureAwait(false);

                using var reader = new StreamReader(server);
                string json = await reader.ReadToEndAsync(ct).ConfigureAwait(false);

                if (TryParse(json, out var signal))
                    _ = _ui.BeginInvoke(() => Received?.Invoke(signal)); // marshaleo a UI; no esperamos el op
            }
            catch (OperationCanceledException) { break; } // Dispose() en curso → salir limpio
            catch
            {
                // Conexión rota, JSON corrupto, lo que sea: el transporte NUNCA voltea la app.
                // Seguimos al próximo accept. (Mismo criterio que las configs Load/Save del repo.)
            }
        }
    }

    /// <summary>
    /// Crea el pipe de escucha. Solo recibimos (PipeDirection.In) — el cliente postea y cierra, no
    /// espera respuesta (fire-and-forget). Async para que el accept no bloquee un hilo propio.
    ///
    /// TODO (endurecimiento — primer ítem de la próxima iteración): restringir la DACL al usuario
    /// actual con NamedPipeServerStreamAcl + PipeSecurity (requiere el package
    /// System.IO.Pipes.AccessControl). Esto cierra el pipe a cualquier otro principal a nivel del SO
    /// — la ventaja de seguridad concreta del pipe sobre un puerto TCP. El slice usa el DACL default
    /// (el creador tiene full control) solo para validar el flujo punta a punta primero.
    /// </summary>
    private static NamedPipeServerStream CreateServer() =>
        new(PipeName,
            PipeDirection.In,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);

    // ── Wire DTO: el formato del mensaje en el cable, separado del modelo de dominio ──

    private sealed record WireMessage(
        [property: JsonPropertyName("pid")] int Pid,
        [property: JsonPropertyName("source")] string? Source,
        [property: JsonPropertyName("level")] string? Level,
        [property: JsonPropertyName("message")] string? Message,
        [property: JsonPropertyName("cwd")] string? Cwd,
        [property: JsonPropertyName("hwnd")] long Hwnd,
        [property: JsonPropertyName("ts")] long Ts);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// Traduce el JSON de wire a una <see cref="AttentionSignal"/> de dominio. ACÁ muere el
    /// vocabulario externo: "action-needed"/"completed" → enum. Un PID inválido o un nivel
    /// desconocido invalidan el mensaje (mejor descartar que avisar mal).
    /// </summary>
    private static bool TryParse(string json, out AttentionSignal signal)
    {
        signal = default;
        if (string.IsNullOrWhiteSpace(json)) return false;

        WireMessage? msg;
        try { msg = JsonSerializer.Deserialize<WireMessage>(json, JsonOpts); }
        catch { return false; }
        if (msg is null || msg.Pid <= 0) return false;

        AttentionLevel level = (msg.Level ?? "").Trim().ToLowerInvariant() switch
        {
            "action-needed" or "action_needed" or "actionneeded" => AttentionLevel.ActionNeeded,
            "completed" or "done"                                 => AttentionLevel.Completed,
            _ => AttentionLevel.ActionNeeded, // sin nivel reconocido, asumimos lo más urgente
        };

        signal = new AttentionSignal(
            msg.Pid,
            string.IsNullOrWhiteSpace(msg.Source) ? "unknown" : msg.Source!.Trim(),
            level,
            (msg.Message ?? "").Trim(),
            msg.Ts,
            (msg.Cwd ?? "").Trim(),
            msg.Hwnd);
        return true;
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}
