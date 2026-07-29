using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace AmpzDesktopBooster.Persistence;

/// <summary>Una entrada de path/URL de un proyecto. "default" sólo se serializa cuando es true.</summary>
public sealed class PathEntry
{
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("path")]  public string Path { get; set; } = "";

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
}
