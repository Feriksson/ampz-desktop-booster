using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using AmpzDesktopBooster.Persistence;

namespace AmpzDesktopBooster.Services.Tasks;

/// <summary>
/// Tareas PERSONALES locales: títulos sueltos que el usuario sabe que tiene que hacer pero NO
/// existen en ningún gestor web (Vikunja/JIRA/Trello). Pensado para esas tareas recurrentes que
/// "uno ya sabe que tiene" y no vale la pena cargar en el gestor.
///
/// CLAVE — por qué DURABLES y no efímeras (a diferencia de <see cref="TaskSessionStore"/>): la
/// regla de oro del repo dice que la SESIÓN de tareas/proyectos es efímera (ver la tarea de ayer
/// sin confirmar confunde). Pero estas tienen OTRA semántica: son un recordatorio personal RECURRENTE
/// — si se borraran al reiniciar, la feature no serviría. Por eso persisten en custom_tasks.json,
/// igual que las notas y los paths del catálogo de proyectos (que también son durables). NO van en
/// el session store.
///
/// Se ven SIEMPRE en el picker, aunque el fetch web falle o no haya cuentas — son independientes del
/// gestor web. Mismo patrón Load/Save silencioso que el resto de configs: un fallo de disco degrada
/// a memoria, nunca tumba la app.
/// </summary>
public sealed class CustomTaskStore
{
    /// <summary>Las entradas personales, en orden de alta. Vacío por defecto.</summary>
    public List<CustomTaskEntry> Entries { get; set; } = new();

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private static string StorePath => Path.Combine(AppPaths.DataDir, "custom_tasks.json");

    public static CustomTaskStore Load()
    {
        try
        {
            if (File.Exists(StorePath))
            {
                var loaded = JsonSerializer.Deserialize<CustomTaskStore>(File.ReadAllText(StorePath));
                if (loaded is not null) return loaded;
            }
        }
        catch
        {
            // archivo corrupto o ilegible → lista vacía, no crasheamos
        }
        return new CustomTaskStore();
    }

    private void Save()
    {
        try
        {
            File.WriteAllText(StorePath, JsonSerializer.Serialize(this, JsonOpts));
        }
        catch
        {
            // sin permisos / disco lleno → seguimos en memoria
        }
    }

    /// <summary>
    /// Agrega una entrada con el título trimeado y persiste. Ignora títulos vacíos (devuelve null).
    /// El Id es un GUID estable para poder descartarla después sin depender del índice.
    /// </summary>
    public CustomTaskEntry? Add(string title)
    {
        title = title.Trim();
        if (title.Length == 0) return null;

        var entry = new CustomTaskEntry { Id = Guid.NewGuid().ToString("N"), Title = title };
        Entries.Add(entry);
        Save();
        return entry;
    }

    /// <summary>Descarta la entrada por Id y persiste. No-op si no existe.</summary>
    public void Remove(string id)
    {
        if (Entries.RemoveAll(e => e.Id == id) > 0)
            Save();
    }
}

/// <summary>Una tarea personal: solo un título y un Id estable para poder descartarla.</summary>
public sealed class CustomTaskEntry
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
}
