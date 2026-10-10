using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Data.Sqlite;

namespace AmpzDesktopBooster.Knowledge;

/// <summary>Qué hacer cuando en el destino ya hay una biblioteca hermana con el mismo nombre.</summary>
public enum ImportCollisionMode
{
    /// <summary>Crear una biblioteca nueva con el nombre sufijado ("Nombre (2)"). Lo existente no se toca.</summary>
    Copy,
    /// <summary>Sumar a la existente lo que falte, emparejando las sub-bibliotecas por nombre.</summary>
    Merge,
}

/// <summary>Dónde cae lo importado (<see cref="DestinationId"/> null = raíz) y cómo resolver un choque de nombres.</summary>
public sealed record ImportOptions(long? DestinationId, ImportCollisionMode Mode);

public enum ImportLibraryState
{
    /// <summary>Se crea, con su nombre.</summary>
    Created,
    /// <summary>Se crea con el nombre sufijado, porque el suyo ya estaba tomado en el destino.</summary>
    CreatedAsCopy,
    /// <summary>Ya existe: se le suma lo que falte.</summary>
    Merged,
}

/// <summary>Una biblioteca del archivo y qué le pasa al importar. En orden de árbol, con su profundidad.</summary>
public sealed record ImportLibraryLine(int Depth, string Name, ImportLibraryState State, int Cards, int Terms, int Skipped);

public enum ImportStatus
{
    Ok,
    /// <summary>La biblioteca elegida como destino ya no existe.</summary>
    DestinationNotFound,
    /// <summary>Falló la base: no se escribió NADA (<c>FailureDetail</c> = motivo técnico, si lo hay).</summary>
    Failed,
}

/// <summary>
/// El resultado de una importación — o de su vista previa, que es el MISMO recorrido sin escribir.
/// Por eso los números que muestra el diálogo antes de confirmar son exactamente los que después
/// ocurren: no hay una cuenta "estimada" que pueda discrepar de la real.
/// </summary>
public sealed record ImportOutcome(
    ImportStatus Status, string FailureDetail,
    IReadOnlyList<ImportLibraryLine> Lines,
    int LibrariesCreated, int LibrariesMerged, int CardsAdded, int TermsAdded, int EntriesSkipped,
    IReadOnlyList<string> NewNatures, IReadOnlyList<string> ExistingNatures,
    IReadOnlyList<string> NewLanguages, IReadOnlyList<string> ExistingLanguages,
    int Collisions, IReadOnlyList<long> RootLibraryIds)
{
    /// <summary>¿Cambiaría algo en la base? false = todo lo del archivo ya está.</summary>
    public bool AddsAnything => LibrariesCreated + CardsAdded + TermsAdded > 0;

    internal static ImportOutcome Failure(ImportStatus status, string detail = "") => new(
        status, detail, Array.Empty<ImportLibraryLine>(), 0, 0, 0, 0, 0,
        Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(),
        0, Array.Empty<long>());
}

// Importar y exportar bibliotecas (el formato vive en KnowledgeExchange). Dos promesas:
//   • Exportar es de sólo lectura y NO incluye progreso: ni repasos ni solidez salen de la base.
//   • Importar es UNA transacción: o entra todo el archivo o no entra nada. Y sólo AGREGA: ninguna
//     entrada existente se modifica ni se borra, y el historial de repasos no se toca.
public sealed partial class KnowledgeStore
{
    // ── Exportar ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// La biblioteca <paramref name="rootId"/> con todo su subárbol, o TODAS las raíces con null.
    /// Devuelve null si falló la base, y un documento sin bibliotecas si la pedida ya no existe.
    /// Se lee en una sola conexión (bibliotecas, entradas, traducciones y los nombres de naturalezas
    /// e idiomas): una foto coherente, no cuatro lecturas entre las que algo pudo cambiar.
    /// </summary>
    public ExchangeDocument? ExportLibraries(long? rootId) => Read<ExchangeDocument?>(null, c =>
    {
        var libraries = Query(c, null,
            "SELECT id, parent_id, name, default_nature_id, sort, created_at FROM libraries ORDER BY sort, id", ReadLibrary);
        var children = libraries.ToLookup(l => l.ParentId);
        var natures = NamedRows(c, null, "natures").ToDictionary(x => x.Id, x => x.Name);
        var languages = NamedRows(c, null, "languages").ToDictionary(x => x.Id, x => x.Name);

        // Todo de una vez y se reparte en memoria: una consulta por biblioteca sería un viaje a
        // disco por nodo del árbol.
        var entries = Query(c, null, $"SELECT {EntryColumns} FROM entries e ORDER BY e.id", ReadEntry)
            .ToLookup(e => e.LibraryId);
        var translations = Query(c, null,
                "SELECT entry_id, language_id, text FROM translations ORDER BY entry_id, language_id",
                r => new Translation(r.GetInt64(0), r.GetInt64(1), r.GetString(2)))
            .ToLookup(t => t.EntryId);

        // Una base tocada a mano puede tener un ciclo (A hija de B, B hija de A): cada biblioteca
        // se exporta una sola vez y el recorrido termina.
        var seen = new HashSet<long>();

        ExchangeLibrary Build(Library library) => new(
            library.Name,
            library.DefaultNatureId is { } d && natures.TryGetValue(d, out var natureName) ? natureName : null,
            entries[library.Id].Select(e => new ExchangeEntry(
                e.Kind, e.Front, e.Back,
                // La PROPIA, nunca la heredada: ver la decisión 3 del formato.
                e.NatureId is { } n && natures.TryGetValue(n, out var own) ? own : null,
                e.WordType, e.UsageContext, e.Origin,
                translations[e.Id]
                    .Where(t => languages.ContainsKey(t.LanguageId) && t.Text.Trim() != "")
                    .Select(t => new KeyValuePair<string, string>(languages[t.LanguageId], t.Text))
                    .ToList())).ToList(),
            children[library.Id].Where(child => seen.Add(child.Id)).Select(Build).ToList());

        var roots = rootId is { } id ? libraries.Where(l => l.Id == id) : children[null];
        return new ExchangeDocument(roots.Where(l => seen.Add(l.Id)).Select(Build).ToList(), DateTime.UtcNow);
    });

    // ── Importar ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Qué PASARÍA al importar <paramref name="document"/> con esas opciones, sin escribir nada: es
    /// lo que muestra el diálogo antes de confirmar. Corre sobre una conexión de lectura.
    /// </summary>
    public ImportOutcome PreviewImport(ExchangeDocument document, ImportOptions options) =>
        Read(ImportOutcome.Failure(ImportStatus.Failed), c => new ImportRun(c, null, apply: false).Run(document, options));

    /// <summary>
    /// Importa el documento en UNA transacción. Ante cualquier fallo a mitad de camino no queda nada
    /// escrito (ni bibliotecas, ni entradas, ni las naturalezas o idiomas que se hubieran creado por
    /// el camino) y el resultado trae el motivo.
    /// </summary>
    public ImportOutcome ImportLibraries(ExchangeDocument document, ImportOptions options)
    {
        // Write() traga la excepción y devuelve el fallback (ninguna operación del store tira). El
        // motivo se rescata acá para poder decirlo; se vuelve a lanzar para que Write deshaga la
        // transacción y trate el fallo como cualquier otro.
        string detail = "";
        var outcome = Write<ImportOutcome?>(null, (c, tx) =>
        {
            try { return new ImportRun(c, tx, apply: true).Run(document, options); }
            catch (Exception ex) { detail = ex.Message; throw; }
        });
        return outcome ?? ImportOutcome.Failure(ImportStatus.Failed, detail);
    }

    /// <summary>
    /// Clave de "esta entrada ya está": tipo + frente + dorso, sin los espacios de las puntas. Es
    /// EXACTA en todo lo demás (mayúsculas y tildes cuentan): ante la duda, una entrada casi igual
    /// se agrega — duplicar algo que el usuario puede borrar es mejor que saltear algo que quería.
    /// </summary>
    private static (EntryKind Kind, string Front, string Back) EntryKey(EntryKind kind, string front, string back) =>
        (kind, (front ?? "").Trim(), (back ?? "").Trim());

    /// <summary>
    /// Para COMBINAR: junta en una las bibliotecas hermanas del archivo que se llaman igual (sus
    /// entradas y sus hijas, recursivamente). Combinar empareja por nombre, así que dos hermanas
    /// homónimas del archivo terminarían de todos modos en la misma biblioteca de la base — pero sólo
    /// a partir de la SEGUNDA importación. Juntarlas antes hace que importar dos veces el mismo
    /// archivo dé siempre lo mismo: la segunda vez, cero agregadas.
    /// </summary>
    private static List<ExchangeLibrary> MergeSameNamedSiblings(IReadOnlyList<ExchangeLibrary> siblings)
    {
        var merged = new List<ExchangeLibrary>();
        var indexByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var library in siblings)
        {
            if (indexByName.TryGetValue(library.Name, out int at))
            {
                var first = merged[at];
                merged[at] = first with
                {
                    DefaultNature = first.DefaultNature ?? library.DefaultNature,
                    Entries = first.Entries.Concat(library.Entries).ToList(),
                    Libraries = first.Libraries.Concat(library.Libraries).ToList(),
                };
            }
            else
            {
                indexByName[library.Name] = merged.Count;
                merged.Add(library);
            }
        }
        for (int i = 0; i < merged.Count; i++)
            merged[i] = merged[i] with { Libraries = MergeSameNamedSiblings(merged[i].Libraries) };
        return merged;
    }

    /// <summary>
    /// UNA importación (o su vista previa) sobre una conexión. Es una clase y no un método largo por
    /// dos motivos: el recorrido del árbol es recursivo y arrastra bastante estado (los nombres ya
    /// resueltos, los contadores, las líneas del resumen), y los comandos de inserción se preparan
    /// UNA vez y se reusan — con unas miles de entradas, armar un comando nuevo por fila es la mayor
    /// parte del tiempo.
    ///
    /// Con <c>apply: false</c> hace exactamente el mismo recorrido y las mismas cuentas sin escribir:
    /// las bibliotecas "creadas" no tienen id, y eso alcanza porque debajo de una biblioteca nueva
    /// todo es nuevo — no hay nada en la base contra qué compararlo.
    /// </summary>
    private sealed class ImportRun : IDisposable
    {
        private readonly SqliteConnection _c;
        private readonly SqliteTransaction? _tx;
        private readonly bool _apply;
        private readonly string _now = UtcNow();

        // Nombre → id de lo que YA está en la base (misma comparación que GetOrCreateNature /
        // GetOrCreateLanguage: sin distinguir mayúsculas, con la regla de .NET y no la de SQLite).
        private readonly Dictionary<string, long> _natures = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, long> _languages = new(StringComparer.OrdinalIgnoreCase);
        // Las que hubo (o habría) que crear, y las existentes que se usan: el resumen las lista.
        private readonly Dictionary<string, long> _newNatures = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, long> _newLanguages = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _usedNatures = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _usedLanguages = new(StringComparer.OrdinalIgnoreCase);

        // Las bibliotecas que ya hay, por madre (0 = raíz), en su orden.
        private readonly Dictionary<long, List<(long Id, string Name)>> _children = new();

        private readonly List<ImportLibraryLine> _lines = new();
        private readonly List<long> _rootIds = new();
        private int _created, _merged, _cards, _terms, _skipped, _collisions;

        private SqliteCommand? _insertEntry;
        private SqliteCommand? _insertTranslation;

        public ImportRun(SqliteConnection c, SqliteTransaction? tx, bool apply)
        {
            _c = c;
            _tx = tx;
            _apply = apply;
        }

        public ImportOutcome Run(ExchangeDocument document, ImportOptions options)
        {
            using (this)
            {
                if (options.DestinationId is { } destination && !Exists(_c, _tx, "libraries", destination))
                    return ImportOutcome.Failure(ImportStatus.DestinationNotFound);

                foreach (var (id, name) in NamedRows(_c, _tx, "natures")) _natures.TryAdd(name.Trim(), id);
                foreach (var (id, name) in NamedRows(_c, _tx, "languages")) _languages.TryAdd(name.Trim(), id);
                foreach (var (id, parent, name) in Query(_c, _tx,
                             "SELECT id, parent_id, name FROM libraries ORDER BY sort, id",
                             r => (r.GetInt64(0), r.IsDBNull(1) ? 0L : r.GetInt64(1), r.GetString(2))))
                {
                    if (!_children.TryGetValue(parent, out var list)) _children[parent] = list = new();
                    list.Add((id, name));
                }

                bool merge = options.Mode == ImportCollisionMode.Merge;
                var roots = merge ? MergeSameNamedSiblings(document.Libraries) : document.Libraries;
                var siblings = ChildrenOf(options.DestinationId);
                // Los nombres tomados en el destino, para elegir el sufijo de una copia. Se le suman
                // los sufijos que se van dando: dos raíces del archivo que choquen con la misma
                // biblioteca no pueden recibir las dos "Nombre (2)".
                var taken = new HashSet<string>(siblings.Select(s => s.Name.Trim()), StringComparer.OrdinalIgnoreCase);

                foreach (var root in roots)
                {
                    var existing = FindByName(siblings, root.Name);
                    if (existing is null)
                    {
                        _rootIds.Add(Create(root, options.DestinationId, root.Name, ImportLibraryState.Created, 0));
                        continue;
                    }

                    _collisions++;
                    if (merge)
                    {
                        MergeInto(existing.Value.Id, existing.Value.Name, root, 0);
                        _rootIds.Add(existing.Value.Id);
                    }
                    else
                    {
                        string name = UniqueName(root.Name, taken);
                        taken.Add(name);
                        _rootIds.Add(Create(root, options.DestinationId, name, ImportLibraryState.CreatedAsCopy, 0));
                    }
                }

                return new ImportOutcome(ImportStatus.Ok, "", _lines, _created, _merged, _cards, _terms, _skipped,
                    Sorted(_newNatures.Keys), Sorted(_usedNatures.Where(n => !_newNatures.ContainsKey(n))),
                    Sorted(_newLanguages.Keys), Sorted(_usedLanguages.Where(l => !_newLanguages.ContainsKey(l))),
                    _collisions, _apply ? _rootIds : Array.Empty<long>());
            }
        }

        /// <summary>Crea la biblioteca y TODO su subárbol: debajo de una nueva no hay nada que comparar.</summary>
        private long Create(ExchangeLibrary node, long? parentId, string name, ImportLibraryState state, int depth)
        {
            long? defaultNature = node.DefaultNature is { } natureName ? Resolve(natureName, nature: true) : null;
            long id = _apply ? InsertLibrary(_c, _tx, name, parentId, defaultNature) : 0;
            _created++;

            int line = _lines.Count;
            _lines.Add(new ImportLibraryLine(depth, name, state, 0, 0, 0));
            int cards = 0, terms = 0;
            foreach (var entry in node.Entries)
            {
                AddEntry(id, entry);
                if (entry.Kind == EntryKind.Term) terms++; else cards++;
            }
            _lines[line] = _lines[line] with { Cards = cards, Terms = terms };

            foreach (var child in node.Libraries)
                Create(child, _apply ? id : null, child.Name, ImportLibraryState.Created, depth + 1);
            return id;
        }

        /// <summary>
        /// Suma a una biblioteca que YA existe lo que le falte. Su nombre, su naturaleza por defecto
        /// y sus entradas quedan como estaban: sólo se agregan entradas que no tenía y se baja por
        /// las sub-bibliotecas (la que ya existe se combina, la que no se crea).
        /// </summary>
        private void MergeInto(long existingId, string existingName, ExchangeLibrary node, int depth)
        {
            _merged++;
            // La foto de lo que tenía ANTES de esta importación: dos entradas iguales dentro del
            // propio archivo no se saltean entre sí (se agregan las dos, como al importar una copia).
            var present = new HashSet<(EntryKind, string, string)>(Query(_c, _tx,
                "SELECT kind, front, back FROM entries WHERE library_id = $l",
                r => EntryKey(KindFromDb(r.GetString(0)), r.GetString(1), r.GetString(2)), ("$l", existingId)));

            int line = _lines.Count;
            _lines.Add(new ImportLibraryLine(depth, existingName, ImportLibraryState.Merged, 0, 0, 0));
            int cards = 0, terms = 0, skipped = 0;
            foreach (var entry in node.Entries)
            {
                if (present.Contains(EntryKey(entry.Kind, entry.Front, entry.Back))) { skipped++; continue; }
                AddEntry(existingId, entry);
                if (entry.Kind == EntryKind.Term) terms++; else cards++;
            }
            _skipped += skipped;
            _lines[line] = _lines[line] with { Cards = cards, Terms = terms, Skipped = skipped };

            var siblings = ChildrenOf(existingId);
            foreach (var child in node.Libraries)
            {
                if (FindByName(siblings, child.Name) is { } match) MergeInto(match.Id, match.Name, child, depth + 1);
                else Create(child, existingId, child.Name, ImportLibraryState.Created, depth + 1);
            }
        }

        private void AddEntry(long libraryId, ExchangeEntry entry)
        {
            if (entry.Kind == EntryKind.Term) _terms++; else _cards++;
            long? nature = entry.Nature is { } natureName ? Resolve(natureName, nature: true) : null;
            // Los idiomas se resuelven también en la vista previa: de ahí sale cuáles son nuevos.
            var translations = entry.Translations.Select(t => (Language: Resolve(t.Key, nature: false), Text: t.Value)).ToList();
            if (!_apply) return;

            _insertEntry ??= Prepare(
                "INSERT INTO entries(library_id, kind, front, back, nature_id, word_type, usage_context, origin, created_at, updated_at) " +
                "VALUES ($l, $k, $f, $b, $n, $w, $u, $o, $now, $now) RETURNING id",
                "$l", "$k", "$f", "$b", "$n", "$w", "$u", "$o", "$now");
            Set(_insertEntry, libraryId, KindToDb(entry.Kind), entry.Front.Trim(), entry.Back.Trim(), nature,
                entry.WordType.Trim(), entry.UsageContext.Trim(), entry.Origin.Trim(), _now);
            long id = Convert.ToInt64(_insertEntry.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);

            foreach (var (language, text) in translations)
            {
                _insertTranslation ??= Prepare(
                    "INSERT INTO translations(entry_id, language_id, text) VALUES ($e, $l, $t)", "$e", "$l", "$t");
                Set(_insertTranslation, id, language, text.Trim());
                _insertTranslation.ExecuteNonQuery();
            }
        }

        /// <summary>
        /// El id de la naturaleza / el idioma con ese nombre, creándolo si no existe. En la vista
        /// previa "crear" es sólo anotarlo como nuevo (con un id de mentira que nunca se escribe).
        /// </summary>
        private long Resolve(string name, bool nature)
        {
            name = name.Trim();
            var existing = nature ? _natures : _languages;
            var created = nature ? _newNatures : _newLanguages;
            (nature ? _usedNatures : _usedLanguages).Add(name);

            if (existing.TryGetValue(name, out long id) || created.TryGetValue(name, out id)) return id;

            if (_apply)
            {
                // `table` es un literal de acá, nunca texto del archivo: por eso puede interpolarse.
                string table = nature ? "natures" : "languages";
                Execute(_c, _tx, $"INSERT INTO {table}(name) VALUES ($n)", ("$n", name));
                id = LastId(_c, _tx);
            }
            created[name] = id;
            return id;
        }

        private List<(long Id, string Name)> ChildrenOf(long? parentId) =>
            _children.TryGetValue(parentId ?? 0, out var list) ? list : new List<(long, string)>();

        // Si hay dos hermanas con el mismo nombre (la app lo permite), gana la primera en su orden.
        private static (long Id, string Name)? FindByName(List<(long Id, string Name)> siblings, string name)
        {
            foreach (var sibling in siblings)
                if (string.Equals(sibling.Name.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase)) return sibling;
            return null;
        }

        private static string UniqueName(string name, HashSet<string> taken)
        {
            for (int n = 2; ; n++)
            {
                string candidate = $"{name.Trim()} ({n})";
                if (!taken.Contains(candidate)) return candidate;
            }
        }

        private static List<string> Sorted(IEnumerable<string> names) =>
            names.OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase).ToList();

        private SqliteCommand Prepare(string sql, params string[] parameters)
        {
            var cmd = _c.CreateCommand();
            cmd.Transaction = _tx;
            cmd.CommandText = sql;
            foreach (string name in parameters) cmd.Parameters.Add(new SqliteParameter { ParameterName = name });
            return cmd;
        }

        // Los valores van en el MISMO orden en que Prepare declaró los parámetros.
        private static void Set(SqliteCommand cmd, params object?[] values)
        {
            for (int i = 0; i < values.Length; i++) cmd.Parameters[i].Value = values[i] ?? DBNull.Value;
        }

        public void Dispose()
        {
            _insertEntry?.Dispose();
            _insertTranslation?.Dispose();
        }
    }
}
