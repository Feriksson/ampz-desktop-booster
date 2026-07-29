using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace AmpzDesktopBooster.Persistence;

/// <summary>Una entrada de path/URL de un proyecto.</summary>
public sealed class PathEntry
{
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("path")]  public string Path { get; set; } = "";

    /// <summary>
    /// LEGACY — el predeterminado ya NO vive en la entrada, vive en el SCOPE (ver
    /// <see cref="ProjectData.Defaults"/>). Se conserva SÓLO para poder migrar los archivos viejos
    /// una vez; después de migrar queda en false y no se vuelve a escribir.
    ///
    /// Por qué se movió: con módulos, una misma entrada del proyecto la ven TODOS sus módulos. Con
    /// el flag en la entrada, marcarla como predeterminada desde un módulo se la cambiaba a todos —
    /// no era propagación, era literalmente el mismo objeto. El predeterminado es una decisión DEL
    /// CONTEXTO en el que estás parado, no una propiedad de la variable.
    /// </summary>
    [JsonPropertyName("default")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Default { get; set; }
}

/// <summary>
/// Un MÓDULO (sub-scope) de un proyecto: "Plataforma" y "App Mobile" dentro de "Geocontrol".
/// Nace del problema real de tener varios desks del MISMO cliente y confundirlos al cambiar de
/// pantalla — por eso cada módulo lleva su propio <see cref="Color"/>: la señal de identificación
/// es CROMÁTICA (se percibe sin leer), no textual.
///
/// OJO: un módulo NO es un proyecto hermano. Sus variables/notas viven bajo la key compuesta
/// "Proyecto/Módulo" (ver <c>ProjectStore.ScopeKey</c>) y HEREDAN de las del proyecto — así lo
/// que es del cliente (repo raíz, Jira) se carga UNA vez y se ve desde todos sus módulos.
/// </summary>
public sealed class ModuleEntry
{
    [JsonPropertyName("name")]  public string Name { get; set; } = "";

    /// <summary>Color de identificación en "#RRGGBB". Vacío → la UI cae al dorado de DESK +N.</summary>
    [JsonPropertyName("color")] public string Color { get; set; } = "";
}

/// <summary>
/// El catálogo persistente de proyectos — mismo shape que el desk_project_data.json del legacy:
///   history       — todos los proyectos conocidos (para autocompletar el setter)
///   notes         — pizarra de texto por proyecto (o por módulo, con key "Proyecto/Módulo")
///   paths         — paths/URLs por proyecto (idem: la key puede ser compuesta)
///   shared_notes  — pizarra GLOBAL (desks sin proyecto)
///   shared_paths  — pool de paths GLOBAL (desks sin proyecto)
///   folder_notes  — pizarra ligada a una CARPETA del disco (independiente de desk/proyecto)
///   modules       — sub-scopes por proyecto (key = nombre del proyecto)
///
/// OJO: este catálogo NO es lo mismo que "qué proyecto está en qué desk HOY" — eso es la sesión
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

    // Módulos (sub-scopes) por proyecto. Key = nombre del proyecto tal cual está en History.
    // Sus variables/notas NO viven acá: viven en Paths/Notes bajo la key compuesta "Proyecto/Módulo".
    // Acá vive sólo el CATÁLOGO del módulo (su nombre y su color de identificación).
    [JsonPropertyName("modules")] public Dictionary<string, List<ModuleEntry>> Modules { get; set; } = new();

    /// <summary>
    /// Predeterminado POR SCOPE: key = scope ("Proyecto" o "Proyecto/Módulo"), value = el PATH de la
    /// variable elegida. El path puede apuntar a una entrada del PROPIO scope o a una HEREDADA del
    /// proyecto padre / de la global — de eso se trata: cada scope ELIGE del pool que ve, sin
    /// duplicar la variable. Así "Geocontrol/App Mobile" y "Geocontrol/Plataforma" pueden tener
    /// predeterminados distintos apuntando a dos entradas del MISMO pool de "Geocontrol".
    ///
    /// Se guarda el path y no el índice porque el índice se corre al borrar/reordenar entradas.
    /// </summary>
    [JsonPropertyName("defaults")] public Dictionary<string, string> Defaults { get; set; } = new();

    /// <summary>Predeterminado del scope GLOBAL (desks sin proyecto). Aparte porque no tiene key de scope.</summary>
    [JsonPropertyName("shared_default")] public string SharedDefault { get; set; } = "";
}
