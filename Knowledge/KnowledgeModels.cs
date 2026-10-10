using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace AmpzDesktopBooster.Knowledge;

// Modelo de la biblioteca de conocimiento. Son records INMUTABLES a propósito: cruzan del store a la
// UI como fotos de lo que había en la base en ese momento; para cambiar algo se le pide al store, que
// es el único que escribe. Nada de esto tiene que ver con Espacio/Contexto de la app.

/// <summary>
/// Una biblioteca. Forman un ÁRBOL por <see cref="ParentId"/> (null = raíz); el store las entrega
/// planas y la UI arma el árbol. <see cref="DefaultNatureId"/> es la naturaleza que HEREDAN sus
/// entradas mientras no fijen una propia; null = la que le llegue de su biblioteca madre (ver
/// <see cref="LibraryTree.EffectiveNatureId(long, IReadOnlyDictionary{long, Library})"/>).
/// </summary>
public sealed record Library(long Id, long? ParentId, string Name, long? DefaultNatureId, int Sort, DateTime CreatedAt);

/// <summary>
/// Preguntas sobre el ÁRBOL de bibliotecas que se contestan en memoria, con la lista plana que
/// entrega el store ya indexada por id. Vive acá y no en el store porque la UI las hace por cada fila
/// que pinta: una consulta por fila sería un viaje a disco por tarjeta.
/// </summary>
public static class LibraryTree
{
    /// <summary>
    /// LA regla de "se llaman igual" entre bibliotecas HERMANAS (mismo padre, o las dos raíz): sin los
    /// espacios de las puntas y sin distinguir mayúsculas. Vive acá, sola, porque la usan cuatro
    /// caminos que no pueden opinar distinto — crear, renombrar, mover e importar —: si uno comparara
    /// con otra regla, lo que uno rechaza el otro lo dejaría pasar y volverían las hermanas homónimas.
    /// Las tildes SÍ cuentan ("Tecnica" y "Técnica" son dos nombres): ante la duda no se le niega un
    /// nombre a nadie. Es la regla de .NET y no el NOCASE de SQLite, que sólo pliega ASCII.
    /// </summary>
    public static readonly IEqualityComparer<string> SiblingNames = new SiblingNameComparer();

    /// <summary>¿Dos hermanas con estos nombres chocarían? (ver <see cref="SiblingNames"/>).</summary>
    public static bool SameSiblingName(string? a, string? b) => SiblingNames.Equals(a ?? "", b ?? "");

    /// <summary>
    /// Un nombre que no choque con ninguno de <paramref name="taken"/> (que tiene que comparar con
    /// <see cref="SiblingNames"/>): el mismo si está libre, o "Nombre (2)", "Nombre (3)"…
    /// </summary>
    public static string UniqueSiblingName(string name, ICollection<string> taken)
    {
        name = (name ?? "").Trim();
        if (!taken.Contains(name)) return name;
        for (int n = 2; ; n++)
        {
            string candidate = $"{name} ({n.ToString(CultureInfo.InvariantCulture)})";
            if (!taken.Contains(candidate)) return candidate;
        }
    }

    private sealed class SiblingNameComparer : IEqualityComparer<string>
    {
        public bool Equals(string? x, string? y) =>
            string.Equals((x ?? "").Trim(), (y ?? "").Trim(), StringComparison.OrdinalIgnoreCase);

        public int GetHashCode(string obj) =>
            StringComparer.OrdinalIgnoreCase.GetHashCode((obj ?? "").Trim());
    }

    /// <summary>
    /// La naturaleza que una biblioteca les da a sus entradas: la suya, o la de la primera ANCESTRO
    /// que tenga una (subiendo hacia la raíz). null si ninguna de la cadena la define. Es la misma
    /// regla que aplica en SQL <c>KnowledgeStore.GetEntriesByNature</c>: mantenerlas iguales.
    /// </summary>
    public static long? EffectiveNatureId(long libraryId, IReadOnlyDictionary<long, Library> librariesById)
    {
        // El tope de pasos es la cantidad de bibliotecas: un árbol sano nunca lo alcanza, y una base
        // tocada a mano con un ciclo (A hija de B, B hija de A) corta acá en vez de girar para siempre.
        long? current = libraryId;
        for (int steps = 0; current is { } id && steps <= librariesById.Count; steps++)
        {
            if (!librariesById.TryGetValue(id, out var library)) return null;
            if (library.DefaultNatureId is { } nature) return nature;
            current = library.ParentId;
        }
        return null;
    }

    /// <summary>La naturaleza EFECTIVA de una entrada: la que fijó, o la que hereda del árbol.</summary>
    public static long? EffectiveNatureId(Entry entry, IReadOnlyDictionary<long, Library> librariesById) =>
        entry.NatureId ?? EffectiveNatureId(entry.LibraryId, librariesById);

    /// <summary>
    /// Los nombres desde la raíz hasta la biblioteca, en ese orden ("Madre", "Hija"). Dos bibliotecas
    /// pueden llamarse igual bajo madres distintas: el camino entero es lo único que las distingue.
    /// </summary>
    public static IReadOnlyList<string> PathOf(long libraryId, IReadOnlyDictionary<long, Library> librariesById)
    {
        var names = new List<string>();
        long? current = libraryId;
        for (int steps = 0; current is { } id && steps <= librariesById.Count; steps++) // mismo tope anti-ciclo
        {
            if (!librariesById.TryGetValue(id, out var library)) break;
            names.Add(library.Name);
            current = library.ParentId;
        }
        names.Reverse();
        return names;
    }
}

/// <summary>
/// Lo que hay DENTRO de una biblioteca contando todo su subárbol: lo que se pierde si se la borra.
/// </summary>
public readonly record struct LibraryContents(int SubLibraries, int Entries, int Reviews);

/// <summary>Tarjeta (frente/dorso libres) o término de vocabulario (palabra/definición + ficha).</summary>
public enum EntryKind { Card, Term }

/// <summary>
/// Una entrada de una biblioteca. Tarjetas y términos comparten tabla, quiz y seguimiento: en un
/// TÉRMINO <see cref="Front"/> es la palabra y <see cref="Back"/> la definición, y además usa
/// <see cref="WordType"/>, <see cref="UsageContext"/> y <see cref="Origin"/> (vacíos en una tarjeta).
/// <see cref="NatureId"/> null NO es "sin naturaleza": es "la que diga mi biblioteca" (o, si ella
/// tampoco tiene, la primera de sus ancestros que sí).
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
    /// <summary>Ya hay una naturaleza / un idioma con ese nombre, o una biblioteca HERMANA (mismo
    /// nivel) que se llama igual (ver <see cref="LibraryTree.SiblingNames"/>).</summary>
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
