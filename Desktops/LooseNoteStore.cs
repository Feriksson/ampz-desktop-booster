using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using AmpzDesktopBooster.Persistence;

namespace AmpzDesktopBooster.Desktops;

/// <summary>
/// Una nota SUELTA: no está atada a ningún desk ni scope, no se abre sola en ningún lado. La etiqueta
/// de Espacio/Contexto (<see cref="Project"/>/<see cref="Module"/>) es OPCIONAL y existe sólo para que
/// la búsqueda del navegador de notas la encuentre ("geo" trae lo de Geocontrol). Identidad = <see cref="Id"/>
/// (Guid): el título se puede repetir y se renombra, así que nunca sirve de key.
/// </summary>
public sealed class LooseNote
{
    [JsonPropertyName("id")]      public string Id { get; set; } = Guid.NewGuid().ToString("N");
    [JsonPropertyName("title")]   public string Title { get; set; } = "";
    [JsonPropertyName("content")] public string Content { get; set; } = "";
    [JsonPropertyName("project")] public string Project { get; set; } = "";
    [JsonPropertyName("module")]  public string Module { get; set; } = "";
    [JsonPropertyName("created")] public DateTime Created { get; set; } = DateTime.Now;
    [JsonPropertyName("updated")] public DateTime Updated { get; set; } = DateTime.Now;
}

/// <summary>
/// Las notas sueltas, en su PROPIO archivo (<see cref="AppPaths.LooseNotesFile"/>) y no dentro del
/// catálogo de espacios. Dos motivos: (1) radio de explosión — si este archivo se daña, no arrastra
/// variables ni servicios; (2) se escribe en CADA edición de una nota, y reescribir los ~50 KB del
/// catálogo por tipear en una nota sería gratuito.
///
/// La vive <see cref="ProjectStore"/> (propiedad <c>LooseNotes</c>) y no App, a propósito: las
/// reorganizaciones de la pestaña Espacios (renombrar/mover/promover/degradar/borrar) tienen que
/// arrastrar las ETIQUETAS en el mismo acto — "una key de scope se mueve en todos lados o en
/// ninguno". Dueño único = imposible que una operación se olvide de avisarle.
///
/// Mismo patrón que el resto de los stores: Load() nunca tira, Save() nunca tira (si falla el disco
/// seguimos en memoria), escritura atómica vía <see cref="SafeFile"/>.
/// </summary>
public sealed class LooseNoteStore
{
    private readonly List<LooseNote> _notes;
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private LooseNoteStore(List<LooseNote> notes) => _notes = notes;

    public static LooseNoteStore Load()
    {
        List<LooseNote>? list = null;
        try
        {
            list = SafeFile.LoadJson(AppPaths.LooseNotesFile,
                json => JsonSerializer.Deserialize<List<LooseNote>>(json));
        }
        catch { /* nunca tumbamos la app por la persistencia */ }

        // Un id vacío (archivo editado a mano) rompería la identidad de la fila: le damos uno nuevo.
        list ??= new List<LooseNote>();
        list.RemoveAll(n => n is null);
        foreach (var n in list.Where(n => string.IsNullOrWhiteSpace(n.Id)))
            n.Id = Guid.NewGuid().ToString("N");
        return new LooseNoteStore(list);
    }

    private void Save()
    {
        try { SafeFile.WriteAllText(AppPaths.LooseNotesFile, JsonSerializer.Serialize(_notes, JsonOpts)); }
        catch { /* disco/permisos → seguimos en memoria */ }
    }

    public IReadOnlyList<LooseNote> All => _notes;

    public LooseNote? Get(string id) => _notes.FirstOrDefault(n => n.Id == id);

    /// <summary>Alta con etiqueta opcional. La etiqueta se normaliza igual que las keys de scope.</summary>
    public LooseNote Create(string title, string project, string module)
    {
        var note = new LooseNote { Title = title.Trim() };
        ApplyTag(note, project, module);
        _notes.Add(note);
        Save();
        return note;
    }

    /// <summary>Guarda el texto. Sólo toca <see cref="LooseNote.Updated"/> si de verdad cambió: el orden
    /// del navegador es "última editada arriba", y abrir una nota sin tocarla no es editarla.</summary>
    public void SetContent(string id, string content)
    {
        if (Get(id) is not { } note || note.Content == content) return;
        note.Content = content;
        note.Updated = DateTime.Now;
        Save();
    }

    /// <summary>Renombra y re-etiqueta (F2 del navegador).</summary>
    public void Update(string id, string title, string project, string module)
    {
        if (Get(id) is not { } note) return;
        note.Title = title.Trim();
        ApplyTag(note, project, module);
        note.Updated = DateTime.Now;
        Save();
    }

    public void Delete(string id)
    {
        if (_notes.RemoveAll(n => n.Id == id) > 0) Save();
    }

    /// <summary>Un contexto sin espacio no existe en el modelo: sin espacio, la etiqueta de contexto se descarta.</summary>
    private static void ApplyTag(LooseNote note, string project, string module)
    {
        note.Project = (project ?? "").Trim();
        note.Module = note.Project == "" ? "" : (module ?? "").Trim();
    }

    // ── Seguir las reorganizaciones del catálogo (las llama ProjectStore) ─────────────────────

    /// <summary>
    /// Aplica a cada etiqueta el MISMO mapeo (espacio, contexto) → (espacio, contexto) que
    /// ProjectStore aplica a la sesión, las sugerencias y los desks dinámicos. Así las cuatro capas
    /// no pueden discrepar sobre adónde fue un scope. No re-fecha las notas: moverles la etiqueta no
    /// es editarlas.
    /// </summary>
    public void RemapScope(Func<string, string, (string Project, string Module)> map)
    {
        bool changed = false;
        foreach (var n in _notes.Where(n => n.Project != ""))
        {
            var (np, nm) = map(n.Project, n.Module);
            if (np == n.Project && nm == n.Module) continue;
            n.Project = np;
            n.Module = nm;
            changed = true;
        }
        if (changed) Save();
    }

    /// <summary>
    /// Se borró un espacio (<paramref name="module"/> null) o un contexto. La NOTA se queda — sólo se
    /// le limpia la etiqueta: el texto es del usuario, no del scope, y borrar un espacio no puede
    /// llevarse puesto algo que nunca estuvo "adentro" de él. Al borrar un contexto la nota conserva
    /// el espacio (sigue siendo de ese cliente).
    /// </summary>
    public void ClearTag(string project, string? module)
    {
        bool changed = false;
        foreach (var n in _notes)
        {
            if (!string.Equals(n.Project, project, StringComparison.OrdinalIgnoreCase)) continue;
            if (module is null)
            {
                n.Project = "";
                n.Module = "";
                changed = true;
            }
            else if (string.Equals(n.Module, module, StringComparison.OrdinalIgnoreCase))
            {
                n.Module = "";
                changed = true;
            }
        }
        if (changed) Save();
    }
}
