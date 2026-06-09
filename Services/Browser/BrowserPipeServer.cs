using System;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace AmpzDesktopBooster.Services.Browser;

/// <summary>
/// Transporte del shim de navegador entre instancias de la app. Cuando Windows lanza el .exe con una
/// URL (porque la app es el navegador elegido) y YA hay una instancia corriendo, esa segunda
/// instancia NO debe montar otra app: escribe la URL a este pipe y muere. La instancia PRIMARIA la
/// recibe acá y la reenvía al navegador real (<see cref="BrowserShim.OpenInBrave"/>) — abriéndola en
/// SU escritorio actual, que es el del usuario.
///
/// Mismo espíritu que <see cref="Attention.AttentionPipeServer"/>: solo transporte, marshaleo a UI por
/// el Dispatcher, y NUNCA se cae (cualquier basura entrante se descarta y seguimos escuchando). El
/// mensaje es la URL cruda (una línea de texto) — no hace falta JSON para un único string.
/// </summary>
public sealed class BrowserPipeServer : IDisposable
{
    /// <summary>Nombre del pipe. Separado del de atención: responsabilidades distintas, contratos distintos.</summary>
    public const string PipeName = "AmpzDesktopBooster.openurl";

    private readonly Dispatcher _ui;
    private readonly CancellationTokenSource _cts = new();

    /// <summary>Se dispara EN EL HILO DE UI por cada URL recibida.</summary>
    public event Action<string>? UrlReceived;

    public BrowserPipeServer()
    {
        _ui = Dispatcher.CurrentDispatcher;
    }

    /// <summary>Arranca el loop de aceptación en segundo plano. No bloquea el arranque.</summary>
    public void Start() => _ = AcceptLoopAsync(_cts.Token);

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(
                    PipeName, PipeDirection.In, maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await server.WaitForConnectionAsync(ct).ConfigureAwait(false);

                using var reader = new StreamReader(server);
                string url = (await reader.ReadToEndAsync(ct).ConfigureAwait(false)).Trim();

                if (!string.IsNullOrWhiteSpace(url))
                    _ = _ui.BeginInvoke(() => UrlReceived?.Invoke(url));
            }
            catch (OperationCanceledException) { break; } // Dispose() en curso → salir limpio
            catch { /* conexión rota / lo que sea: el transporte NUNCA voltea la app */ }
        }
    }

    /// <summary>
    /// Lado CLIENTE: la instancia secundaria conecta al pipe de la primaria, escribe la URL y cierra.
    /// Fire-and-forget con timeout corto: si la primaria no responde rápido, devolvemos false y el
    /// caller decide (típicamente: abrir la URL por su cuenta antes de salir). Estático: no necesita
    /// montar nada de la app.
    /// </summary>
    public static bool SendUrl(string url, int timeoutMs = 2000)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(timeoutMs);
            using var writer = new StreamWriter(client);
            writer.Write(url);
            writer.Flush();
            return true;
        }
        catch { return false; }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}
