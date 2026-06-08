namespace AmpzDesktopBooster.Services.Attention;

/// <summary>
/// Nivel de una señal de atención — vocabulario NEUTRO del core. A propósito NO habla "Claude"
/// (ni "Notification"/"Stop"): esas son palabras de UN integrador. La traducción de cualquier
/// fuente externa a estos dos niveles vive en el BORDE (el adaptador/cliente que postea al pipe),
/// nunca acá. Así, sumar mañana CI, tests, Discord, etc. = escribir un adaptador nuevo en el borde;
/// este enum (y todo el core) no se toca.
/// </summary>
public enum AttentionLevel
{
    /// <summary>Algo está BLOQUEANDO y necesita acción tuya — andá YA. (Claude: hook Notification.)</summary>
    ActionNeeded,

    /// <summary>Una tarea TERMINÓ, es informativo — revisá cuando puedas. (Claude: hook Stop.)</summary>
    Completed,
}

/// <summary>
/// Señal de atención YA resuelta al dominio: una fuente externa avisa que el proceso <see cref="Pid"/>
/// (que vive en algún escritorio virtual) reclama tu atención con cierto <see cref="Level"/>.
///
/// Snapshot inmutable que cruza del transporte al dominio/UI — mismo criterio que SystemSnapshot:
/// <c>record struct</c> para que sea barato y no se mute por el camino.
///
/// OJO: NO trae el desk. El desk lo deduce la app desde el PID (posición REAL de la ventana del
/// proceso), porque el mismo proyecto puede estar abierto en dos desks distintos → resolver por
/// nombre sería ambiguo. El PID es único; la ventana de su árbol de procesos cae en UN solo desk.
/// </summary>
public readonly record struct AttentionSignal(
    int Pid,
    string Source,        // quién reclama (ej. "claude-code"); gancho futuro para el ícono (Providers/)
    AttentionLevel Level,
    string Message,       // texto libre para el toast; "" si la fuente no mandó nada
    long TimestampUnix,   // epoch en segundos; lo estampa el cliente (el core no usa el reloj acá)
    string Cwd = "",      // folder del proyecto. CLAVE para desambiguar: un host Electron (VS Code) con
                          // varias ventanas comparte el PID del proceso main → el PID solo NO distingue
                          // qué ventana es. El cwd (que el hook ya conoce) elige la correcta por su título.
    long Hwnd = 0);       // ventana EXACTA, si el cliente la conoce (capturó el foreground). Si viene,
                          // la usamos directo y salteamos la resolución por PID — más preciso, e inmune
                          // al re-resolver tardío. El hook headless no la conoce → manda 0 y va por PID+cwd.
