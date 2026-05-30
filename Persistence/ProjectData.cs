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
/// El catálogo persistente de proyectos — mismo shape que el desk_project_data.json del legacy:
///   history       — todos los proyectos conocidos (para autocompletar el setter)
///   notes         — pizarra de texto por proyecto
///   paths         — paths/URLs por proyecto
///   shared_notes  — pizarra GLOBAL (desks sin proyecto)
///   shared_paths  — pool de paths GLOBAL (desks sin proyecto)
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
}
