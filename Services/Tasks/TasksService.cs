using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace AmpzDesktopBooster.Services.Tasks;

/// <summary>
/// Punto de orquestación de tareas multi-cuenta. Resuelve QUÉ ITaskProvider corresponde a cada
/// TaskAccount según su Kind, y agrega los fetches de TODAS las cuentas habilitadas en paralelo.
///
/// "En paralelo + aislado por cuenta" es clave: con un solo provider, un 401 te apagaba todo. Con N
/// cuentas, una falla NO debe tumbar al resto — el aggregator junta lo que sí pudo y reporta las que
/// fallaron por separado, así el llamador (HotkeyRouter / ConfigWindow) decide qué hacer.
///
/// Por ahora NO hay polling: el fetch arranca por demanda (Win+NumLock o "Probar conexión"). El
/// cableado al arranque core (polling + feedback al "setear proyecto") llega en una tanda futura.
/// </summary>
public static class TasksService
{
    /// <summary>
    /// Instancia el provider concreto para una cuenta, o null si no tiene el bloque de credenciales
    /// que su Kind requiere (cuenta a medio configurar). El llamador decide cómo reportarlo.
    /// </summary>
    public static ITaskProvider? CreateProvider(TaskAccount account) => account.Kind switch
    {
        "vikunja" => account.Vikunja is null ? null : new VikunjaTaskProvider(account, account.Vikunja),
        "jira"    => account.Jira    is null ? null : new JiraTaskProvider(account, account.Jira),
        "trello"  => account.Trello  is null ? null : new TrelloTaskProvider(account, account.Trello),
        _         => null,
    };

    /// <summary>
    /// Pide tareas a TODAS las cuentas habilitadas en paralelo (Task.WhenAll). Cada resultado queda
    /// pegado a su TaskAccount: el llamador puede separar éxitos de fallos sin perder de qué cuenta
    /// vino cada uno.
    ///
    /// Una cuenta sin credenciales (CreateProvider devuelve null) NO se omite silenciosamente:
    /// devuelve un Failed con mensaje claro, así el usuario se entera de que su cuenta está incompleta.
    /// </summary>
    public static async Task<IReadOnlyList<AccountFetchResult>> FetchAllAsync(
        TasksSettings settings, CancellationToken ct = default)
    {
        var enabled = settings.Accounts.Where(a => a.Enabled).ToList();
        if (enabled.Count == 0)
            return System.Array.Empty<AccountFetchResult>();

        // Lanzamos todos en paralelo — el ConfigureAwait(false) lo ponen los providers internamente.
        var tasks = enabled.Select(async a =>
        {
            var provider = CreateProvider(a);
            if (provider is null)
                return new AccountFetchResult(a, TaskFetchResult.Failed(
                    a.Id, $"Cuenta '{a.DisplayName}' sin credenciales para {a.Kind}."));
            var r = await provider.GetOpenTasksAsync(ct).ConfigureAwait(false);
            return new AccountFetchResult(a, r);
        });

        var all = await Task.WhenAll(tasks).ConfigureAwait(false);
        return all;
    }
}

/// <summary>
/// Resultado de UNA cuenta dentro del fetch agregado. Mantiene la TaskAccount original (no sólo el
/// Id) para que el llamador pueda mostrar el DisplayName en mensajes sin tener que recruzar contra
/// la lista de cuentas.
/// </summary>
public sealed record AccountFetchResult(TaskAccount Account, TaskFetchResult Result);
