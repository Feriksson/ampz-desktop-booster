using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace AmpzDesktopBooster.Knowledge;

// El formato de INTERCAMBIO de bibliotecas: un único JSON portable para compartir o respaldar una
// biblioteca con todo su subárbol. Acá vive sólo la forma del archivo —el modelo, cómo se escribe y
// cómo se lee y valida—; ni una ventana ni una conexión. Lo que entra y sale de la base lo hace
// KnowledgeStore (ver KnowledgeStore.Exchange.cs), que habla en estos mismos records.
//
// Tres decisiones del formato que NO hay que "simplificar":
//   1. Viaja el CONTENIDO, nunca el progreso. Ni repasos ni solidez: lo que se comparte es lo que hay
//      para aprender, no cuánto lo sabe quien lo exportó. Tampoco ids ni fechas por entrada, que sólo
//      significan algo dentro de la base donde nacieron.
//   2. Naturalezas e idiomas viajan por NOMBRE. Los ids son locales: el 3 de una base es "Legal" y el
//      de otra es "Inglés". Al importar se buscan por nombre y se crean los que falten.
//   3. La naturaleza de una entrada es la PROPIA, o null si la hereda. Escribir la heredada como si
//      fuera propia la congelaría: en el destino dejaría de seguir a la biblioteca al cambiarla.

/// <summary>Una entrada tal como viaja en el archivo. <see cref="Nature"/> null = la hereda.</summary>
public sealed record ExchangeEntry(
    EntryKind Kind, string Front, string Back, string? Nature,
    string WordType, string UsageContext, string Origin,
    IReadOnlyList<KeyValuePair<string, string>> Translations);

/// <summary>Una biblioteca con sus entradas y sus hijas (recursivo).</summary>
public sealed record ExchangeLibrary(
    string Name, string? DefaultNature,
    IReadOnlyList<ExchangeEntry> Entries, IReadOnlyList<ExchangeLibrary> Libraries);

/// <summary>El archivo entero: una o varias bibliotecas raíz.</summary>
public sealed record ExchangeDocument(IReadOnlyList<ExchangeLibrary> Libraries, DateTime? ExportedAt)
{
    /// <summary>Cuántas bibliotecas trae, contando todos los niveles.</summary>
    public int CountLibraries() => Libraries.Sum(CountLibraries);

    /// <summary>Cuántas entradas trae, contando todos los niveles.</summary>
    public int CountEntries() => Libraries.Sum(CountEntries);

    /// <summary>Niveles de anidación de la rama más profunda (una biblioteca sin hijas = 1).</summary>
    public int Depth() => Libraries.Count == 0 ? 0 : Libraries.Max(DepthOf);

    private static int CountLibraries(ExchangeLibrary library) => 1 + library.Libraries.Sum(CountLibraries);
    private static int CountEntries(ExchangeLibrary library) => library.Entries.Count + library.Libraries.Sum(CountEntries);
    private static int DepthOf(ExchangeLibrary library) =>
        1 + (library.Libraries.Count == 0 ? 0 : library.Libraries.Max(DepthOf));
}

/// <summary>Por qué un archivo no se puede importar. Cada motivo tiene su propio texto en la UI.</summary>
public enum ExchangeErrorKind
{
    /// <summary>Pesa más que <see cref="KnowledgeExchange.MaxFileBytes"/>.</summary>
    FileTooLarge,
    /// <summary>No se pudo abrir o leer (<c>Detail</c> = motivo técnico).</summary>
    FileUnreadable,
    /// <summary>Está vacío.</summary>
    EmptyFile,
    /// <summary>No es JSON válido (<c>Line</c> / <c>Column</c> = dónde se rompe).</summary>
    MalformedJson,
    /// <summary>Es JSON, pero no un objeto con la forma de este formato.</summary>
    NotAnObject,
    /// <summary>Falta <c>format</c> o es de otra cosa (<c>Detail</c> = lo que decía).</summary>
    WrongFormat,
    /// <summary>La versión del formato no es una que esta app sepa leer (<c>Detail</c> = cuál).</summary>
    UnsupportedVersion,
    /// <summary>No trae ninguna biblioteca.</summary>
    NoLibraries,
    /// <summary>Falta un campo obligatorio (<c>Field</c>).</summary>
    MissingField,
    /// <summary>Un campo tiene un tipo que no corresponde (<c>Field</c>).</summary>
    WrongType,
    /// <summary>Una biblioteca sin nombre.</summary>
    EmptyName,
    /// <summary>Una entrada con el frente vacío.</summary>
    EmptyFront,
    /// <summary>Una entrada cuyo <c>kind</c> no es "card" ni "term" (<c>Detail</c> = lo que decía).</summary>
    UnknownKind,
    /// <summary>Una traducción cuyo idioma no tiene nombre.</summary>
    EmptyLanguageName,
    /// <summary>Más niveles de sub-bibliotecas que <see cref="KnowledgeExchange.MaxDepth"/>.</summary>
    TooDeep,
}

/// <summary>
/// El primer problema encontrado en un archivo, con DÓNDE está: <see cref="Library"/> es el camino de
/// la biblioteca ("Madre › Hija"; una biblioteca sin nombre legible figura como "#3", su posición) y
/// <see cref="Entry"/> el número de entrada dentro de ella (1 = la primera; 0 = el problema es de la
/// biblioteca). Un "archivo inválido" a secas, en un JSON de miles de líneas, no le sirve a nadie.
/// </summary>
public sealed record ExchangeError(
    ExchangeErrorKind Kind, string Library = "", int Entry = 0, string Field = "", string Detail = "",
    long Line = 0, long Column = 0);

/// <summary>Lo que salió de leer un archivo: el documento, o el motivo por el que no se pudo.</summary>
public sealed record ExchangeReadResult(ExchangeDocument? Document, ExchangeError? Error);

public static class KnowledgeExchange
{
    /// <summary>La firma del formato: lo primero que se comprueba, para no confundir cualquier JSON con una biblioteca.</summary>
    public const string FormatName = "ampz-knowledge-library";

    /// <summary>La única versión que existe hoy. Un archivo de una versión MAYOR se rechaza con su
    /// propio mensaje: leerlo "como se pueda" importaría la mitad sin avisar.</summary>
    public const int FormatVersion = 1;

    // Los dos topes existen porque el archivo es ENTRADA NO CONFIABLE (lo mandó otra persona, o lo
    // editó alguien a mano): se lee entero en memoria y el árbol se recorre con recursión, y ambas
    // cosas corren en el thread de UI. Veinte megas son varios cientos de miles de entradas — muy por
    // encima de cualquier biblioteca real — y treinta y dos niveles no los arma nadie a propósito.
    public const long MaxFileBytes = 20L * 1024 * 1024;
    public const int MaxDepth = 32;

    /// <summary>Separador del camino de una biblioteca en los mensajes (el mismo que usa la config).</summary>
    public const string PathSeparator = " › ";

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    // ── Escritura ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// El documento como texto JSON indentado. <paramref name="asList"/> elige la forma de arriba:
    /// false = <c>library</c> (una sola raíz: exportar una biblioteca); true = <c>libraries</c>
    /// (exportar todo). Los opcionales vacíos NO se escriben: un término sin origen no lleva un
    /// <c>"origin": ""</c> que alguien tenga que leer.
    /// </summary>
    public static string Serialize(ExchangeDocument document, bool asList)
    {
        using var buffer = new MemoryStream();
        // Sin escape agresivo: tildes, eñes y comillas angulares quedan legibles, que para un archivo
        // que se comparte y a veces se edita a mano importa. Mismo criterio que AppShortcutStore.
        using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions
                   { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            w.WriteStartObject();
            w.WriteString("format", FormatName);
            w.WriteNumber("version", FormatVersion);
            w.WriteString("exportedAt",
                (document.ExportedAt ?? DateTime.UtcNow).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture));

            if (!asList && document.Libraries.Count == 1)
            {
                w.WritePropertyName("library");
                WriteLibrary(w, document.Libraries[0]);
            }
            else
            {
                w.WriteStartArray("libraries");
                foreach (var library in document.Libraries) WriteLibrary(w, library);
                w.WriteEndArray();
            }
            w.WriteEndObject();
        }
        return Utf8NoBom.GetString(buffer.ToArray());
    }

    private static void WriteLibrary(Utf8JsonWriter w, ExchangeLibrary library)
    {
        w.WriteStartObject();
        w.WriteString("name", library.Name);
        // defaultNature SIEMPRE se escribe, aunque sea null: "esta biblioteca no fija naturaleza" es
        // un dato del árbol (hereda de la de arriba), no un opcional que falta.
        if (string.IsNullOrWhiteSpace(library.DefaultNature)) w.WriteNull("defaultNature");
        else w.WriteString("defaultNature", library.DefaultNature);

        w.WriteStartArray("entries");
        foreach (var entry in library.Entries) WriteEntry(w, entry);
        w.WriteEndArray();

        w.WriteStartArray("libraries");
        foreach (var child in library.Libraries) WriteLibrary(w, child);
        w.WriteEndArray();
        w.WriteEndObject();
    }

    private static void WriteEntry(Utf8JsonWriter w, ExchangeEntry entry)
    {
        w.WriteStartObject();
        w.WriteString("kind", entry.Kind == EntryKind.Term ? "term" : "card");
        w.WriteString("front", entry.Front);
        w.WriteString("back", entry.Back);
        if (string.IsNullOrWhiteSpace(entry.Nature)) w.WriteNull("nature");
        else w.WriteString("nature", entry.Nature);

        if (entry.Kind == EntryKind.Term)
        {
            if (entry.WordType != "") w.WriteString("wordType", entry.WordType);
            if (entry.UsageContext != "") w.WriteString("usageContext", entry.UsageContext);
            if (entry.Origin != "") w.WriteString("origin", entry.Origin);
            if (entry.Translations.Count > 0)
            {
                w.WriteStartObject("translations");
                foreach (var (language, text) in entry.Translations) w.WriteString(language, text);
                w.WriteEndObject();
            }
        }
        w.WriteEndObject();
    }

    /// <summary>
    /// Nombre de archivo sugerido: el de la biblioteca hecho apto para un nombre de archivo, más la
    /// fecha. Sólo se usa como SUGERENCIA en el diálogo de guardar; la ruta la elige el usuario.
    /// </summary>
    public static string SuggestFileName(string baseName, DateTime localDate)
    {
        var invalid = new HashSet<char>(Path.GetInvalidFileNameChars());
        var sb = new StringBuilder();
        foreach (char ch in (baseName ?? "").Trim())
        {
            // Lo que Windows no acepta en un nombre, y cualquier espacio en blanco, pasa a guion.
            bool bad = invalid.Contains(ch) || char.IsControl(ch) || char.IsWhiteSpace(ch);
            if (!bad) sb.Append(ch);
            else if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
        }
        string clean = sb.ToString().Trim('-', '.', ' ');
        if (clean.Length > 60) clean = clean[..60].Trim('-', '.', ' ');
        if (clean == "") clean = "library";
        // CON, NUL, COM1… son dispositivos para Windows aunque lleven extensión.
        string[] reserved = { "CON", "PRN", "AUX", "NUL" };
        if (reserved.Contains(clean, StringComparer.OrdinalIgnoreCase)
            || (clean.Length == 4 && char.IsDigit(clean[3])
                && (clean.StartsWith("COM", StringComparison.OrdinalIgnoreCase)
                    || clean.StartsWith("LPT", StringComparison.OrdinalIgnoreCase))))
            clean = "_" + clean;
        return clean + "-" + localDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".json";
    }

    // ── Lectura y validación ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Lee y valida un archivo ENTERO antes de que nadie toque la base. Nunca tira: devuelve el
    /// documento o el primer problema. Del archivo sólo sale texto que se guarda como texto — ninguna
    /// ruta, comando ni referencia externa se toma de él.
    /// </summary>
    public static ExchangeReadResult ReadFile(string path)
    {
        byte[] bytes;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (fs.Length > MaxFileBytes) return Fail(new ExchangeError(ExchangeErrorKind.FileTooLarge, Detail: Megabytes(fs.Length)));

            // El tope se vuelve a medir sobre lo que realmente se lee: el tamaño de recién es una
            // foto, y un archivo que sigue creciendo no puede pasarse de largo.
            using var memory = new MemoryStream((int)fs.Length);
            var chunk = new byte[81920];
            int read;
            while ((read = fs.Read(chunk, 0, chunk.Length)) > 0)
            {
                memory.Write(chunk, 0, read);
                if (memory.Length > MaxFileBytes)
                    return Fail(new ExchangeError(ExchangeErrorKind.FileTooLarge, Detail: Megabytes(memory.Length)));
            }
            bytes = memory.ToArray();
        }
        catch (Exception ex)
        {
            return Fail(new ExchangeError(ExchangeErrorKind.FileUnreadable, Detail: ex.Message));
        }
        return Parse(bytes);
    }

    /// <summary>Igual que <see cref="ReadFile"/>, sobre un texto ya en memoria.</summary>
    public static ExchangeReadResult Parse(string json) => Parse(Utf8NoBom.GetBytes(json ?? ""));

    public static ExchangeReadResult Parse(byte[] utf8)
    {
        if (utf8.Length > MaxFileBytes) return Fail(new ExchangeError(ExchangeErrorKind.FileTooLarge, Detail: Megabytes(utf8.Length)));

        // El lector de JSON no acepta la marca BOM que el Bloc de notas pone delante: se saltea.
        var span = new ReadOnlyMemory<byte>(utf8);
        if (utf8.Length >= 3 && utf8[0] == 0xEF && utf8[1] == 0xBB && utf8[2] == 0xBF) span = span[3..];
        if (span.Span.IsEmpty || IsBlank(span.Span)) return Fail(new ExchangeError(ExchangeErrorKind.EmptyFile));

        JsonDocument json;
        try
        {
            // Comas finales y comentarios se toleran (es lo que deja quien lo edita a mano). El tope
            // de profundidad del lector es más alto que el nuestro: así un árbol de 40 niveles llega
            // a nuestra validación y recibe el mensaje que lo explica, no un "JSON inválido".
            json = JsonDocument.Parse(span, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
                MaxDepth = MaxDepth * 2 + 64,
            });
        }
        catch (JsonException ex)
        {
            return Fail(new ExchangeError(ExchangeErrorKind.MalformedJson,
                Line: (ex.LineNumber ?? 0) + 1, Column: (ex.BytePositionInLine ?? 0) + 1));
        }

        using (json)
        {
            try { return new ExchangeReadResult(ReadDocument(json.RootElement), null); }
            catch (InvalidExchangeException ex) { return Fail(ex.Error); }
        }
    }

    private static ExchangeDocument ReadDocument(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) throw Invalid(new ExchangeError(ExchangeErrorKind.NotAnObject));

        // La firma primero: con cualquier otro JSON (un package.json, la config de otra app) el
        // mensaje útil es "esto no es una biblioteca", no "falta el campo version".
        string? format = root.TryGetProperty("format", out var f) && f.ValueKind == JsonValueKind.String ? f.GetString() : null;
        if (!string.Equals(format, FormatName, StringComparison.Ordinal))
            throw Invalid(new ExchangeError(ExchangeErrorKind.WrongFormat, Detail: Clip(format ?? "")));

        if (!root.TryGetProperty("version", out var v)) throw Invalid(new ExchangeError(ExchangeErrorKind.MissingField, Field: "version"));
        if (v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out int version))
            throw Invalid(new ExchangeError(ExchangeErrorKind.WrongType, Field: "version"));
        if (version != FormatVersion)
            throw Invalid(new ExchangeError(ExchangeErrorKind.UnsupportedVersion, Detail: version.ToString(CultureInfo.InvariantCulture)));

        DateTime? exportedAt = root.TryGetProperty("exportedAt", out var at) && at.ValueKind == JsonValueKind.String
                               && DateTime.TryParse(at.GetString(), CultureInfo.InvariantCulture,
                                   DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : null;

        // Las dos formas: `library` (una) y `libraries` (varias). Se aceptan las dos a la vez.
        var libraries = new List<ExchangeLibrary>();
        if (root.TryGetProperty("library", out var single) && single.ValueKind != JsonValueKind.Null)
            libraries.Add(ReadLibrary(single, "", 1, 1));
        if (root.TryGetProperty("libraries", out var many) && many.ValueKind != JsonValueKind.Null)
        {
            if (many.ValueKind != JsonValueKind.Array) throw Invalid(new ExchangeError(ExchangeErrorKind.WrongType, Field: "libraries"));
            int position = libraries.Count;
            foreach (var item in many.EnumerateArray()) libraries.Add(ReadLibrary(item, "", ++position, 1));
        }
        if (libraries.Count == 0) throw Invalid(new ExchangeError(ExchangeErrorKind.NoLibraries));

        return new ExchangeDocument(libraries, exportedAt);
    }

    private static ExchangeLibrary ReadLibrary(JsonElement element, string parentPath, int position, int depth)
    {
        // Mientras no se sepa el nombre, la biblioteca se señala por su posición entre las hermanas.
        string placeholder = Join(parentPath, "#" + position.ToString(CultureInfo.InvariantCulture));
        if (depth > MaxDepth) throw Invalid(new ExchangeError(ExchangeErrorKind.TooDeep, placeholder));
        if (element.ValueKind != JsonValueKind.Object)
            throw Invalid(new ExchangeError(ExchangeErrorKind.WrongType, placeholder, Field: "library"));

        string name = RequiredText(element, "name", placeholder, 0).Trim();
        if (name == "") throw Invalid(new ExchangeError(ExchangeErrorKind.EmptyName, placeholder));
        string path = Join(parentPath, name);

        string? defaultNature = NullIfBlank(OptionalText(element, "defaultNature", path, 0));

        var entries = new List<ExchangeEntry>();
        if (TryGetArray(element, "entries", path, out var entryItems))
        {
            int index = 0;
            foreach (var item in entryItems.EnumerateArray()) entries.Add(ReadEntry(item, path, ++index));
        }

        var children = new List<ExchangeLibrary>();
        if (TryGetArray(element, "libraries", path, out var childItems))
        {
            int index = 0;
            foreach (var item in childItems.EnumerateArray()) children.Add(ReadLibrary(item, path, ++index, depth + 1));
        }

        return new ExchangeLibrary(name, defaultNature, entries, children);
    }

    private static ExchangeEntry ReadEntry(JsonElement element, string library, int index)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw Invalid(new ExchangeError(ExchangeErrorKind.WrongType, library, index, "entry"));

        string kindText = RequiredText(element, "kind", library, index).Trim();
        EntryKind kind = kindText.ToLowerInvariant() switch
        {
            "card" => EntryKind.Card,
            "term" => EntryKind.Term,
            _ => throw Invalid(new ExchangeError(ExchangeErrorKind.UnknownKind, library, index, Detail: Clip(kindText))),
        };

        string front = RequiredText(element, "front", library, index).Trim();
        if (front == "") throw Invalid(new ExchangeError(ExchangeErrorKind.EmptyFront, library, index));
        string back = (OptionalText(element, "back", library, index) ?? "").Trim();
        string? nature = NullIfBlank(OptionalText(element, "nature", library, index));

        // La ficha de vocabulario sólo existe en un término. En una tarjeta esos campos se ignoran
        // aunque vengan: la app no tiene dónde mostrarlos ni editarlos, y guardarlos sería dejar
        // datos fantasma.
        if (kind != EntryKind.Term)
            return new ExchangeEntry(kind, front, back, nature, "", "", "", Array.Empty<KeyValuePair<string, string>>());

        string wordType = (OptionalText(element, "wordType", library, index) ?? "").Trim();
        string usage = (OptionalText(element, "usageContext", library, index) ?? "").Trim();
        string origin = (OptionalText(element, "origin", library, index) ?? "").Trim();

        var translations = new List<KeyValuePair<string, string>>();
        if (element.TryGetProperty("translations", out var t) && t.ValueKind != JsonValueKind.Null)
        {
            if (t.ValueKind != JsonValueKind.Object)
                throw Invalid(new ExchangeError(ExchangeErrorKind.WrongType, library, index, "translations"));
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in t.EnumerateObject())
            {
                string language = property.Name.Trim();
                if (language == "") throw Invalid(new ExchangeError(ExchangeErrorKind.EmptyLanguageName, library, index));
                if (property.Value.ValueKind != JsonValueKind.String)
                    throw Invalid(new ExchangeError(ExchangeErrorKind.WrongType, library, index, "translations"));
                string text = (property.Value.GetString() ?? "").Trim();
                // Una traducción vacía es "no tiene"; y un idioma repetido en la misma entrada (un
                // archivo editado a mano) conserva la primera: la base admite una sola por idioma.
                if (text != "" && seen.Add(language)) translations.Add(new KeyValuePair<string, string>(language, text));
            }
        }
        return new ExchangeEntry(kind, front, back, nature, wordType, usage, origin, translations);
    }

    // ── Plomería de la validación ────────────────────────────────────────────────────────────

    private static string RequiredText(JsonElement owner, string field, string library, int entry)
    {
        if (!owner.TryGetProperty(field, out var value) || value.ValueKind == JsonValueKind.Null)
            throw Invalid(new ExchangeError(ExchangeErrorKind.MissingField, library, entry, field));
        if (value.ValueKind != JsonValueKind.String)
            throw Invalid(new ExchangeError(ExchangeErrorKind.WrongType, library, entry, field));
        return value.GetString() ?? "";
    }

    private static string? OptionalText(JsonElement owner, string field, string library, int entry)
    {
        if (!owner.TryGetProperty(field, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.String)
            throw Invalid(new ExchangeError(ExchangeErrorKind.WrongType, library, entry, field));
        return value.GetString();
    }

    private static bool TryGetArray(JsonElement owner, string field, string library, out JsonElement array)
    {
        if (!owner.TryGetProperty(field, out array) || array.ValueKind == JsonValueKind.Null) return false;
        if (array.ValueKind != JsonValueKind.Array)
            throw Invalid(new ExchangeError(ExchangeErrorKind.WrongType, library, 0, field));
        return true;
    }

    private static string? NullIfBlank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private static string Join(string parentPath, string name) => parentPath == "" ? name : parentPath + PathSeparator + name;

    // Lo que se cita del archivo en un mensaje va recortado: un "format" de dos megas no entra en un cartel.
    private static string Clip(string text) => text.Length <= 60 ? text : text[..60] + "…";

    private static string Megabytes(long bytes) =>
        Math.Ceiling(bytes / (1024.0 * 1024.0)).ToString("0", CultureInfo.InvariantCulture);

    private static bool IsBlank(ReadOnlySpan<byte> utf8)
    {
        foreach (byte b in utf8)
            if (b is not ((byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')) return false;
        return true;
    }

    private static ExchangeReadResult Fail(ExchangeError error) => new(null, error);

    private static InvalidExchangeException Invalid(ExchangeError error) => new(error);

    /// <summary>Corta la lectura en el primer problema. Nunca sale de esta clase.</summary>
    private sealed class InvalidExchangeException : Exception
    {
        public InvalidExchangeException(ExchangeError error) => Error = error;
        public ExchangeError Error { get; }
    }
}
