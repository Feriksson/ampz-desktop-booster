using System;
using System.Globalization;
using System.Text;

namespace AmpzDesktopBooster.Knowledge;

// Modelo de la biblioteca de conocimiento. Son records INMUTABLES a propósito: cruzan del store a la
// UI como fotos de lo que había en la base en ese momento; para cambiar algo se le pide al store, que
// es el único que escribe. Nada de esto tiene que ver con Espacio/Contexto de la app.

/// <summary>
/// Una biblioteca. Forman un ÁRBOL por <see cref="ParentId"/> (null = raíz); el store las entrega
/// planas y la UI arma el árbol. <see cref="DefaultNatureId"/> es la naturaleza que HEREDAN sus
/// entradas mientras no fijen una propia.
/// </summary>
public sealed record Library(long Id, long? ParentId, string Name, long? DefaultNatureId, int Sort, DateTime CreatedAt);

/// <summary>Tarjeta (frente/dorso libres) o término de vocabulario (palabra/definición + ficha).</summary>
public enum EntryKind { Card, Term }

/// <summary>
/// Una entrada de una biblioteca. Tarjetas y términos comparten tabla, quiz y seguimiento: en un
/// TÉRMINO <see cref="Front"/> es la palabra y <see cref="Back"/> la definición, y además usa
/// <see cref="WordType"/>, <see cref="UsageContext"/> y <see cref="Origin"/> (vacíos en una tarjeta).
/// <see cref="NatureId"/> null NO es "sin naturaleza": es "la que diga mi biblioteca".
/// </summary>
public sealed record Entry(
    long Id, long LibraryId, EntryKind Kind, string Front, string Back, long? NatureId,
    string WordType, string UsageContext, string Origin, DateTime CreatedAt, DateTime UpdatedAt);

/// <summary>Lo que el usuario edita de una entrada (alta y modificación): todo menos identidad y fechas.</summary>
public sealed record EntryDraft(
    EntryKind Kind, string Front, string Back, long? NatureId = null,
    string WordType = "", string UsageContext = "", string Origin = "");

/// <summary>Naturaleza: la lista personal y creciente (técnica, legal, general…). UNA por entrada.</summary>
public sealed record Nature(long Id, string Name);

/// <summary>Idioma de traducción de un término. Lista dinámica: el usuario suma los que estudia.</summary>
public sealed record Language(long Id, string Name);

/// <summary>La traducción de un término a un idioma (a lo sumo una por par entrada/idioma).</summary>
public sealed record Translation(long EntryId, long LanguageId, string Text);

/// <summary>
/// Un repaso: qué lado se MOSTRÓ como pregunta (<see cref="Side"/>, ver <see cref="ReviewSide"/>),
/// cómo le fue y cuándo (UTC). El historial es de sólo-agregar: la solidez se DERIVA de él.
/// </summary>
public sealed record Review(long Id, long EntryId, string Side, Grade Grade, DateTime ReviewedAt);

/// <summary>Las cuatro respuestas del quiz. El número ES lo que se persiste (1..4): no reordenar.</summary>
public enum Grade
{
    /// <summary>"No lo sé".</summary>
    DontKnow = 1,
    /// <summary>"Tuve dudas".</summary>
    Doubts = 2,
    /// <summary>"Bien pero mejorable".</summary>
    Improvable = 3,
    /// <summary>"Un espectáculo".</summary>
    Spectacle = 4,
}

/// <summary>
/// El "lado" de un repaso, como texto: <c>front</c>, <c>back</c> o <c>lang:&lt;idIdioma&gt;</c>. Saber
/// una palabra viéndola no es lo mismo que evocarla desde su definición o desde el inglés: cada lado
/// lleva su propia solidez, y por eso el lado va en la key de todo el seguimiento.
/// </summary>
public static class ReviewSide
{
    public const string Front = "front";
    public const string Back = "back";
    private const string LanguagePrefix = "lang:";

    /// <summary>El lado "se mostró la traducción a este idioma".</summary>
    public static string ForLanguage(long languageId) =>
        LanguagePrefix + languageId.ToString(CultureInfo.InvariantCulture);

    /// <summary>Si <paramref name="side"/> es un lado de idioma, devuelve cuál.</summary>
    public static bool TryGetLanguage(string side, out long languageId)
    {
        languageId = 0;
        return side.StartsWith(LanguagePrefix, StringComparison.Ordinal)
            && long.TryParse(side.AsSpan(LanguagePrefix.Length), NumberStyles.None,
                             CultureInfo.InvariantCulture, out languageId);
    }

    /// <summary>¿Es uno de los tres formatos válidos? El store rechaza cualquier otro texto.</summary>
    public static bool IsValid(string? side) =>
        side is Front or Back || (side is not null && TryGetLanguage(side, out _));
}

/// <summary>
/// Por qué salió (o no) una operación sobre bibliotecas / naturalezas / idiomas. Un MOTIVO y no un
/// bool por lo mismo que <c>ScopeOpResult</c>: se disparan desde botones, y un botón que no hace nada
/// se lee igual que uno roto.
/// </summary>
public enum KnowledgeOpResult
{
    Ok,
    /// <summary>El origen o el destino ya no existe.</summary>
    NotFound,
    /// <summary>El nombre quedó vacío después de recortar espacios.</summary>
    EmptyName,
    /// <summary>Ya hay una naturaleza / un idioma con ese nombre (sin distinguir mayúsculas).</summary>
    NameTaken,
    /// <summary>Ya estaba ahí: no hay nada que mover.</summary>
    SameTarget,
    /// <summary>Mover la biblioteca dentro de sí misma o de una descendiente cerraría un ciclo.</summary>
    WouldCycle,
    /// <summary>Falló la base de datos (ya se avisó por el notificador del store).</summary>
    Failed,
}

/// <summary>Qué le pasó a la base, para que <c>App</c> elija el texto del aviso (el store no sabe de UI).</summary>
public enum KnowledgeIncidentKind
{
    /// <summary>No se pudo abrir / leer / escribir. <c>Detail</c> = mensaje técnico.</summary>
    Unavailable,
    /// <summary>El archivo estaba ilegible: se apartó (<c>Detail</c> = nombre nuevo) y se arrancó de cero.</summary>
    CorruptMovedAside,
    /// <summary>El archivo estaba ilegible y NO se pudo apartar: la biblioteca queda inaccesible.</summary>
    CorruptNotMoved,
}

public sealed record KnowledgeIncident(KnowledgeIncidentKind Kind, string Detail);

/// <summary>
/// Normalización para buscar: minúsculas y SIN tildes ("búsqueda" == "busqueda"). Es el mismo criterio
/// que el buscador del Shortcuts Helper (su <c>Fold</c> es privado de la ventana; acá vive la copia
/// que usa este dominio, también registrada como función SQL <c>fold()</c> por el store).
/// </summary>
public static class TextFold
{
    public static string Fold(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var d = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(d.Length);
        foreach (var ch in d)
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
                sb.Append(char.ToLowerInvariant(ch));
        return sb.ToString();
    }
}
