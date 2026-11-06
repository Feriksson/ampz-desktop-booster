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
/// Proveedor de identidad (OAuth) de una cuenta: marca que ESTA credencial es "la cuenta de Google /
/// Microsoft / …" con la que otras inician sesión. Es un catálogo CERRADO a propósito: son los botones
/// "Continuar con…" que aparecen en los logins, y "Other" absorbe el resto sin que haga falta tocar
/// el código por cada proveedor nuevo.
/// </summary>
public enum CredentialProvider { None, Google, Microsoft, GitHub, Facebook, Apple, Other }

public static class CredentialProviders
{
    /// <summary>Orden en que se ofrecen en el editor (None primero: es lo normal).</summary>
    public static readonly CredentialProvider[] All =
    {
        CredentialProvider.None, CredentialProvider.Google, CredentialProvider.Microsoft,
        CredentialProvider.GitHub, CredentialProvider.Facebook, CredentialProvider.Apple, CredentialProvider.Other,
    };

    /// <summary>
    /// Se persiste como texto en minúsculas y NO como número del enum: un número cambiaría de
    /// significado el día que alguien reordene el enum, y un archivo viejo pasaría a decir otro
    /// proveedor sin ningún error. None → null (no escribe nada).
    /// </summary>
    public static string? ToKey(CredentialProvider p) => p == CredentialProvider.None ? null : p.ToString().ToLowerInvariant();

    /// <summary>
    /// null/vacío = sin proveedor; un valor desconocido (archivo de una versión futura o tocado a
    /// mano) se lee como Other — nunca tira, y la cuenta sigue marcada como identidad.
    /// </summary>
    public static CredentialProvider FromKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return CredentialProvider.None;
        foreach (var p in All)
            if (p != CredentialProvider.None && string.Equals(p.ToString(), key.Trim(), StringComparison.OrdinalIgnoreCase))
                return p;
        return CredentialProvider.Other;
    }
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

    /// <summary>Proveedor tal como viaja en el archivo (ver <see cref="CredentialProviders.ToKey"/>).</summary>
    [JsonPropertyName("provider")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ProviderKey { get; set; }

    /// <summary>Vista tipada del proveedor; lo que se guarda es <see cref="ProviderKey"/>.</summary>
    [JsonIgnore]
    public CredentialProvider Provider
    {
        get => CredentialProviders.FromKey(ProviderKey);
        set => ProviderKey = CredentialProviders.ToKey(value);
    }

    /// <summary>
    /// "Inicia sesión con": ids de OTRAS credenciales (de cualquier scope) que son la identidad con la
    /// que se entra acá — Resend vía "Gmail personal". Por ID y no por título: el título se repite y
    /// se renombra, y por id el vínculo sobrevive a cualquier reorganización del catálogo sin que
    /// nadie lo reescriba. El orden importa (el primero es el que usan "vía X" y "copiar usuario").
    /// Es independiente de la contraseña: una cuenta puede tener las dos cosas.
    /// Un archivo viejo sin la clave carga con la lista vacía (default de System.Text.Json).
    /// </summary>
    [JsonPropertyName("signInWith")] public List<Guid> SignInWith { get; set; } = new();

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
        ProviderKey = ProviderKey,
        SignInWith = (SignInWith ?? new()).ToList(),
    };

    /// <summary>
    /// Deja <see cref="SignInWith"/> sano: sin vacíos, sin repetidos (conserva la PRIMERA aparición,
    /// así el orden elegido no cambia) y nunca apuntándose a sí misma — "entra con ella misma" no
    /// significa nada y haría que "Ir a la cuenta" no fuera a ningún lado.
    /// </summary>
    public void NormalizeLinks()
    {
        var seen = new HashSet<Guid>();
        SignInWith = (SignInWith ?? new()).Where(g => g != Guid.Empty && g != Id && seen.Add(g)).ToList();
    }
}
