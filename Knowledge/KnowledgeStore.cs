using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using AmpzDesktopBooster.Persistence;
using Microsoft.Data.Sqlite;

namespace AmpzDesktopBooster.Knowledge;

/// <summary>
/// La biblioteca de conocimiento en disco: bibliotecas (árbol), entradas (tarjetas y términos),
/// naturalezas, idiomas, traducciones e historial de repasos. Es la ÚNICA clase que toca SQLite — el
/// resto de la app habla en records (<see cref="Library"/>, <see cref="Entry"/>…) y no ve ni una
/// conexión.
///
/// Por qué SQLite acá y JSON en todo lo demás: el historial de repasos crece sin techo (una fila por
/// respuesta del quiz) y se consulta por agregados. Los stores JSON reescriben el archivo entero en
/// cada cambio, que está perfecto para 50 KB de config y es un despropósito para esto.
///
/// Tres reglas que NO hay que "simplificar":
///   1. UNA conexión por operación, con <c>Pooling=False</c>. <c>App.OnExit</c> mata el proceso sin
///      desmontar nada y "Restablecer todo" BORRA la carpeta de datos: un handle que quede vivo en un
///      pool mantiene el archivo tomado y ese borrado falla en silencio. Abrir es barato (archivo local).
///   2. Cada escritura es su propia transacción y se confirma antes de volver. No hay "guardar al
///      cerrar": al cerrar no hay nadie.
///   3. NINGÚN método público tira. Las lecturas degradan a vacío, las escrituras devuelven
///      false / <see cref="KnowledgeOpResult.Failed"/>, y el primer fallo se avisa UNA vez por
///      <see cref="Notify"/> — misma promesa que el resto: la persistencia nunca voltea la app.
/// </summary>
public sealed partial class KnowledgeStore
{
    private const int SchemaVersion = 1;

    // Códigos primarios de SQLite que significan "esto no es (o ya no es) una base sana". Cualquier
    // otro error (archivo tomado, sin permisos, disco lleno) NO es corrupción: apartar el archivo por
    // un bloqueo pasajero sería tirar una biblioteca sana.
    private const int SqliteCorrupt = 11;
    private const int SqliteNotADb = 26;

    private const string EntryColumns =
        "e.id, e.library_id, e.kind, e.front, e.back, e.nature_id, e.word_type, e.usage_context, e.origin, e.created_at, e.updated_at";

    private readonly string _path;
    private readonly string _connectionString;

    private readonly object _gate = new();
    private bool _ready;
    private long _retryInitAfter;                  // TickCount64 hasta el que NO se reintenta inicializar
    private const int InitRetryCooldownMs = 15_000;
    private bool _failureReported;
    private bool _corruptReported;

    private readonly List<KnowledgeIncident> _queuedIncidents = new();
    private Action<KnowledgeIncident>? _notify;

    /// <summary>Abre (o crea) la base del usuario en <see cref="AppPaths.KnowledgeDb"/>.</summary>
    public KnowledgeStore() : this(AppPaths.KnowledgeDb) { }

    /// <summary>
    /// Abre (o crea) la base en <paramref name="dbPath"/>. Existe para poder apuntar el store a un
    /// archivo temporal (pruebas, import/export) sin tocar los datos reales del usuario.
    /// </summary>
    public KnowledgeStore(string dbPath)
    {
        _path = dbPath;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,     // regla 1 de arriba
            ForeignKeys = true,  // SQLite las trae APAGADAS por conexión: sin esto no hay cascadas
            // El default son 30s de reintentos ante un archivo tomado, y esto corre en el thread de
            // UI: preferimos fallar rápido y degradar antes que congelar la barra medio minuto.
            DefaultTimeout = 2,
        }.ToString();

        // Se inicializa YA (esquema + ejemplo si es nueva) y no en el primer uso: así un archivo
        // dañado se detecta y se avisa al arrancar, no en medio de un quiz.
        EnsureReady();
    }

    /// <summary>
    /// Adónde avisar los problemas de la base (el store no sabe de toasts). Los incidentes que
    /// ocurrieron ANTES de asignarlo —el store se construye temprano en el arranque, sin UI montada—
    /// quedan en cola y se entregan al conectar. Mismo mecanismo que <c>SafeFile.AttachNotifier</c>.
    /// </summary>
    public Action<KnowledgeIncident>? Notify
    {
        get { lock (_queuedIncidents) return _notify; }
        set
        {
            List<KnowledgeIncident> drained;
            lock (_queuedIncidents)
            {
                _notify = value;
                if (value is null) return;
                drained = new List<KnowledgeIncident>(_queuedIncidents);
                _queuedIncidents.Clear();
            }
            foreach (var i in drained)
            {
                try { value(i); } catch { /* el aviso nunca voltea nada */ }
            }
        }
    }

    // ── Bibliotecas ──────────────────────────────────────────────────────────────────────────

    /// <summary>Todas, PLANAS, ordenadas por padre y posición. El árbol lo arma la UI.</summary>
    public IReadOnlyList<Library> GetLibraries() => Read<IReadOnlyList<Library>>(Array.Empty<Library>(), c =>
        Query(c, null,
            "SELECT id, parent_id, name, default_nature_id, sort, created_at FROM libraries " +
            "ORDER BY parent_id IS NOT NULL, parent_id, sort, id",
            ReadLibrary));

    /// <summary>Alta al FINAL de sus hermanas. <paramref name="parentId"/> null = biblioteca raíz.</summary>
    public KnowledgeOpResult CreateLibrary(string name, long? parentId, long? defaultNatureId, out long id)
    {
        name = (name ?? "").Trim();
        id = 0;
        if (name == "") return KnowledgeOpResult.EmptyName;

        var (result, newId) = Write((KnowledgeOpResult.Failed, 0L), (c, tx) =>
        {
            if (parentId is { } p && !Exists(c, tx, "libraries", p)) return (KnowledgeOpResult.NotFound, 0L);
            long created = InsertLibrary(c, tx, name, parentId, ExistingOrNull(c, tx, "natures", defaultNatureId));
            return (KnowledgeOpResult.Ok, created);
        });
        id = newId;
        return result;
    }

    public KnowledgeOpResult RenameLibrary(long id, string name)
    {
        name = (name ?? "").Trim();
        if (name == "") return KnowledgeOpResult.EmptyName;
        return Write(KnowledgeOpResult.Failed, (c, tx) =>
            Execute(c, tx, "UPDATE libraries SET name = $n WHERE id = $id", ("$n", name), ("$id", id)) > 0
                ? KnowledgeOpResult.Ok
                : KnowledgeOpResult.NotFound);
    }

    /// <summary>
    /// Mueve una biblioteca (con todo su subárbol) bajo otra, o a la raíz con null. Meterla dentro de
    /// sí misma o de una descendiente se RECHAZA (<see cref="KnowledgeOpResult.WouldCycle"/>): el
    /// subárbol quedaría colgando de sí mismo, inalcanzable desde la raíz e imposible de borrar.
    /// </summary>
    public KnowledgeOpResult MoveLibrary(long id, long? newParentId) => Write(KnowledgeOpResult.Failed, (c, tx) =>
    {
        var current = Query(c, tx, "SELECT parent_id FROM libraries WHERE id = $id",
            r => r.IsDBNull(0) ? (long?)null : r.GetInt64(0), ("$id", id));
        if (current.Count == 0) return KnowledgeOpResult.NotFound;
        if (newParentId == id) return KnowledgeOpResult.WouldCycle;
        if (current[0] == newParentId) return KnowledgeOpResult.SameTarget;
        if (newParentId is { } p)
        {
            if (!Exists(c, tx, "libraries", p)) return KnowledgeOpResult.NotFound;
            if (DescendantIds(c, tx, id).Contains(p)) return KnowledgeOpResult.WouldCycle;
        }
        Execute(c, tx, "UPDATE libraries SET parent_id = $p, sort = $s WHERE id = $id",
            ("$p", newParentId), ("$s", NextSort(c, tx, newParentId)), ("$id", id));
        return KnowledgeOpResult.Ok;
    });

    /// <summary>Borra la biblioteca y, en cascada, sus hijas, entradas, traducciones y repasos.</summary>
    public bool DeleteLibrary(long id) => Write(false, (c, tx) =>
        Execute(c, tx, "DELETE FROM libraries WHERE id = $id", ("$id", id)) > 0);

    /// <summary>La naturaleza que heredan sus entradas sin naturaleza propia (null = la que le
    /// llegue de su biblioteca madre, si alguna de las de arriba tiene una).</summary>
    public bool SetLibraryDefaultNature(long id, long? natureId) => Write(false, (c, tx) =>
        Execute(c, tx, "UPDATE libraries SET default_nature_id = $n WHERE id = $id",
            ("$n", ExistingOrNull(c, tx, "natures", natureId)), ("$id", id)) > 0);

    /// <summary>Ids de TODAS las descendientes (hijas, nietas…), sin incluir a la propia.</summary>
    public IReadOnlyList<long> GetDescendantLibraryIds(long id) =>
        Read<IReadOnlyList<long>>(Array.Empty<long>(), c => DescendantIds(c, null, id));

    /// <summary>Cuántas entradas tiene cada biblioteca (las PROPIAS, sin sumar descendientes). Una
    /// sola consulta para pintar el árbol entero; la que no aparece tiene cero.</summary>
    public IReadOnlyDictionary<long, int> GetEntryCountsByLibrary() =>
        Read<IReadOnlyDictionary<long, int>>(new Dictionary<long, int>(), c =>
            Query(c, null, "SELECT library_id, COUNT(*) FROM entries GROUP BY library_id",
                    r => (Id: r.GetInt64(0), Count: r.GetInt32(1)))
                .ToDictionary(x => x.Id, x => x.Count));

    // ── Entradas ─────────────────────────────────────────────────────────────────────────────

    /// <summary>Entradas de una biblioteca, opcionalmente sumando las de todo su subárbol.</summary>
    public IReadOnlyList<Entry> GetEntries(long libraryId, bool includeDescendants = false) =>
        Read<IReadOnlyList<Entry>>(Array.Empty<Entry>(), c => includeDescendants
            ? Query(c, null,
                "WITH RECURSIVE sub(id) AS (SELECT $id UNION ALL " +
                "  SELECT l.id FROM libraries l JOIN sub s ON l.parent_id = s.id) " +
                $"SELECT {EntryColumns} FROM entries e WHERE e.library_id IN (SELECT id FROM sub) ORDER BY e.id",
                ReadEntry, ("$id", libraryId))
            : Query(c, null,
                $"SELECT {EntryColumns} FROM entries e WHERE e.library_id = $id ORDER BY e.id",
                ReadEntry, ("$id", libraryId)));

    /// <summary>
    /// Entradas cuya naturaleza EFECTIVA es <paramref name="natureId"/>: la propia si la fijó; si no,
    /// la predeterminada de su biblioteca; y si esa tampoco tiene, la de la primera biblioteca
    /// ANCESTRO que tenga una. La herencia sube por todo el árbol porque así se arma en la práctica:
    /// se le pone la naturaleza a la biblioteca madre ("Legal") y las hijas que se van creando abajo
    /// no deberían obligar a repetirla una por una.
    ///
    /// Es la MISMA regla que <see cref="LibraryTree.EffectiveNatureId(Entry, IReadOnlyDictionary{long, Library})"/>,
    /// que la resuelve en memoria para la UI. Si una cambia, cambia la otra: "jugar por naturaleza"
    /// y la naturaleza que muestra una tarjeta no pueden discrepar.
    /// </summary>
    public IReadOnlyList<Entry> GetEntriesByNature(long natureId) =>
        Read<IReadOnlyList<Entry>>(Array.Empty<Entry>(), c => Query(c, null,
            // inherited(id, nature_id): por cada biblioteca, la naturaleza que le llega bajando desde
            // la raíz — la suya si tiene, si no la que ya traía su madre.
            "WITH RECURSIVE inherited(id, nature_id) AS (" +
            "  SELECT id, default_nature_id FROM libraries WHERE parent_id IS NULL UNION ALL " +
            "  SELECT l.id, COALESCE(l.default_nature_id, p.nature_id) " +
            "  FROM libraries l JOIN inherited p ON l.parent_id = p.id) " +
            $"SELECT {EntryColumns} FROM entries e JOIN inherited i ON i.id = e.library_id " +
            "WHERE COALESCE(e.nature_id, i.nature_id) = $n ORDER BY e.id",
            ReadEntry, ("$n", natureId)));

    /// <summary>
    /// TODAS las entradas, en una consulta. Es lo que carga el playbooker al abrir: con esto, las
    /// bibliotecas y el historial arma en memoria los conteos y la solidez de cada fila de su lista,
    /// en vez de preguntarle a la base una vez por biblioteca y otra por naturaleza.
    /// </summary>
    public IReadOnlyList<Entry> GetAllEntries() => Read<IReadOnlyList<Entry>>(Array.Empty<Entry>(), c =>
        Query(c, null, $"SELECT {EntryColumns} FROM entries e ORDER BY e.id", ReadEntry));

    public Entry? GetEntry(long id) => Read<Entry?>(null, c =>
        Query(c, null, $"SELECT {EntryColumns} FROM entries e WHERE e.id = $id", ReadEntry, ("$id", id))
            .FirstOrDefault());

    /// <summary>
    /// Busca por texto en frente, dorso y traducciones, sin distinguir mayúsculas NI tildes ("tecnica"
    /// encuentra "técnica"). Varias palabras = tienen que estar TODAS, en cualquier orden — mismo
    /// criterio que el buscador del Shortcuts Helper. Texto vacío devuelve todo (hasta
    /// <paramref name="limit"/>): sirve para hojear el vocabulario sin tipear.
    /// </summary>
    public IReadOnlyList<Entry> SearchEntries(string? text, EntryKind? kind = null, int limit = 200) =>
        Read<IReadOnlyList<Entry>>(Array.Empty<Entry>(), c =>
        {
            var terms = TextFold.Fold(text).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            var args = new List<(string, object?)>
            {
                ("$kind", kind is { } k ? KindToDb(k) : null),
                ("$limit", Math.Max(1, limit)),
            };

            // El pajar se arma UNA vez por entrada (frente + dorso + todas sus traducciones) y cada
            // palabra se busca ahí con instr() y no con LIKE: así un % o un _ tipeado es texto, no comodín.
            string sql =
                "WITH h AS (SELECT e.id, fold(e.front || char(10) || e.back || char(10) || COALESCE(" +
                "  (SELECT group_concat(t.text, char(10)) FROM translations t WHERE t.entry_id = e.id), '')) AS hay " +
                "  FROM entries e WHERE ($kind IS NULL OR e.kind = $kind)) " +
                $"SELECT {EntryColumns} FROM entries e JOIN h ON h.id = e.id WHERE 1 = 1";
            for (int i = 0; i < terms.Length; i++)
            {
                sql += $" AND instr(h.hay, $q{i}) > 0";
                args.Add(($"$q{i}", terms[i]));
            }
            // Arriba lo que coincide en el FRENTE (la palabra, el título): es casi siempre lo buscado.
            sql += terms.Length > 0
                ? " ORDER BY instr(fold(e.front), $q0) = 0, e.front COLLATE NOCASE LIMIT $limit"
                : " ORDER BY e.front COLLATE NOCASE LIMIT $limit";
            return Query(c, null, sql, ReadEntry, args.ToArray());
        });

    /// <summary>Alta. Devuelve el id, o null si el frente quedó vacío, la biblioteca no existe o falló la base.</summary>
    public long? CreateEntry(long libraryId, EntryDraft draft)
    {
        if (draft is null || string.IsNullOrWhiteSpace(draft.Front)) return null;
        return Write<long?>(null, (c, tx) =>
            Exists(c, tx, "libraries", libraryId) ? InsertEntry(c, tx, libraryId, draft, UtcNow()) : null);
    }

    /// <summary>Reemplaza el contenido de una entrada (no la mueve de biblioteca ni toca sus repasos).</summary>
    public bool UpdateEntry(long id, EntryDraft draft)
    {
        if (draft is null || string.IsNullOrWhiteSpace(draft.Front)) return false;
        return Write(false, (c, tx) => Execute(c, tx,
            "UPDATE entries SET kind = $k, front = $f, back = $b, nature_id = $n, word_type = $w, " +
            "usage_context = $u, origin = $o, updated_at = $now WHERE id = $id",
            ("$k", KindToDb(draft.Kind)), ("$f", draft.Front.Trim()), ("$b", (draft.Back ?? "").Trim()),
            ("$n", ExistingOrNull(c, tx, "natures", draft.NatureId)), ("$w", (draft.WordType ?? "").Trim()),
            ("$u", (draft.UsageContext ?? "").Trim()), ("$o", (draft.Origin ?? "").Trim()),
            ("$now", UtcNow()), ("$id", id)) > 0);
    }

    /// <summary>Borra la entrada y, en cascada, sus traducciones y su historial de repasos.</summary>
    public bool DeleteEntry(long id) => Write(false, (c, tx) =>
        Execute(c, tx, "DELETE FROM entries WHERE id = $id", ("$id", id)) > 0);

    // ── Traducciones ─────────────────────────────────────────────────────────────────────────

    public IReadOnlyList<Translation> GetTranslations(long entryId) =>
        GetTranslations(new[] { entryId }).TryGetValue(entryId, out var list) ? list : Array.Empty<Translation>();

    /// <summary>Traducciones de MUCHAS entradas en una sola consulta (el quiz las pide de a mazo).
    /// La entrada sin traducciones no aparece en el resultado.</summary>
    public IReadOnlyDictionary<long, IReadOnlyList<Translation>> GetTranslations(IEnumerable<long> entryIds)
    {
        string ids = IdsJson(entryIds);
        return Read<IReadOnlyDictionary<long, IReadOnlyList<Translation>>>(
            new Dictionary<long, IReadOnlyList<Translation>>(), c =>
                Query(c, null,
                        "SELECT entry_id, language_id, text FROM translations " +
                        "WHERE entry_id IN (SELECT value FROM json_each($ids)) ORDER BY entry_id, language_id",
                        r => new Translation(r.GetInt64(0), r.GetInt64(1), r.GetString(2)), ("$ids", ids))
                    .GroupBy(t => t.EntryId)
                    .ToDictionary(g => g.Key, g => (IReadOnlyList<Translation>)g.ToList()));
    }

    /// <summary>
    /// Deja las traducciones de la entrada EXACTAMENTE como <paramref name="textByLanguageId"/>: las
    /// que no vienen (o vienen vacías) se borran. Reemplazo total y no upsert suelto porque el diálogo
    /// de edición guarda la ficha entera; así "vacié el campo" significa "ya no tiene traducción".
    /// </summary>
    public bool SetTranslations(long entryId, IReadOnlyDictionary<long, string> textByLanguageId) => Write(false, (c, tx) =>
    {
        if (!Exists(c, tx, "entries", entryId)) return false;
        Execute(c, tx, "DELETE FROM translations WHERE entry_id = $e", ("$e", entryId));
        foreach (var (languageId, text) in textByLanguageId)
        {
            string clean = (text ?? "").Trim();
            if (clean == "" || !Exists(c, tx, "languages", languageId)) continue;
            Execute(c, tx, "INSERT INTO translations(entry_id, language_id, text) VALUES ($e, $l, $t)",
                ("$e", entryId), ("$l", languageId), ("$t", clean));
        }
        Execute(c, tx, "UPDATE entries SET updated_at = $now WHERE id = $e", ("$now", UtcNow()), ("$e", entryId));
        return true;
    });

    // ── Naturalezas e idiomas (dos listas con nombre único: misma mecánica) ───────────────────

    public IReadOnlyList<Nature> GetNatures() => Read<IReadOnlyList<Nature>>(Array.Empty<Nature>(), c =>
        NamedRows(c, null, "natures").Select(x => new Nature(x.Id, x.Name)).ToList());

    /// <summary>La naturaleza con ese nombre (sin distinguir mayúsculas), creándola si no existe.
    /// null si el nombre está vacío o falló la base.</summary>
    public Nature? GetOrCreateNature(string name) =>
        GetOrCreateNamed("natures", name) is { } x ? new Nature(x.Id, x.Name) : null;

    public KnowledgeOpResult RenameNature(long id, string name) => RenameNamed("natures", id, name);

    /// <summary>Borra la naturaleza. Las entradas y bibliotecas que la usaban NO se borran: vuelven
    /// a null (la entrada pasa a heredar de su biblioteca; la biblioteca, de su biblioteca madre).</summary>
    public bool DeleteNature(long id) => Write(false, (c, tx) =>
        Execute(c, tx, "DELETE FROM natures WHERE id = $id", ("$id", id)) > 0);

    public IReadOnlyList<Language> GetLanguages() => Read<IReadOnlyList<Language>>(Array.Empty<Language>(), c =>
        NamedRows(c, null, "languages").Select(x => new Language(x.Id, x.Name)).ToList());

    public Language? GetOrCreateLanguage(string name) =>
        GetOrCreateNamed("languages", name) is { } x ? new Language(x.Id, x.Name) : null;

    public KnowledgeOpResult RenameLanguage(long id, string name) => RenameNamed("languages", id, name);

    /// <summary>
    /// Borra el idioma con sus traducciones (cascada) Y los repasos jugados desde ese idioma. Esos
    /// repasos guardan el idioma dentro del texto del lado (<c>lang:&lt;id&gt;</c>), fuera del alcance
    /// de una foreign key: si no se barren acá quedan huérfanos, sumando a un lado que ya no existe.
    /// </summary>
    public bool DeleteLanguage(long id) => Write(false, (c, tx) =>
    {
        if (Execute(c, tx, "DELETE FROM languages WHERE id = $id", ("$id", id)) == 0) return false;
        Execute(c, tx, "DELETE FROM reviews WHERE side = $s", ("$s", ReviewSide.ForLanguage(id)));
        return true;
    });

    // ── Repasos ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Anota un repaso. <paramref name="side"/> es el lado que se MOSTRÓ como pregunta (ver
    /// <see cref="ReviewSide"/>). <paramref name="reviewedAtUtc"/> null = ahora. El historial es de
    /// sólo-agregar: nunca se edita ni se resume, porque la solidez se recalcula entera desde él.
    /// </summary>
    public bool AddReview(long entryId, string side, Grade grade, DateTime? reviewedAtUtc = null)
    {
        if (!ReviewSide.IsValid(side) || (int)grade is < 1 or > 4) return false;
        string at = ToDb(reviewedAtUtc ?? DateTime.UtcNow);
        return Write(false, (c, tx) =>
        {
            if (!Exists(c, tx, "entries", entryId)) return false;
            Execute(c, tx, "INSERT INTO reviews(entry_id, side, grade, reviewed_at) VALUES ($e, $s, $g, $at)",
                ("$e", entryId), ("$s", side), ("$g", (int)grade), ("$at", at));
            return true;
        });
    }

    /// <summary>
    /// Todos los repasos de un conjunto de entradas en UNA consulta (no una por entrada), listos para
    /// <c>Solidity.ComputeAll</c>. Los ids viajan como un único parámetro JSON: sin límite de
    /// cantidad de parámetros y sin armar SQL a mano.
    /// </summary>
    public IReadOnlyList<Review> GetReviews(IEnumerable<long> entryIds)
    {
        string ids = IdsJson(entryIds);
        return Read<IReadOnlyList<Review>>(Array.Empty<Review>(), c => Query(c, null,
            "SELECT id, entry_id, side, grade, reviewed_at FROM reviews " +
            "WHERE entry_id IN (SELECT value FROM json_each($ids)) ORDER BY reviewed_at, id",
            ReadReview, ("$ids", ids)));
    }

    /// <summary>El historial completo (estadísticas y evolución en el tiempo).</summary>
    public IReadOnlyList<Review> GetAllReviews() => Read<IReadOnlyList<Review>>(Array.Empty<Review>(), c =>
        Query(c, null, "SELECT id, entry_id, side, grade, reviewed_at FROM reviews ORDER BY reviewed_at, id", ReadReview));

    // ── Apertura, esquema y recuperación ─────────────────────────────────────────────────────

    /// <summary>
    /// Deja la base lista (creada, migrada, sembrada si es nueva). Si falla NO queda marcada como
    /// lista: la próxima operación lo reintenta, así un bloqueo pasajero al arrancar se cura solo.
    /// </summary>
    private bool EnsureReady()
    {
        if (_ready) return true;
        lock (_gate)
        {
            if (_ready) return true;
            // Con el archivo tomado, cada intento de apertura le cuesta a SQLite ~1,5s de reintentos
            // internos, y esto corre en el thread de UI: sin esta pausa, cada tecla del quiz congelaría
            // la app. Entre intentos se degrada al instante.
            if (Environment.TickCount64 < _retryInitAfter) return false;
            _retryInitAfter = Environment.TickCount64 + InitRetryCooldownMs;
            try
            {
                Initialize(seedIfNew: true);
                _ready = true;
            }
            catch (Exception ex) when (IsCorruption(ex))
            {
                // Archivo ilegible. Se APARTA (nunca se borra: puede ser la única copia y alguien
                // podría rescatarla) y se arranca de cero. Sin ejemplo sembrado a propósito: encontrar
                // la biblioteca de muestra donde estaban tus tarjetas se leería como "me las cambiaron".
                string? aside = MoveAside();
                if (aside is null)
                {
                    if (!_corruptReported) Report(new KnowledgeIncident(KnowledgeIncidentKind.CorruptNotMoved, ""));
                    _corruptReported = true;
                    return false;
                }
                Report(new KnowledgeIncident(KnowledgeIncidentKind.CorruptMovedAside, aside));
                try
                {
                    Initialize(seedIfNew: false);
                    _ready = true;
                }
                catch (Exception ex2) { ReportFailure(ex2); }
            }
            catch (Exception ex) { ReportFailure(ex); }
            return _ready;
        }
    }

    private void Initialize(bool seedIfNew)
    {
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        using var c = OpenConnection();

        // Un archivo que no es SQLite recién explota en la primera lectura real (acá). quick_check
        // además caza una base que ABRE pero tiene páginas rotas; con el tamaño de esta base son ms.
        long version = Convert.ToInt64(Scalar(c, null, "PRAGMA user_version"), CultureInfo.InvariantCulture);
        if (!string.Equals(Scalar(c, null, "PRAGMA quick_check(1)") as string, "ok", StringComparison.OrdinalIgnoreCase))
            throw new DamagedDatabaseException();

        if (version >= SchemaVersion) return;

        // Migraciones: una por versión, en orden, todo en UNA transacción junto con el user_version.
        // Si el proceso muere en el medio no queda una base a medio migrar: o la vieja o la nueva.
        using var tx = c.BeginTransaction();
        if (version < 1)
        {
            Execute(c, tx, SchemaV1);
            // El ejemplo se siembra SÓLO en el paso 0→1, que ocurre una única vez en la vida de la
            // base. Una biblioteca que el usuario vació sigue en versión 1: nunca se re-siembra.
            if (seedIfNew) Seed(c, tx);
        }
        Execute(c, tx, $"PRAGMA user_version = {SchemaVersion}");
        tx.Commit();
    }

    // Fechas como texto ISO-8601 UTC: se ordenan bien como texto y se leen a ojo en cualquier visor.
    // nature_id usa ON DELETE SET NULL: borrar una naturaleza no borra contenido, lo devuelve a
    // "heredada". La regla CHECK de grade evita que un bug escriba una nota fuera de 1..4.
    private const string SchemaV1 = """
        CREATE TABLE natures(
            id   INTEGER PRIMARY KEY,
            name TEXT NOT NULL UNIQUE COLLATE NOCASE);

        CREATE TABLE languages(
            id   INTEGER PRIMARY KEY,
            name TEXT NOT NULL UNIQUE COLLATE NOCASE);

        CREATE TABLE libraries(
            id                INTEGER PRIMARY KEY,
            parent_id         INTEGER NULL REFERENCES libraries(id) ON DELETE CASCADE,
            name              TEXT NOT NULL,
            default_nature_id INTEGER NULL REFERENCES natures(id) ON DELETE SET NULL,
            sort              INTEGER NOT NULL DEFAULT 0,
            created_at        TEXT NOT NULL);
        CREATE INDEX ix_libraries_parent ON libraries(parent_id);

        CREATE TABLE entries(
            id            INTEGER PRIMARY KEY,
            library_id    INTEGER NOT NULL REFERENCES libraries(id) ON DELETE CASCADE,
            kind          TEXT NOT NULL CHECK(kind IN ('card', 'term')),
            front         TEXT NOT NULL,
            back          TEXT NOT NULL DEFAULT '',
            nature_id     INTEGER NULL REFERENCES natures(id) ON DELETE SET NULL,
            word_type     TEXT NOT NULL DEFAULT '',
            usage_context TEXT NOT NULL DEFAULT '',
            origin        TEXT NOT NULL DEFAULT '',
            created_at    TEXT NOT NULL,
            updated_at    TEXT NOT NULL);
        CREATE INDEX ix_entries_library ON entries(library_id);
        CREATE INDEX ix_entries_nature ON entries(nature_id);

        CREATE TABLE translations(
            entry_id    INTEGER NOT NULL REFERENCES entries(id) ON DELETE CASCADE,
            language_id INTEGER NOT NULL REFERENCES languages(id) ON DELETE CASCADE,
            text        TEXT NOT NULL,
            PRIMARY KEY(entry_id, language_id));

        CREATE TABLE reviews(
            id          INTEGER PRIMARY KEY,
            entry_id    INTEGER NOT NULL REFERENCES entries(id) ON DELETE CASCADE,
            side        TEXT NOT NULL,
            grade       INTEGER NOT NULL CHECK(grade BETWEEN 1 AND 4),
            reviewed_at TEXT NOT NULL);
        CREATE INDEX ix_reviews_entry ON reviews(entry_id);
        """;

    /// <summary>La base abre pero <c>quick_check</c> encontró páginas rotas.</summary>
    private sealed class DamagedDatabaseException : Exception { }

    private static bool IsCorruption(Exception ex) =>
        ex is DamagedDatabaseException ||
        ex is SqliteException { SqliteErrorCode: SqliteCorrupt or SqliteNotADb };

    /// <summary>Renombra el archivo ilegible a <c>.corrupt-&lt;fecha&gt;</c> (misma convención que
    /// <c>SafeFile</c>). Devuelve el nombre nuevo, o null si no se pudo mover.</summary>
    private string? MoveAside()
    {
        string stamp = DateTime.Now.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        for (int n = 0; n < 10; n++)
        {
            string target = $"{_path}.corrupt-{stamp}" + (n == 0 ? "" : $"-{n}");
            if (File.Exists(target)) continue;
            try { File.Move(_path, target); }
            catch { return null; }

            // Los archivos auxiliares de SQLite se van CON la base. Un -journal viejo que quede al
            // lado de la base nueva sería tomado como una transacción a deshacer sobre ella.
            foreach (string suffix in new[] { "-journal", "-wal", "-shm" })
            {
                try { if (File.Exists(_path + suffix)) File.Move(_path + suffix, target + suffix); }
                catch { try { File.Delete(_path + suffix); } catch { } }
            }
            return Path.GetFileName(target);
        }
        return null;
    }

    // ── Plomería: conexión, guardas y avisos ─────────────────────────────────────────────────

    private SqliteConnection OpenConnection()
    {
        var c = new SqliteConnection(_connectionString);
        try
        {
            c.Open();
            // fold(): la normalización sin tildes de la búsqueda, disponible DENTRO del SQL. Las
            // funciones propias viven en la conexión, así que se registra en cada apertura.
            c.CreateFunction("fold", (string? s) => TextFold.Fold(s), isDeterministic: true);
            return c;
        }
        catch
        {
            c.Dispose(); // nunca dejar un handle a medio abrir (regla 1)
            throw;
        }
    }

    /// <summary>Lectura guardada: ante CUALQUIER fallo devuelve <paramref name="fallback"/> y avisa una vez.</summary>
    private T Read<T>(T fallback, Func<SqliteConnection, T> body)
    {
        if (!EnsureReady()) return fallback;
        try
        {
            using var c = OpenConnection();
            return body(c);
        }
        catch (Exception ex)
        {
            ReportFailure(ex);
            return fallback;
        }
    }

    /// <summary>Escritura guardada, en SU transacción: o entra todo lo que hace <paramref name="body"/>
    /// o no entra nada. Ante un fallo se deshace sola (al soltar la transacción) y devuelve el fallback.</summary>
    private T Write<T>(T fallback, Func<SqliteConnection, SqliteTransaction, T> body)
    {
        if (!EnsureReady()) return fallback;
        try
        {
            using var c = OpenConnection();
            using var tx = c.BeginTransaction();
            T result = body(c, tx);
            tx.Commit();
            return result;
        }
        catch (Exception ex)
        {
            ReportFailure(ex);
            return fallback;
        }
    }

    /// <summary>
    /// Avisa el PRIMER fallo de la sesión y calla los siguientes: con la base caída fallan todas las
    /// operaciones, y un toast por cada tecla del quiz sería peor que el problema.
    /// </summary>
    private void ReportFailure(Exception ex)
    {
        lock (_queuedIncidents)
        {
            if (_failureReported) return;
            _failureReported = true;
        }
        Report(new KnowledgeIncident(KnowledgeIncidentKind.Unavailable, ex.Message));
    }

    private void Report(KnowledgeIncident incident)
    {
        Action<KnowledgeIncident>? notify;
        lock (_queuedIncidents)
        {
            notify = _notify;
            if (notify is null) _queuedIncidents.Add(incident);
        }
        try { notify?.Invoke(incident); } catch { /* el aviso nunca voltea una operación */ }
    }

    // ── Plomería: SQL ────────────────────────────────────────────────────────────────────────

    private static SqliteCommand Command(SqliteConnection c, SqliteTransaction? tx, string sql, (string Name, object? Value)[] args)
    {
        var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd;
    }

    private static int Execute(SqliteConnection c, SqliteTransaction? tx, string sql, params (string, object?)[] args)
    {
        using var cmd = Command(c, tx, sql, args);
        return cmd.ExecuteNonQuery();
    }

    private static object? Scalar(SqliteConnection c, SqliteTransaction? tx, string sql, params (string, object?)[] args)
    {
        using var cmd = Command(c, tx, sql, args);
        return cmd.ExecuteScalar();
    }

    private static List<T> Query<T>(SqliteConnection c, SqliteTransaction? tx, string sql,
        Func<SqliteDataReader, T> map, params (string, object?)[] args)
    {
        using var cmd = Command(c, tx, sql, args);
        using var reader = cmd.ExecuteReader();
        var list = new List<T>();
        while (reader.Read()) list.Add(map(reader));
        return list;
    }

    // `table` es SIEMPRE un literal de esta clase (nunca texto del usuario): por eso puede interpolarse.
    private static bool Exists(SqliteConnection c, SqliteTransaction? tx, string table, long id) =>
        Scalar(c, tx, $"SELECT 1 FROM {table} WHERE id = $id", ("$id", id)) is not null;

    /// <summary>El id si la fila existe; si no, null. Para referencias OPCIONALES (naturaleza): un id
    /// viejo que la UI todavía tenía en pantalla degrada a "heredada" en vez de romper la escritura.</summary>
    private static long? ExistingOrNull(SqliteConnection c, SqliteTransaction? tx, string table, long? id) =>
        id is { } v && Exists(c, tx, table, v) ? v : null;

    private static List<long> DescendantIds(SqliteConnection c, SqliteTransaction? tx, long id) => Query(c, tx,
        "WITH RECURSIVE sub(id) AS (SELECT id FROM libraries WHERE parent_id = $id UNION ALL " +
        "  SELECT l.id FROM libraries l JOIN sub s ON l.parent_id = s.id) SELECT id FROM sub",
        r => r.GetInt64(0), ("$id", id));

    // `IS` y no `=`: con parent_id NULL (raíz) el `=` nunca sería verdadero.
    private static long NextSort(SqliteConnection c, SqliteTransaction? tx, long? parentId) =>
        Convert.ToInt64(Scalar(c, tx, "SELECT COALESCE(MAX(sort), -1) + 1 FROM libraries WHERE parent_id IS $p",
            ("$p", parentId)), CultureInfo.InvariantCulture);

    private static long LastId(SqliteConnection c, SqliteTransaction? tx) =>
        Convert.ToInt64(Scalar(c, tx, "SELECT last_insert_rowid()"), CultureInfo.InvariantCulture);

    private static long InsertLibrary(SqliteConnection c, SqliteTransaction? tx, string name, long? parentId, long? defaultNatureId)
    {
        Execute(c, tx,
            "INSERT INTO libraries(parent_id, name, default_nature_id, sort, created_at) VALUES ($p, $n, $d, $s, $at)",
            ("$p", parentId), ("$n", name), ("$d", defaultNatureId), ("$s", NextSort(c, tx, parentId)), ("$at", UtcNow()));
        return LastId(c, tx);
    }

    private static long InsertEntry(SqliteConnection c, SqliteTransaction? tx, long libraryId, EntryDraft d, string now)
    {
        Execute(c, tx,
            "INSERT INTO entries(library_id, kind, front, back, nature_id, word_type, usage_context, origin, created_at, updated_at) " +
            "VALUES ($l, $k, $f, $b, $n, $w, $u, $o, $now, $now)",
            ("$l", libraryId), ("$k", KindToDb(d.Kind)), ("$f", d.Front.Trim()), ("$b", (d.Back ?? "").Trim()),
            ("$n", ExistingOrNull(c, tx, "natures", d.NatureId)), ("$w", (d.WordType ?? "").Trim()),
            ("$u", (d.UsageContext ?? "").Trim()), ("$o", (d.Origin ?? "").Trim()), ("$now", now));
        return LastId(c, tx);
    }

    // ── Plomería: las dos listas con nombre único ────────────────────────────────────────────

    private static List<(long Id, string Name)> NamedRows(SqliteConnection c, SqliteTransaction? tx, string table) =>
        Query(c, tx, $"SELECT id, name FROM {table} ORDER BY name COLLATE NOCASE, id", r => (r.GetInt64(0), r.GetString(1)));

    // La unicidad se compara en C# y no se le confía al COLLATE NOCASE de la tabla: el NOCASE de
    // SQLite sólo pliega ASCII, así que para él "Técnica" y "TÉCNICA" son dos naturalezas distintas.
    private static (long Id, string Name)? FindNamed(SqliteConnection c, SqliteTransaction? tx, string table, string name)
    {
        foreach (var row in NamedRows(c, tx, table))
            if (string.Equals(row.Name, name, StringComparison.OrdinalIgnoreCase)) return row;
        return null;
    }

    private (long Id, string Name)? GetOrCreateNamed(string table, string name)
    {
        name = (name ?? "").Trim();
        if (name == "") return null;
        return Write<(long, string)?>(null, (c, tx) =>
        {
            if (FindNamed(c, tx, table, name) is { } found) return found;
            Execute(c, tx, $"INSERT INTO {table}(name) VALUES ($n)", ("$n", name));
            return (LastId(c, tx), name);
        });
    }

    private KnowledgeOpResult RenameNamed(string table, long id, string name)
    {
        name = (name ?? "").Trim();
        if (name == "") return KnowledgeOpResult.EmptyName;
        return Write(KnowledgeOpResult.Failed, (c, tx) =>
        {
            if (!Exists(c, tx, table, id)) return KnowledgeOpResult.NotFound;
            // Chocar consigo misma no es choque: así se puede corregir sólo la mayúscula.
            if (FindNamed(c, tx, table, name) is { } other && other.Id != id) return KnowledgeOpResult.NameTaken;
            Execute(c, tx, $"UPDATE {table} SET name = $n WHERE id = $id", ("$n", name), ("$id", id));
            return KnowledgeOpResult.Ok;
        });
    }

    // ── Plomería: conversiones fila ↔ record ─────────────────────────────────────────────────

    private static Library ReadLibrary(SqliteDataReader r) => new(
        r.GetInt64(0), NullableLong(r, 1), r.GetString(2), NullableLong(r, 3), r.GetInt32(4), FromDb(r.GetString(5)));

    private static Entry ReadEntry(SqliteDataReader r) => new(
        r.GetInt64(0), r.GetInt64(1), KindFromDb(r.GetString(2)), r.GetString(3), r.GetString(4), NullableLong(r, 5),
        r.GetString(6), r.GetString(7), r.GetString(8), FromDb(r.GetString(9)), FromDb(r.GetString(10)));

    private static Review ReadReview(SqliteDataReader r) => new(
        r.GetInt64(0), r.GetInt64(1), r.GetString(2), (Grade)r.GetInt32(3), FromDb(r.GetString(4)));

    private static long? NullableLong(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetInt64(i);

    private static string KindToDb(EntryKind kind) => kind == EntryKind.Term ? "term" : "card";
    private static EntryKind KindFromDb(string kind) => kind == "term" ? EntryKind.Term : EntryKind.Card;

    private static string UtcNow() => ToDb(DateTime.UtcNow);

    // Formato "o" (ida y vuelta) siempre en UTC. Una fecha sin Kind se asume UTC — es lo que este
    // dominio maneja — en vez de dejar que .NET la suponga local y la corra unas horas.
    private static string ToDb(DateTime d) =>
        (d.Kind == DateTimeKind.Local ? d.ToUniversalTime() : DateTime.SpecifyKind(d, DateTimeKind.Utc))
        .ToString("o", CultureInfo.InvariantCulture);

    private static DateTime FromDb(string s) =>
        DateTime.TryParse(s, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d)
            ? d
            : DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc); // fila tocada a mano: no rompemos la lista

    private static string IdsJson(IEnumerable<long>? ids) =>
        "[" + string.Join(",", (ids ?? Array.Empty<long>()).Distinct()
            .Select(i => i.ToString(CultureInfo.InvariantCulture))) + "]";
}
