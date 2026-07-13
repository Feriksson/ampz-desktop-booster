using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace AmpzDesktopBooster.Persistence;

/// <summary>
/// Una entrada del catálogo de puertos: un servicio web local que corrés en tu máquina.
/// SOLO guardamos lo que el usuario aporta (título + puerto). La URL (localhost o IP de red),
/// el estado "escuchando" y el proceso dueño NO se persisten — son datos VOLÁTILES que se
/// derivan/recalculan en vivo (el puerto puede estar arriba o abajo según qué tengas corriendo).
/// </summary>
public sealed class PortEntry
{
    public string Title { get; set; } = "";
    public int Port { get; set; }
}

/// <summary>
/// Catálogo durable de puertos/servicios locales — lo que abre la Win+Numpad+ (Add).
/// Lista GLOBAL única (decisión del usuario): no depende del desk ni del proyecto, tus apps web
/// corren igual estés parado donde estés. Se persiste en %APPDATA%\AmpzDesktopBooster\ports.json.
///
/// Mismo patrón de config que <see cref="Services.WidgetSettings"/> y el resto del repo:
/// Load() con try/catch → defaults si corrupto; cada mutación llama Save() con try/catch silencioso
/// (si falla el disco seguimos en memoria — la persistencia NUNCA voltea la app).
/// </summary>
public sealed class PortStore
{
    public List<PortEntry> Entries { get; set; } = new();

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static PortStore Load()
    {
        try
        {
            var path = AppPaths.PortsFile;
            if (File.Exists(path))
            {
                var loaded = JsonSerializer.Deserialize<PortStore>(File.ReadAllText(path));
                if (loaded is not null)
                {
                    loaded.Entries ??= new List<PortEntry>();
                    return loaded;
                }
            }
        }
        catch
        {
            // ports.json corrupto o ilegible → arrancamos vacío, no crasheamos.
        }
        return new PortStore();
    }

    public void Save()
    {
        try
        {
            var path = AppPaths.PortsFile;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOpts));
        }
        catch
        {
            // permisos/disco: seguimos en memoria igual.
        }
    }

    // ── Mutaciones (auto-persisten, como PathPool) ─────────────────────────────

    public void Add(string title, int port)
    {
        Entries.Add(new PortEntry { Title = title.Trim(), Port = port });
        Save();
    }

    public void Delete(int index)
    {
        if (index < 0 || index >= Entries.Count) return;
        Entries.RemoveAt(index);
        Save();
    }

    public void UpdateTitle(int index, string title)
    {
        if (index < 0 || index >= Entries.Count) return;
        Entries[index].Title = title.Trim();
        Save();
    }

    public void UpdatePort(int index, int port)
    {
        if (index < 0 || index >= Entries.Count) return;
        Entries[index].Port = port;
        Save();
    }
}
