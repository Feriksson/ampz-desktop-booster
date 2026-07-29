using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using AmpzDesktopBooster.Persistence;

namespace AmpzDesktopBooster.Desktops;

/// <summary>Qué hay cargado en un desk HOY: el proyecto y, opcionalmente, su MÓDULO (sub-scope).</summary>
public readonly record struct DeskAssignment(string Project, string Module);

/// <summary>
/// Orquesta las TRES capas de "proyectos por desk" del legacy (ver CLAUDE.md del legacy):
///   1. Sesión (_session)      — qué proyecto/módulo está en qué desk HOY. Efímero, se pierde al cerrar.
///   2. Sugerencias (INI)      — última asignación por desk, para pre-llenar el setter el próximo día.
///   3. Catálogo (JSON)        — history + modules + paths + notes durables. Se carga al arrancar.
///
/// Regla de oro del legacy: la sesión NUNCA se rellena del INI al arrancar (sería confuso ver
/// proyectos de ayer sin confirmar). El INI sólo pre-llena el textbox del setter.
///
/// MÓDULOS (sub-scope, agregado sobre el legacy): un proyecto puede tener módulos ("Geocontrol" →
/// "Plataforma" / "App Mobile"). Sus variables y notas viven bajo la key compuesta "Proyecto/Módulo"
/// (<see cref="ScopeKey"/>), lo que deja el shape del JSON intacto y hace que TODO lo que ya operaba
/// sobre una key de proyecto siga funcionando tal cual sobre una de módulo.
/// </summary>
public sealed class ProjectStore
{
    /// <summary>
    /// Separador de la key compuesta "Proyecto/Módulo". El "/" está PROHIBIDO en los nombres
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

    // ── Sesión: proyecto + módulo activos por desk (efímero) ───────────────────

    public string GetDeskProject(int idx) => _session.TryGetValue(idx, out var a) ? a.Project : "";

    /// <summary>Módulo activo del desk, o "" si el proyecto está cargado sin módulo.</summary>
    public string GetDeskModule(int idx) => _session.TryGetValue(idx, out var a) ? a.Module : "";

    /// <summary>
    /// Módulo del desk resuelto para la UI: nombre + color. Es lo que consumen la barra, el overlay
    /// y los pickers — resolver el color acá (y no en cada ventana) mantiene UNA sola fuente de verdad.
    /// </summary>
    public DeskModule GetDeskModuleInfo(int idx)
    {
        if (!_session.TryGetValue(idx, out var a) || a.Module == "")
            return DeskModule.None;
        return new DeskModule(a.Module, GetModuleColor(a.Project, a.Module));
    }

    /// <summary>
    /// Confirma el proyecto del desk: lo guarda en sesión, en sugerencias (INI) e historial.
    /// CAMBIAR de proyecto LIMPIA el módulo: un módulo pertenece a SU proyecto, arrastrar
    /// "Plataforma" de un cliente al siguiente sería exactamente la confusión que vinimos a matar.
    /// Re-confirmar el MISMO proyecto conserva el módulo (no es un cambio de contexto).
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

        if (!_data.History.Any(h => string.Equals(h, name, StringComparison.OrdinalIgnoreCase)))
        {
            _data.History.Add(name);
            Save();
        }
    }

    /// <summary>
    /// Setea (o limpia, con "") el módulo del desk. No-op si el desk no tiene proyecto: un módulo
    /// SIN proyecto padre no significa nada — el scope compuesto no se podría resolver.
    /// Da de alta el módulo en el catálogo del proyecto si es nuevo (con color auto-asignado).
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

    /// <summary>Saca proyecto Y módulo del desk SÓLO en la sesión (no toca historial ni catálogo).</summary>
    public void RemoveDeskProject(int idx) => _session.Remove(idx);

    public void ClearAllSession() => _session.Clear();

    public IEnumerable<(int Idx, string Project, string Module)> SessionEntries() =>
        _session.Select(kv => (kv.Key, kv.Value.Project, kv.Value.Module));

    /// <summary>Sugerencia persistida para pre-llenar el setter de este desk (o "").</summary>
    public string GetSuggestion(int idx) => _ini.Read("Projects", "desk_" + idx, "");

    /// <summary>Sugerencia de MÓDULO del desk — pre-llena el picker igual que la de proyecto.</summary>
    public string GetModuleSuggestion(int idx) => _ini.Read("Projects", $"desk_{idx}_module", "");

    // ── Historial / catálogo ───────────────────────────────────────────────────

    public IReadOnlyList<string> GetHistory() => _data.History;

    // ── Módulos (sub-scopes de un proyecto) ────────────────────────────────────

    /// <summary>Key de catálogo de un scope: el proyecto solo, o "Proyecto/Módulo" si hay módulo.</summary>
    public static string ScopeKey(string project, string module) =>
        string.IsNullOrEmpty(module) ? project : project + ScopeSeparator + module;

    /// <summary>Key compuesta → etiqueta legible ("Geocontrol/Plataforma" → "Geocontrol / Plataforma").</summary>
    public static string PrettyScope(string key) =>
        key.Replace(ScopeSeparator.ToString(), " " + ScopeSeparator + " ");

    /// <summary>
    /// Saca el separador de scope y colapsa espacios de un nombre tipeado por el usuario. Sin esto,
    /// un proyecto llamado "A/B" generaría una key compuesta FALSA e indistinguible de un módulo real.
    /// </summary>
    public static string Sanitize(string s) => s.Replace(ScopeSeparator, ' ').Trim();

    /// <summary>Módulos catalogados de un proyecto (lista vacía si no tiene).</summary>
    public IReadOnlyList<ModuleEntry> GetModules(string project) =>
        _data.Modules.TryGetValue(project, out var list) ? list : new List<ModuleEntry>();

    /// <summary>Color "#RRGGBB" del módulo, o "" si no está catalogado (la UI cae al dorado).</summary>
    public string GetModuleColor(string project, string module) =>
        GetModules(project).FirstOrDefault(m =>
            string.Equals(m.Name, module, StringComparison.OrdinalIgnoreCase))?.Color ?? "";

    /// <summary>
    /// Da de alta el módulo en el catálogo del proyecto si no existía, con el primer color LIBRE de
    /// la paleta (ver <see cref="ModulePalette.NextFree"/>). Idempotente: si ya existe, no toca nada.
    /// </summary>
    public ModuleEntry EnsureModule(string project, string module)
    {
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

    /// <summary>Cambia el color de un módulo (el F3 del picker cicla la paleta).</summary>
    public void SetModuleColor(string project, string module, string color)
    {
        var entry = GetModules(project).FirstOrDefault(m =>
            string.Equals(m.Name, module, StringComparison.OrdinalIgnoreCase));
        if (entry is null) return;
        entry.Color = color;
        Save();
    }

    /// <summary>
    /// Borra un módulo EN CASCADA, con el mismo criterio que <see cref="DeleteFromHistory"/>: lo saca
    /// del catálogo, borra sus variables y notas (la key compuesta) y limpia cualquier desk de la
    /// sesión que lo tuviera activo — ese desk queda en el proyecto pelado, no huérfano.
    /// </summary>
    public void DeleteModule(string project, string module)
    {
        if (_data.Modules.TryGetValue(project, out var list))
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
    /// ser un proyecto pelado o una key compuesta "Proyecto/Módulo" (<see cref="ScopeKey"/>) — de ahí
    /// que todo lo que ya operaba sobre proyectos funcione igual sobre módulos, sin ramas nuevas.
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

    /// <summary>Pool GLOBAL compartida — la usan los desks sin proyecto (MAIN/CONSOLES/MISCS/DESK+ vacío).</summary>
    public PathPool GetSharedPool() => new(_data.SharedPaths, Save, "Global");

    /// <summary>
    /// TODAS las pools de proyecto del catálogo (una por key de <c>_data.Paths</c>), para el toggle
    /// "ver todos los proyectos" del Paths Manager. NO incluye la global (esa se anexa por su lado) ni
    /// excluye el proyecto actual — eso queda a cargo del caller, que es quien conoce el contexto del
    /// desk. El Label de cada pool es el nombre del proyecto (sirve de rótulo de sección en la vista).
    /// </summary>
    public IReadOnlyList<PathPool> GetAllProjectPools()
    {
        var list = new List<PathPool>();
        foreach (var kv in _data.Paths)
            list.Add(new PathPool(kv.Value, Save, PrettyScope(kv.Key)));
        return list;
    }

    /// <summary>
    /// Resuelve QUÉ pool corresponde según el desk actual. Con módulo activo la pool primaria es la
    /// DEL MÓDULO (key compuesta); si no, la del proyecto; y en cualquier otro caso, la global.
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
    /// tres niveles: <b>módulo → proyecto → global</b>.
    ///   · Con módulo:      primaria = módulo, <paramref name="parent"/> = proyecto, global = global.
    ///   · Sin módulo:      primaria = proyecto, parent = null, global = global.
    ///   · Scope global:    primaria = global, parent y global = null (ya la estás viendo como primaria).
    /// La regla vive acá, en la capa que la conoce — la ventana no la re-implementa.
    /// </summary>
    public PathPool ResolvePoolWithGlobal(string deskName, int deskIdx, out PathPool? global, out PathPool? parent)
    {
        if (UseProjectScope(deskName, deskIdx, out var project, out var module))
        {
            global = GetSharedPool();
            // Con módulo, el proyecto es el "padre" del que se hereda; sin módulo no hay nada arriba.
            parent = module == "" ? null : GetProjectPool(project);
            return GetProjectPool(ScopeKey(project, module));
        }
        global = null;
        parent = null;
        return GetSharedPool();
    }

    // ── Predeterminado POR SCOPE ───────────────────────────────────────────────
    // El predeterminado NO es una propiedad de la variable, es una decisión del CONTEXTO en el que
    // estás parado. Con módulos eso se volvió obligatorio: una entrada del proyecto la ven todos sus
    // módulos, así que un flag en la entrada hacía que marcarla desde "App Mobile" se la cambiara
    // también a "Plataforma" (mismo objeto, no propagación). Guardamos el PATH elegido por scope.

    /// <summary>Key de scope para la pool GLOBAL. Vacío: un proyecto nunca puede llamarse así.</summary>
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
    /// Key de scope del desk: "" si es global, el proyecto, o "Proyecto/Módulo". Es lo que la
    /// ventana de Variables necesita para saber DÓNDE guardar el predeterminado que marques.
    /// </summary>
    public string ResolveScopeKey(string deskName, int deskIdx) =>
        UseProjectScope(deskName, deskIdx, out var project, out var module)
            ? ScopeKey(project, module)
            : GlobalScope;

    /// <summary>
    /// Key del scope PADRE del que se hereda, o null si no hay (sin módulo, o scope global). Sólo
    /// existe un nivel de herencia de predeterminado: módulo → proyecto.
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
    /// con módulo activo ves las DEL MÓDULO y punto. Es deliberado — una nota es una pizarra de
    /// trabajo, y mezclarle la del proyecto la volvería un cajón de sastre imposible de escanear.
    /// </summary>
    public string GetNotes(string deskName, int deskIdx)
    {
        if (UseProjectScope(deskName, deskIdx, out var project, out var module))
            return _data.Notes.TryGetValue(ScopeKey(project, module), out var n) ? n : "";
        return _data.SharedNotes;
    }

    /// <summary>Guarda las notas en el scope que corresponda (módulo, proyecto o global) y persiste.</summary>
    public void SetNotes(string deskName, int deskIdx, string text)
    {
        if (UseProjectScope(deskName, deskIdx, out var project, out var module))
            _data.Notes[ScopeKey(project, module)] = text;
        else
            _data.SharedNotes = text;
        Save();
    }

    // ── Notas de CARPETA (ligadas al disco, no al desk/proyecto) ────────────────

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

    /// <summary>Etiqueta del scope para el header: "Proyecto", "Proyecto / Módulo" o "Global".</summary>
    public string ScopeLabel(string deskName, int deskIdx) =>
        UseProjectScope(deskName, deskIdx, out var project, out var module)
            ? PrettyScope(ScopeKey(project, module))
            : "Global";

    /// <summary>
    /// true si el desk usa scope de proyecto (DESK +N con proyecto activo). <paramref name="module"/>
    /// sale "" cuando el proyecto está cargado sin módulo — la key compuesta degrada sola al proyecto.
    /// </summary>
    private bool UseProjectScope(string deskName, int deskIdx, out string project, out string module)
    {
        bool isProjectDesk = deskName.Contains("DESK +", StringComparison.OrdinalIgnoreCase);
        project = GetDeskProject(deskIdx);
        module = project == "" ? "" : GetDeskModule(deskIdx);
        return isProjectDesk && project != "";
    }

    /// <summary>
    /// Borra un proyecto del historial EN CASCADA: lo saca de history, paths, notes y del catálogo de
    /// módulos, limpia cualquier sesión que apuntara a él, y persiste. Sin huérfanos (igual que el
    /// legacy). Los MÓDULOS del proyecto se van con él: sus keys compuestas arrancan con "Proyecto/",
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

    /// <summary>
    /// Capitaliza la primera letra de cada palabra: "space consortium" → "Space Consortium".
    /// Se aplica al confirmar un proyecto nuevo, así el nombre queda normalizado en TODAS las
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
