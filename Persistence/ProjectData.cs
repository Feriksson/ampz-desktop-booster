using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace AmpzDesktopBooster.Persistence;

/// <summary>Una entrada de path/URL de un espacio.</summary>
public sealed class PathEntry
{
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("path")]  public string Path { get; set; } = "";

    /// <summary>
    /// LEGACY — el predeterminado ya NO vive en la entrada, vive en el SCOPE (ver
    /// <see cref="ProjectData.Defaults"/>). Se conserva SÓLO para poder migrar los archivos viejos
    /// una vez; después de migrar queda en false y no se vuelve a escribir.
    ///
    /// Por qué se movió: con contextos, una misma entrada del espacio la ven TODOS sus contextos. Con
    /// el flag en la entrada, marcarla como predeterminada desde un contexto se la cambiaba a todos —
    /// no era propagación, era literalmente el mismo objeto. El predeterminado es una decisión DEL
    /// CONTEXTO en el que estás parado, no una propiedad de la variable.
    /// </summary>
    [JsonPropertyName("default")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Default { get; set; }
}

/// <summary>
/// Un CONTEXTO (sub-scope) de un espacio: "Plataforma" y "App Mobile" dentro de "Geocontrol".
/// Nace del problema real de tener varios desks del MISMO cliente y confundirlos al cambiar de
/// pantalla — por eso cada contexto lleva su propio <see cref="Color"/>: la señal de identificación
/// es CROMÁTICA (se percibe sin leer), no textual.
///
/// OJO: un contexto NO es un espacio hermano. Sus variables/notas viven bajo la key compuesta
/// "Espacio/Contexto" (ver <c>ProjectStore.ScopeKey</c>) y HEREDAN de las del espacio — así lo
/// que es del cliente (repo raíz, Jira) se carga UNA vez y se ve desde todos sus contextos.
/// </summary>
public sealed class ModuleEntry
{
    [JsonPropertyName("name")]  public string Name { get; set; } = "";

    /// <summary>Color de identificación en "#RRGGBB". Vacío → la UI cae al dorado de DESK +N.</summary>
    [JsonPropertyName("color")] public string Color { get; set; } = "";
}

/// <summary>
/// El catálogo persistente de espacios — mismo shape que el desk_project_data.json del legacy:
///   history       — todos los espacios conocidos (para autocompletar el setter)
///   notes         — pizarra de texto por espacio (o por contexto, con key "Espacio/Contexto")
///   paths         — paths/URLs por espacio (idem: la key puede ser compuesta)
///   shared_notes  — pizarra GLOBAL (desks sin espacio)
///   shared_paths  — pool de paths GLOBAL (desks sin espacio)
///   folder_notes  — pizarra ligada a una CARPETA del disco (independiente de desk/espacio)
///   modules       — sub-scopes por espacio (key = nombre del espacio)
///
/// OJO: este catálogo NO es lo mismo que "qué espacio está en qué desk HOY" — eso es la sesión
/// (efímera, en memoria, en <see cref="Desktops.ProjectStore"/>). Acá vive sólo el catálogo durable.
/// </summary>
public sealed class ProjectData
{
    [JsonPropertyName("history")]      public List<string> History { get; set; } = new();
    [JsonPropertyName("notes")]        public Dictionary<string, string> Notes { get; set; } = new();
    [JsonPropertyName("paths")]        public Dictionary<string, List<PathEntry>> Paths { get; set; } = new();
    [JsonPropertyName("shared_notes")] public string SharedNotes { get; set; } = "";
    [JsonPropertyName("shared_paths")] public List<PathEntry> SharedPaths { get; set; } = new();

    // Notas ligadas a una carpeta del disco. La key es el NOMBRE de la carpeta (hoja) en minúsculas,
    // NO el path completo: así mover/renombrar el path base (Desktop → D:\) no pierde las notas.
    // Ver ProjectStore.FolderKey para el criterio y el porqué de la decisión.
    [JsonPropertyName("folder_notes")] public Dictionary<string, string> FolderNotes { get; set; } = new();

    // Contextos (sub-scopes) por espacio. Key = nombre del espacio tal cual está en History.
    // Sus variables/notas NO viven acá: viven en Paths/Notes bajo la key compuesta "Espacio/Contexto".
    // Acá vive sólo el CATÁLOGO del contexto (su nombre y su color de identificación).
    [JsonPropertyName("modules")] public Dictionary<string, List<ModuleEntry>> Modules { get; set; } = new();

    /// <summary>
    /// Predeterminado POR SCOPE: key = scope ("Espacio" o "Espacio/Contexto"), value = el PATH de la
    /// variable elegida. El path puede apuntar a una entrada del PROPIO scope o a una HEREDADA del
    /// espacio padre / de la global — de eso se trata: cada scope ELIGE del pool que ve, sin
    /// duplicar la variable. Así "Geocontrol/App Mobile" y "Geocontrol/Plataforma" pueden tener
    /// predeterminados distintos apuntando a dos entradas del MISMO pool de "Geocontrol".
    ///
    /// Se guarda el path y no el índice porque el índice se corre al borrar/reordenar entradas.
    /// </summary>
    [JsonPropertyName("defaults")] public Dictionary<string, string> Defaults { get; set; } = new();

    /// <summary>Predeterminado del scope GLOBAL (desks sin espacio). Aparte porque no tiene key de scope.</summary>
    [JsonPropertyName("shared_default")] public string SharedDefault { get; set; } = "";
}
