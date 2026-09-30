using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using AmpzDesktopBooster.Hotkeys;
using AmpzDesktopBooster.Persistence;

namespace AmpzDesktopBooster.Desktops;

/// <summary>
/// Una asignación de escritorio DINÁMICO: qué espacio/contexto vive en el desk identificado por
/// <see cref="Id"/> (el GUID estable de VirtualDesktopAccessor), y qué tecla del numpad lo navega.
/// </summary>
public sealed class DynamicDeskEntry
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("project")]
    public string Project { get; set; } = "";

    [JsonPropertyName("module")]
    public string Module { get; set; } = "";

    /// <summary>Tecla del numpad (nombre del enum, "D4".."D9"), igual serialización que ManagedDesktop.</summary>
    [JsonPropertyName("key")]
    public string Key { get; set; } = "";

    [JsonIgnore]
    public NumpadKey ShortcutKey
    {
        get => Enum.TryParse<NumpadKey>(Key, ignoreCase: true, out var k) ? k : NumpadKey.None;
        set => Key = value == NumpadKey.None ? "" : value.ToString();
    }
}

/// <summary>
/// Registro de los escritorios DINÁMICOS que crea el launcher (Win+NumpadEnter, ver ProjectStore /
/// HotkeyRouter). A diferencia del catálogo de <see cref="DesktopConfig"/> (desks FIJOS, configurados
/// de antemano por el usuario), estas entradas nacen y mueren en caliente: se crean al confirmar un
/// espacio+contexto y se borran al re-press del launcher sobre ese mismo desk.
///
/// ⚠ POR QUÉ SE INDEXA POR GUID Y NO POR ÍNDICE. El índice de un desktop se corre apenas se crea o
/// borra CUALQUIER OTRO — incluidos los fijos, si el usuario reordena su catálogo. El GUID que expone
/// la DLL (ver VirtualDesktopAccessor.GetDesktopIdByNumber) es la única identidad que sobrevive a eso,
/// así que el registro persiste por GUID y resuelve el índice VIVO recién al consultarlo — nunca
/// guarda un índice como si fuera durable.
///
/// Persiste en <see cref="AppPaths.DynamicDesksFile"/> para que, tras reiniciar la app, los desks
/// dinámicos que sigan abiertos (Windows no los cerró) se "re-adopten" con su espacio/contexto/tecla
/// intactos. Un GUID que ya no resuelve a ningún desktop (lo borraste por fuera, o cerraste sesión de
/// Windows sin este) se descarta al cargar — nunca queda un dato fantasma sin desk detrás.
/// </summary>
public sealed class DynamicDeskStore
{
    private readonly DesktopService _desktops;
    private readonly Dictionary<Guid, DynamicDeskEntry> _entries;

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private DynamicDeskStore(DesktopService desktops, Dictionary<Guid, DynamicDeskEntry> entries)
    {
        _desktops = desktops;
        _entries = entries;
    }

    public static DynamicDeskStore Load(DesktopService desktops)
    {
        var entries = new Dictionary<Guid, DynamicDeskEntry>();
        try
        {
            // Lectura a prueba de archivo roto (.bak / aparta el dañado) — ver SafeFile.
            var list = SafeFile.LoadJson(AppPaths.DynamicDesksFile,
                json => JsonSerializer.Deserialize<List<DynamicDeskEntry>>(json)) ?? new List<DynamicDeskEntry>();
            foreach (var e in list)
                entries[e.Id] = e;
        }
        catch { /* corrupto → arrancamos vacío, nunca tumbamos la app por esto */ }

        var store = new DynamicDeskStore(desktops, entries);
        store.PruneStale();
        return store;
    }

    /// <summary>Saca del registro cualquier GUID que ya no resuelva a un desktop vivo, y persiste si cambió algo.</summary>
    private void PruneStale()
    {
        bool changed = false;
        foreach (var id in _entries.Keys.ToList())
        {
            if (_desktops.IndexOfId(id) < 0)
            {
                _entries.Remove(id);
                changed = true;
            }
        }
        if (changed) Save();
    }

    private void Save()
    {
        try { SafeFile.WriteAllText(AppPaths.DynamicDesksFile, JsonSerializer.Serialize(_entries.Values.ToList(), JsonOpts)); }
        catch { /* disco/permisos → seguimos en memoria */ }
    }

    /// <summary>Entrada por GUID exacto, o null.</summary>
    public DynamicDeskEntry? Get(Guid id) => _entries.TryGetValue(id, out var e) ? e : null;

    /// <summary>Entrada del desk en ese ÍNDICE actual, resolviendo su GUID vivo. Null si no es dinámico.</summary>
    public DynamicDeskEntry? GetByIndex(int index)
    {
        if (index < 0) return null;
        var id = _desktops.IdOf(index);
        return _entries.TryGetValue(id, out var e) ? e : null;
    }

    /// <summary>¿El desk en ese índice es uno de los que gestiona este registro? Lo consulta DeskCatalog.</summary>
    public bool IsDynamicIndex(int index) => GetByIndex(index) is not null;

    /// <summary>
    /// Entrada dinámica VIVA cuyo desk se llama HOY <paramref name="name"/> (case-insensitive), o null.
    /// Lo usa el bootstrapper para el caso de corrupción: un desk dinámico que terminó con el MISMO
    /// nombre que un fijo del catálogo (ver DesktopBootstrapper — el bug real que motivó este método:
    /// tras cerrar un fijo y reabrir Windows, el bootstrapper VIEJO renombraba por ÍNDICE y le pegaba
    /// el nombre del fijo a un desk dinámico vivo, dejando dos atajos apuntando al mismo escritorio).
    /// </summary>
    public DynamicDeskEntry? FindLiveByDeskName(string name)
    {
        foreach (var (idx, e) in LiveEntries())
            if (string.Equals(_desktops.GetName(idx), name, StringComparison.OrdinalIgnoreCase))
                return e;
        return null;
    }

    /// <summary>
    /// Si el par espacio+contexto YA está abierto en un desk dinámico vivo, su GUID + índice actual.
    /// Es el chequeo de DEDUPE del launcher: "un Espacio+Contexto no puede estar activo en dos desks".
    /// </summary>
    public (Guid Id, int Index)? FindOpenAssignment(string project, string module)
    {
        foreach (var e in _entries.Values)
        {
            if (!string.Equals(e.Project, project, StringComparison.OrdinalIgnoreCase)) continue;
            if (!string.Equals(e.Module, module, StringComparison.OrdinalIgnoreCase)) continue;
            int idx = _desktops.IndexOfId(e.Id);
            if (idx >= 0) return (e.Id, idx);
        }
        return null;
    }

    /// <summary>Teclas que YA tiene tomadas algún desk dinámico vivo (para el reparto de la más baja libre).</summary>
    public HashSet<NumpadKey> UsedKeys() =>
        LiveEntries().Select(t => t.Entry.ShortcutKey).Where(k => k != NumpadKey.None).ToHashSet();

    /// <summary>
    /// Cuántos contextos de ESE espacio están abiertos AHORA en un desk dinámico vivo — incluye el
    /// desk abierto SIN contexto (módulo ""), que también cuenta como uno. Lo usa el paso 1 del
    /// launcher (<c>ProjectSetterWindow</c>) para la marca "N abiertos" junto al espacio; mismo
    /// criterio de comparación (OrdinalIgnoreCase) que <see cref="FindOpenAssignment"/>, para no
    /// inventar un segundo criterio de "es el mismo espacio".
    /// </summary>
    public int CountOpenModules(string project) =>
        LiveEntries().Count(t => string.Equals(t.Entry.Project, project, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Tecla del desk donde YA está abierto ese par espacio+contexto, o null si no lo está. Reusa
    /// <see cref="FindOpenAssignment"/> (mismo criterio de dedupe) — lo consume el paso 2 del launcher
    /// (<c>ModulePickerWindow</c>) para marcar filas ya abiertas, incluida la opción "sin contexto"
    /// (módulo "").
    /// </summary>
    public NumpadKey? OpenKeyFor(string project, string module)
    {
        var found = FindOpenAssignment(project, module);
        return found is { } f ? Get(f.Id)?.ShortcutKey : null;
    }

    /// <summary>Da de alta (o reemplaza) la asignación de un desk recién creado, y persiste.</summary>
    public void Register(Guid id, string project, string module, NumpadKey key)
    {
        _entries[id] = new DynamicDeskEntry { Id = id, Project = project, Module = module, Key = "" };
        _entries[id].ShortcutKey = key;
        Save();
    }

    /// <summary>Saca la asignación (el desk YA se borró de Windows por otro lado) y persiste.</summary>
    public void Unregister(Guid id)
    {
        if (_entries.Remove(id)) Save();
    }

    /// <summary>Todas las entradas vivas con su índice ACTUAL resuelto (para el picker/la barra/la navegación).</summary>
    public IEnumerable<(int Index, DynamicDeskEntry Entry)> LiveEntries()
    {
        foreach (var e in _entries.Values)
        {
            int idx = _desktops.IndexOfId(e.Id);
            if (idx >= 0) yield return (idx, e);
        }
    }

    /// <summary>Entrada VIVA dueña de esa tecla, o null. Gemelo de <see cref="DesktopConfig.ByKey"/> para dinámicos.</summary>
    public (int Index, DynamicDeskEntry Entry)? ByKey(NumpadKey key)
    {
        if (key == NumpadKey.None) return null;
        foreach (var t in LiveEntries())
            if (t.Entry.ShortcutKey == key) return t;
        return null;
    }

    /// <summary>
    /// Re-mapea espacio/contexto de TODAS las entradas vivas según <paramref name="map"/>. Lo llaman
    /// las operaciones de reorganización de <see cref="ProjectStore"/> (Rename/Move/Promote/Demote)
    /// — mismo criterio que <c>MapSession</c>/<c>MapSuggestions</c>: si una key de scope se mueve, se
    /// mueve en TODOS lados o un desk dinámico queda apuntando a un espacio/contexto que ya no existe.
    /// </summary>
    public void RemapScope(Func<string, string, (string Project, string Module)> map)
    {
        bool changed = false;
        foreach (var e in _entries.Values)
        {
            var (np, nm) = map(e.Project, e.Module);
            if (np != e.Project || nm != e.Module)
            {
                e.Project = np;
                e.Module = nm;
                changed = true;
            }
        }
        if (changed) Save();
    }
}
