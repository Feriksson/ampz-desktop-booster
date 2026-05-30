using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using AmpzDesktopBooster.Persistence;

namespace AmpzDesktopBooster.Apps;

/// <summary>Un atajo de una app concreta: combinación, descripción y filtro de título opcional.</summary>
public sealed class AppShortcut
{
    [JsonPropertyName("id")]    public int Id { get; set; }
    [JsonPropertyName("key")]   public string Key { get; set; } = "";
    [JsonPropertyName("desc")]  public string Desc { get; set; } = "";

    /// <summary>Si NO está vacío, el atajo sólo se muestra cuando el título activo lo contiene.</summary>
    [JsonPropertyName("title")] public string Title { get; set; } = "";
}

/// <summary>
/// Cheatsheets de atajos POR APP + aliases visibles, para el Shortcuts Helper (Win+/). Persiste en
/// %APPDATA%\AmpzDesktopBooster\app_shortcuts.json. Reemplaza las secciones [AppShortcuts_&lt;proc&gt;],
/// [AppAliases] y los flags [Meta] preloaded_&lt;proc&gt; del settings.ini del legacy — mismo modelo,
/// formato moderno (JSON anidado). Patrón Load/Save con try/catch silencioso como el resto de configs.
/// </summary>
public sealed class AppShortcutStore
{
    /// <summary>procName (ej. "Notion.exe") → alias visible (ej. "Notion"). Sin alias = se muestra el proc.</summary>
    [JsonPropertyName("aliases")]
    public Dictionary<string, string> Aliases { get; set; } = new();

    /// <summary>procName → sus atajos.</summary>
    [JsonPropertyName("apps")]
    public Dictionary<string, List<AppShortcut>> Apps { get; set; } = new();

    /// <summary>procName → ya se precargaron sus defaults (no re-precargar aunque el user los borre).</summary>
    [JsonPropertyName("preloaded")]
    public Dictionary<string, bool> Preloaded { get; set; } = new();

    // ── Persistencia (mismo patrón que AppsConfig / UsageSettings) ──
    // Encoder relajado: el archivo es config en español que el usuario puede abrir/editar; sin esto
    // los acentos y el '+' salen escapados (ó, +) e ilegibles. No es contenido web → seguro.
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
    private static string FilePath => Path.Combine(AppPaths.DataDir, "app_shortcuts.json");

    public static AppShortcutStore Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var loaded = JsonSerializer.Deserialize<AppShortcutStore>(File.ReadAllText(FilePath));
                if (loaded is not null) return loaded;
            }
        }
        catch { /* corrupto → vacío */ }
        return new AppShortcutStore();
    }

    public void Save()
    {
        try { File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOpts)); }
        catch { /* disco/permisos → seguimos en memoria */ }
    }

    // ── Atajos por app ──

    /// <summary>Atajos de la app, ordenados por id. Lista vacía si no hay (o proc vacío).</summary>
    public IReadOnlyList<AppShortcut> GetShortcuts(string proc)
    {
        if (string.IsNullOrEmpty(proc) || !Apps.TryGetValue(proc, out var list))
            return System.Array.Empty<AppShortcut>();
        return list.OrderBy(s => s.Id).ToList();
    }

    /// <summary>Alta (id 0/nuevo) o edición (si el id ya existe). Persiste.</summary>
    public void AddOrUpdate(string proc, AppShortcut shortcut)
    {
        if (string.IsNullOrEmpty(proc)) return;
        if (!Apps.TryGetValue(proc, out var list))
        {
            list = new List<AppShortcut>();
            Apps[proc] = list;
        }
        var existing = list.FirstOrDefault(s => s.Id == shortcut.Id);
        if (existing is null)
            list.Add(shortcut);
        else
        {
            existing.Key = shortcut.Key;
            existing.Desc = shortcut.Desc;
            existing.Title = shortcut.Title;
        }
        Save();
    }

    public void Delete(string proc, int id)
    {
        if (Apps.TryGetValue(proc, out var list))
        {
            list.RemoveAll(s => s.Id == id);
            Save();
        }
    }

    /// <summary>Siguiente id libre para la app (max + 1), igual que el legacy.</summary>
    public int NextId(string proc)
    {
        int max = 0;
        foreach (var s in GetShortcuts(proc))
            if (s.Id > max) max = s.Id;
        return max + 1;
    }

    // ── Aliases ──

    public string GetAlias(string proc)
        => !string.IsNullOrEmpty(proc) && Aliases.TryGetValue(proc, out var a) ? a : "";

    public void SetAlias(string proc, string alias)
    {
        if (string.IsNullOrEmpty(proc)) return;
        alias = alias.Trim();
        if (alias == "") Aliases.Remove(proc);
        else Aliases[proc] = alias;
        Save();
    }

    // ── Preload de defaults (una vez por app; el flag persiste aunque el user los borre) ──

    public void PreloadDefaults()
    {
        bool changed = false;
        foreach (var (proc, shortcuts) in Defaults)
        {
            if (Preloaded.TryGetValue(proc, out var done) && done)
                continue;

            // Si ya tiene atajos cargados a mano, no pisamos — sólo marcamos como hecho.
            if (GetShortcuts(proc).Count == 0)
            {
                var list = new List<AppShortcut>();
                int i = 1;
                foreach (var (key, desc) in shortcuts)
                    list.Add(new AppShortcut { Id = i++, Key = key, Desc = desc, Title = "" });
                Apps[proc] = list;
            }
            Preloaded[proc] = true;
            changed = true;
        }
        if (changed) Save();
    }

    /// <summary>Cheatsheets default precargadas (mismas que el legacy _PreloadDefaultAppShortcuts).</summary>
    private static readonly Dictionary<string, (string key, string desc)[]> Defaults = new()
    {
        ["explorer.exe"] = new[]
        {
            ("Ctrl+N", "Nueva ventana"),
            ("Ctrl+W", "Cerrar ventana"),
            ("Ctrl+Shift+N", "Nueva carpeta"),
            ("F2", "Renombrar"),
            ("Alt+Up", "Subir un nivel"),
            ("Alt+Left", "Atrás"),
            ("Alt+Right", "Adelante"),
            ("Ctrl+L", "Foco en barra de dirección"),
            ("Ctrl+F", "Buscar en la carpeta"),
            ("Ctrl+Shift+Enter", "Abrir como admin (selección)"),
            ("F11", "Pantalla completa"),
        },
        ["Notion.exe"] = new[]
        {
            ("Ctrl+N", "Nueva página"),
            ("Ctrl+P", "Buscar páginas (Quick Find)"),
            ("Ctrl+\\", "Toggle sidebar"),
            ("Ctrl+[", "Atrás"),
            ("Ctrl+]", "Adelante"),
            ("Ctrl+/", "Menú de bloque (formatear / convertir)"),
            ("Ctrl+Shift+L", "Toggle dark mode"),
            ("Ctrl+Shift+M", "Comentar selección"),
            ("Ctrl+D", "Duplicar bloque"),
            ("Ctrl+Shift+U", "Subir a página padre"),
            ("Ctrl+Enter", "Toggle check en to-do"),
        },
        ["dbeaver.exe"] = new[]
        {
            ("Ctrl+Enter", "Ejecutar SQL actual"),
            ("Alt+X", "Ejecutar SQL (alternativo)"),
            ("F5", "Refresh"),
            ("Ctrl+Shift+F", "Format SQL"),
            ("Ctrl+Space", "Autocompletar"),
            ("Ctrl+/", "Comentar línea"),
            ("Ctrl+Shift+/", "Comentar bloque"),
            ("Ctrl+F", "Buscar"),
            ("Ctrl+H", "Buscar y reemplazar"),
            ("F4", "Ver objeto en editor"),
            ("Ctrl+Shift+T", "Abrir nuevo SQL editor"),
        },
        ["Spotify.exe"] = new[]
        {
            ("Space", "Play / Pausa"),
            ("Ctrl+Right", "Siguiente canción"),
            ("Ctrl+Left", "Canción anterior"),
            ("Ctrl+Up", "Subir volumen"),
            ("Ctrl+Down", "Bajar volumen"),
            ("Ctrl+L", "Buscar"),
            ("Ctrl+Shift+H", "Like / Save canción"),
            ("Ctrl+R", "Toggle repeat"),
            ("Ctrl+S", "Toggle shuffle"),
            ("Ctrl+P", "Preferencias"),
        },
        ["Claude.exe"] = new[]
        {
            ("Ctrl+N", "Nuevo chat"),
            ("Ctrl+Shift+N", "Nueva ventana"),
            ("Ctrl+K", "Buscar / cambiar de chat"),
            ("Ctrl+,", "Configuración"),
            ("Ctrl+R", "Recargar app"),
            ("Ctrl+W", "Cerrar ventana"),
            ("Up", "Editar último mensaje enviado"),
            ("Esc", "Cancelar respuesta en streaming"),
            ("Ctrl+Shift+I", "DevTools (si está habilitado)"),
            ("Ctrl+Q", "Salir de la app"),
        },
    };
}
