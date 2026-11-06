using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace AmpzDesktopBooster.Credentials;

/// <summary>
/// Un campo extra de una credencial (API key, PIN, "base de datos"…). <see cref="IsSecret"/> decide
/// si la UI lo enmascara por defecto: no todo lo que acompaña a un usuario es un secreto (un host o
/// un nombre de base se quieren ver de un vistazo), y enmascarar todo entrena a revelar sin mirar.
/// </summary>
public sealed class CustomField
{
    [JsonPropertyName("name")]     public string Name { get; set; } = "";
    [JsonPropertyName("value")]    public string Value { get; set; } = "";
    [JsonPropertyName("isSecret")] public bool IsSecret { get; set; }

    public CustomField Clone() => new() { Name = Name, Value = Value, IsSecret = IsSecret };
}

/// <summary>
/// Una credencial. Identidad = <see cref="Id"/>: el título se repite ("Admin" en cada cliente) y se
/// renombra, así que nunca sirve de key. Las fechas van en UTC: sólo se usan para ordenar y comparar,
/// y en hora local un cambio de horario de verano las desordenaría.
///
/// El scope NO vive acá adentro sino como key del diccionario del store: así una reorganización del
/// catálogo mueve la LISTA entera sin tocar cada credencial, con el mismo criterio que variables y notas.
/// </summary>
public sealed class Credential
{
    [JsonPropertyName("id")]        public Guid Id { get; set; } = Guid.NewGuid();
    [JsonPropertyName("title")]     public string Title { get; set; } = "";
    [JsonPropertyName("user")]      public string User { get; set; } = "";
    [JsonPropertyName("password")]  public string Password { get; set; } = "";
    [JsonPropertyName("url")]       public string Url { get; set; } = "";
    [JsonPropertyName("note")]      public string Note { get; set; } = "";
    [JsonPropertyName("fields")]    public List<CustomField> Fields { get; set; } = new();
    [JsonPropertyName("createdAt")] public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    [JsonPropertyName("updatedAt")] public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Copia PROFUNDA. El store entrega y recibe copias: es el único que escribe, y una ventana que
    /// editara el objeto vivo cambiaría el dato en memoria sin pasar por el Save — en disco quedaría
    /// otra cosa, y al reiniciar el cambio "desaparecería".
    /// </summary>
    public Credential Clone() => new()
    {
        Id = Id, Title = Title, User = User, Password = Password, Url = Url, Note = Note,
        Fields = (Fields ?? new()).Where(f => f is not null).Select(f => f.Clone()).ToList(),
        CreatedAt = CreatedAt, UpdatedAt = UpdatedAt,
    };
}
