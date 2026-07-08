using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using AmpzDesktopBooster.Persistence;

namespace AmpzDesktopBooster.Desktops;

/// <summary>
/// Orquesta las TRES capas de "proyectos por desk" del legacy (ver CLAUDE.md del legacy):
///   1. Sesión (_session)      — qué proyecto está en qué desk HOY. Efímero, se pierde al cerrar.
///   2. Sugerencias (INI)      — última asignación por desk, para pre-llenar el setter el próximo día.
///   3. Catálogo (JSON)        — history + paths + notes durables. Se carga al arrancar.
///
/// Regla de oro del legacy: la sesión NUNCA se rellena del INI al arrancar (sería confuso ver
/// proyectos de ayer sin confirmar). El INI sólo pre-llena el textbox del setter.
/// </summary>
public sealed class ProjectStore
{
    private readonly IniFile _ini = new(AppPaths.SettingsIni);
    private readonly string _jsonPath = AppPaths.ProjectDataFile;
    private readonly Dictionary<int, string> _session = new();
    private ProjectData _data;

    public ProjectData Data => _data;

    public ProjectStore() => _data = Load();

    // ── Sesión: proyecto activo por desk (efímero) ─────────────────────────────

    public string GetDeskProject(int idx) => _session.TryGetValue(idx, out var p) ? p : "";

    /// <summary>Confirma el proyecto del desk: lo guarda en sesión, en sugerencias (INI) e historial.</summary>
    public void SetDeskProject(int idx, string name)
    {
        name = TitleCase(name.Trim());
        if (name == "") { RemoveDeskProject(idx); return; }

        _session[idx] = name;
        _ini.Write("Projects", "desk_" + idx, name);

        if (!_data.History.Any(h => string.Equals(h, name, StringComparison.OrdinalIgnoreCase)))
        {
            _data.History.Add(name);
            Save();
        }
    }

    /// <summary>Saca el proyecto del desk SÓLO en la sesión (no toca historial ni catálogo).</summary>
    public void RemoveDeskProject(int idx) => _session.Remove(idx);

    public void ClearAllSession() => _session.Clear();

    public IEnumerable<(int Idx, string Project)> SessionEntries() =>
        _session.Select(kv => (kv.Key, kv.Value));

    /// <summary>Sugerencia persistida para pre-llenar el setter de este desk (o "").</summary>
    public string GetSuggestion(int idx) => _ini.Read("Projects", "desk_" + idx, "");

    // ── Historial / catálogo ───────────────────────────────────────────────────

    public IReadOnlyList<string> GetHistory() => _data.History;

    // ── Pools de variables (paths/URLs) ─────────────────────────────────────────

    /// <summary>Pool de variables de un proyecto (crea la lista si el proyecto no tenía).</summary>
    public PathPool GetProjectPool(string project)
    {
        if (!_data.Paths.TryGetValue(project, out var list))
        {
            list = new List<PathEntry>();
            _data.Paths[project] = list;
        }
        return new PathPool(list, Save, project);
    }

    /// <summary>Pool GLOBAL compartida — la usan los desks sin proyecto (MAIN/MAILS/MISCS/DESK+ vacío).</summary>
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
            list.Add(new PathPool(kv.Value, Save, kv.Key));
        return list;
    }

    /// <summary>
    /// Resuelve QUÉ pool corresponde según el desk actual (la regla dual-scope del legacy):
    /// DESK +N con proyecto activo → pool del proyecto; cualquier otro caso → pool global.
    /// </summary>
    public PathPool ResolvePool(string deskName, int deskIdx)
    {
        return UseProjectScope(deskName, deskIdx, out var project)
            ? GetProjectPool(project)
            : GetSharedPool();
    }

    /// <summary>
    /// Como <see cref="ResolvePool"/>, pero además expone la pool GLOBAL cuando el scope es de
    /// proyecto, para mostrarla de SOLO-LECTURA junto a las del proyecto (así no quedás ciego a las
    /// compartidas). En scope global <paramref name="global"/> queda null: ya estás viendo las
    /// globales como pool primaria, no hay nada que anexar. Mantiene la regla dual-scope acá, en la
    /// capa que la conoce — la ventana no la re-implementa.
    /// </summary>
    public PathPool ResolvePoolWithGlobal(string deskName, int deskIdx, out PathPool? global)
    {
        if (UseProjectScope(deskName, deskIdx, out var project))
        {
            global = GetSharedPool();
            return GetProjectPool(project);
        }
        global = null;
        return GetSharedPool();
    }

    // ── Notas (mismo dual-scope que las variables) ──────────────────────────────

    /// <summary>Lee las notas que correspondan al desk: del proyecto activo o las globales.</summary>
    public string GetNotes(string deskName, int deskIdx)
    {
        if (UseProjectScope(deskName, deskIdx, out var project))
            return _data.Notes.TryGetValue(project, out var n) ? n : "";
        return _data.SharedNotes;
    }

    /// <summary>Guarda las notas en el scope que corresponda (proyecto o global) y persiste.</summary>
    public void SetNotes(string deskName, int deskIdx, string text)
    {
        if (UseProjectScope(deskName, deskIdx, out var project))
            _data.Notes[project] = text;
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

    /// <summary>Etiqueta del scope para el header: el nombre del proyecto o "Global".</summary>
    public string ScopeLabel(string deskName, int deskIdx) =>
        UseProjectScope(deskName, deskIdx, out var project) ? project : "Global";

    /// <summary>true si el desk usa scope de proyecto (DESK +N con proyecto activo).</summary>
    private bool UseProjectScope(string deskName, int deskIdx, out string project)
    {
        bool isProjectDesk = deskName.Contains("DESK +", StringComparison.OrdinalIgnoreCase);
        project = GetDeskProject(deskIdx);
        return isProjectDesk && project != "";
    }

    /// <summary>
    /// Borra un proyecto del historial EN CASCADA: lo saca de history, paths y notes, limpia
    /// cualquier sesión que apuntara a él, y persiste. Sin huérfanos (igual que el legacy).
    /// </summary>
    public void DeleteFromHistory(string name)
    {
        _data.History.RemoveAll(h => string.Equals(h, name, StringComparison.OrdinalIgnoreCase));
        _data.Paths.Remove(name);
        _data.Notes.Remove(name);

        foreach (var idx in _session
                     .Where(kv => string.Equals(kv.Value, name, StringComparison.OrdinalIgnoreCase))
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
