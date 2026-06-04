using System;
using System.Collections.Generic;

namespace AmpzDesktopBooster.Services.Tasks;

/// <summary>
/// Una tarea traída de un gestor externo (Vikunja, JIRA…), ya normalizada. Agnóstica del proveedor:
/// la UI la muestra sin saber de dónde vino. Identifier es el código corto y lindo para mostrar
/// (ej. "VKJ-123" en JIRA, o el identifier de Vikunja); si el provider no lo trae, queda "".
/// Mismo espíritu que UsageGauge: un record inmutable que cruza a la UI.
/// </summary>
public sealed record TaskItem(
    string Id,
    string Title,
    string Identifier,
    bool Done,
    DateTimeOffset? DueDate,
    int Priority,
    string? Project,
    string? Url);

/// <summary>
/// Resultado de pedirle las tareas a un provider. Mismo contrato que UsageSnapshot: NUNCA se tira,
/// ante cualquier fallo se devuelve Failed con un mensaje listo para la UI. Ok = sin error.
/// El llamador (la pantalla de Config o, mañana, el timer de la barra) no envuelve en try/catch.
/// </summary>
public sealed class TaskFetchResult
{
    /// <summary>Id del provider que generó este resultado (ej. "vikunja").</summary>
    public required string ProviderId { get; init; }

    /// <summary>Las tareas abiertas del usuario, en orden. Vacío si hubo error.</summary>
    public required IReadOnlyList<TaskItem> Items { get; init; }

    /// <summary>Cuándo se trajo (para el "actualizado hace X").</summary>
    public DateTimeOffset FetchedAt { get; init; }

    /// <summary>null = todo OK. Si no, mensaje listo para mostrarle al usuario.</summary>
    public string? Error { get; init; }

    public bool Ok => Error is null;

    /// <summary>Resultado de error: sin tareas, con un mensaje para la UI.</summary>
    public static TaskFetchResult Failed(string providerId, string error) => new()
    {
        ProviderId = providerId,
        Items = Array.Empty<TaskItem>(),
        Error = error,
        FetchedAt = DateTimeOffset.Now,
    };

    /// <summary>Resultado OK con las tareas traídas.</summary>
    public static TaskFetchResult Success(string providerId, IReadOnlyList<TaskItem> items) => new()
    {
        ProviderId = providerId,
        Items = items,
        FetchedAt = DateTimeOffset.Now,
    };
}
