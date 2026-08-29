using System;

namespace AmpzDesktopBooster.Services.Tasks;

/// <summary>
/// Una CUENTA de tareas: una credencial concreta contra un gestor concreto. El usuario (un consultor
/// que trabaja con varios clientes) puede tener N cuentas activas a la vez — dos Trellos distintos,
/// un JIRA y un Vikunja, lo que sea. El fetch las pide TODAS en paralelo y agrega.
///
/// Por qué un solo TaskAccount con tres sub-objetos opcionales en vez de jerarquía polimórfica:
/// la persistencia es JSON plano (igual que el resto del proyecto); los polimórficos en
/// System.Text.Json necesitan TypeDiscriminator/converters, ruido innecesario para 4 kinds. Sólo el
/// sub-objeto que matchea Kind tiene valor — los otros quedan null y no se persisten.
///
/// Id es un guid estable: la UI referencia cuentas por Id, no por DisplayName (que el usuario edita).
/// Borrar una cuenta no rompe tareas ya pickeadas — éstas guardaron AccountId/AccountName como
/// snapshot inmutable; lo único que pierden es la posibilidad de refrescarse.
/// </summary>
public sealed class TaskAccount
{
    /// <summary>Guid estable. La UI referencia cuentas por este Id, no por DisplayName.</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>Qué gestor: "vikunja" | "jira" | "trello" | "clickup". Define qué sub-objeto de credenciales aplica.</summary>
    public string Kind { get; set; } = "vikunja";

    /// <summary>Nombre que ve el usuario (ej. "Trello — Cliente Coca"). Editable.</summary>
    public string DisplayName { get; set; } = "";

    /// <summary>Si false, la cuenta se ignora en el fetch (sin borrar credenciales).</summary>
    public bool Enabled { get; set; } = true;

    // Solo uno de estos tiene valor según Kind — el resto queda null y no se persiste.
    public VikunjaSettings? Vikunja { get; set; }
    public JiraSettings? Jira { get; set; }
    public TrelloSettings? Trello { get; set; }
    public ClickUpSettings? ClickUp { get; set; }
}
