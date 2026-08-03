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
/// Un SERVICIO de un scope: CÓMO se levanta algo para laburar en este espacio/contexto
/// ("npm run dev" en el repo de Plataforma, "php -S" con su puerto, "npm start" del Expo).
///
/// Nace de que el catálogo anterior (ports.json) modelaba el PUERTO — que es la CONSECUENCIA — y no
/// el servicio, que es la cosa. Y le faltaba la mitad del ciclo: sabía decirte si algo corría, pero
/// no sabía hacerlo correr. Por eso vive acá, con scope y herencia, y no en un catálogo global suelto.
///
/// EL PUERTO da el ESTADO (🟢/⚪): es lo único que podemos observar de afuera sin mentir.
///
/// OJO — el puerto NO alcanza para decidir qué entra en "levantar todo", y esto se aprendió a los
/// golpes. La regla original era "Port &gt; 0 = servidor, Port == 0 = tarea", y es FALSA en cuanto
/// aparece un worker: `php artisan queue:work`, `schedule:work`, un watcher de assets — corren para
/// siempre y NO escuchan ningún puerto. Clasificarlos como tarea los dejaba afuera del arranque
/// grupal en silencio: levantabas el 80% del stack creyendo que habías levantado todo, que es el peor
/// tipo de bug porque se lee como éxito. Por eso el arranque tiene su propio campo,
/// <see cref="AutoStart"/>. Son dos preguntas distintas y ahora tienen dos campos distintos.
/// </summary>
public sealed class ServiceEntry
{
    [JsonPropertyName("title")]   public string Title { get; set; } = "";

    /// <summary>
    /// Comando a correr en <see cref="WorkDir"/> (ej. "npm run dev"). VACÍO = entrada de SOLO
    /// MONITOREO: sabemos mirarle el puerto pero no sabemos levantarla. Ése es exactamente el caso
    /// de las entradas migradas del viejo ports.json — el caso DEGENERADO del modelo nuevo.
    /// </summary>
    [JsonPropertyName("command")] public string Command { get; set; } = "";

    /// <summary>
    /// Directorio donde corre el comando. Hoy es un path TIPEADO, literal.
    /// Convención reservada para la fase 2: si arranca con "{" es una REFERENCIA a una variable del
    /// scope (ej. "{Repo}"), que se resuelve al lanzar. Se deja el campo como string plano
    /// justamente para que ese día la migración sea CERO — el campo no cambia de forma.
    /// Precedente del mismo idioma en el repo: <c>AppsConfig.Args</c> con "{path}".
    /// </summary>
    [JsonPropertyName("workDir")] public string WorkDir { get; set; } = "";

    /// <summary>Puerto que escucha, o 0 si no escucha ninguno. Da el estado 🟢/⚪ y nada más.</summary>
    [JsonPropertyName("port")]    public int Port { get; set; }

    /// <summary>
    /// ¿Entra en "levantar todo"? NULLABLE a propósito: <c>null</c> = "no lo decidí, usá el default
    /// inteligente" (<see cref="AutoStartEffective"/>).
    ///
    /// Es nullable y no un bool pelado porque un bool se deserializa en <c>false</c> cuando la key no
    /// está en el JSON — y eso habría apagado EN SILENCIO el arranque grupal de todos los servicios
    /// ya cargados al actualizar la app. Con nullable, "campo ausente" y "el usuario lo destildó" son
    /// dos cosas distintas, que es exactamente la diferencia que importa acá. Migración: CERO.
    /// </summary>
    [JsonPropertyName("autoStart")] public bool? AutoStart { get; set; }

    /// <summary>
    /// Si entra o no en el arranque grupal. Default cuando el usuario no se pronunció: lo dicta el
    /// puerto (con puerto = servidor = arranca; sin puerto = tarea suelta = no). Cubre el 95% sin que
    /// tengas que tocar nada, y deja el worker sin puerto a un solo clic.
    /// </summary>
    [JsonIgnore]
    public bool AutoStartEffective => AutoStart ?? Port > 0;
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

    /// <summary>
    /// Servicios por scope: key = "Espacio" o "Espacio/Contexto", igual que <see cref="Paths"/>.
    ///
    /// POR QUÉ VIVEN ACÁ Y NO EN UN services.json APARTE — no es comodidad, es la única forma de no
    /// romper la disciplina del repo: <c>ProjectStore.MoveScope</c> mueve una key de scope en TODOS
    /// los diccionarios de un saque. Estando acá, renombrar/mover/promover/degradar un espacio arrastra
    /// los servicios con UNA línea. En un archivo aparte habría que re-implementar ese barrido, y media
    /// migración deja servicios huérfanos colgando de un scope que ya no existe: invisibles desde la UI
    /// e imposibles de borrar.
    /// </summary>
    [JsonPropertyName("services")] public Dictionary<string, List<ServiceEntry>> Services { get; set; } = new();

    /// <summary>
    /// Servicios del scope GLOBAL (desks sin espacio). Aparte de <see cref="Services"/> por el mismo
    /// motivo que <see cref="SharedPaths"/>: la global no tiene key de scope. Acá aterrizan las
    /// entradas migradas del viejo ports.json (puerto sin comando).
    /// </summary>
    [JsonPropertyName("shared_services")] public List<ServiceEntry> SharedServices { get; set; } = new();
}
