using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using AmpzDesktopBooster.Persistence;

namespace AmpzDesktopBooster.Desktops;

/// <summary>Qué hay cargado en un desk HOY: el espacio y, opcionalmente, su CONTEXTO (sub-scope).</summary>
public readonly record struct DeskAssignment(string Project, string Module);

/// <summary>
/// Por qué salió (o no salió) una operación de reorganización de espacios/contextos.
///
/// Devuelve un MOTIVO y no un bool a propósito: estas operaciones se disparan desde botones, y un
/// botón que no hace nada se lee IGUAL que un botón roto. Con el motivo, la ventana puede decir
/// exactamente qué pasó ("ya hay un contexto con ese nombre", "primero movés sus contextos") en vez
/// de un "no se pudo" que no orienta a nadie.
/// </summary>
public enum ScopeOpResult
{
    Ok,
    /// <summary>El origen o el destino ya no existe (catálogo cambiado por otra vía).</summary>
    NotFound,
    /// <summary>Ya hay un espacio/contexto hermano con ese nombre.</summary>
    NameTaken,
    /// <summary>El nombre quedó vacío después de sanitizar.</summary>
    EmptyName,
    /// <summary>Degradar este espacio anidaría un TERCER nivel: tiene contextos propios.</summary>
    WouldNest,
    /// <summary>Origen y destino son el mismo — no hay nada que mover.</summary>
    SameTarget,
}

/// <summary>
/// Orquesta las TRES capas de "espacios por desk" del legacy (ver CLAUDE.md del legacy):
///   1. Sesión (_session)      — qué espacio/contexto está en qué desk HOY. Efímero, se pierde al cerrar.
///   2. Sugerencias (INI)      — última asignación por desk, para pre-llenar el setter el próximo día.
///   3. Catálogo (JSON)        — history + modules + paths + notes durables. Se carga al arrancar.
///
/// Regla de oro del legacy: la sesión NUNCA se rellena del INI al arrancar (sería confuso ver
/// espacios de ayer sin confirmar). El INI sólo pre-llena el textbox del setter.
///
/// CONTEXTOS (sub-scope, agregado sobre el legacy): un espacio puede tener contextos ("Geocontrol" →
/// "Plataforma" / "App Mobile"). Sus variables y notas viven bajo la key compuesta "Espacio/Contexto"
/// (<see cref="ScopeKey"/>), lo que deja el shape del JSON intacto y hace que TODO lo que ya operaba
/// sobre una key de espacio siga funcionando tal cual sobre una de contexto.
/// </summary>
public sealed class ProjectStore
{
    /// <summary>
    /// Separador de la key compuesta "Espacio/Contexto". El "/" está PROHIBIDO en los nombres
    /// (<see cref="Sanitize"/> lo saca al confirmar), así la key nunca es ambigua al partirla.
    /// </summary>
    public const char ScopeSeparator = '/';

    private readonly IniFile _ini = new(AppPaths.SettingsIni);
    private readonly string _jsonPath = AppPaths.ProjectDataFile;
    private readonly Dictionary<int, DeskAssignment> _session = new();
    private ProjectData _data;

    public ProjectData Data => _data;

    public ProjectStore()
    {
        _data = Load();
        MigrateLegacyDefaults(); // predeterminados de la entrada → por scope (una sola vez)
    }

    // ── Sesión: espacio + contexto activos por desk (efímero) ───────────────────

    public string GetDeskProject(int idx) => _session.TryGetValue(idx, out var a) ? a.Project : "";

    /// <summary>Contexto activo del desk, o "" si el espacio está cargado sin contexto.</summary>
    public string GetDeskModule(int idx) => _session.TryGetValue(idx, out var a) ? a.Module : "";

    /// <summary>
    /// Contexto del desk resuelto para la UI: nombre + color. Es lo que consumen la barra, el overlay
    /// y los pickers — resolver el color acá (y no en cada ventana) mantiene UNA sola fuente de verdad.
    /// </summary>
    public DeskModule GetDeskModuleInfo(int idx)
    {
        if (!_session.TryGetValue(idx, out var a) || a.Module == "")
            return DeskModule.None;
        return new DeskModule(a.Module, GetModuleColor(a.Project, a.Module));
    }

    /// <summary>
    /// Confirma el espacio del desk: lo guarda en sesión, en sugerencias (INI) e historial.
    /// CAMBIAR de espacio LIMPIA el contexto: un contexto pertenece a SU espacio, arrastrar
    /// "Plataforma" de un cliente al siguiente sería exactamente la confusión que vinimos a matar.
    /// Re-confirmar el MISMO espacio conserva el contexto (no es un cambio de contexto).
    /// </summary>
    public void SetDeskProject(int idx, string name)
    {
        name = TitleCase(Sanitize(name));
        if (name == "") { RemoveDeskProject(idx); return; }

        string module = string.Equals(GetDeskProject(idx), name, StringComparison.OrdinalIgnoreCase)
            ? GetDeskModule(idx)
            : "";

        _session[idx] = new DeskAssignment(name, module);
        _ini.Write("Projects", "desk_" + idx, name);
        _ini.Write("Projects", $"desk_{idx}_module", module);

        // El historial se mantiene alineado con la forma NORMALIZADA. Antes sólo evitaba duplicados
        // (comparando case-insensitive) pero dejaba la entrada vieja con su casing original: el
        // historial quedaba mostrando "Ampz desktop Booster" mientras la sesión, el INI y las keys del
        // catálogo usaban "Ampz Desktop Booster". Esa divergencia es la que hacía que el picker de
        // contextos apareciera vacío al llegar desde el setter, y la que dejaría huérfanos al borrar del
        // historial (DeleteFromHistory borra paths/notas/contextos por string exacto).
        int at = _data.History.FindIndex(h => string.Equals(h, name, StringComparison.OrdinalIgnoreCase));
        if (at < 0)
        {
            _data.History.Add(name);
            Save();
        }
        else if (_data.History[at] != name)
        {
            _data.History[at] = name; // re-alinea la casing de una entrada vieja
            Save();
        }
    }

    /// <summary>
    /// Setea (o limpia, con "") el contexto del desk. No-op si el desk no tiene espacio: un contexto
    /// SIN espacio padre no significa nada — el scope compuesto no se podría resolver.
    /// Da de alta el contexto en el catálogo del espacio si es nuevo (con color auto-asignado).
    /// </summary>
    public void SetDeskModule(int idx, string module)
    {
        string project = GetDeskProject(idx);
        if (project == "") return;

        module = TitleCase(Sanitize(module));
        if (module != "") EnsureModule(project, module);

        _session[idx] = new DeskAssignment(project, module);
        _ini.Write("Projects", $"desk_{idx}_module", module);
    }

    /// <summary>Saca espacio Y contexto del desk SÓLO en la sesión (no toca historial ni catálogo).</summary>
    public void RemoveDeskProject(int idx) => _session.Remove(idx);

    public void ClearAllSession() => _session.Clear();

    public IEnumerable<(int Idx, string Project, string Module)> SessionEntries() =>
        _session.Select(kv => (kv.Key, kv.Value.Project, kv.Value.Module));

    /// <summary>Sugerencia persistida para pre-llenar el setter de este desk (o "").</summary>
    public string GetSuggestion(int idx) => _ini.Read("Projects", "desk_" + idx, "");

    /// <summary>Sugerencia de CONTEXTO del desk — pre-llena el picker igual que la de espacio.</summary>
    public string GetModuleSuggestion(int idx) => _ini.Read("Projects", $"desk_{idx}_module", "");

    // ── Historial / catálogo ───────────────────────────────────────────────────

    public IReadOnlyList<string> GetHistory() => _data.History;

    // ── Contextos (sub-scopes de un espacio) ────────────────────────────────────

    /// <summary>Key de catálogo de un scope: el espacio solo, o "Espacio/Contexto" si hay contexto.</summary>
    public static string ScopeKey(string project, string module) =>
        string.IsNullOrEmpty(module) ? project : project + ScopeSeparator + module;

    /// <summary>Key compuesta → etiqueta legible ("Geocontrol/Plataforma" → "Geocontrol / Plataforma").</summary>
    public static string PrettyScope(string key) =>
        key.Replace(ScopeSeparator.ToString(), " " + ScopeSeparator + " ");

    /// <summary>
    /// Saca el separador de scope y colapsa espacios de un nombre tipeado por el usuario. Sin esto,
    /// un espacio llamado "A/B" generaría una key compuesta FALSA e indistinguible de un contexto real.
    /// </summary>
    public static string Sanitize(string s) => s.Replace(ScopeSeparator, ' ').Trim();

    /// <summary>
    /// Key REAL del catálogo de contextos para este espacio, resolviendo diferencias de mayúsculas.
    /// Los diccionarios de <see cref="ProjectData"/> son case-SENSITIVE, y el nombre de un espacio
    /// puede llegar con otra casing desde el historial (entradas viejas, anteriores al TitleCase).
    /// Sin esto, "Ampz desktop Booster" y "Ampz Desktop Booster" son dos catálogos distintos: uno
    /// con tus contextos y otro vacío, según por qué camino hayas entrado. Devuelve el nombre tal cual
    /// si todavía no existe (para que el alta lo cree con la casing actual).
    /// </summary>
    private string ResolveModulesKey(string project) =>
        _data.Modules.Keys.FirstOrDefault(k => string.Equals(k, project, StringComparison.OrdinalIgnoreCase))
        ?? project;

    /// <summary>Contextos catalogados de un espacio (lista vacía si no tiene).</summary>
    public IReadOnlyList<ModuleEntry> GetModules(string project) =>
        _data.Modules.TryGetValue(ResolveModulesKey(project), out var list) ? list : new List<ModuleEntry>();

    /// <summary>Color "#RRGGBB" del contexto, o "" si no está catalogado (la UI cae al dorado).</summary>
    public string GetModuleColor(string project, string module) =>
        GetModules(project).FirstOrDefault(m =>
            string.Equals(m.Name, module, StringComparison.OrdinalIgnoreCase))?.Color ?? "";

    /// <summary>
    /// Da de alta el contexto en el catálogo del espacio si no existía, con el primer color LIBRE de
    /// la paleta (ver <see cref="ModulePalette.NextFree"/>). Idempotente: si ya existe, no toca nada.
    /// </summary>
    public ModuleEntry EnsureModule(string project, string module)
    {
        project = ResolveModulesKey(project); // no crear un catálogo paralelo por diferencia de casing
        if (!_data.Modules.TryGetValue(project, out var list))
        {
            list = new List<ModuleEntry>();
            _data.Modules[project] = list;
        }

        var existing = list.FirstOrDefault(m => string.Equals(m.Name, module, StringComparison.OrdinalIgnoreCase));
        if (existing is not null) return existing;

        var entry = new ModuleEntry { Name = module, Color = ModulePalette.NextFree(list.Select(m => m.Color)) };
        list.Add(entry);
        Save();
        return entry;
    }

    /// <summary>Cambia el color de un contexto (el F3 del picker cicla la paleta).</summary>
    public void SetModuleColor(string project, string module, string color)
    {
        var entry = GetModules(project).FirstOrDefault(m =>
            string.Equals(m.Name, module, StringComparison.OrdinalIgnoreCase));
        if (entry is null) return;
        entry.Color = color;
        Save();
    }

    /// <summary>
    /// Borra un contexto EN CASCADA, con el mismo criterio que <see cref="DeleteFromHistory"/>: lo saca
    /// del catálogo, borra sus variables y notas (la key compuesta) y limpia cualquier desk de la
    /// sesión que lo tuviera activo — ese desk queda en el espacio pelado, no huérfano.
    /// </summary>
    public void DeleteModule(string project, string module)
    {
        if (_data.Modules.TryGetValue(ResolveModulesKey(project), out var list))
            list.RemoveAll(m => string.Equals(m.Name, module, StringComparison.OrdinalIgnoreCase));

        string key = ScopeKey(project, module);
        _data.Paths.Remove(key);
        _data.Notes.Remove(key);
        _data.Defaults.Remove(key); // sin scope no hay predeterminado que resolver

        foreach (var idx in _session
                     .Where(kv => string.Equals(kv.Value.Project, project, StringComparison.OrdinalIgnoreCase)
                               && string.Equals(kv.Value.Module, module, StringComparison.OrdinalIgnoreCase))
                     .Select(kv => kv.Key).ToList())
            _session[idx] = new DeskAssignment(project, "");

        Save();
    }

    // ── Pools de variables (paths/URLs) ─────────────────────────────────────────

    /// <summary>
    /// Pool de variables de un scope (crea la lista si no existía). <paramref name="project"/> puede
    /// ser un espacio pelado o una key compuesta "Espacio/Contexto" (<see cref="ScopeKey"/>) — de ahí
    /// que todo lo que ya operaba sobre espacios funcione igual sobre contextos, sin ramas nuevas.
    /// El Label sale SIEMPRE por <see cref="PrettyScope"/> para que coincida con el de
    /// <see cref="GetAllProjectPools"/> (el caller compara pools por Label para no duplicarlas).
    /// </summary>
    public PathPool GetProjectPool(string project)
    {
        if (!_data.Paths.TryGetValue(project, out var list))
        {
            list = new List<PathEntry>();
            _data.Paths[project] = list;
        }
        return new PathPool(list, Save, PrettyScope(project));
    }

    /// <summary>Pool GLOBAL compartida — la usan los desks sin espacio (MAIN/CONSOLES/MISCS/DESK+ vacío).</summary>
    public PathPool GetSharedPool() => new(_data.SharedPaths, Save, "Global");

    /// <summary>
    /// TODAS las pools de espacio del catálogo (una por key de <c>_data.Paths</c>), para el toggle
    /// "ver todos los espacios" del Paths Manager. NO incluye la global (esa se anexa por su lado) ni
    /// excluye el espacio actual — eso queda a cargo del caller, que es quien conoce el contexto del
    /// desk. El Label de cada pool es el nombre del espacio (sirve de rótulo de sección en la vista).
    /// </summary>
    public IReadOnlyList<PathPool> GetAllProjectPools()
    {
        var list = new List<PathPool>();
        foreach (var kv in _data.Paths)
            list.Add(new PathPool(kv.Value, Save, PrettyScope(kv.Key)));
        return list;
    }

    /// <summary>
    /// Resuelve QUÉ pool corresponde según el desk actual. Con contexto activo la pool primaria es la
    /// DEL CONTEXTO (key compuesta); si no, la del espacio; y en cualquier otro caso, la global.
    /// </summary>
    public PathPool ResolvePool(string deskName, int deskIdx)
    {
        return UseProjectScope(deskName, deskIdx, out var project, out var module)
            ? GetProjectPool(ScopeKey(project, module))
            : GetSharedPool();
    }

    /// <summary>
    /// Como <see cref="ResolvePool"/>, pero además expone las pools HEREDADAS que la ventana anexa
    /// de SOLO-LECTURA, para no quedar ciego a lo que no es de tu scope exacto. Es la herencia de
    /// tres niveles: <b>contexto → espacio → global</b>.
    ///   · Con contexto:      primaria = contexto, <paramref name="parent"/> = espacio, global = global.
    ///   · Sin contexto:      primaria = espacio, parent = null, global = global.
    ///   · Scope global:    primaria = global, parent y global = null (ya la estás viendo como primaria).
    /// La regla vive acá, en la capa que la conoce — la ventana no la re-implementa.
    /// </summary>
    public PathPool ResolvePoolWithGlobal(string deskName, int deskIdx, out PathPool? global, out PathPool? parent)
    {
        if (UseProjectScope(deskName, deskIdx, out var project, out var module))
        {
            global = GetSharedPool();
            // Con contexto, el espacio es el "padre" del que se hereda; sin contexto no hay nada arriba.
            parent = module == "" ? null : GetProjectPool(project);
            return GetProjectPool(ScopeKey(project, module));
        }
        global = null;
        parent = null;
        return GetSharedPool();
    }

    // ── Predeterminado POR SCOPE ───────────────────────────────────────────────
    // El predeterminado NO es una propiedad de la variable, es una decisión del CONTEXTO en el que
    // estás parado. Con contextos eso se volvió obligatorio: una entrada del espacio la ven todos sus
    // contextos, así que un flag en la entrada hacía que marcarla desde "App Mobile" se la cambiara
    // también a "Plataforma" (mismo objeto, no propagación). Guardamos el PATH elegido por scope.

    /// <summary>Key de scope para la pool GLOBAL. Vacío: un espacio nunca puede llamarse así.</summary>
    public const string GlobalScope = "";

    /// <summary>Path predeterminado de un scope, o null si ese scope no eligió ninguno.</summary>
    public string? GetScopeDefault(string scopeKey)
    {
        string v = scopeKey == GlobalScope
            ? _data.SharedDefault
            : _data.Defaults.TryGetValue(scopeKey, out var d) ? d : "";
        return v == "" ? null : v;
    }

    /// <summary>Fija (o limpia, con null) el predeterminado de un scope y persiste.</summary>
    public void SetScopeDefault(string scopeKey, string? path)
    {
        if (scopeKey == GlobalScope)
            _data.SharedDefault = path ?? "";
        else if (path is null)
            _data.Defaults.Remove(scopeKey);
        else
            _data.Defaults[scopeKey] = path;
        Save();
    }

    /// <summary>
    /// Key de scope del desk: "" si es global, el espacio, o "Espacio/Contexto". Es lo que la
    /// ventana de Variables necesita para saber DÓNDE guardar el predeterminado que marques.
    /// </summary>
    public string ResolveScopeKey(string deskName, int deskIdx) =>
        UseProjectScope(deskName, deskIdx, out var project, out var module)
            ? ScopeKey(project, module)
            : GlobalScope;

    /// <summary>
    /// Key del scope PADRE del que se hereda, o null si no hay (sin contexto, o scope global). Sólo
    /// existe un nivel de herencia de predeterminado: contexto → espacio.
    /// </summary>
    public string? ResolveParentScopeKey(string deskName, int deskIdx) =>
        UseProjectScope(deskName, deskIdx, out var project, out var module) && module != ""
            ? project
            : null;

    /// <summary>
    /// Migra los archivos anteriores a los predeterminados por scope: el flag vivía en la entrada
    /// (<see cref="PathEntry.Default"/>). Corre una sola vez — si ya hay predeterminados por scope,
    /// no toca nada. Después de migrar LIMPIA los flags viejos para no dejar dos fuentes de verdad.
    /// </summary>
    private void MigrateLegacyDefaults()
    {
        if (_data.Defaults.Count > 0 || _data.SharedDefault != "") return;

        bool migrated = false;

        foreach (var (key, list) in _data.Paths)
        {
            var def = list.FirstOrDefault(e => e.Default);
            if (def is not null && def.Path != "")
            {
                _data.Defaults[key] = def.Path;
                migrated = true;
            }
            foreach (var e in list) e.Default = false;
        }

        var shared = _data.SharedPaths.FirstOrDefault(e => e.Default);
        if (shared is not null && shared.Path != "")
        {
            _data.SharedDefault = shared.Path;
            migrated = true;
        }
        foreach (var e in _data.SharedPaths) e.Default = false;

        if (migrated) Save(); // sólo escribimos si de verdad había algo que migrar
    }

    // ── Notas (mismo dual-scope que las variables) ──────────────────────────────

    /// <summary>
    /// Lee las notas que correspondan al desk. A diferencia de las variables, las notas NO heredan:
    /// con contexto activo ves las DEL CONTEXTO y punto. Es deliberado — una nota es una pizarra de
    /// trabajo, y mezclarle la del espacio la volvería un cajón de sastre imposible de escanear.
    /// </summary>
    public string GetNotes(string deskName, int deskIdx)
    {
        if (UseProjectScope(deskName, deskIdx, out var project, out var module))
            return _data.Notes.TryGetValue(ScopeKey(project, module), out var n) ? n : "";
        return _data.SharedNotes;
    }

    /// <summary>Guarda las notas en el scope que corresponda (contexto, espacio o global) y persiste.</summary>
    public void SetNotes(string deskName, int deskIdx, string text)
    {
        if (UseProjectScope(deskName, deskIdx, out var project, out var module))
            _data.Notes[ScopeKey(project, module)] = text;
        else
            _data.SharedNotes = text;
        Save();
    }

    // ── Notas de CARPETA (ligadas al disco, no al desk/espacio) ────────────────

    /// <summary>
    /// Key estable de una carpeta para sus notas: el NOMBRE de la carpeta hoja, en minúsculas.
    /// Decisión de diseño (a propósito NO usamos el path completo): así mover o renombrar el path
    /// base — pasar el repo de Desktop a D:\, por ejemplo — NO pierde las notas. El nombre de un
    /// repo es único en la práctica, que es el caso de uso real ("le anoto detalles a un repo").
    /// Contra: dos carpetas distintas con el MISMO nombre comparten notas — caso muy raro y, por eso,
    /// la ventana muestra el path completo en el subtítulo para que la ambigüedad sea visible.
    /// Si algún día molesta, migrar a path-exacto es cambiar SOLO esta función.
    /// </summary>
    public static string FolderKey(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        var name = Path.GetFileName(path.TrimEnd('\\', '/', ' '));
        return name.ToLowerInvariant();
    }

    /// <summary>Lee las notas de una carpeta (por su <see cref="FolderKey"/>), o "" si no tiene.</summary>
    public string GetFolderNotes(string path)
    {
        var key = FolderKey(path);
        if (key == "") return "";
        return _data.FolderNotes.TryGetValue(key, out var n) ? n : "";
    }

    /// <summary>
    /// Guarda las notas de una carpeta. Si el texto queda vacío, borra la entrada en vez de dejar
    /// una key muerta — así el catálogo no se llena de carpetas que abriste de paso y no anotaste.
    /// </summary>
    public void SetFolderNotes(string path, string text)
    {
        var key = FolderKey(path);
        if (key == "") return;
        if (string.IsNullOrEmpty(text))
            _data.FolderNotes.Remove(key);
        else
            _data.FolderNotes[key] = text;
        Save();
    }

    /// <summary>Etiqueta del scope para el header: "Espacio", "Espacio / Contexto" o "Global".</summary>
    public string ScopeLabel(string deskName, int deskIdx) =>
        UseProjectScope(deskName, deskIdx, out var project, out var module)
            ? PrettyScope(ScopeKey(project, module))
            : "Global";

    /// <summary>
    /// true si el desk usa scope de espacio (DESK +N con espacio activo). <paramref name="module"/>
    /// sale "" cuando el espacio está cargado sin contexto — la key compuesta degrada sola al espacio.
    /// </summary>
    private bool UseProjectScope(string deskName, int deskIdx, out string project, out string module)
    {
        bool isProjectDesk = deskName.Contains("DESK +", StringComparison.OrdinalIgnoreCase);
        project = GetDeskProject(deskIdx);
        module = project == "" ? "" : GetDeskModule(deskIdx);
        return isProjectDesk && project != "";
    }

    /// <summary>
    /// Borra un espacio del historial EN CASCADA: lo saca de history, paths, notes y del catálogo de
    /// contextos, limpia cualquier sesión que apuntara a él, y persiste. Sin huérfanos (igual que el
    /// legacy). Los CONTEXTOS del espacio se van con él: sus keys compuestas arrancan con "Espacio/",
    /// así que se barren por prefijo — si no, quedarían pools fantasma sin dueño en el JSON.
    /// </summary>
    public void DeleteFromHistory(string name)
    {
        _data.History.RemoveAll(h => string.Equals(h, name, StringComparison.OrdinalIgnoreCase));
        _data.Paths.Remove(name);
        _data.Notes.Remove(name);
        _data.Modules.Remove(name);
        _data.Defaults.Remove(name);

        string prefix = name + ScopeSeparator;
        foreach (var key in _data.Paths.Keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList())
            _data.Paths.Remove(key);
        foreach (var key in _data.Notes.Keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList())
            _data.Notes.Remove(key);
        foreach (var key in _data.Defaults.Keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList())
            _data.Defaults.Remove(key);

        foreach (var idx in _session
                     .Where(kv => string.Equals(kv.Value.Project, name, StringComparison.OrdinalIgnoreCase))
                     .Select(kv => kv.Key).ToList())
            _session.Remove(idx);

        Save();
    }

    // ── Gestión de Espacios y Contextos (la pestaña de config) ─────────────────────────────────
    //
    // Hasta acá el catálogo sólo sabía CREAR (setter / picker) y BORRAR en cascada. Reorganizar
    // —renombrar, mover un contexto a otro espacio, promoverlo, degradar un espacio— había que
    // hacerlo editando el JSON a mano. Y no es un caso raro: la estructura mental del usuario cambia
    // (todo este bloque nace de que 11 "espacios" resultaron ser contextos de dos espacios).
    //
    // Disciplina común a TODAS: si una key de scope se mueve, se mueve en TODOS lados —variables,
    // notas, predeterminados, catálogo de contextos, sesión y sugerencias del INI— o no se mueve en
    // ninguno. Media migración deja variables huérfanas que el usuario NO puede encontrar ni borrar
    // desde la UI: quedan vivas en el JSON, colgando de un scope que ya no existe.

    /// <summary>Cuántos desks escanea al reescribir sugerencias. Cubre de sobra el set gestionado.</summary>
    private const int SuggestionScanRange = 32;

    /// <summary>Comparación de nombres del dominio: SIEMPRE case-insensitive (el usuario los tipea).</summary>
    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// La key REAL del diccionario que coincide (case-insensitive), o null. Los dicts de ProjectData
    /// son case-SENSITIVE: sin esto, "Synxs" y "synxs" son dos scopes distintos y una operación
    /// movería uno dejando vivo al otro.
    /// </summary>
    private static string? FindKey<T>(Dictionary<string, T> dict, string key) =>
        dict.Keys.FirstOrDefault(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase));

    /// <summary>Mueve el valor de una key a otra. No-op si el origen no existe.</summary>
    private static void MoveKey<T>(Dictionary<string, T> dict, string from, string to)
    {
        string? src = FindKey(dict, from);
        if (src is null) return;
        var value = dict[src];
        dict.Remove(src);
        if (FindKey(dict, to) is string clash) dict.Remove(clash);
        dict[to] = value;
    }

    /// <summary>Re-prefija TODAS las keys compuestas que cuelgan de un espacio ("Viejo/X" → "Nuevo/X").</summary>
    private static void MovePrefix<T>(Dictionary<string, T> dict, string oldPrefix, string newPrefix)
    {
        foreach (var key in dict.Keys
                     .Where(k => k.StartsWith(oldPrefix, StringComparison.OrdinalIgnoreCase)).ToList())
            MoveKey(dict, key, newPrefix + key[oldPrefix.Length..]);
    }

    /// <summary>Mueve una key de scope en los TRES diccionarios que la usan. Un solo lugar, sin olvidos.</summary>
    private void MoveScope(string fromKey, string toKey)
    {
        MoveKey(_data.Paths, fromKey, toKey);
        MoveKey(_data.Notes, fromKey, toKey);
        MoveKey(_data.Defaults, fromKey, toKey);
    }

    /// <summary>Reescribe la sesión en vivo: los desks que apuntaban al scope viejo siguen al nuevo.</summary>
    private void MapSession(Func<string, string, DeskAssignment> map)
    {
        foreach (var idx in _session.Keys.ToList())
        {
            var a = _session[idx];
            _session[idx] = map(a.Project, a.Module);
        }
    }

    /// <summary>
    /// Idem para las sugerencias del INI. Si no se tocan, mañana el setter te pre-llena con un nombre
    /// que ya no existe — y el picker de contextos aparece vacío sin que se entienda por qué.
    /// </summary>
    private void MapSuggestions(Func<string, string, (string Project, string Module)> map)
    {
        for (int i = 0; i < SuggestionScanRange; i++)
        {
            string p = _ini.Read("Projects", "desk_" + i, "");
            if (p == "") continue;
            string m = _ini.Read("Projects", $"desk_{i}_module", "");
            var (np, nm) = map(p, m);
            if (np != p) _ini.Write("Projects", "desk_" + i, np);
            if (nm != m) _ini.Write("Projects", $"desk_{i}_module", nm);
        }
    }

    /// <summary>true si ya existe un espacio con ese nombre.</summary>
    public bool ProjectExists(string name) => _data.History.Any(h => Same(h, name));

    /// <summary>true si el espacio ya tiene un contexto con ese nombre.</summary>
    public bool ModuleExists(string project, string module) => GetModules(project).Any(m => Same(m.Name, module));

    /// <summary>Renombra un espacio y arrastra TODO: sus variables, notas, predeterminados y contextos.</summary>
    public ScopeOpResult RenameProject(string oldName, string newName)
    {
        newName = TitleCase(Sanitize(newName));
        if (newName == "") return ScopeOpResult.EmptyName;
        if (!ProjectExists(oldName)) return ScopeOpResult.NotFound;
        if (!Same(oldName, newName) && ProjectExists(newName)) return ScopeOpResult.NameTaken;

        int at = _data.History.FindIndex(h => Same(h, oldName));
        if (at >= 0) _data.History[at] = newName;

        MoveScope(oldName, newName);                 // la key propia del espacio
        MoveKey(_data.Modules, oldName, newName);    // su catálogo de contextos

        // …y las compuestas de CADA contexto: si esto falta, todas sus variables quedan huérfanas.
        string oldPrefix = oldName + ScopeSeparator, newPrefix = newName + ScopeSeparator;
        MovePrefix(_data.Paths, oldPrefix, newPrefix);
        MovePrefix(_data.Notes, oldPrefix, newPrefix);
        MovePrefix(_data.Defaults, oldPrefix, newPrefix);

        MapSession((p, m) => new DeskAssignment(Same(p, oldName) ? newName : p, m));
        MapSuggestions((p, m) => (Same(p, oldName) ? newName : p, m));
        Save();
        return ScopeOpResult.Ok;
    }

    /// <summary>Renombra un contexto dentro de su espacio. Conserva color, variables y notas.</summary>
    public ScopeOpResult RenameModule(string project, string oldName, string newName)
    {
        newName = TitleCase(Sanitize(newName));
        if (newName == "") return ScopeOpResult.EmptyName;
        if (!ModuleExists(project, oldName)) return ScopeOpResult.NotFound;
        if (!Same(oldName, newName) && ModuleExists(project, newName)) return ScopeOpResult.NameTaken;

        var entry = GetModules(project).First(m => Same(m.Name, oldName));
        entry.Name = newName; // la entrada es la MISMA referencia que vive en el catálogo

        MoveScope(ScopeKey(project, oldName), ScopeKey(project, newName));
        MapSession((p, m) => Same(p, project) && Same(m, oldName)
            ? new DeskAssignment(p, newName) : new DeskAssignment(p, m));
        MapSuggestions((p, m) => Same(p, project) && Same(m, oldName) ? (p, newName) : (p, m));
        Save();
        return ScopeOpResult.Ok;
    }

    /// <summary>
    /// Mueve un contexto a OTRO espacio. Ojo con lo que NO viaja: sus variables propias sí, pero las
    /// que HEREDABA del espacio viejo no — pasan a ser las del nuevo. Es lo correcto (heredar del
    /// padre anterior sería arrastrar el contexto del cliente que dejaste), pero hay que avisarlo.
    /// </summary>
    public ScopeOpResult MoveModule(string fromProject, string module, string toProject)
    {
        if (Same(fromProject, toProject)) return ScopeOpResult.SameTarget;
        if (!ModuleExists(fromProject, module)) return ScopeOpResult.NotFound;
        if (!ProjectExists(toProject)) return ScopeOpResult.NotFound;
        if (ModuleExists(toProject, module)) return ScopeOpResult.NameTaken;

        var src = _data.Modules[ResolveModulesKey(fromProject)];
        var entry = src.First(m => Same(m.Name, module));
        src.Remove(entry);

        string dstKey = ResolveModulesKey(toProject);
        if (!_data.Modules.TryGetValue(dstKey, out var dst))
        {
            dst = new List<ModuleEntry>();
            _data.Modules[dstKey] = dst;
        }
        // Si un hermano del destino ya usa ese color, se le da el primer libre: dos contextos del
        // MISMO espacio con el mismo color es exactamente la confusión que el color vino a matar.
        if (dst.Any(m => Same(m.Color, entry.Color)))
            entry.Color = ModulePalette.NextFree(dst.Select(m => m.Color));
        dst.Add(entry);

        MoveScope(ScopeKey(fromProject, module), ScopeKey(toProject, module));
        MapSession((p, m) => Same(p, fromProject) && Same(m, module)
            ? new DeskAssignment(toProject, module) : new DeskAssignment(p, m));
        MapSuggestions((p, m) => Same(p, fromProject) && Same(m, module) ? (toProject, module) : (p, m));
        Save();
        return ScopeOpResult.Ok;
    }

    /// <summary>
    /// Promueve un contexto a espacio propio. PIERDE el color: los colores identifican contextos
    /// DENTRO de un espacio; un espacio no tiene color (decisión explícita del modelo).
    /// </summary>
    public ScopeOpResult PromoteModule(string project, string module)
    {
        if (!ModuleExists(project, module)) return ScopeOpResult.NotFound;
        if (ProjectExists(module)) return ScopeOpResult.NameTaken;

        _data.Modules[ResolveModulesKey(project)].RemoveAll(m => Same(m.Name, module));
        MoveScope(ScopeKey(project, module), module);
        _data.History.Add(module);

        MapSession((p, m) => Same(p, project) && Same(m, module)
            ? new DeskAssignment(module, "") : new DeskAssignment(p, m));
        MapSuggestions((p, m) => Same(p, project) && Same(m, module) ? (module, "") : (p, m));
        Save();
        return ScopeOpResult.Ok;
    }

    /// <summary>
    /// Degrada un espacio a contexto de otro. Se RECHAZA si el espacio tiene contextos propios:
    /// serían un tercer nivel ("A/B/C"), que el modelo no tiene — y aplanarlos en silencio sería
    /// destruir su jerarquía sin avisar. El usuario primero mueve o promueve esos contextos.
    /// </summary>
    public ScopeOpResult DemoteProject(string project, string toProject)
    {
        if (Same(project, toProject)) return ScopeOpResult.SameTarget;
        if (!ProjectExists(project) || !ProjectExists(toProject)) return ScopeOpResult.NotFound;
        if (GetModules(project).Count > 0) return ScopeOpResult.WouldNest;
        if (ModuleExists(toProject, project)) return ScopeOpResult.NameTaken;

        _data.History.RemoveAll(h => Same(h, project));
        if (FindKey(_data.Modules, project) is string emptyCatalog) _data.Modules.Remove(emptyCatalog);

        MoveScope(project, ScopeKey(toProject, project));
        EnsureModule(toProject, project); // alta en el catálogo del destino, con color libre

        MapSession((p, m) => Same(p, project) ? new DeskAssignment(toProject, project) : new DeskAssignment(p, m));
        MapSuggestions((p, m) => Same(p, project) ? (toProject, project) : (p, m));
        Save();
        return ScopeOpResult.Ok;
    }

    /// <summary>
    /// Capitaliza la primera letra de cada palabra: "space consortium" → "Space Consortium".
    /// Se aplica al confirmar un espacio nuevo, así el nombre queda normalizado en TODAS las
    /// capas (sesión, historial, INI) — un solo punto de verdad, sin sorpresas de mayúsculas.
    /// Respeta espacios múltiples y separadores; sólo toca la 1ra letra de cada token.
    /// </summary>
    public static string TitleCase(string s)
    {
        if (string.IsNullOrWhiteSpace(s))
            return "";
        var chars = s.ToCharArray();
        bool startOfWord = true;
        for (int i = 0; i < chars.Length; i++)
        {
            if (char.IsWhiteSpace(chars[i]))
            {
                startOfWord = true;
            }
            else if (startOfWord)
            {
                chars[i] = char.ToUpper(chars[i], CultureInfo.CurrentCulture);
                startOfWord = false;
            }
        }
        return new string(chars);
    }

    // ── Persistencia del catálogo ──────────────────────────────────────────────

    private ProjectData Load()
    {
        try
        {
            if (File.Exists(_jsonPath))
            {
                var json = File.ReadAllText(_jsonPath);
                var loaded = JsonSerializer.Deserialize<ProjectData>(json);
                if (loaded is not null) return loaded;
            }
        }
        catch { /* JSON corrupto → arrancamos con catálogo vacío, no crasheamos */ }
        return new ProjectData();
    }

    public void Save()
    {
        try
        {
            var opts = new JsonSerializerOptions { WriteIndented = true };
            File.WriteAllText(_jsonPath, JsonSerializer.Serialize(_data, opts));
        }
        catch { /* disco/permisos → seguimos en memoria */ }
    }
}
